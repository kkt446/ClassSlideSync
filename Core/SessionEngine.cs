using System;
using System.Collections.Generic;
using System.Threading;

namespace ClassSlideSync.Plugin.Core
{
    public sealed class CardHistoryItem
    {
        public string FilePath = "";  // 这条记录属于哪份文件（跨文件历史跳页必须认它，不能只认页码）
        public int Slide;
        public string Label = "";     // 整行文字；== 不含文件路径，卡片上放不下也没必要
        public bool IsLatest;
    }

    public sealed class CardState
    {
        public bool Showing;
        public int Current;
        public int SlideCount;
        public int Previous;
        public double PreviousAgeDays = -1;
        public bool PreviousTrusted = true;
        public string FileName = "";
        public string Hint = "";
        public string Context = "";      // subject / lesson name, when ClassIsland knows them
        public string Status = "";       // the last one-off message (e.g. jump result)

        public int HistoryTotal;

        public List<CardHistoryItem> History = new List<CardHistoryItem>();

        public List<CardFileItem> Files = new List<CardFileItem>();

        public bool DeckOpen;

        public bool SuiteOnly;

        public string SuiteName = "";

        public int Hash()
        {
            int h = Showing
                ? 100000 + Current * 7 + SlideCount
                : 200000 + Previous * 7 + Current + (DeckOpen ? 500000 : 0)
                  + (SuiteOnly ? 1000000 : 0) + SuiteName.Length * 10000;
            h = h * 31 + HistoryTotal;
            if (History != null && History.Count > 0) h = h * 31 + History[0].Slide;
            if (Files != null)
            {
                h = h * 31 + Files.Count;
                for (int i = 0; i < Files.Count; i++)
                {
                    var f = Files[i];
                    if (f == null) { h = h * 31 + 17; continue; }
                    h = h * 31 + (f.Key ?? "").GetHashCode();
                    h = h * 31 + f.Total;
                    h = h * 31 + (f.IsCurrent ? 1 : 0);
                    if (f.History != null && f.History.Count > 0) h = h * 31 + f.History[0].Slide;
                }
            }
            return h;
        }
    }

    public sealed class SessionEngine : IDisposable
    {
        readonly Ledger ledger;
        readonly SyncSettings settings;
        readonly object gate = new object();

        Thread thread;
        volatile bool stop;
        PptBridge bridge;
        readonly SeewoBridge seewo;
        PptSnapshot last;
        DateTime lastAttachTry = DateTime.MinValue;
        DateTime lastVerify = DateTime.MinValue;
        int lastPushHash = -1;
        DateTime lastSnapAt = DateTime.MinValue;
        bool lastOnScreen;                         // 上一轮"演示软件窗口在屏幕上"
        string status = "";

        string filePath = "";
        string lastFilePath = "";
        bool showing;
        bool deckOpen;
        bool usingSeewo;
        string lastSeewoTitle;
        string lastSeewoDiag = "";
        public string LastSeewoError { get; private set; } = "";
        int highest;
        int lastSlide;
        int startSlide;
        int slideCount;
        DateTime startAt;
        SessionRecord current;
        string currentFp;
        DateTime? pendingEnd;
        int pendingEndSlide;
        int jumpPending;
        int jumpTarget;
        string? jumpFile;

        SessionRecord displayed;
        SessionRecord recentEnded;
        DateTime recentEndedAt = DateTime.MinValue;
        const int RecentSessionSeconds = 120;

        public SessionEngine(Ledger ledger, SyncSettings settings)
        {
            this.ledger = ledger;
            this.settings = settings;
            this.seewo = new SeewoBridge(ledger);
        }

        public event Action<CardState> StateChanged;

        public event Action Started;

        public Func<string[]> LessonContext { get; set; }

        public bool Diag { get; set; } = false;

        public bool SuppressCard;

        public bool SuiteRunning;

        public string SuiteName = "";

        public Ledger Ledger { get { return ledger; } }
        public PptSnapshot Last { get { return last; } }
        public bool IsShowing { get { return showing; } }
        public string LastFilePath { get { return lastFilePath; } }
        public string AttachMode { get { return bridge == null ? "" : bridge.AttachMode; } }
        public string AppLabel { get { return bridge == null ? "" : bridge.Label; } }

        public DateTime LastStepUtc { get; private set; }

        DateTime lastSlowCom = DateTime.MinValue;

        public void PushCheapState()
        {
            try { Push(showing, lastSlide, slideCount); } catch { }
        }

