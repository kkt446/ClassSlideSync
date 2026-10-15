using System;
using System.Threading;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>Everything the card needs to draw itself, as one immutable value.</summary>
    public sealed class CardState
    {
        public bool Showing;             // a slideshow is on screen right now
        public int Current;              // current slide (0 when unknown)
        public int SlideCount;           // total slides (0 when unknown)
        public int Previous;             // where the last lesson stopped (0 when unknown)
        public double PreviousAgeDays = -1;
        public bool PreviousTrusted = true;   // false => the deck was edited since the record
        public string FileName = "";
        public string Hint = "";
        public string Context = "";      // subject / lesson name, when ClassIsland knows them
        public string Status = "";       // the last one-off message (e.g. jump result)

        /// <summary>
        /// true 只表示"演示软件此刻真的打开着某份课件"。不能用 FileName 判断：没有课件时
        /// FileName 会退回台账里最近一条记录的名字，卡片会因此以为自己有课件而一直露在屏幕上。
        /// </summary>
        public bool DeckOpen;

        /// <summary>
        /// 演示软件已经打开了、但还没打开课件（用户 2026-10-15 要求："只要 WPS 演示/PowerPoint
        /// 开着就把小窗露出来，关了就隐藏"）。这时候卡片没有页码可讲，显示"等待打开课件"，
        /// 让老师一眼看出插件在工作，而不是怀疑它没生效。
        /// </summary>
        public bool SuiteOnly;

        /// <summary>正在跑的演示软件名（"WPS 演示" / "PowerPoint"），空白表示没有。</summary>
        public string SuiteName = "";

        /// <summary>Key for change detection: identical states are not re-published.</summary>
        public int Hash()
        {
            if (Showing) return 100000 + Current * 7 + SlideCount;
            return 200000 + Previous * 7 + Current + (DeckOpen ? 500000 : 0)
                   + (SuiteOnly ? 1000000 : 0) + SuiteName.Length * 10000;
        }
    }

    /// <summary>
    /// The one thread that touches PowerPoint's COM object.
    ///
    /// COM calls into a busy presentation suite can take tens or hundreds of milliseconds
    /// (and a modal dialog in the suite can block them outright), so nothing here ever runs
    /// on the UI thread. The card is pushed immutable state snapshots instead.
    ///
    /// The session state machine is deliberately tiny:
    ///   showing=true  -> a lesson is being delivered, remember the highest page reached
    ///   showing=false -> the lesson ended, write one ledger record for it
    /// "Highest page reached" rather than "last page seen" because turning back to an earlier
    /// slide at the end of a lesson is normal and must not rewrite the record.
    /// </summary>
    public sealed class SessionEngine : IDisposable
    {
        readonly Ledger ledger;
        readonly SyncSettings settings;
        readonly object gate = new object();

        Thread thread;
        volatile bool stop;
        PptBridge bridge;
        PptSnapshot last;
        DateTime lastAttachTry = DateTime.MinValue;
        DateTime lastVerify = DateTime.MinValue;
        int lastPushHash = -1;
        string status = "";

        // session state
        string filePath = "";
        string lastFilePath = "";
        bool showing;
        bool deckOpen;    // 演示软件里此刻真的开着课件（不是台账里的历史文件名）
        int highest;      // 这节课翻到过的最远一页，只用于日志
        int lastSlide;    // 老师最后停下的那一页 —— 台账记的是这个
        int startSlide;
        int slideCount;
        DateTime startAt;
        SessionRecord current;
        string currentFp;
        DateTime? pendingEnd;
        int pendingEndSlide;
        int jumpPending;   // 1 = 卡片上有人点了跳页，等工作线程去执行

        public SessionEngine(Ledger ledger, SyncSettings settings)
        {
            this.ledger = ledger;
            this.settings = settings;
        }

        /// <summary>Raised whenever the card's contents change. Fires off the UI thread.</summary>
        public event Action<CardState> StateChanged;

        /// <summary>Raised once the worker thread is up, for startup logging.</summary>
        public event Action Started;

        /// <summary>
        /// Supplies the current subject / lesson name for the session being recorded. Set by
        /// the plugin from ClassIsland's lessons service; null is fine.
        /// </summary>
        public Func<string[]> LessonContext { get; set; }

        // 诊断日志默认关：开着一节课能写几万行，日志文件会被冲爆（老师的机器不是开发机）。
        // 需要排查时在设置页里打开。
        public bool Diag { get; set; } = false;

        /// <summary>
        /// true = 现在不该让卡片露面（用户 2026-10-13 的要求："下课后改为隐藏"）。课表说了算的
        /// 时候，只有上课时段里才显示卡片；下课铃一响，卡片立刻消失，哪怕课件还开着、引擎还在
        /// 记页码。课表之外（没排课/关掉跟课表）由调用方把它置回 false，卡片退回"有课件就显示"。
        /// </summary>
        public bool SuppressCard;

        /// <summary>
        /// 演示软件（PowerPoint / WPS 演示）此刻在桌面上开着。用户 2026-10-15 要求："插件后台一直
        /// 常驻，只看演示软件的进程——开了就把卡片露出来，关了就隐藏"，所以这个值由调度器每两秒
        /// 喂进来（见 SlideSyncService.ScheduleTick），引擎只负责照它画卡片。
        /// </summary>
        public bool SuiteRunning;

        /// <summary>正在跑的演示软件名，卡片拿它当标题（"WPS 演示" / "PowerPoint"）。</summary>
        public string SuiteName = "";

        public Ledger Ledger { get { return ledger; } }
        public PptSnapshot Last { get { return last; } }
        public bool IsShowing { get { return showing; } }
        public string LastFilePath { get { return lastFilePath; } }
        public string AttachMode { get { return bridge == null ? "" : bridge.AttachMode; } }
        public string AppLabel { get { return bridge == null ? "" : bridge.Label; } }

        /// <summary>
        /// When the worker last finished one full step. The service watches this: a COM call can
        /// block forever (PowerPoint sitting on a modal dialog does exactly that), and a blocked
        /// worker means the plugin silently stops noticing lessons. Age it and restart the engine.
        /// </summary>
        public DateTime LastStepUtc { get; private set; }

        public void Start()
        {
            if (thread != null) return;
            LastStepUtc = DateTime.UtcNow;
            thread = new Thread(Loop) { IsBackground = true, Name = "ClassSlideSync-com" };
            thread.Start();
        }

        void Loop()
        {
            ledger.Log("worker started (diag=" + Diag + ")");
            var started = Started;
            if (started != null) started();

            while (!stop)
            {
                try { Step(); }
                catch (Exception ex) { ledger.Log("step failed: " + ex.GetType().Name + " " + ex.Message); }
                LastStepUtc = DateTime.UtcNow;   // 心跳：给看门狗用（Step 里若卡在 COM 上，这行就不会执行）
                Thread.Sleep(400);
            }
            ledger.Log("worker stopped");
        }

        void Step()
        {
            // 跳页请求必须最先处理。2026-10-13 实测的坑：老师点“从第 N 页开始放映”时 PowerPoint
            // 往往根本没开，而下面 bridge == null 的分支会直接 return，请求就永远轮不到执行——
            // 卡片停在“正在跳转…”，什么也不发生。DoJump 里会按需把 PowerPoint 拉起来。
            if (Interlocked.Exchange(ref jumpPending, 0) != 0)
            {
                try { DoJump(); }
                catch (Exception ex) { SetStatus("跳页出错：" + ex.Message); }
            }

            if (bridge == null)
            {
                // 1.2 秒试一次：WPS 的 COM 实例要等窗口起来几秒后才登记进 ROT，重试慢的话
                // 老师刚打开课件那几秒卡片只会显示"正在识别课件…"（2026-10-15 实测大约 4 秒）。
                if ((DateTime.UtcNow - lastAttachTry).TotalSeconds >= 1.2)
                {
                    lastAttachTry = DateTime.UtcNow;
                    PptBridge b = null;
                    try { b = PptBridge.Connect(); }
                    catch (Exception ex) { if (Diag) ledger.Log("attach threw: " + ex.GetType().Name + " " + ex.Message); }
                    if (b != null)
                    {
                        bridge = b;
                        ledger.Log("attached to " + b.Label + " via " + b.ProgId + " [mode=" + b.AttachMode + "]");
                    }
                    else if (Diag)
                    {
                        ledger.Log("attach: nothing answered, and COM could not start a suite");
                        ledger.Log("ROT dump:" + Environment.NewLine + PptBridge.DumpRot());
                    }
                }
                if (bridge == null)
                {
                    if (showing) EndSession(0);
                    Push(false, 0, 0);
                    return;
                }
            }

            // While idle, re-locate the suite every few seconds. A teacher's PC restarts PowerPoint,
            // or the teacher closes and reopens it, and the cached COM pointer then points at a dead
            // server: every call on it fails, the snapshot reads as "nobody is presenting", and the
            // plugin looks like it is ignoring the lesson. Re-connecting finds the new instance by
            // itself. Only done while idle -- an in-progress show proves its own pointer is alive.
            //
            // 2026-10-15 的护栏：只有在"找到一个更值得用的实例"时才换。老师双击文件打开课件时
            // WPS 会开一个**新**实例，而这台机器上往往还挂着别的演示实例（空壳、或者 WPS 一启动
            // 自带的那张没存过的"演示文稿1"）。原来每 5 秒盲换一次，可能把手里真握着课件的实例
            // 换掉，也可能一直挂在空白稿上 —— 老师看到的就是"打开 PPT 了，屏幕上什么都没有"。
            if (!showing && (DateTime.UtcNow - lastVerify).TotalSeconds >= 5)
            {
                lastVerify = DateTime.UtcNow;
                try
                {
                    int mine = bridge.DeckScore;   // 3=有真课件 2=只有没存过的空白稿 1=什么都没有 0=死的
                    if (mine < 3)
                    {
                        var fresh = PptBridge.Connect();
                        int theirs = fresh == null ? 0 : fresh.DeckScore;
                        if (fresh != null && theirs > mine)
                        {
                            bridge = fresh;
                            ledger.Log("reattached: " + fresh.Label + " via " + fresh.ProgId + " [mode=" + fresh.AttachMode + "] (score " + mine + "→" + theirs + ")");
                        }
                        else if (Diag)
                        {
                            ledger.Log("verified: " + bridge.Label + " score=" + mine
                                + (fresh == null ? "（没找到别的实例）" : "（找到的 " + fresh.Label + " score=" + theirs + "，不比现在好）"));
                        }
                    }
                }
                catch (Exception ex) { if (Diag) ledger.Log("verify failed: " + ex.GetType().Name + " " + ex.Message); }
            }

            PptSnapshot snap;
            try { snap = bridge.Snapshot(); }
            catch (Exception ex)
            {
                ledger.Log("snapshot threw: " + ex.Message + " -- dropping the COM reference");
                DropBridge();
                return;
            }

            // A suite closed mid-lesson leaves us holding a dead pointer. The snapshot reports
            // that rather than throwing, so treat *any* failed snapshot as a disconnect and
            // reattach later -- the old code only reacted to one exception type and sat on a dead
            // pointer for the rest of the lesson when the message happened to differ.
            if (snap.Diag != null && snap.Diag.StartsWith("snapshot failed:", StringComparison.Ordinal))
            {
                ledger.Log("snapshot failed (" + snap.Diag + ") -- dropping the COM reference");
                DropBridge();
                return;
            }

            last = snap;
            if (Diag) ledger.Log("snapshot: " + snap + " | " + snap.Diag);

            if (!string.IsNullOrEmpty(snap.FilePath))
            {
                filePath = snap.FilePath;
                lastFilePath = snap.FilePath;
            }
            if (snap.SlideCount > 0) slideCount = snap.SlideCount;

            // "演示软件里真的开着课件"和"台账里有历史文件名"是两件事，卡片显示与否只看前者。
            // 判据是**像路径**、不只是非空：WPS 演示刚启动时自带一份没存过的空白稿，FullName 是
            // "演示文稿1"，那种"课件"没地方跳页、也不该顶掉"等待打开课件"的提示（用户 2026-10-15）。
            deckOpen = PptBridge.LooksLikePath(snap.FilePath);

            if (snap.Showing)
            {
                if (!showing) BeginSession(snap);
                else Advance(snap);
                pendingEnd = null;
                Push(true, snap.Current, snap.SlideCount);
            }
            else
            {
                if (showing)
                {
                    // Some suites briefly report zero windows while a show is still up, so wait
                    // a couple of polls before declaring the lesson over.
                    if (!pendingEnd.HasValue) { pendingEnd = DateTime.UtcNow; pendingEndSlide = lastSlide; }
                    else if ((DateTime.UtcNow - pendingEnd.Value).TotalSeconds >= 2)
                    {
                        EndSession(pendingEndSlide);
                        pendingEnd = null;
                    }
                }
                Push(false, 0, snap.SlideCount);
            }
        }

        void DropBridge()
        {
            var b = bridge;
            bridge = null;
            if (b != null) { try { b.Dispose(); } catch { } }
        }

        // ------------------------------------------------------------------ sessions

        void BeginSession(PptSnapshot snap)
        {
            showing = true;
            startAt = DateTime.Now;   // 本地时间：老师看台账时要对得上墙上钟，不能写 UTC
            startSlide = snap.Current > 0 ? snap.Current : 1;
            highest = startSlide;
            lastSlide = startSlide;
            current = new SessionRecord
            {
                FilePath = filePath,
                StartedAt = startAt.ToString("yyyy-MM-dd HH:mm:ss"),
                StartSlide = startSlide,
                EndSlide = startSlide,
                SlideCount = snap.SlideCount
            };

            var ctx = LessonContext;
            if (ctx != null)
            {
                try
                {
                    var v = ctx();
                    if (v != null && v.Length >= 2)
                    {
                        current.Subject = v[0];
                        current.LessonName = v[1];
                    }
                }
                catch { }
            }

            // Fingerprinting reads up to 128 KB; once per lesson, off the hot path.
            currentFp = Ledger.Fingerprint(filePath);
            ledger.Log("show started: file=" + filePath + " start=" + startSlide + "/" + snap.SlideCount
                + (string.IsNullOrEmpty(current.Subject) ? "" : " subject=" + current.Subject));
        }

        void Advance(PptSnapshot snap)
        {
            if (snap.Current > highest) highest = snap.Current;
            // 记“最后停在哪一页”，不是“翻到过的最远页”：老师上完课往往会把课件翻回第 1 页保存，
            // 那一页才是下次要接着讲的地方。
            if (snap.Current > 0) { lastSlide = snap.Current; current.EndSlide = lastSlide; }
            if (snap.SlideCount > 0)
            {
                current.SlideCount = snap.SlideCount;
                slideCount = snap.SlideCount;
            }
            // Keep the on-disk copy honest even if the plugin is killed mid-lesson.
            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            current.Seconds = (int)(DateTime.Now - startAt).TotalSeconds;
            if (settings.RecordLedger && current.Seconds >= settings.MinSessionSeconds) Commit(current);
        }

        void EndSession(int endSlide)
        {
            showing = false;
            if (current == null) return;
            if (endSlide <= 0) endSlide = lastSlide;
            current.EndSlide = endSlide > 0 ? endSlide : current.StartSlide;
            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            current.Seconds = (int)(DateTime.Now - startAt).TotalSeconds;
            current.Fingerprint = currentFp;
            if (current.Seconds >= settings.MinSessionSeconds)
            {
                if (settings.RecordLedger) Commit(current);
                ledger.Log("session recorded: " + System.IO.Path.GetFileName(current.FilePath) +
                    " pages " + current.StartSlide + "->" + current.EndSlide +
                    " (max " + highest + ")" +
                    " for " + current.Seconds + "s");
            }
            else ledger.Log("session ignored (only " + current.Seconds + "s)");
            current = null;
        }

        void Commit(SessionRecord r)
        {
            var same = ledger.Last(r.FilePath);
            if (same != null && same.StartedAt == r.StartedAt)
            {
                same.EndSlide = r.EndSlide;
                same.EndedAt = r.EndedAt;
                same.Seconds = r.Seconds;
                same.SlideCount = r.SlideCount;
                same.Fingerprint = r.Fingerprint;
            }
            else ledger.Add(r);
            ledger.Save();
        }

        // ------------------------------------------------------------------ actions

        /// <summary>
        /// Jump the running show to where the last lesson stopped. Called from the UI thread;
        /// the COM call itself happens on the worker's bridge, which is only ever touched here.
        /// </summary>
        /// <summary>
        /// Ask for a jump to where the last lesson stopped. Called from the card's UI thread, so it
        /// only raises a flag -- the COM work happens on the worker thread in <see cref="Step"/>.
        /// </summary>
        public void RequestJump()
        {
            Interlocked.Exchange(ref jumpPending, 1);
            SetStatus("正在跳转…");
        }

        void DoJump()
        {
            var r = ledger.Last(lastFilePath);
            if (r == null) r = ledger.Newest();   // 刚开机/刚上完课时还不知道是哪个文件
            if (r == null) { SetStatus("还没有这堂课的记录"); return; }

            var b = bridge;
            if (b == null)
            {
                // 老师点了“从第 N 页开始放映”，这时候连 PowerPoint 一起拉起来才是他要的。
                SetStatus("正在打开 PowerPoint…");
                b = PptBridge.Launch(r.FilePath);
                if (b == null) { SetStatus("打不开 PowerPoint"); return; }
                bridge = b;   // 让后面的轮询也用上这个实例
                ledger.Log("jump: launched " + b.ProgId + " for " + r.FilePath);
            }

            bool wasShowing = last != null && last.Showing;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var err = b.JumpToSlide(r.EndSlide, settings.JumpResetsAnimation, r.FilePath);
            sw.Stop();
            SetStatus(err.Length == 0
                ? (wasShowing ? "已跳到第 " + r.EndSlide + " 页" : "已从第 " + r.EndSlide + " 页开始放映")
                : err);
            ledger.Log("jump -> " + r.EndSlide + " : " + (err.Length == 0 ? "ok" : err)
                + " (" + sw.ElapsedMilliseconds + " ms"
                + (string.IsNullOrEmpty(b.LastViewRoute) ? "" : ", view=" + b.LastViewRoute) + ")");
        }

        /// <summary>Clear the one-off status line shown on the card.</summary>
        public void ClearStatus() { SetStatus(""); }

        void SetStatus(string s)
        {
            lock (gate) { status = s; }
            lastPushHash = -1;   // force the next push to go out even if the page did not move
            Push(showing, last == null ? 0 : last.Current, slideCount);
        }

        // ------------------------------------------------------------------ push

        void Push(bool isShowing, int currentSlide, int count)
        {
            var st = BuildState(isShowing, currentSlide, count);
            int h = st.Hash();
            if (h == lastPushHash) return;
            lastPushHash = h;
            var ev = StateChanged;
            if (ev != null)
            {
                try { ev(st); } catch { }
            }
        }

        CardState BuildState(bool isShowing, int currentSlide, int count)
        {
            var st = new CardState();
            st.Showing = isShowing;
            // SuppressCard = 课表说了算、而且现在不在正课时间里（下课了、或者还没到上课时间）。
            // 这时候卡片整体不出现——下课铃一响屏幕上就该干净（用户 2026-10-13 要求）。
            // 注意引擎照旧在跑、照旧记页码，只是不往屏幕上画。
            bool hasDeck = deckOpen || isShowing;
            // 演示软件开着也算"该露面"（用户 2026-10-15 要求）：这时候哪怕还没打开课件，
            // 也要让老师看见卡片在等他打开。
            st.DeckOpen = (hasDeck || SuiteRunning) && !SuppressCard;
            st.SuiteOnly = SuiteRunning && !hasDeck && !SuppressCard;
            st.SuiteName = SuiteRunning ? SuiteName : "";
            st.Current = currentSlide;
            st.SlideCount = count;
            lock (gate) { st.Status = status; }
            try { st.FileName = string.IsNullOrEmpty(lastFilePath) ? "" : System.IO.Path.GetFileName(lastFilePath); }
            catch { st.FileName = lastFilePath; }

            var ctx = LessonContext;
            if (ctx != null)
            {
                try
                {
                    var v = ctx();
                    if (v != null && v.Length >= 1) st.Context = v.Length >= 2 && !string.IsNullOrEmpty(v[0])
                        ? v[0] + " · " + v[1] : v[0];
                }
                catch { }
            }

            // 没有在放映时 lastFilePath 是空的，此时退回到“最近一条记录”，
            // 否则老师刚上完课、放映一结束，卡片就会说“还没有这堂课的记录”。
            var r = ledger.Last(lastFilePath);
            if (r == null) r = ledger.Newest();
            if (r != null)
            {
                st.Previous = r.EndSlide;
                st.PreviousAgeDays = Age(r);
                if (string.IsNullOrEmpty(r.Fingerprint)) st.PreviousTrusted = true;
                else
                {
                    var fpFile = Ledger.Fingerprint(r.FilePath);
                    st.PreviousTrusted = fpFile.Length > 0 && fpFile == r.Fingerprint;
                }
                if (string.IsNullOrEmpty(st.FileName))
                {
                    try { st.FileName = System.IO.Path.GetFileName(r.FilePath); } catch { }
                }
                st.Hint = st.PreviousTrusted
                    ? "记录于 " + ShortAge(st.PreviousAgeDays)
                    : "文件在那之后被改过，页码可能已偏移";
            }
            else st.Hint = "还没有这堂课的记录";

            // 演示软件开着、课件还没打开：这时候台账里的"最近一条记录"跟眼前无关（那份课件还没被
            // 打开），所以标题写软件名、页码留空、也不给跳页行，只说一句在等什么（用户 2026-10-15）。
            if (st.SuiteOnly)
            {
                st.FileName = SuiteName;
                st.Previous = 0;
                st.Context = "";
                st.Hint = "打开课件就能接着上次讲";
            }
            return st;
        }

        static double Age(SessionRecord r)
        {
            DateTime t;
            if (!DateTime.TryParse(r.EndedAt ?? r.StartedAt, out t)) return -1;
            return (DateTime.Now - t).TotalDays;
        }

        static string ShortAge(double days)
        {
            if (days < 0) return "刚刚";
            if (days < 1.0 / 24) return "刚刚";
            if (days < 1) return (int)(days * 24) + " 小时前";
            if (days < 30) return (int)days + " 天前";
            return (int)(days / 30) + " 个月前";
        }

        public void Stop()
        {
            stop = true;
            var t = thread;
            if (t != null && t.IsAlive) { try { t.Join(1500); } catch { } }
            DropBridge();
        }

        public void Dispose() { Stop(); }
    }
}
