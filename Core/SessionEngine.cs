using System;
using System.Collections.Generic;
using System.Threading;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>One row in the card's history panel: where a lesson stopped, and when.</summary>
    public sealed class CardHistoryItem
    {
        public string FilePath = "";  // 这条记录属于哪份文件（跨文件历史跳页必须认它，不能只认页码）
        public int Slide;             // 点这行要跳到的页码（那条记录停下的地方）
        public string Label = "";     // 整行文字；== 不含文件路径，卡片上放不下也没必要
        /// <summary>
        /// 同一行的几种写法，从最全到最省（见 <see cref="HistoryLine.Variants"/>）。
        /// 卡片两百多像素宽，"周三 第 3 节 · 15:20 · 第 5 → 8 页"常常放不下，
        /// 绘制时挑第一个塞得进的，先砍秒级细节、绝不动节次和页码。
        /// Label 永远是这里的第一项，取不到 Variants 时绘制代码只用 Label。
        /// </summary>
        public string[] Labels = new string[0];
        public bool IsLatest;         // 最近一条，用强调色标出来
    }

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
        /// 任务 6：课件改过、页码和旧版对不齐时，这里放「已按内容对齐到第 N 页」或
        /// 「课件改得太多，页码对不上了」这类说明；空串表示没有可说的，退回通用提示。
        /// </summary>
        public string RemapNote = "";

        /// <summary>
        /// 「还剩 N 页 · 约 M 分钟」（2026-10-24 功能1）：0 表示这一段不该出现在卡片上。
        /// 只有放映中、总页数已知、当前页后面还有页时才有值 —— 算法和边界都在 <see cref="PagePace"/> 里，
        /// 这里只放算好的结果，卡片拿到什么画什么。
        /// </summary>
        public int RemainingPages;
        public int RemainingMinutes;

        /// <summary>这份课件的总记录数；历史入口上的 N 用它，而不是截断后的 History.Count。</summary>
        public int HistoryTotal;

        /// <summary>最近几条记录，新的在前。最多 6 条，卡片面板不做滚动。</summary>
        public List<CardHistoryItem> History = new List<CardHistoryItem>();

        /// <summary>
        /// 台账里所有打开过的课件（归并后），卡片右下角文件夹面板用。和 History 一样是
        /// 引擎线程算好的快照，卡片只读；最多 8 份，新的在前。
        /// </summary>
        public List<CardFileItem> Files = new List<CardFileItem>();

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

        /// <summary>
        /// 功能7（2026-10-24）：课前预备态。true 表示现在没有任何课件，但这一科上次那份没讲完，
        /// 卡片要换成「文件名 + 上次停在第 N 页 + 还有 M 页没讲 + 打开并接着讲」。
        /// 卡片只读这个标志和下面三个快照字段，**不要**去读台账 —— 台账只在引擎/服务线程上碰。
        /// </summary>
        public bool ReadyOnly;
        /// <summary>预备态要打开的那份课件的真实路径（卡片不解析路径，只看这个）。</summary>
        public string ReadyFile = "";
        /// <summary>上次停下、这次要接着讲的那一页。</summary>
        public int ReadySlide;
        /// <summary>还有多少页没讲（SlideCount - EndSlide）。0 = 页数未知，卡片不写这一行。</summary>
        public int ReadyRemaining;

        /// <summary>Key for change detection: identical states are not re-published.</summary>
        public int Hash()
        {
            int h = Showing
                ? 100000 + Current * 7 + SlideCount
                : 200000 + Previous * 7 + Current + (DeckOpen ? 500000 : 0)
                  + (SuiteOnly ? 1000000 : 0) + SuiteName.Length * 10000;
            // 台账只追加、旧记录不会被改，所以“总数 + 最新一条的页码”足以代表整份历史；
            // 新记录即使停在同一页也会让总数 +1，卡片照样会重画。
            h = h * 31 + HistoryTotal;
            // 「还剩几页 · 约几分钟」也是卡片上的字：换一页、或者一节课结束让每页耗时变了，
            // 这两个数都会变，哈希不带上它们卡片就会停在上一秒的估计上。
            h = h * 31 + RemainingPages * 7 + RemainingMinutes;
            if (History != null && History.Count > 0) h = h * 31 + History[0].Slide;
            // 预备态是三种卡片形态之一（2026-10-24 功能7），它和正常态/课表面板的排版完全不同：
            // 只换 Hint 不改这几项的话，卡片收到新状态却不重画，会继续画上一个形态。
            if (ReadyOnly)
                h = h * 31 + 700000 + ReadySlide * 7 + ReadyRemaining * 13 + (ReadyFile ?? "").GetHashCode();
            // 文件面板的内容变化也要让卡片重画：新开一份课件、旧记录被清掉、当前文件换人，
            // 任何一项变了哈希就得变，否则面板会停在上一秒的列表上。
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
                    // 科目也会画在面板上（分组标题、筛选条）。课表换课导致科目变了，卡片也得重画。
                    h = h * 31 + (f.Subject ?? "").GetHashCode();
                    // 功能9：同名候选也会画在卡片上（「哪一份？」面板的行）。
                    // 候选换了一份、或多了一份、或某一份停到了别的页，哈希必须跟着变，
                    // 否则面板会停在上一秒的候选上，老师点下去跳的是旧路径。
                    // FolderHint 不单独算：它就是 FilePath 的那两级目录，路径变了它自然变。
                    if (f.SameName != null && f.SameName.Count > 0)
                    {
                        h = h * 31 + 4000000 + f.SameName.Count;
                        for (int j = 0; j < f.SameName.Count; j++)
                        {
                            var c = f.SameName[j];
                            if (c == null) { h = h * 31 + 23; continue; }
                            h = h * 31 + (c.FilePath ?? "").GetHashCode();
                            h = h * 31 + c.Slide;
                            h = h * 31 + (c.Detail ?? "").GetHashCode();
                        }
                    }
                    if (f.History != null && f.History.Count > 0) h = h * 31 + f.History[0].Slide;
                }
            }
            return h;
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
        // 发布锁：Push 把 BuildState 的结果发给卡片，而 Push 可能同时来自 worker（250ms 循环）
        // 和 UI 线程（点跳转 -> SetStatus -> Push）。没有它 lastPushHash 会被两边踩，
        // 出现「该刷的没刷」，displayed 也可能被另一线程改成半截状态。
        readonly object pushGate = new object();

        Thread thread;
        volatile bool stop;
        PptBridge bridge;
        readonly SeewoBridge seewo;
        PptSnapshot last;
        DateTime lastAttachTry = DateTime.MinValue;
        DateTime lastVerify = DateTime.MinValue;
        int lastPushHash = -1;
        DateTime lastSnapAt = DateTime.MinValue;   // COM 取数节流（2026-10-17）
        bool lastOnScreen;                         // 上一轮"演示软件窗口在屏幕上"
        string status = "";

        // session state
        string filePath = "";
        string lastFilePath = "";
        bool showing;
        bool deckOpen;    // 演示软件里此刻真的开着课件（不是台账里的历史文件名）
        bool usingSeewo;  // 当前这堂会话是从希沃 MCP 起的（不是 COM）
        string lastSeewoTitle;   // 希沃停止放映后仍保留课件名，用来收尾/显示
        string lastSeewoDiag = "";
        public string LastSeewoError { get; private set; } = "";
        int highest;      // 这节课翻到过的最远一页，只用于日志
        int lastSlide;    // 老师最后停下的那一页 —— 台账记的是这个
        int startSlide;
        int slideCount;

        /// <summary>
        /// 「每页耗时」的快照，给 CardState.Remaining* 用（2026-10-24 功能1）。
        /// 台账只在引擎线程读：Push 也会从 UI 线程（SetStatus）和服务线程（PushCheapState）过来，
        /// 让它们在 BuildState 里现翻台账，就会和引擎线程上正在追加的 Ledger.Add 撞车。
        /// 所以由引擎线程算好一个不可变快照放这里，其它线程只读快照。
        /// </summary>
        volatile PaceSnapshot pace = PaceSnapshot.Empty;

        /// <summary>
        /// 每页停留的落盘层（2026-10-25 功能1）。文件在插件配置目录里，和 ledger.json 同级
        /// （data\Config\Plugins\ClassSlideSync.Plugin\dwell.json）；读盘/落盘失败都只在内部
        /// 记 lastError、绝不抛，最坏情况只是少一份"每页停多久"的统计。
        /// </summary>
        readonly PageDwell dwell;

        /// <summary>
        /// 本场放映的每页停留累加器（2026-10-25 功能1）：BeginSession 起、Advance 里随翻页结算、
        /// EndSession 结算最后一页并 flush。只有引擎线程碰它 —— 别的线程读的是下面那个快照。
        /// </summary>
        PageDwellTracker? tracker;

        /// <summary>
        /// 「本次课实测速度」的快照（2026-10-25 功能2）：已翻页数 ÷ 已用时长。和 <see cref="pace"/>
        /// 一样是不可变快照 —— Push 会从 UI 线程（SetStatus）和服务线程（PushCheapState）来，
        /// 让它们现场去读 tracker（普通可变对象，引擎线程正在改）会读到半截状态。
        /// Sample &lt; 0 表示样本不足，回退链在 <see cref="PagePace.SecondsPerPage(IList{SessionRecord}, IList{SessionRecord}, int, double)"/>。
        /// </summary>
        volatile SessionSample sessionSample = SessionSample.Empty;

        DateTime startAt;
        SessionRecord current;
        string currentFp;
        DateTime? pendingEnd;
        int pendingEndSlide;
        int jumpPending;   // 1 = 卡片上有人点了跳页，等工作线程去执行
        int jumpTarget;    // >0 = 这次跳页的显式目标页（点历史行）；0 = 用台账里的结束页
        string? jumpFile;   // 非空 = 这次跳页的显式文件（跨文件历史行）；null = 沿用卡片展示的那份

        // 功能8（2026-10-24）：卡片上点了「打开并接着讲」之后，等这份课件真的被演示软件认出来
        // （Bridge 第一次给出 FilePath）再跳页。只认同一路径 —— 老师中途自己打开了别的课件，
        // 这次跳页请求就该作废，不能把别的课件的放映跳走。
        string? openJumpFile;
        int openJumpSlide;
        DateTime openJumpUntil = DateTime.MinValue;

        /// <summary>
        /// 课前预备态的快照（见 <see cref="CardState.ReadyOnly"/>）。由服务线程每 2 秒算一次整份
        /// 换进来，引擎线程只读 —— 和 <see cref="pace"/> 同一个模式：台账在服务线程读，
        /// 卡片和引擎不碰台账。整份替换保证读到的永远是自洽的一组值。
        /// </summary>
        volatile ReadySnapshot ready = ReadySnapshot.None;

        // 「卡片此刻展示的那条记录」：BuildState 写、DoJump 读。
        // 跳页必须落在老师点的那一刻看到的那一页上，而不能在执行时重新查一遍
        // （2026-10-18 日志：卡片上是希沃第 8 页，点下去跳了 WPS 第 6 页）。
        SessionRecord displayed;
        // 刚结束的那节课：多套件并存时用它压过另一台引擎留下的旧会话。
        SessionRecord recentEnded;
        DateTime recentEndedAt = DateTime.MinValue;
        // 刚讲完的两分钟内，卡片优先展示那一节课，不让别的套件的旧记录抢走（见 PickRecord）。
        const int RecentSessionSeconds = 120;

        public SessionEngine(Ledger ledger, SyncSettings settings)
        {
            this.ledger = ledger;
            this.settings = settings;
            this.seewo = new SeewoBridge(ledger);
            // 2026-10-25 功能1：每页停留单独一个文件，构造时把已有数据读进来（坏文件在内部改名留档）。
            // 用 ledger 的目录和日志出口，dwell.json 永远躺在 ledger.json 旁边（官方文档要求
            // 插件数据放 PluginConfigFolder，不要放插件安装目录）。
            this.dwell = PageDwell.Load(ledger.Root, ledger.Log);
        }

        /// <summary>Raised whenever the card's contents change. Fires off the UI thread.</summary>
        public event Action<CardState> StateChanged;

        /// <summary>Raised once the worker thread is up, for startup logging.</summary>
        public event Action Started;

        /// <summary>
        /// Supplies the current subject / lesson name for the session being recorded. Set by
        /// the plugin from ClassIsland's lessons service; null is fine.
        ///
        /// 2026-10-24 修 bug：原来这个字段是 <c>Func&lt;string[]&gt;</c>，而 SlideSyncService 里
        /// 声明它的那个字段**从来没被赋值过**，于是 BeginSession 里那段永远不执行，台账里所有
        /// Subject / LessonName 都是空串（老师真实台账 7 个桶 39 条记录无一例外），卡片课件库
        /// 的科目分组也就永远只剩「未标注科目」一类。现在换成一次给出全部课表读数的
        /// <see cref="LessonTag"/>，第几节、周几也跟着一起记进台账。
        /// </summary>
        public Func<LessonTag> LessonContext { get; set; }

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

        /// <summary>
        /// 每页停留的内存视图（2026-10-25 功能1）。只读快照都走 <see cref="PageDwell.Decks"/> /
        /// <see cref="PageDwell.Subjects"/>（返回深拷贝，随便改不影响下一次落盘），
        /// 以后面板/卡片要显示「这一页平均停多久」直接从这里拿，不要自己解析 dwell.json。
        /// </summary>
        public PageDwell Dwell { get { return dwell; } }
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

        /// <summary>最近一次"慢 COM 调用"的时间：之后 6 秒内不再发 COM（见 Step 里的熔断）。</summary>
        DateTime lastSlowCom = DateTime.MinValue;

        /// <summary>
        /// 引擎卡在 COM 上时的兜底：不碰 COM，只用内存里已有的状态推一次，让卡片至少显示
        /// 「正在识别课件…」而不是整块屏幕十几秒没反应（2026-10-17 用户报的「关掉 PPT 再打开，
        /// 插件要好几十秒才响应」）。服务端每 2 秒看心跳兜一次。
        /// </summary>
        public void PushCheapState()
        {
            try { Push(showing, lastSlide, slideCount); } catch { }
        }

        /// <summary>
        /// 免费的兜底线索：从可见演示窗口的标题里读出文档名，去台账里翻出上次的记录，用它当"当前课件"。
        /// 2026-10-17 用户实测「卡片一直显示正在识别课件，过了十秒才识别出来」——那十秒全卡在 COM 上，
        /// 而窗口标题（GetWindowTextW）不需要 COM。COM 之后回话时会用自己的路径覆盖，更准。
        /// </summary>
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

        /// <summary>记一次 COM 调用的耗时；超过 4000ms（对象模型被占住的典型表现）就熔断 6 秒。</summary>
        void NoteComCost(string what, long ms)
        {
            if (ms < 4000) return;
            lastSlowCom = DateTime.UtcNow;
            try { ledger.Log("slow COM: " + what + " took " + ms + " ms -- 暂停发 COM 6 秒"); } catch { }
        }

        public void Start()
        {
            if (thread != null) return;
            // 平台护栏（要求 4）：这个引擎线程干的全是 COM（PptBridge）和 Win32 窗口轮询。
            // 非 Windows 上不起线程 —— Step() 里的 PptBridge.Connect 因此一次都不会被调用。
            if (!PlatformGuard.IsWindows) { PlatformGuard.Announce("session-engine:start", ledger.Log); return; }
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
                // 2026-10-17：节拍固定 250ms。用户反馈"打开课件后显示页码、点跳页都要等好久"——
                // 之前把整个循环睡到 400/800/1500ms，反应自然慢。现在改成"跑得勤、但 COM 取数限速"
                // （见 Step 里的 lastSnapAt），打开课件和点跳页都能在 250ms 内被接住。
                // 第 5 条（关 WPS 卡死）要省的 COM 调用由限速负责，不再靠睡大觉。
                Thread.Sleep(250);
            }
            ledger.Log("worker stopped");
        }

        void Step()
        {
            // 平台护栏（要求 4 的「SessionEngine 的桥创建」）：Step 的每一步都在碰 COM 和
            // 桌面窗口。非 Windows 上整个跳过 —— 桥永远不会被创建，PptBridge 一次都不会被调用。
            // 正常路径上 Start() 已经拦住了线程，这里是双保险（就算有人直接调 Step 也不炸）。
            if (!PlatformGuard.IsWindows) { PlatformGuard.Announce("session-engine:step", ledger.Log); return; }
            // 跳页请求必须最先处理。2026-10-13 实测的坑：老师点“从第 N 页开始放映”时 PowerPoint
            // 往往根本没开，而下面 bridge == null 的分支会直接 return，请求就永远轮不到执行——
            // 卡片停在“正在跳转…”，什么也不发生。DoJump 里会按需把 PowerPoint 拉起来。
            if (Interlocked.Exchange(ref jumpPending, 0) != 0)
            {
                try { DoJump(); }
                catch (Exception ex) { SetStatus("跳页出错：" + ex.Message); }
            }

            // 2026-10-17 用户实测：关掉课件再重新打开时，插件要"好几十秒"才响应 —— WPS 打开文件
            // 的那几十秒里，对象模型会接受调用但不回话，任何一次 COM 调用都会把这条引擎线程钉死
            // （连心跳都刷不了，只能等看门狗 30 秒后重启引擎）。三点兜住：
            //   1) 刚发生过慢调用（>4000ms）就熔断 6 秒，期间一次 COM 都不发；
            //   2) 窗口出现/消失是便宜的窗口检查、不碰 COM，仍然放行去取数；
            //   3) 服务端每 2 秒用 PushCheapState() 兜一次，卡片不跟着僵住。
            bool comBlocked = (DateTime.UtcNow - lastSlowCom).TotalSeconds < 6;

            if (bridge == null)
            {
                // 希沃白板没有 COM 入口，只能走它自带的 MCP HTTP 服务。先在这里试一次：
                // 没装/没开时 GetState 会很快返回 false（探测结果有 30 秒 TTL，不会每 250ms 打网络）。
                if (comBlocked)
                {
                    if (TrySeewo()) return;
                    Push(showing, lastSlide, slideCount);
                    return;
                }

                // 1.2 秒试一次：WPS 的 COM 实例要等窗口起来几秒后才登记进 ROT，重试慢的话
                // 老师刚打开课件那几秒卡片只会显示"正在识别课件…"（2026-10-15 实测大约 4 秒）。
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
                    // WPS / PowerPoint 都没接上：希沃在放映就交给希沃（台账键是「希沃:课件名」）。
                    if (TrySeewo()) return;
                    if (showing) EndSession(0);
                    // 2026-10-17：没有 COM 也先把卡片点亮（窗口标题里的文档名足够翻出"上次讲到第几页"）。
                    ApplyTitleFallback();
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
            if (!showing && !comBlocked && (DateTime.UtcNow - lastVerify).TotalSeconds >= 5)
            {
                lastVerify = DateTime.UtcNow;
                try
                {
                    var swVerify = System.Diagnostics.Stopwatch.StartNew();
                    int mine = bridge.DeckScore;   // 3=有真课件 2=只有没存过的空白稿 1=什么都没有 0=死的
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

            // 2026-10-17：省 COM 但不牺牲反应速度。便宜的窗口检查每 250ms 做一次（外加系统调度器
            // 每 2 秒喂进来的 SuiteRunning）；真正的 COM 取数按状态限速：放映中 350ms（页码跟手）、
            // 演示软件开着 700ms、什么都没开 2000ms。窗口"刚出现/刚消失"时立刻放行，不等限速。
            bool onScreen;
            try { onScreen = PptBridge.SuiteOnScreen(); } catch { onScreen = false; }
            int snapInterval = showing ? 350 : ((SuiteRunning || onScreen) ? 700 : 2000);
            bool windowChanged = onScreen != lastOnScreen;
            lastOnScreen = onScreen;
            if (!windowChanged && (DateTime.UtcNow - lastSnapAt).TotalMilliseconds < snapInterval) return;
            lastSnapAt = DateTime.UtcNow;

            // 熔断期间一次 COM 都不发：只把内存里的状态推给卡片，别让老师对着一个僵住的屏幕。
            if (comBlocked)
            {
                // 但希沃走的是 HTTP，不受 COM 熔断影响；它还在放映就继续由它给状态。
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

            // A suite closed mid-lesson leaves us holding a dead pointer. The snapshot reports
            // that rather than throwing, so treat *any* failed snapshot as a disconnect and
            // reattach later -- the old code only reacted to one exception type and sat on a dead
            // pointer for the rest of the lesson when the message happened to differ.
            if (snap.Diag != null && snap.Diag.StartsWith("snapshot failed:", StringComparison.Ordinal))
            {
                ledger.Log("snapshot failed (" + snap.Diag + ") -- dropping the COM reference");
                DropBridge();
                // COM 死了不代表没在上课：希沃可能正在另一个进程里放映。
                if (TrySeewo()) return;
                return;
            }

            // COM 桥回到"正在放映"时，如果上一段是希沃记的，先把希沃那段收尾，
            // 不然会有两条同时开着（台账里两个键，卡片也会串）。
            if (snap.Showing && usingSeewo)
            {
                EndSession(lastSlide);
                pendingEnd = null;
                usingSeewo = false;
            }

            // COM 拿不到"正在放映"时，再看希沃一眼。只在两种情况下查：
            //   - COM 手里根本没有可用课件（空白稿/没打开），希沃是唯一的线索；
            //   - 希沃进程确实在跑（便宜的进程检查，有 3 秒缓存），避免每次轮询都打一次 MCP。
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
            // COM 还没给出可用路径（WPS 的空白稿 FullName 是"演示文稿1"、或者干脆没回话）时，
            // 用窗口标题里的文档名兜一下，省得卡片一直停在"正在识别课件…"（2026-10-17 用户实测）。
            if (!PptBridge.LooksLikePath(filePath)) ApplyTitleFallback();
            if (snap.SlideCount > 0) slideCount = snap.SlideCount;

            // "演示软件里真的开着课件"和"台账里有历史文件名"是两件事，卡片显示与否只看前者。
            // 判据是**像路径**、不只是非空：WPS 演示刚启动时自带一份没存过的空白稿，FullName 是
            // "演示文稿1"，那种"课件"没地方跳页、也不该顶掉"等待打开课件"的提示（用户 2026-10-15）。
            // 2026-10-17：改用 filePath（上面可能已经被窗口标题兜底填过），WPS 窗口一出现就点亮卡片。
            deckOpen = PptBridge.LooksLikePath(filePath);

            // 功能8：如果卡片上刚点过「打开并接着讲」，课件此刻刚被认出来，就在这里兑现跳页。
            TryJumpPendingOpen();

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
                    else
                    {
                        // 放映窗口已经没了，但编辑视图还停在刚才那一页 —— 用它兜住"最后一面"：
                        // 老师连按两下翻页直接退出放映时，400ms 的轮询可能一次都没看到最后一页。
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
            // 2026-10-16 用户第 6 条：老师在最后一面结束课件时，WPS 可能直接把文档关掉、指针当场失效。
            // 先前的分支直接 return，这堂课就没收尾（台账里停在倒数第二页，下一节课看着像"没记录"）。
            // 丢指针之前先把会话记完。
            if (showing && current != null)
            {
                try { EndSession(lastSlide); } catch { }
            }
            var b = bridge;
            bridge = null;
            if (b != null) { try { b.Dispose(); } catch { } }
        }

        // ------------------------------------------------------------------ seewo

        /// <summary>
        /// 希沃白板没有 COM，只有进程内的 MCP HTTP 服务。这里把它的状态翻译成和 COM 一样的
        /// PptSnapshot，后面 BeginSession / Advance / EndSession / Push 全部复用，卡片和台账
        /// 不需要知道数据是 COM 来的还是 HTTP 来的。
        /// 返回 true 表示“这一轮已经由希沃给出了结论”，调用方不要再走 COM 的收尾逻辑。
        /// </summary>
        bool TrySeewo()
        {
            try
            {
                var s = seewo.GetState();
                if (s == null) return false;

                // 服务不可用（进程没开、端口没探到、请求超时）。如果当前这段本来就是希沃记的：
                //   - 进程真的没了 -> 当作停止放映，走 2 秒宽限期收尾；
                //   - 进程还在（MCP 临时抽风）-> 保持现状，别让 COM 分支把会话误收掉。
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
                    // 希沃这边真的没有打开的课件（shells=[]）。绝不能退回 lastSeewoTitle：
                    // 老师关掉课件后，卡片会一直挂着上一份课件的名字和页码。
                    // 但只有当前卡片本来就是希沃的课件（或者会话是希沃起的）才清；
                    // 否则 COM 那边还开着一份编辑中的课件，不能被希沃的空白状态抢掉。
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
                        // COM 那边正在放另一个课件，而希沃也开始放映了：希沃是老师此刻看的屏幕，
                        // 把 COM 那段先收掉，避免两条会话并行。
                        EndSession(lastSlide);
                        pendingEnd = null;
                    }
                    usingSeewo = true;
                    ProcessSeewoSnapshot(NewSeewoSnapshot(true, title, s.Current, s.SlideCount, s.Diag));
                    return true;
                }

                // 没在放映。纯希沃环境（没有 COM 桥）里，课件开着也应该让卡片显示
                // 课件名、总页数和上次停在哪一页——和 PPT/WPS 的编辑态一致。
                // COM 桥还在时不抢：那可能是 PowerPoint 编辑中的另一份课件。
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
            // 诊断行只记变化，别每 250ms 往日志里灌一遍。
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
                // 希沃把课件关了：路径必须清掉，否则卡片会一直挂着上一份课件的名字。
                filePath = "";
                lastFilePath = "";
            }
            if (snap.SlideCount > 0) slideCount = snap.SlideCount;
            // 和 COM 分支的 deckOpen=LooksLikePath(filePath) 对齐：只要识到课件名，
            // 就算这份课件"开着"（编辑态也要让卡片显示课件名和上次页码），而不是只有
            // 放映时才算。否则编辑态会掉进 SuiteOnly 分支，标题变成"希沃白板"。
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
                    // 和 COM 分支一样给 2 秒宽限：希沃从放映退回编辑时，MCP 可能短暂报“没在放映”，
                    // 立刻收尾会把最后翻的那一页丢掉。
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

        // ------------------------------------------------------------------ sessions

        void BeginSession(PptSnapshot snap)
        {
            showing = true;
            startAt = DateTime.Now;   // 本地时间：老师看台账时要对得上墙上钟，不能写 UTC
            startSlide = snap.Current > 0 ? snap.Current : 1;
            highest = startSlide;
            lastSlide = startSlide;

            // Fingerprinting reads up to 128 KB; once per lesson, off the hot path.
            currentFp = Ledger.Fingerprint(filePath);

            // 任务 6：课件一旦被编辑，旧版的每页文字就再也读不出来了，页码对齐只能靠历史记录猜。
            // 所以每次开课都把这一版的分页文字留个底（按指纹存，见 DeckRemap.deckpages.json）。
            if (currentFp.Length > 0)
            {
                try { DeckRemap.Remember(ledger.Root, filePath); }
                catch (Exception ex) { ledger.Log("decksnapshot failed: " + ex.Message); }
            }

            // 2026-10-24 第二轮 F3：课件改名/挪目录后键变了，但内容没变（指纹相同）。以前这里
            // 直接另起一条，同一份课件在台账里出现两条"各自为战"的记录，卡片也不再提示
            // "上次停在第 5 页"。当前键在台账里查不到时，先用内容指纹认领旧桶，把历史迁过来。
            // 注意：Fingerprint 变了（课件被真的编辑过）就不认领，页码本来也不该继承。
            if (currentFp.Length > 0 && ledger.Last(filePath) == null)
            {
                var old = ledger.FindByFingerprint(filePath);
                if (old != null && !string.IsNullOrEmpty(old.FilePath) && !SameBucket(old.FilePath, filePath))
                {
                    try
                    {
                        string oldPath = old.FilePath;
                        int oldEnd = old.EndSlide;
                        if (ledger.Adopt(oldPath, filePath))
                        {
                            ledger.Save();
                            ledger.Log("renamed deck adopted: " + FileLabel(oldPath) + " -> " + FileLabel(filePath)
                                + " (was at page " + oldEnd + ")");
                        }
                    }
                    catch (Exception ex) { ledger.Log("adopt failed: " + ex.Message); }
                }
            }

            current = new SessionRecord
            {
                FilePath = filePath,
                StartedAt = startAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                StartSlide = startSlide,
                EndSlide = startSlide,
                SlideCount = snap.SlideCount
            };

            var ctx = LessonContext;
            if (ctx != null)
            {
                try
                {
                    var tag = ctx();
                    if (tag != null)
                    {
                        // 空串也照抄：老记录里 Subject 是 ""，新记录里"课间/没课表"也是 ""，
                        // 两者在台账里等价，渲染时都退回日期格式。
                        current.Subject = tag.Subject;
                        current.LessonName = tag.LessonName;
                        // 2026-10-24 功能3：周几 + 第几节，历史行靠它们定位到某一节课。
                        current.Weekday = tag.Weekday;
                        current.Period = tag.Period;
                        current.PeriodName = tag.PeriodName;
                    }
                }
                catch { }
            }

            // 指纹在方法开头就算好了（认领改名旧记录要用），这里不再重复读文件。
            // 2026-10-25 功能1/2：本场放映的每页停留从这里起算（起点就是台账的 startAt，
            // 两份数据不用两套时钟）；实测速度的样本也一并清零，免得上一场的翻页数漏进来。
            // subject 用刚记下的 current.Subject —— dwell.json 的科目聚合就取这个值。
            if (tracker == null) tracker = new PageDwellTracker();
            tracker.Begin(current.FilePath, current.Subject, startSlide, startAt);
            sessionSample = SessionSample.Empty;

            // 任务 10：课件被移动/改名/另存成新版后指纹变了，按指纹认领不上旧记录。
            // 退一步按「内容像 + 名字像」去本课科目文件夹和这份课件的原目录里找，
            // 找得到就把整桶历史迁到新路径（找不到就什么都不动，卡片照常显示"还没记录"）。
            if (ledger.Last(filePath) == null)
            {
                try
                {
                    var moved = DeckRemap.Relink(ledger, filePath,
                        new[] { settings.FindSubjectFolder(current.Subject) });
                    if (moved.Found && !SameBucket(moved.Path, filePath))
                    {
                        ledger.Log("relinked deck: " + FileLabel(filePath) + " <- " + FileLabel(moved.Path));
                    }
                }
                catch (Exception ex) { ledger.Log("relink failed: " + ex.Message); }
            }

            ledger.Log("show started: file=" + filePath + " start=" + startSlide + "/" + snap.SlideCount
                + (string.IsNullOrEmpty(current.Subject) ? "" : " subject=" + current.Subject)
                + (current.Period > 0 ? " period=" + current.Period : ""));
        }

        void Advance(PptSnapshot snap)
        {
            // 一拍之内换了课件：WPS 里关掉 A 直接双击 B，Step 仍然走 Advance。若不在这里分界，
            // B 的页码会被写进 A 的记录 —— A 变成 B 的页，B 一条都没有（同一份也就算了，
            // 两份不同的课件这么串，第二天谁的“上次停在第几页”都是错的）。
            if (current != null
                && !string.IsNullOrEmpty(snap.FilePath)
                && PptBridge.LooksLikePath(snap.FilePath)
                && !string.IsNullOrEmpty(current.FilePath)
                && !SameBucket(snap.FilePath, current.FilePath))
            {
                EndSession(lastSlide);   // 先把上一份收尾（EndSlide 取 max，见下）
                BeginSession(snap);
                return;
            }

            if (snap.Current > highest) highest = snap.Current;
            // 记“翻到过的最远页”，不是“关掉时停在哪一页”：老师上完课常把课件翻回第 1 页再关，
            // 那一页不是下次要接着讲的地方。EndSlide 的语义就是这份课件讲到哪儿了。
            if (snap.Current > 0) { lastSlide = snap.Current; current.EndSlide = highest; }

            // 2026-10-25 功能1/2：同一拍里取一次时钟，页面停留和会话时长用同一个 now，
            // 免得两份数据差出几毫秒（测试里更难对齐）。
            var now = DateTime.Now;
            // 功能1：页面真的换了才结算上一页。同一页的重复快照（每 250ms 一次）不算翻页、
            // 也不重置计时 —— 否则老师停在一页上看十分钟会被记成 4 秒一次 × 150 次。
            if (tracker != null && snap.Current > 0) tracker.Enter(snap.Current, now);
            // 功能2：每翻一页刷新一次实测速度，卡片上的「约 M 分钟」下一拍就跟着变。
            UpdateSessionSample(now);

            if (snap.SlideCount > 0)
            {
                current.SlideCount = snap.SlideCount;
                slideCount = snap.SlideCount;
            }
            // Keep the on-disk copy honest even if the plugin is killed mid-lesson.
            current.EndedAt = now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            current.Seconds = (int)(now - startAt).TotalSeconds;
            // 指纹也要一起写：这条中途落盘的记录很可能就是这堂课的最后一条（课件关得太快、
            // 或者插件被强杀），少了指纹下次会判成"文件在那之后被改过"。
            current.Fingerprint = currentFp;
            if (settings.RecordLedger && current.Seconds >= settings.MinSessionSeconds) Commit(current);
        }

        void EndSession(int endSlide)
        {
            showing = false;
            if (current == null) return;
            var now = DateTime.Now;
            if (endSlide <= 0) endSlide = lastSlide;
            if (endSlide <= 0) endSlide = current.StartSlide;   // 都拿不到时至少别写成 0（用户第 6 条）
            // EndSlide 一律取“翻到过的最远页”：关课件前翻回第 1 页不该把这堂课讲到哪儿抹掉。
            int end = Math.Max(highest, endSlide);
            current.EndSlide = end > 0 ? end : 1;
            current.EndedAt = now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            current.Seconds = (int)(now - startAt).TotalSeconds;
            current.Fingerprint = currentFp;
            if (current.Seconds >= settings.MinSessionSeconds)
            {
                if (settings.RecordLedger)
                {
                    Commit(current);
                    // 2026-10-25 功能1：这一场的「每页停留」跟着台账这条记录一起落盘。
                    // 挂在 RecordLedger/最短时长这两个条件里面不是偷懒：台账都不记的会话
                    // （打开一下就关、不到 MinSessionSeconds）写进去，只会把「这一页平均停
                    // 多久」拉成一堆 3 秒的噪音，和台账"太短不算一节课"的规矩也自相矛盾。
                    FlushDwell(current, now);
                }
                ledger.Log("session recorded: " + FileLabel(current.FilePath) +
                    " pages " + current.StartSlide + "->" + current.EndSlide +
                    " (max " + highest + ")" +
                    " for " + current.Seconds + "s");
            }
            else ledger.Log("session ignored (only " + current.Seconds + "s)");

            // 2026-10-25 功能1/2：会话到这里就结束了。累加器收掉，实测速度的样本也清空 ——
            // 那个数字只代表"这一场"，下一场从零攒起；卡片下一拍回退历史均值。
            tracker = null;
            sessionSample = SessionSample.Empty;

            // 记住“刚刚讲完的是哪一节”。希沃退出授课后课件常常立刻被关掉，而 WPS 那边
            // 可能还开着一份上次留下的旧课件；没有这条信息，卡片下一拍就会换成那份的记录，
            // 老师刚在希沃操作完、回头点卡片就跳到了别的课件上（2026-10-18 实测）。
            recentEnded = current;
            recentEndedAt = DateTime.Now;
            current = null;
        }

        void Commit(SessionRecord r)
        {
            var same = ledger.Last(r.FilePath);
            // 2026-10-18 用户实测：课件只是被打开了一下（还没进全屏放映），
            // 也会走一次"开始/结束"，于是一条「第 1 页、几秒钟」的记录把上一堂课
            // 「停在第 9 页」的历史顶掉了。
            // 判据不能挂时长：希沃在编辑态里开着课件一放就是几分钟（日志里那次 81 秒），
            // 照样把 9 盖成 2。所以只要一页都没翻就不写；只有该文件还没有任何记录时
            // （第一次见面）才记，因为那一下确实是有意义的信息。
            // 2026-10-24 第二轮 F4：这条判定必须排在"同一 StartedAt 就地更新"之前。
            // StartedAt 只有秒精度，上一堂课刚结束、一秒内又打开一次时，两条不同会话的
            // StartedAt 会相同，旧顺序会把「上次停在第 5 页」就地改写成「第 3 页」且条数不变。
            bool noise = r.EndSlide == r.StartSlide && same != null;
            if (noise)
            {
                ledger.Log("session ignored (no page advance, " + r.Seconds + "s): " + FileLabel(r.FilePath));
                return;
            }

            // 就地更新只对"同一场放映自己"成立：Advance/EndSession 每隔几拍就把同一个
            // current 对象再 Commit 一次，而第一次提交 Add 进台账的就是**这个对象**，
            // 所以 last 取回来必定是同一个引用。
            // 光看 StartedAt（只有秒精度）不够：上一堂课刚结束、一秒内又打开一次时，
            // 两场不同会话的 StartedAt 完全相同，旧写法会把上一堂课那条记录就地改写 ——
            // 条数不变（老师以为这节课只讲了一次）、页码变成后一场的（"上次停在第 9 页"
            // 变成"第 3 页"）、而它自己的记录就此消失。改成引用相等后，撞在同一秒的两场
            // 会话各自留一条，先后顺序由 Ledger.Recent 用下标兜住。
            if (same != null && ReferenceEquals(same, r))
            {
                same.EndSlide = r.EndSlide;
                same.EndedAt = r.EndedAt;
                same.Seconds = r.Seconds;
                same.SlideCount = r.SlideCount;
                same.Fingerprint = r.Fingerprint;
            }
            else
            {
                ledger.Add(r);
            }
            ledger.Save();
        }

        /// <summary>
        /// 把这一场攒下的「每页停留」并进 dwell.json 并落盘（2026-10-25 功能1）。
        ///
        /// 最后一页的结算也在这里做（tracker.Settle）：老师停在第 12 页讲了 10 分钟然后关掉
        /// 课件，EndSession 拿到的 endSlide 只是页码，停留时长只有累加器自己知道。
        /// 整段包 try/catch：每页停留是锦上添花的数据，写坏了也只是少一份统计，
        /// 绝不能影响台账落盘、更不能把引擎循环带崩。
        /// </summary>
        void FlushDwell(SessionRecord r, DateTime now)
        {
            var t = tracker;
            if (t == null || r == null) return;
            try
            {
                t.Settle(now);
                var pages = t.Pages;
                if (pages.Count == 0) return;   // 一页都没停住（例如全程只给第 0 页的脏快照），不写空壳
                dwell.AddSession(r.FilePath, r.Subject, FileLabel(r.FilePath), pages, t.PageVisits, now);
                dwell.Save();
                if (dwell.LastError.Length > 0)
                    ledger.Log("dwell save failed: " + dwell.LastError);
                else
                    ledger.Log("dwell recorded: " + FileLabel(r.FilePath) + " " + pages.Count + " pages "
                        + t.TotalSeconds + "s");
            }
            catch (Exception ex)
            {
                ledger.Log("dwell flush failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// 刷新「本次课实测速度」快照（2026-10-25 功能2）：已翻页数 ÷ 已用时长。
        ///
        /// 分子用 tracker.Turns（页面真的变了几次），不用页号差：老师从第 2 页直接跳到第 30 页
        /// 只算翻了一次，用页号差会把每页耗时算成两秒，卡片立刻变成「约 1 分钟」。
        /// 样本不足时 Sample 是 -1，SecondsPerPage 会回退历史均值 —— 回退的判断（&lt;3 页或
        /// &lt;60 秒）收在 PagePace 里，这里只负责喂当前读数。
        /// </summary>
        void UpdateSessionSample(DateTime now)
        {
            double elapsed = (now - startAt).TotalSeconds;
            if (elapsed < 0) elapsed = 0;   // 系统校时把时钟往回拨：不能给出负时长
            int turns = tracker == null ? 0 : tracker.Turns;
            sessionSample = new SessionSample
            {
                Turns = turns,
                Seconds = elapsed,
                Sample = PagePace.SessionSecondsPerPage(turns, elapsed)
            };
        }

        // ------------------------------------------------------------------ actions

        /// <summary>
        /// Ask for a jump to where the last lesson stopped. Called from the card's UI thread, so it
        /// only raises a flag -- the COM work happens on the worker thread in <see cref="Step"/>.
        /// </summary>
        public void RequestJump()
        {
            // 普通跳页没有显式目标：先清掉上一次历史行留下的页码和文件，DoJump 才会用台账里的结束页。
            Interlocked.Exchange(ref jumpTarget, 0);
            Interlocked.Exchange(ref jumpFile, null);
            Interlocked.Exchange(ref jumpPending, 1);
            SetStatus("正在跳转…");
        }

        /// <summary>
        /// 跳到这份课件历史上某一页。和 <see cref="RequestJump"/> 一样只在 UI 线程抬标志，
        /// 真正的 COM / MCP 调用仍由工作线程在 <see cref="Step"/> 里执行。
        /// </summary>
        public void RequestJumpTo(int slide)
        {
            RequestJumpTo(null, slide);
        }

        /// <summary>
        /// 跳到「某份文件」的某一页（卡片文件夹面板里的跨文件历史）。
        /// 保留 <see cref="RequestJumpTo(int)"/> 的旧行为：filePath 为空 = 用卡片此刻展示的那份。
        /// </summary>
        public void RequestJumpTo(string filePath, int slide)
        {
            string file = CardFileList.Normalize(filePath);
            // 旧重载允许传 0（表示“用台账里的结束页”）；但跨文件跳页如果没有页码就没有意义，
            // 这里不能悄悄去跳那份文件的最后记录，否则等于替老师选了一页。
            if (slide <= 0 && file.Length > 0) return;
            Interlocked.Exchange(ref jumpFile, file);
            Interlocked.Exchange(ref jumpTarget, slide);
            Interlocked.Exchange(ref jumpPending, 1);
            SetStatus("正在跳转…");
        }

        /// <summary>预备态的一份不可变快照（见 <see cref="ready"/>）。</summary>
        sealed class ReadySnapshot
        {
            public static readonly ReadySnapshot None = new ReadySnapshot();
            public bool On;
            public string FilePath = "";
            public int Slide;
            public int Remaining;
        }

        /// <summary>
        /// 功能7（2026-10-24）：换入「这一科上次那份没讲完」的快照。由服务线程在每次课表 tick 里
        /// 调；传空（页数 &lt;= 0 或路径为空）= 离开预备窗口，卡片回到正常态。
        /// </summary>
        public void SetReady(string filePath, int slide, int remaining)
        {
            bool on = settings.ReadyCardEnabled && slide > 0 && !string.IsNullOrWhiteSpace(filePath);
            int rem = Math.Max(0, remaining);
            var cur = ready;
            if (cur.On == on
                && string.Equals(cur.FilePath, on ? filePath : "", StringComparison.OrdinalIgnoreCase)
                && cur.Slide == (on ? slide : 0)
                && cur.Remaining == (on ? rem : 0)) return;

            ready = on
                ? new ReadySnapshot { On = true, FilePath = filePath, Slide = slide, Remaining = rem }
                : ReadySnapshot.None;
            // 立刻推一次：预备态要在打铃前两分钟就出现，不能等到下一次翻页/下一次 tick 才刷新。
            Push(showing, last == null ? 0 : last.Current, slideCount);
        }

        public void ClearReady()
        {
            SetReady("", 0, 0);
        }

        /// <summary>
        /// 功能8（2026-10-24）：卡片上点了「打开并接着讲」。先记下"等这份课件出现就跳到第 N 页"，
        /// 真正的跳页交给工作线程：外部的进程刚被拉起来，这一刻 COM 桥可能还没连上。
        /// </summary>
        public void RequestJumpOnOpen(string filePath, int slide)
        {
            string file = CardFileList.Normalize(filePath);
            if (file.Length == 0 || slide <= 0) return;
            Interlocked.Exchange(ref openJumpFile, file);
            Interlocked.Exchange(ref openJumpSlide, slide);
            openJumpUntil = DateTime.UtcNow.AddSeconds(90);
            ledger.Log("open-request: " + file + " -> " + slide);
        }

        /// <summary>
        /// 每轮 <see cref="Step"/> 在拿到 Bridge 的 FilePath 之后调：同一份课件第一次被认出来时
        /// 把「打开并接着讲」兑现成一次跳页。用 SameBucket 比较而不是字符串相等：COM / 标题兜底
        /// 给出的路径大小写、短路径、相对路径都可能不同，但台账键是同一个。
        /// </summary>
        void TryJumpPendingOpen()
        {
            string want = Interlocked.CompareExchange(ref openJumpFile, null, null);
            if (want == null) return;
            if (DateTime.UtcNow > openJumpUntil)
            {
                // 90 秒都没等到这份课件（老师点了按钮又去干别的了）：请求作废，
                // 免得下一节课打开同一份课件时莫名其妙跳页。
                Interlocked.Exchange(ref openJumpFile, null);
                return;
            }
            string cur = filePath;
            if (cur.Length == 0 || !SameBucket(cur, want)) return;

            int slide = Interlocked.Exchange(ref openJumpSlide, 0);
            if (Interlocked.Exchange(ref openJumpFile, null) == null) return;   // 被别的线程先取走了
            openJumpUntil = DateTime.MinValue;
            if (slide <= 0) return;
            ledger.Log("open-request matched: " + cur + " -> " + slide);
            RequestJumpTo(cur, slide);
        }

        /// <summary>
        /// 卡片“正在讲的那份课件”用哪个键查台账。<see cref="BuildState"/> 和 <see cref="DoJump"/>
        /// 必须共用这一个：两边各算一遍就是多套件并存时错位的根源。
        /// </summary>
        string CurrentKey()
        {
            return filePath.Length > 0 ? filePath : lastFilePath;
        }

        static bool SameBucket(string a, string b)
        {
            string ka = Ledger.Key(a), kb = Ledger.Key(b);
            return ka.Length > 0 && ka == kb;
        }

        /// <summary>
        /// 卡片此刻该展示的台账记录 —— 同时也是跳页要用的那条记录。
        /// 显示和动作必须来自同一次计算：以前 BuildState 用 filePath 查一次、DoJump 执行时
        /// 又重查一次（还多一个 ledger.Newest() 兜底），于是出现“卡片显示希沃第 8 页，
        /// 点下去跳了 WPS 第 6 页”（2026-10-18 用户实测）。
        /// </summary>
        SessionRecord PickRecord(out string histKey, out bool recent)
        {
            recent = false;
            histKey = CurrentKey();
            string curKey = histKey;   // 眼前这份课件的键（改名后 histKey 可能会挪到旧桶上）
            var r = histKey.Length > 0 ? ledger.Last(histKey) : null;
            if (r == null && histKey.Length > 0)
            {
                // COM 给的 FullName 可能只是同一份课件的另一种写法（相对路径 / 另一份同名副本），
                // 精确查会扑空：卡片既没有“上次停在第几页”，也没有历史入口（实测台账里明明有
                // 7 条，卡片高度却一直停在“无历史”的尺寸，用户报“看不到历史入口”）。
                // 同名即同一份课件，比“什么都不显示”更接近事实，也不至于张冠李戴到别的课件。
                var byName = ledger.FindByName(FileLabel(histKey));
                if (byName != null) { r = byName; histKey = byName.FilePath; }
                else
                {
                    // 改名或换目录后文件名对不上，但文件内容没变：按指纹认回来，
                    // histKey 留在原来的桶里，历史记录照旧看得见。
                    var byFp = ledger.FindByFingerprint(curKey);
                    if (byFp != null) { r = byFp; histKey = byFp.FilePath; }
                }
            }
            // 记录指向的那份文件已经不在了（改名/移动），而眼前这份确实存在：把记录认到眼前的路径上。
            // 不做这一步，卡片会得出“文件在那之后被改过”的结论，点跳转还会去打开一个不存在的文件。
            r = Retarget(r, curKey);

            // 最近活动优先：两分钟内刚结束的那节课压过其他套件留下的旧会话。
            // 没有这一条，希沃一退出授课（课件随即关掉）、WPS 里还开着旧课件时，卡片会立刻
            // 换成 WPS 的记录 —— 老师眼里“这张卡刚才还是希沃的第 8 页”。
            // 但**正在放映**时不抢：老师已经在新课件上讲起来了，卡片必须跟着眼前这块屏幕，
            // 不能被两分钟前那节课的页码顶掉（那样最先看到的是别人的页）。
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

        /// <summary>
        /// 台账记录里的文件已经不在了（改名/移动），眼前这份却存在：返回一份指向眼前路径的副本。
        /// 原记录不动 —— 历史里那条仍然属于它自己的文件，只是“这次要跳的那份”换成了眼前的。
        /// </summary>
        static SessionRecord? Retarget(SessionRecord? r, string path)
        {
            if (r == null || string.IsNullOrEmpty(path)) return r;
            if (string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)) return r;
            // 只在“两边都是磁盘路径”时换路径：希沃的记录是伪键，绝不能认到眼前这份 WPS 课件上。
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
                Weekday = r.Weekday,
                Period = r.Period,
                PeriodName = r.PeriodName,
            };
        }

        void DoJump()
        {
            // 历史面板点的行不一定是“最后一次停下”的页，所以先把显式目标取出来。
            int explicitSlide = Interlocked.Exchange(ref jumpTarget, 0);
            // 文件夹面板里的历史可能是另一份课件的：文件也必须按点击那一刻带过来，
            // 不能等到工作线程执行时再按 CurrentKey() 查一遍（那时可能已经换了课件）。
            string explicitFile = CardFileList.Normalize(Interlocked.Exchange(ref jumpFile, null));
            // 优先用卡片上正在显示的那条记录：从点击到工作线程执行之间，换套件、关课件都可能
            // 让 filePath 变成另一份课件，重查一次就会跳到别的记录上（2026-10-18 实测：
            // 14:26:26 记录落到希沃的 1->8，14:26:29 希沃里课件关了，14:26:32 点跳转却跳了
            // WPS 的第 6 页）。查不到时才重新算一遍，也不再退到 ledger.Newest()（全库最新一条
            // 往往是另一台引擎的旧课件）。
            string histKey;
            bool recent;
            var r = displayed;
            if (r == null) r = PickRecord(out histKey, out recent);
            // 显式文件优先；没有就退回旧逻辑：用卡片展示的那条记录里的文件。
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

            // 希沃台账的键是「希沃:课件名」，不是磁盘路径：既不能 GetFileName，也不能
            // PptBridge.Launch（那会拿这个假路径去起 PowerPoint）。走 MCP 的 goto_slide。
            // 旧版污染键（c:\…\希沃:xxx）在 CardFileList.Normalize 里已经还原，这里只认干净的。
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
                // 老师点了“从第 N 页开始放映”，这时候连 PowerPoint 一起拉起来才是他要的。
                SetStatus("正在打开 PowerPoint…");
                b = PptBridge.Launch(jumpPath);
                if (b == null) { SetStatus("打不开 PowerPoint"); return; }
                bridge = b;   // 让后面的轮询也用上这个实例
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

        /// <summary>Clear the one-off status line shown on the card.</summary>
        public void ClearStatus() { SetStatus(""); }

        void SetStatus(string s)
        {
            lock (gate) { status = s; }
            lastPushHash = -1;   // force the next push to go out even if the page did not move
            Push(showing, last == null ? 0 : last.Current, slideCount);
        }

        // ------------------------------------------------------------------ push

        /// <summary>「每页耗时」的一次计算结果。算好就不再改，换一个数就整份换掉 —— 别的线程正在读它。</summary>
        sealed class PaceSnapshot
        {
            public static readonly PaceSnapshot Empty = new PaceSnapshot();
            public string Key = "";           // 算的是哪份课件（引擎线程当轮的 CurrentKey）
            public int DeckCount;             // 这份课件的记录数：变了才值得重算
            public int TotalCount;            // 全台账记录数
            public double DeckSeconds = -1;   // 这份课件每页秒数；-1 = 它没有可用记录
            public double AllSeconds = -1;    // 全台账每页秒数；-1 = 一条可用记录都没有
        }

        /// <summary>
        /// 「本次课实测速度」的一次计算结果（2026-10-25 功能2）。整份不可变、换一个数就整份替换 ——
        /// 卡片线程正在读它。Sample = 已翻页数 ÷ 已用时长；-1 表示样本还不够
        /// （不足 3 页或不足 60 秒），回退链在 PagePace.SecondsPerPage 里。
        /// Turns/Seconds 只用于诊断日志和以后面板上显示"本课已翻 N 页"。
        /// </summary>
        sealed class SessionSample
        {
            public static readonly SessionSample Empty = new SessionSample { Sample = -1 };
            public int Turns;
            public double Seconds;
            public double Sample = -1;
        }

        /// <summary>
        /// 重算「每页耗时」快照。只在这一条引擎线程上调用（见 <see cref="pace"/> 的注释）。
        /// 用「键 + 记录数」判断要不要重算：台账只追加、旧记录不改，记录数没变就说明没有新数据，
        /// 每 250ms 重扫一遍 ΣSeconds 是白费力气。
        /// </summary>
        void RefreshPace()
        {
            string key = CurrentKey();
            // 整段在台账锁里算：Count/Recent 各自会拿同一把锁（Monitor 可重入），但"这份课件
            // 有几条记录"和"全库有哪几条记录"必须是同一个瞬间的样子。引擎线程正在
            // Add/Prune 时分开拿锁，会得到"DeckCount 是新的、all 是旧的"这种自相矛盾的
            // 快照 —— 而它一旦被 pace 缓存住（Key/DeckCount/TotalCount 相同就不再重算），
            // 错的每页秒数会一直用到下一次记录数变化为止。
            lock (ledger.Sync)
            {
                int deckCount = key.Length > 0 ? ledger.Count(key) : 0;
                var byFile = ledger.ByFile;
                int total = 0;
                if (byFile != null) foreach (var kv in byFile) if (kv.Value != null) total += kv.Value.Count;

                var cur = pace;
                if (cur.Key == key && cur.DeckCount == deckCount && cur.TotalCount == total) return;

                var deck = key.Length > 0 ? ledger.Recent(key, PagePace.MaxRecordsPerDeck) : null;
                double deckSeconds = PagePace.Average(deck);
                // 全台账的平均值只有「这份课件没数据」时才用得上；真要用就得扫全库，
                // 所以只在那种时候扫（一节课只发生一次：记录数变了才重算）。
                List<SessionRecord> all = null;
                if (deckSeconds <= 0 && byFile != null)
                {
                    all = new List<SessionRecord>(total > 0 ? total : 8);
                    foreach (var kv in byFile) if (kv.Value != null) all.AddRange(kv.Value);
                }
                pace = new PaceSnapshot
                {
                    Key = key,
                    DeckCount = deckCount,
                    TotalCount = total,
                    DeckSeconds = deckSeconds,
                    AllSeconds = PagePace.Average(all)
                };
            }
        }

        /// <summary>
        /// 眼前这份放映中的课件每页大概多少秒：优先用「本次课实测速度」（已翻页数 ÷ 已用时长，
        /// 2026-10-25 功能2），样本不足再退回它自己的历史，再退回全台账，最后 60 秒兜底。
        /// 键对不上（改名 / 按文件名或指纹认回来的旧桶）时用快照里 CurrentKey 那份的数 ——
        /// 那张屏幕上的课件才是要估的对象，换成别的课件的平均没有意义。
        /// </summary>
        double SecondsPerPage()
        {
            var p = pace;
            var s = sessionSample;
            double v = s.Sample;        // 这一场自己测出来的：最贴眼前这块屏幕
            if (v <= 0) v = p.DeckSeconds;
            if (v <= 0) v = p.AllSeconds;
            if (v <= 0) v = PagePace.DefaultSecondsPerPage;
            return PagePace.ClampSecondsPerPage(v);
        }

        void Push(bool isShowing, int currentSlide, int count)
        {
            // 只有引擎线程才重算「每页耗时」快照（pace 是 volatile 的不可变快照，见字段注释）。
            // 注意下面 BuildState 会从任意线程读台账（Last/Count/Recent/CardFileList.Build）——
            // 那条路不是"只在引擎线程读"，所以 Ledger 内部有锁（见 Ledger.Sync 的注释）。
            if (Thread.CurrentThread == thread) RefreshPace();

            CardState st;
            lock (pushGate)
            {
                st = BuildState(isShowing, currentSlide, count);
                int h = st.Hash();
                if (h == lastPushHash) return;
                lastPushHash = h;
            }
            // 事件在锁外抛：卡片的事件处理器会回头调 RequestJumpTo / ClearStatus，
            // 在锁里调虽然 Monitor 可重入，但没必要冒这个险。
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
            st.FileName = FileLabel(lastFilePath);

            var ctx = LessonContext;
            if (ctx != null)
            {
                try
                {
                    // 只读内存里的课表快照（SlideSyncService 每 2 秒刷新一次），不在这里读服务。
                    var tag = ctx();
                    if (tag != null) st.Context = tag.Context;
                }
                catch { }
            }

            // 只认“这份课件”的记录。以前查不到时会退回到 ledger.Newest()（全库最新一条），
            // 结果希沃那边退出授课、filePath 被清空之后，卡片上直接冒出上一份 WPS 课件的历史
            // （2026-10-18 用户报“历史变成以前翻过的测试课件”）。宁可不显示，也不张冠李戴。
            string histKey;
            bool recent;
            var r = PickRecord(out histKey, out recent);
            // 这一条就是卡片正在展示的记录，DoJump 直接用它 —— 显示什么就跳什么。
            displayed = r;

            // 「还剩 N 页 · 约 M 分钟」（2026-10-24 功能1）。没在放映时一律不给值：那时候
            // Current / SlideCount 可能是上一节课的残留，或者窗口标题兜底猜出来的，
            // 算出来的会是"屏幕上并不存在的剩余页数"。
            PagePace.Remaining(isShowing, count, currentSlide, SecondsPerPage(),
                               out st.RemainingPages, out st.RemainingMinutes);
            if (r != null)
            {
                st.Previous = r.EndSlide > 0 ? r.EndSlide : r.StartSlide;
                // 2026-10-17 用户第 6 条澄清：课件停在最后一页收场 = 这份课件讲完了，下次打开不该再
                // 提示"上次停在第 N 页"（点了也是从头讲），所以干脆不给续讲页，只说一句已讲完。
                bool finished = st.Previous > 0 && r.SlideCount > 0 && st.Previous >= r.SlideCount;
                if (finished) st.Previous = 0;
                st.PreviousAgeDays = Age(r);
                if (string.IsNullOrEmpty(r.Fingerprint)) st.PreviousTrusted = true;
                else if (SeewoBridge.IsSeewoKey(r.FilePath)) st.PreviousTrusted = true;   // 希沃没有文件路径，谈不上指纹
                else st.PreviousTrusted = TrustedFingerprint(r);
                // 标题必须跟着这条记录走：recent 的时候 filePath 往往已经是另一套件的课件，
                // 否则会出现“标题写着 WPS 的课件、页码却是希沃的第 8 页”。
                if (recent || string.IsNullOrEmpty(st.FileName))
                {
                    st.FileName = FileLabel(r.FilePath);
                }
                // 任务 6：文件改过（指纹变了）时不再只说「可能偏移」——把旧版的每页文字和现在这版
                // 对一遍，真能对上就给出对齐后的页码；对不上就明说改得太多、别乱跳。
                if (!st.PreviousTrusted && st.Previous > 0)
                {
                    try
                    {
                        var remap = DeckRemap.RemapFor(ledger, r, histKey, st.Previous, ledger.Root);
                        if (remap != null)
                        {
                            if (remap.Usable && remap.NewSlide > 0) st.Previous = remap.NewSlide;
                            else if (remap.Kind == RemapKind.Replaced) st.Previous = 0;
                            if (!string.IsNullOrEmpty(remap.Note)) st.RemapNote = remap.Note;
                        }
                    }
                    catch (Exception ex) { ledger.Log("remap failed: " + ex.Message); }
                }
                st.Hint = st.PreviousTrusted
                    ? "记录于 " + ShortAge(st.PreviousAgeDays)
                    : (string.IsNullOrEmpty(st.RemapNote) ? "文件在那之后被改过，页码可能已偏移" : st.RemapNote);
                if (finished) st.Hint = "上次已讲到最后一页";
                // 刚讲完的这份：明说一句，免得老师把它当成“别的套件里那份课件”。
                if (recent) st.Hint = "刚讲完 · " + (st.PreviousTrusted ? "点这里接着讲" : "文件改过，页码可能偏移");
            }
            else st.Hint = "还没有这堂课的记录";

            // 历史列表跟着“卡片正在讲的那份课件”走：有当前文件就用当前文件，没有就退回
            // 上面兜底用的最近一条记录。这样卡片标题显示的是哪份课件，历史就是哪份的。
            if (string.IsNullOrEmpty(histKey) && r != null) histKey = r.FilePath;
            st.HistoryTotal = ledger.Count(histKey);
            if (st.HistoryTotal > 0)
            {
                var recents = ledger.Recent(histKey, 6);
                for (int i = 0; i < recents.Count; i++)
                {
                    var hr = recents[i];
                    var labels = HistoryLine.Variants(hr.Weekday, hr.Period, hr.StartedAt,
                                                      hr.StartSlide, hr.EndSlide, DateTime.Now);
                    st.History.Add(new CardHistoryItem
                    {
                        // EndSlide 正是“下次接着讲的那一页”，历史跳转也应该落在那里。
                        // 文件也一起带上：以后单文件面板也可能点开旧污染键里的记录。
                        FilePath = CardFileList.Normalize(hr.FilePath),
                        Slide = hr.EndSlide,
                        Label = labels.Length > 0 ? labels[0] : "",
                        Labels = labels,
                        IsLatest = i == 0
                    });
                }
            }

            // 文件夹面板的完整列表：所有打开过的课件（归并同名/旧污染键），新的在前。
            // 放在这里一次算好，卡片和面板都只读这份快照（见 CardFileList 的线程说明）。
            // 传 MaxSnapshot 而不是 MaxFiles：面板每页只画 8 行，但快照得带够全部课件，
            // 否则第 9 份起根本不在快照里，分页翻到下一页也是空的。
            st.Files = CardFileList.Build(ledger, histKey, CardFileList.MaxSnapshot, 6);

            // 演示软件开着、课件还没打开：这时候台账里的"最近一条记录"跟眼前无关（那份课件还没被
            // 打开），所以标题写软件名、页码留空、也不给跳页行，只说一句在等什么（用户 2026-10-15）。
            // 例外：刚讲完两分钟内（recent）——那节课就是老师刚才在用的，不能因为课件关了就把
            // 它连带历史一起擦掉，否则卡片会瞬间变成“等待打开课件”（用户 2026-10-18 报的就是这个）。
            if (st.SuiteOnly && !recent)
            {
                st.FileName = SuiteName;
                st.Previous = 0;
                st.Context = "";
                st.Hint = "打开课件就能接着上次讲";
                // 课件还没打开时，台账里那份“最近记录”跟眼前无关，别拿它的历史冒充“本课件”。
                st.History.Clear();
                st.HistoryTotal = 0;
            }

            // 功能7（2026-10-24）：课前预备态。只有"眼前真的没有任何课件"时才顶替正常态：
            // DeckOpen / SuiteOnly / 正在放映任一为真，就说明老师已经自己打开了课件，预备态让位。
            // 数据全部来自服务线程算好的 ready 快照，这里不读台账 —— 卡片只吃这一份快照。
            var rd = ready;
            if (rd.On && !st.DeckOpen && !st.SuiteOnly && !isShowing)
            {
                st.ReadyOnly = true;
                st.ReadyFile = rd.FilePath;
                st.ReadySlide = rd.Slide;
                st.ReadyRemaining = rd.Remaining;
                // 标题就是文件名；页码/历史全部清掉，免得预备态一边写"上次停在第 N 页"、
                // 一边又把正常态的字段画一遍。SlideCount 给 0：这条快照里没有"共 N 页"。
                st.FileName = FileLabel(rd.FilePath);
                st.Current = 0;
                st.SlideCount = 0;
                st.Previous = 0;
                st.Hint = "";
                st.History.Clear();
                st.HistoryTotal = 0;
            }
            return st;
        }

        // ---- 「记录里的指纹 == 磁盘上的指纹」的短缓存（见 TrustedFingerprint）----
        readonly object fpGate = new object();
        string fpPath = "";
        string fpStamp = "";
        bool fpTrusted;
        bool fpValid;
        DateTime fpAt = DateTime.MinValue;
        const double FingerprintCacheSeconds = 10;

        /// <summary>
        /// 记录里的指纹和磁盘上的是不是还一致。
        ///
        /// 为什么要缓存：BuildState 被三条线程按 250ms~2s 的节奏调用（引擎线程每拍一次 Push、
        /// 服务线程每 2 秒一次、卡片被点击时 UI 线程再来一次），而"校验指纹"要打开课件读
        /// 头尾各 64KB 算一遍 SHA256。一节 40 分钟的课等于把同一个文件读几百遍；机械盘或
        /// 网络盘上还会和 PowerPoint 自己的保存抢文件锁（用户报过"放映时保存变卡"）。
        /// 这个判断只用来决定卡片上那句「文件在那之后被改过，页码可能已偏移」的提示，
        /// 晚十秒发现完全无害，所以按"路径 + 记录里的指纹"缓存 10 秒：换了一条记录
        /// （指纹不同）立刻重算，同一个文件被原地改写也会在 10 秒内被发现。
        /// </summary>
        bool TrustedFingerprint(SessionRecord r)
        {
            string path = r.FilePath ?? "";
            string stamp = r.Fingerprint ?? "";
            var now = DateTime.UtcNow;
            lock (fpGate)
            {
                if (fpValid && fpPath == path && fpStamp == stamp &&
                    (now - fpAt).TotalSeconds < FingerprintCacheSeconds) return fpTrusted;
            }
            // 读文件放在锁外：算一次指纹可能要几百毫秒（网络盘更久），不该把别的线程卡住。
            var fpFile = Ledger.Fingerprint(path);
            bool trusted = fpFile.Length > 0 && fpFile == stamp;
            lock (fpGate)
            {
                fpPath = path;
                fpStamp = stamp;
                fpTrusted = trusted;
                fpValid = true;
                fpAt = now;
            }
            return trusted;
        }

        static double Age(SessionRecord r)
        {
            DateTime t;
            if (!DateTime.TryParse(r.EndedAt ?? r.StartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return -1;
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

        // 历史行的文字 2026-10-24 搬到 Core\HistoryLine.cs（功能3）：原来这里拼的是
        // "第 N 页 · MM-dd HH:mm · 讲了 M 分钟"，看不出是哪一节课记的；现在要有周几和
        // 第几节，而且"放不下时先砍哪一段"的逻辑要能离线测，所以整块挪出去成纯函数。

        /// <summary>
        /// 台账键既可能是真实路径，也可能是「希沃:课件名」。后者直接取冒号后的名字，
        /// 不能走 Path.GetFileName（它会把整串当文件名，某些非法字符组合还会抛异常）。
        /// </summary>
        static string FileLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (SeewoBridge.IsSeewoKey(key)) return SeewoBridge.DisplayName(key);
            try
            {
                string name = System.IO.Path.GetFileName(key);

                // 2026-10-24：WPS 在**没有打开任何文档**时，Presentation.Name 会返回软件自己的名字
                // （"WPS 演示" / "WPS Office" / "PowerPoint"），它没有扩展名、也不像路径。以前这里
                // 原样收下，卡片就显示「WPS 演示」并当成一份课件去记，日志里
                // `card: show=True deck=True file=WPS 演示` 就是它。认不出文件名就当作没认出课件，
                // 交给标题兜底或下一轮 COM 再说。
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

            // 2026-10-17：线程还卡在 COM 里的时候**绝不能**去释放它的 RCW —— 一边有调用在飞、
            // 一边 ReleaseComObject/终结器把接口指针放掉，轻则调用失败，重则访问违例、把整个
            // ClassIsland 进程 FailFast。这种情况宁可把这几组 RCW 留给 GC（进程退出前不会出乱子），
            // 也不要为了"关 WPS 更干净"去冒崩进程的险。
            if (joined) DropBridge();
            else { try { ledger.Log("engine thread still busy in COM -- 这次不释放 COM 引用，避免释放竞态"); } catch { } }

            // HTTP 客户端没有 COM 的释放竞态问题，无论线程是否退出都可以安全关闭。
            try { seewo.Dispose(); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