        void ApplyTitleFallback()
        {
            try
            {
                if (PptBridge.LooksLikePath(filePath)) return;
                var docs = PptBridge.SuiteFrameDocuments();
                for (int i = 0; i < docs.Length; i++)
                {
                    var rec = ledger.FindByName(docs[i]);
                    if (rec == null || !PptBridge.LooksLikePath(rec.FilePath)) continue;
                    filePath = rec.FilePath;
                    lastFilePath = rec.FilePath;
                    if (rec.SlideCount > 0 && slideCount <= 0) slideCount = rec.SlideCount;
                    if (Diag) ledger.Log("deck from window title: " + docs[i] + " -> " + rec.FilePath);
                    return;
                }
            }
            catch { }
        }

        void NoteComCost(string what, long ms)
        {
            if (ms < 4000) return;
            lastSlowCom = DateTime.UtcNow;
            try { ledger.Log("slow COM: " + what + " took " + ms + " ms -- 暂停发 COM 6 秒"); } catch { }
        }

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
                LastStepUtc = DateTime.UtcNow;
                Thread.Sleep(250);
            }
            ledger.Log("worker stopped");
        }

        void Step()
        {
            if (Interlocked.Exchange(ref jumpPending, 0) != 0)
            {
                try { DoJump(); }
                catch (Exception ex) { SetStatus("跳页出错：" + ex.Message); }
            }

            bool comBlocked = (DateTime.UtcNow - lastSlowCom).TotalSeconds < 6;

            if (bridge == null)
            {
                if (comBlocked)
                {
                    if (TrySeewo()) return;
                    Push(showing, lastSlide, slideCount);
                    return;
                }

                if ((DateTime.UtcNow - lastAttachTry).TotalSeconds >= 1.2)
                {
                    lastAttachTry = DateTime.UtcNow;
                    PptBridge b = null;
                    var swAttach = System.Diagnostics.Stopwatch.StartNew();
                    try { b = PptBridge.Connect(); }
                    catch (Exception ex) { if (Diag) ledger.Log("attach threw: " + ex.GetType().Name + " " + ex.Message); }
                    swAttach.Stop();
                    NoteComCost("Connect", swAttach.ElapsedMilliseconds);
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
                    if (TrySeewo()) return;
                    if (showing) EndSession(0);
                    ApplyTitleFallback();
                    Push(false, 0, 0);
                    return;
                }
            }

            if (!showing && !comBlocked && (DateTime.UtcNow - lastVerify).TotalSeconds >= 5)
            {
                lastVerify = DateTime.UtcNow;
                try
                {
                    var swVerify = System.Diagnostics.Stopwatch.StartNew();
                    int mine = bridge.DeckScore;
                    swVerify.Stop();
                    NoteComCost("DeckScore", swVerify.ElapsedMilliseconds);
                    if (mine < 3)
                    {
                        var swFresh = System.Diagnostics.Stopwatch.StartNew();
                        var fresh = PptBridge.Connect();
                        int theirs = fresh == null ? 0 : fresh.DeckScore;
                        swFresh.Stop();
                        NoteComCost("Connect+DeckScore", swFresh.ElapsedMilliseconds);
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

            bool onScreen;
            try { onScreen = PptBridge.SuiteOnScreen(); } catch { onScreen = false; }
            int snapInterval = showing ? 350 : ((SuiteRunning || onScreen) ? 700 : 2000);
            bool windowChanged = onScreen != lastOnScreen;
            lastOnScreen = onScreen;
            if (!windowChanged && (DateTime.UtcNow - lastSnapAt).TotalMilliseconds < snapInterval) return;
            lastSnapAt = DateTime.UtcNow;

            if (comBlocked)
            {
                if (TrySeewo()) return;
                Push(showing, lastSlide, slideCount);
                return;
            }

            PptSnapshot snap;
            var swSnap = System.Diagnostics.Stopwatch.StartNew();
            try { snap = bridge.Snapshot(); }
            catch (Exception ex)
            {
                swSnap.Stop();
                NoteComCost("Snapshot(抛异常)", swSnap.ElapsedMilliseconds);
                ledger.Log("snapshot threw: " + ex.Message + " -- dropping the COM reference");
                DropBridge();
                if (TrySeewo()) return;
                return;
            }
            swSnap.Stop();
            NoteComCost("Snapshot", swSnap.ElapsedMilliseconds);

            if (snap.Diag != null && snap.Diag.StartsWith("snapshot failed:", StringComparison.Ordinal))
            {
                ledger.Log("snapshot failed (" + snap.Diag + ") -- dropping the COM reference");
                DropBridge();
                if (TrySeewo()) return;
                return;
            }

            if (snap.Showing && usingSeewo)
            {
                EndSession(lastSlide);
                pendingEnd = null;
                usingSeewo = false;
            }

            if (!snap.Showing
                && (!PptBridge.LooksLikePath(snap.FilePath) || SeewoBridge.LooksRunning())
                && TrySeewo())
            {
                return;
            }

            last = snap;
            if (Diag) ledger.Log("snapshot: " + snap + " | " + snap.Diag);

            if (!string.IsNullOrEmpty(snap.FilePath))
            {
                filePath = snap.FilePath;
                lastFilePath = snap.FilePath;
            }
            if (!PptBridge.LooksLikePath(filePath)) ApplyTitleFallback();
            if (snap.SlideCount > 0) slideCount = snap.SlideCount;

            deckOpen = PptBridge.LooksLikePath(filePath);

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
                    if (!pendingEnd.HasValue) { pendingEnd = DateTime.UtcNow; pendingEndSlide = lastSlide; }
                    else
                    {
                        try
                        {
                            int edit = bridge.EditingSlide();
                            if (edit > pendingEndSlide && (slideCount <= 0 || edit <= slideCount)) pendingEndSlide = edit;
                        }
                        catch { }

                        if ((DateTime.UtcNow - pendingEnd.Value).TotalSeconds >= 2)
                        {
                            EndSession(pendingEndSlide);
                            pendingEnd = null;
                        }
                    }
                }
                Push(false, 0, snap.SlideCount);
            }
        }

        void DropBridge()
        {
            if (showing && current != null)
            {
                try { EndSession(lastSlide); } catch { }
            }
            var b = bridge;
            bridge = null;
            if (b != null) { try { b.Dispose(); } catch { } }
        }

        bool TrySeewo()
        {
            try
            {
                var s = seewo.GetState();
                if (s == null) return false;

                if (!s.Found)
                {
                    if (!usingSeewo) return false;
                    if (!s.Running)
                    {
                        ProcessSeewoSnapshot(NewSeewoSnapshot(false, lastSeewoTitle, 0, slideCount, ""));
                        usingSeewo = false;
                    }
                    return true;
                }

                string title = (s.Title ?? "").Trim();
                if (title.Length == 0)
                {
                    if (!usingSeewo && !SeewoBridge.IsSeewoKey(lastFilePath)) return false;
                    ProcessSeewoSnapshot(NewSeewoSnapshot(false, "", 0, 0, s.Diag));
                    usingSeewo = false;
                    return true;
                }
                lastSeewoTitle = title;

                if (s.Showing)
                {
                    if (showing && !usingSeewo)
                    {
                        EndSession(lastSlide);
                        pendingEnd = null;
                    }
                    usingSeewo = true;
                    ProcessSeewoSnapshot(NewSeewoSnapshot(true, title, s.Current, s.SlideCount, s.Diag));
                    return true;
                }

                if (!usingSeewo)
                {
                    if (bridge != null) return false;
                    usingSeewo = true;
                }
                ProcessSeewoSnapshot(NewSeewoSnapshot(false, title, 0, s.SlideCount, s.Diag));
                return true;
            }
            catch (Exception ex)
            {
                LastSeewoError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        PptSnapshot NewSeewoSnapshot(bool showingNow, string title, int current, int total, string diag = null)
        {
            return new PptSnapshot
            {
                AppFound = true,
                AppKind = "希沃白板",
                ProgId = "EasiNote.McpServer",
                Showing = showingNow,
                FilePath = SeewoBridge.KeyFor(title),
                Current = current,
                SlideCount = total,
                Diag = diag ?? ""
            };
        }

        void ProcessSeewoSnapshot(PptSnapshot snap)
        {
            last = snap;
            string d = snap.Diag ?? "";
            if (d.Length > 0 && d != lastSeewoDiag)
            {
                lastSeewoDiag = d;
                ledger.Log("snapshot: " + snap + " | " + d);
            }
            if (!string.IsNullOrEmpty(snap.FilePath))
            {
                filePath = snap.FilePath;
                lastFilePath = snap.FilePath;
            }
            else if (!snap.Showing)
            {
                filePath = "";
                lastFilePath = "";
            }
            if (snap.SlideCount > 0) slideCount = snap.SlideCount;
            deckOpen = !string.IsNullOrEmpty(snap.FilePath);

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
                    if (!pendingEnd.HasValue) { pendingEnd = DateTime.UtcNow; pendingEndSlide = lastSlide; }
                    else if ((DateTime.UtcNow - pendingEnd.Value).TotalSeconds >= 2)
                    {
                        EndSession(pendingEndSlide);
                        pendingEnd = null;
                        usingSeewo = false;
                    }
                }
                else
                {
                    usingSeewo = false;
                }
                Push(false, 0, snap.SlideCount > 0 ? snap.SlideCount : slideCount);
            }
        }

        void BeginSession(PptSnapshot snap)
        {
            showing = true;
            startAt = DateTime.Now;
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

            currentFp = Ledger.Fingerprint(filePath);
            ledger.Log("show started: file=" + filePath + " start=" + startSlide + "/" + snap.SlideCount
                + (string.IsNullOrEmpty(current.Subject) ? "" : " subject=" + current.Subject));
        }

        void Advance(PptSnapshot snap)
        {
            if (current != null
                && !string.IsNullOrEmpty(snap.FilePath)
                && PptBridge.LooksLikePath(snap.FilePath)
                && !string.IsNullOrEmpty(current.FilePath)
                && !SameBucket(snap.FilePath, current.FilePath))
            {
                EndSession(lastSlide);
                BeginSession(snap);
                return;
            }

            if (snap.Current > highest) highest = snap.Current;
            if (snap.Current > 0) { lastSlide = snap.Current; current.EndSlide = highest; }
            if (snap.SlideCount > 0)
            {
                current.SlideCount = snap.SlideCount;
                slideCount = snap.SlideCount;
            }
            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            current.Seconds = (int)(DateTime.Now - startAt).TotalSeconds;
            current.Fingerprint = currentFp;
            if (settings.RecordLedger && current.Seconds >= settings.MinSessionSeconds) Commit(current);
        }

        void EndSession(int endSlide)
        {
            showing = false;
            if (current == null) return;
            if (endSlide <= 0) endSlide = lastSlide;
            if (endSlide <= 0) endSlide = current.StartSlide;
            int end = Math.Max(highest, endSlide);
            current.EndSlide = end > 0 ? end : 1;
            current.EndedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            current.Seconds = (int)(DateTime.Now - startAt).TotalSeconds;
            current.Fingerprint = currentFp;
            if (current.Seconds >= settings.MinSessionSeconds)
            {
                if (settings.RecordLedger) Commit(current);
                ledger.Log("session recorded: " + FileLabel(current.FilePath) +
                    " pages " + current.StartSlide + "->" + current.EndSlide +
                    " (max " + highest + ")" +
                    " for " + current.Seconds + "s");
            }
            else ledger.Log("session ignored (only " + current.Seconds + "s)");

            recentEnded = current;
            recentEndedAt = DateTime.Now;
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
            else
            {
                bool noise = r.EndSlide == r.StartSlide && same != null;
                if (noise)
                {
                    ledger.Log("session ignored (no page advance, " + r.Seconds + "s): " + FileLabel(r.FilePath));
                    return;
                }
                ledger.Add(r);
            }
            ledger.Save();
        }

        public void RequestJump()
        {
            Interlocked.Exchange(ref jumpTarget, 0);
            Interlocked.Exchange(ref jumpFile, null);
            Interlocked.Exchange(ref jumpPending, 1);
            SetStatus("正在跳转…");
        }

        public void RequestJumpTo(int slide)
        {
            RequestJumpTo(null, slide);
        }

        public void RequestJumpTo(string filePath, int slide)
        {
            string file = CardFileList.Normalize(filePath);
            if (slide <= 0 && file.Length > 0) return;
            Interlocked.Exchange(ref jumpFile, file);
            Interlocked.Exchange(ref jumpTarget, slide);
            Interlocked.Exchange(ref jumpPending, 1);
            SetStatus("正在跳转…");
        }

        string CurrentKey()
        {
            return filePath.Length > 0 ? filePath : lastFilePath;
        }

        static bool SameBucket(string a, string b)
        {
            string ka = Ledger.Key(a), kb = Ledger.Key(b);
            return ka.Length > 0 && ka == kb;
        }

        SessionRecord PickRecord(out string histKey, out bool recent)
        {
            recent = false;
            histKey = CurrentKey();
            string curKey = histKey;
            var r = histKey.Length > 0 ? ledger.Last(histKey) : null;
            if (r == null && histKey.Length > 0)
            {
                var byName = ledger.FindByName(FileLabel(histKey));
                if (byName != null) { r = byName; histKey = byName.FilePath; }
                else
                {
                    var byFp = ledger.FindByFingerprint(curKey);
                    if (byFp != null) { r = byFp; histKey = byFp.FilePath; }
                }
            }
            r = Retarget(r, curKey);

            if (!showing
                && recentEnded != null
                && (DateTime.Now - recentEndedAt).TotalSeconds <= RecentSessionSeconds
                && !SameBucket(histKey, recentEnded.FilePath))
            {
                recent = true;
                histKey = recentEnded.FilePath;
                return recentEnded;
            }
            return r;
        }

        static SessionRecord? Retarget(SessionRecord? r, string path)
        {
            if (r == null || string.IsNullOrEmpty(path)) return r;
            if (string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)) return r;
            if (!PptBridge.LooksLikePath(r.FilePath) || !PptBridge.LooksLikePath(path)) return r;
            if (File.Exists(r.FilePath) || !File.Exists(path)) return r;
            return new SessionRecord
            {
                FilePath = path,
                StartedAt = r.StartedAt,
                EndedAt = r.EndedAt,
                StartSlide = r.StartSlide,
                EndSlide = r.EndSlide,
                SlideCount = r.SlideCount,
                Fingerprint = r.Fingerprint,
                Seconds = r.Seconds,
                Subject = r.Subject,
                LessonName = r.LessonName,
            };
        }

        void DoJump()
        {
            int explicitSlide = Interlocked.Exchange(ref jumpTarget, 0);
            string explicitFile = CardFileList.Normalize(Interlocked.Exchange(ref jumpFile, null));
            string histKey;
            bool recent;
            var r = displayed;
            if (r == null) r = PickRecord(out histKey, out recent);
            string jumpPath = explicitFile;
            if (jumpPath.Length == 0 && r != null) jumpPath = CardFileList.Normalize(r.FilePath);
            if (jumpPath.Length == 0)
            {
                SetStatus("还没有这堂课的记录");
                ledger.Log("jump ignored: no record for " + FileLabel(CurrentKey()));
                return;
            }
            int target = explicitSlide > 0 ? explicitSlide : (r != null ? r.EndSlide : 0);
            if (target <= 0)
            {
                SetStatus("这条记录没有页码");
                ledger.Log("jump ignored: no slide for " + FileLabel(jumpPath));
                return;
            }

            if (SeewoBridge.IsSeewoKey(jumpPath))
            {
                SetStatus("正在跳转…");
                string seewoErr = seewo.GotoSlide(target);
                SetStatus(seewoErr.Length == 0 ? "已跳到第 " + target + " 页" : seewoErr);
                ledger.Log("jump -> " + target + " (seewo): " + (seewoErr.Length == 0 ? "ok" : seewoErr));
                return;
            }

            var b = bridge;
            if (b == null)
            {
                SetStatus("正在打开 PowerPoint…");
                b = PptBridge.Launch(jumpPath);
                if (b == null) { SetStatus("打不开 PowerPoint"); return; }
                bridge = b;
                ledger.Log("jump: launched " + b.ProgId + " for " + jumpPath);
            }

            bool wasShowing = last != null && last.Showing;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var err = b.JumpToSlide(target, settings.JumpResetsAnimation, jumpPath);
            sw.Stop();
            SetStatus(err.Length == 0
                ? (wasShowing ? "已跳到第 " + target + " 页" : "已从第 " + target + " 页开始放映")
                : err);
            ledger.Log("jump -> " + target + " : " + (err.Length == 0 ? "ok" : err)
                + " (" + sw.ElapsedMilliseconds + " ms"
                + (string.IsNullOrEmpty(b.LastViewRoute) ? "" : ", view=" + b.LastViewRoute) + ")");
        }

        public void ClearStatus() { SetStatus(""); }

        void SetStatus(string s)
        {
            lock (gate) { status = s; }
            lastPushHash = -1;
            Push(showing, last == null ? 0 : last.Current, slideCount);
        }

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
            bool hasDeck = deckOpen || isShowing;
            st.DeckOpen = (hasDeck || SuiteRunning) && !SuppressCard;
            st.SuiteOnly = SuiteRunning && !hasDeck && !SuppressCard;
            st.SuiteName = SuiteRunning ? SuiteName : "";
            st.Current = currentSlide;
            st.SlideCount = count;
            lock (gate) { st.Status = status; }
            st.FileName = FileLabel(lastFilePath);

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

            string histKey;
            bool recent;
            var r = PickRecord(out histKey, out recent);
            displayed = r;
            if (r != null)
            {
                st.Previous = r.EndSlide > 0 ? r.EndSlide : r.StartSlide;
                bool finished = st.Previous > 0 && r.SlideCount > 0 && st.Previous >= r.SlideCount;
                if (finished) st.Previous = 0;
                st.PreviousAgeDays = Age(r);
                if (string.IsNullOrEmpty(r.Fingerprint)) st.PreviousTrusted = true;
                else if (SeewoBridge.IsSeewoKey(r.FilePath)) st.PreviousTrusted = true;
                else
                {
                    var fpFile = Ledger.Fingerprint(r.FilePath);
                    st.PreviousTrusted = fpFile.Length > 0 && fpFile == r.Fingerprint;
                }
                if (recent || string.IsNullOrEmpty(st.FileName))
                {
                    st.FileName = FileLabel(r.FilePath);
                }
                st.Hint = st.PreviousTrusted
                    ? "记录于 " + ShortAge(st.PreviousAgeDays)
                    : "文件在那之后被改过，页码可能已偏移";
                if (finished) st.Hint = "上次已讲到最后一页";
                if (recent) st.Hint = "刚讲完 · " + (st.PreviousTrusted ? "点这里接着讲" : "文件改过，页码可能偏移");
            }
            else st.Hint = "还没有这堂课的记录";

            if (string.IsNullOrEmpty(histKey) && r != null) histKey = r.FilePath;
            st.HistoryTotal = ledger.Count(histKey);
            if (st.HistoryTotal > 0)
            {
                var recents = ledger.Recent(histKey, 6);
                for (int i = 0; i < recents.Count; i++)
                {
                    var hr = recents[i];
                    st.History.Add(new CardHistoryItem
                    {
                        FilePath = CardFileList.Normalize(hr.FilePath),
                        Slide = hr.EndSlide,
                        Label = HistoryLabel(hr),
                        IsLatest = i == 0
                    });
                }
            }

            st.Files = CardFileList.Build(ledger, histKey, CardFileList.MaxFiles, 6);

            if (st.SuiteOnly && !recent)
            {
                st.FileName = SuiteName;
                st.Previous = 0;
                st.Context = "";
                st.Hint = "打开课件就能接着上次讲";
                st.History.Clear();
                st.HistoryTotal = 0;
            }
            return st;
        }

        static double Age(SessionRecord r)
        {
            DateTime t;
            if (!DateTime.TryParse(r.EndedAt ?? r.StartedAt, out t)) return -1;
            return (DateTime.Now - t).TotalDays;
        }

        internal static string ShortAge(double days)
        {
            if (days < 0) return "刚刚";
            if (days < 1.0 / 24) return "刚刚";
            if (days < 1) return (int)(days * 24) + " 小时前";
            if (days < 30) return (int)days + " 天前";
            return (int)(days / 30) + " 个月前";
        }

        internal static string HistoryLabel(SessionRecord r)
        {
            int slide = r.EndSlide > 0 ? r.EndSlide : r.StartSlide;
            var sb = new System.Text.StringBuilder();
            sb.Append("第 ").Append(slide).Append(" 页");
            DateTime t;
            if (DateTime.TryParse(r.StartedAt, out t)) sb.Append(" · ").Append(t.ToString("MM-dd HH:mm"));
            sb.Append(" · 讲了 ").Append(Duration(r.Seconds));
            return sb.ToString();
        }

        static string Duration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + " 秒";
            if (seconds < 3600) return (seconds / 60) + " 分钟";
            return (seconds / 3600) + " 小时 " + ((seconds % 3600) / 60) + " 分";
        }

        static string FileLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (SeewoBridge.IsSeewoKey(key)) return SeewoBridge.DisplayName(key);
            try
            {
                string name = System.IO.Path.GetFileName(key);

                if (name.IndexOf('.') < 0 && name.IndexOf('\\') < 0 && name.IndexOf('/') < 0)
                {
                    return "";
                }

                return name;
            }
            catch { return key; }
        }

        public void Stop()
        {
            stop = true;
            var t = thread;
            bool joined = true;
            if (t != null && t.IsAlive) { try { joined = t.Join(1500); } catch { joined = false; } }

            if (joined) DropBridge();
            else { try { ledger.Log("engine thread still busy in COM -- 这次不释放 COM 引用，避免释放竞态"); } catch { } }

            try { seewo.Dispose(); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
