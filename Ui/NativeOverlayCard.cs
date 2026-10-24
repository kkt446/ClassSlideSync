using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Ui
{
    /// <summary>
    /// The floating card, as a plain Win32 window painted with GDI.
    ///
    /// Why native and not Avalonia or WPF? ClassIsland 2 runs on Avalonia, while the plugin
    /// targets net8.0-windows against the ordinary .NET runtime -- a classroom PC is not
    /// guaranteed to have the WindowsDesktop/WPF runtime, and System.Drawing is not part of the
    /// shared framework at all. A native window (user32 + gdi32, both always present on Windows)
    /// is the one implementation that cannot fail to load inside the host.
    ///
    /// Two measured Windows facts shape this file:
    ///
    /// 1. The card must be clickable but must NOT take focus, because a slideshow runs
    ///    full-screen and owns the keyboard. That needs WS_EX_NOACTIVATE plus handling
    ///    WM_MOUSEACTIVATE and returning MA_NOACTIVATE, which is 3. Returning 4 -- a value that
    ///    does not exist -- leaves the window in an undefined state where clicks arrive halved
    ///    and the message pump stops delivering WM_TIMER, so the card looks hung.
    ///
    /// 2. A WS_EX_NOACTIVATE window never receives WM_TIMER. The auto-hide countdown therefore
    ///    runs on a background thread and wakes this window with PostMessage(WM_APP_HIDE).
    /// </summary>
    public sealed class NativeOverlayCard : IDisposable
    {
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        const int WM_DPICHANGED = 0x02E0;
        const int WM_APP_APPLY = 0x8001;
        const int WM_APP_HIDE = 0x8002;
        const int WM_PAINT = 0x000F;
        const int WM_CLOSE = 0x0010;
        const int WM_DESTROY = 0x0002;
        const int WM_ERASEBKGND = 0x0014;
        const int WM_MOUSEMOVE = 0x0200;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_LBUTTONDBLCLK = 0x0203;
        const int WM_SETCURSOR = 0x0020;
        const int IDC_HAND = 32649;
        const int IDC_ARROW = 32512;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const int SW_HIDE = 0;
        const int SW_SHOWNOACTIVATE = 4;

        readonly object gate = new object();
        CardState pending = new CardState();
        string statusText = "";

        /// <summary>窗口此刻是不是可见的。没有课件时必须彻底不可见（待命期间屏幕上啥都不留）。</summary>
        bool shown;
        /// <summary>设置页点"预览"之后的一段时间里，即使没有课件也把卡片显示出来。</summary>
        DateTime previewUntil = DateTime.MinValue;

        Thread thread;
        IntPtr hwnd;
        IntPtr pumpHwnd;
        uint taskbarCreated;
        WndProcDelegate proc;

        // 2026-10-17 崩溃根因（把 ClassIsland.Desktop 整个进程带崩的那个 FailFast）：
        // RegisterClassEx 只是把函数指针交给系统，托管侧不留任何引用。委托一旦被 GC 回收，
        // 下一条窗口消息就会踩到"在已回收的委托上回调"，运行时立刻 FailFast。事件日志原文：
        //   A callback was made on a garbage collected delegate of type
        //   'ClassSlideSync.Plugin!ClassSlideSync.Plugin.Ui.NativeOverlayCard+WndProcDelegate::Invoke'
        //   栈：NativeOverlayCard.DispatchMessage → DispatchMessage → Run
        // 所以每注册一个窗口类，就把它的委托放进这个静态表里钉住（类活到进程结束，委托也得活着）。
        static readonly System.Collections.Generic.List<WndProcDelegate> classRoots =
            new System.Collections.Generic.List<WndProcDelegate>();

        // 窗口类名也改成"一个卡片实例一个"：以前固定叫 ClassSlideSyncCard，第二次注册必然 1410
        // （已存在）而沿用**上一个实例**的 wndproc —— 于是卸下再待命之后，新窗口的消息全被
        // 旧实例的代码处理，而旧实例的 hwnd 早就销毁了，绘制全落到空处：表现就是老师看到的
        // "第一节课还有卡片，后面开 WPS 就不显示了"（2026-10-17 日志：00:25 那次有 paint，
        // 00:31/00:37 两次 applies 涨、paint 一直是 0）。
        static int classSeq;
        readonly string className = "ClassSlideSyncCard" + System.Threading.Interlocked.Increment(ref classSeq);
        bool quit;

        // Layout is authored against 96 dpi and scaled to whatever the window's monitor runs at.
        // Classroom PCs are regularly at 125% or 150%, where a fixed-pixel card looks tiny; the
        // host is per-monitor DPI aware, so this window must scale itself rather than rely on
        // Windows bitmap-stretching it.
        const int BaseCardWidth = 236;
        const int BaseCardHeight = 150;
        const int BaseTabWidth = 32;
        /// <summary>历史面板最多列几条。卡片不做滚动，多出来的旧记录就不显示了。</summary>
        const int MaxHistoryRows = 6;
        /// <summary>「打开过的课件」最多列几份。第 9 份起不画（也不做滚动）。</summary>
        const int MaxFileRows = 8;
        /// <summary>右下角文件夹图标的稿子尺寸（96 dpi），实际绘制走 Scaled()。</summary>
        const int FolderIconSize = 24;
        int dpi = 96;

        int Scaled(int v) { return (int)Math.Round(v * dpi / 96.0); }

        int cardWidth = BaseCardWidth;
        int cardHeight = BaseCardHeight;
        int tabWidth = BaseTabWidth;
        int screenLeft;
        int screenRight;
        int screenTop;
        int screenHeight;

        // 外观：ClassIsland 主题 ⊕ 用户自定义（见 Core/Appearance.cs）。绘制只读这一个对象，
        // 于是"跟随主题"和"自定义"的优先级只在一处实现。
        CardAppearance look = CardAppearance.Resolve(null, null);
        bool fontInstalled;
        string fontNote = "";
        bool expanded;

        // 历史面板（用户 2026-10-18 要求：从卡片上翻这份课件的历史页码并跳过去）。
        // 命中矩形和页码都在 Paint 里现算，只在浮层线程读写，不需要额外加锁。
        bool historyPanel;
        Rect32 historyHit;
        Rect32 backHit;
        readonly Rect32[] historyRowHits = new Rect32[MaxHistoryRows];
        readonly int[] historyRowSlides = new int[MaxHistoryRows];
        // 面板二（某份文件的历史）点开的那份文件；为 null 时沿用旧的“本课件历史”。
        CardFileItem? selectedFile;
        // true = 面板二是从「打开过的课件」进来的，返回要回面板一，而不是直接回卡片。
        bool historyBackToFiles;

        // 面板一（文件夹 → 打开过的全部课件）。folderHit 是右下角那个文件夹图标。
        bool filePanel;
        Rect32 folderHit;
        readonly Rect32[] fileRowHits = new Rect32[MaxFileRows];

        // 拖拽（2026-10-16 用户要求：悬浮窗可以鼠标拖着走，松手记住位置）。
        // 按下时先只记"预备"，移动超过几个像素才算真拖 —— 否则每一次点击都会被当成抖动的拖拽，
        // 跳页就再也点不动了。
        bool dragArmed;
        bool dragging;
        POINT dragStartCursor;
        RECT dragStartRect;

        // 这一份课件打开后是不是已经自动弹过一次小窗了。老师手动收回之后不能再弹回来，
        // 只有"关掉课件再打开一份"才算新的一轮（用户 2026-10-13 要求：打开课件先弹小窗，
        // 15 秒后自己贴边收回）。
        bool expandedForDeck;
        // 2026-10-23 用户第 2 条：从一份课件换到另一份（WPS 里打开别的文件、希沃里换课件）时，
        // 卡片要"再弹一次"，而不是因为他上一份已经收起过就一直是把手。判据是"文件名 + 页数"，
        // 换文件一定变；同一份课件被改过（页数变了）也当新的一轮，反正只是弹一次。
        string lastDeckKey = "";
        DateTime deckLatchUntil = DateTime.MinValue;
        bool autoHideEnabled = true;
        bool autoShow = true;
        int autoHideSeconds = 15;
        DateTime lastActivity = DateTime.UtcNow;
        Timer hideTimer;
        Timer consistencyTimer;

        public NativeOverlayCard()
        {
            proc = WindowProc;
            thread = new Thread(Run) { IsBackground = true, Name = "ClassSlideSync-overlay" };
            thread.SetApartmentState(ApartmentState.STA);
        }

        /// <summary>Raised on the overlay thread when the teacher clicks the page number.</summary>
        public event Action JumpRequested;

        /// <summary>
        /// 历史面板里点了一条记录（浮层线程）。参数是「哪份文件 + 要跳到的页码」；真正的
        /// COM / MCP 调用由服务转给引擎的工作线程，浮层线程不碰。
        /// 文件夹面板可以跨文件翻历史，所以文件必须跟着页码一起走。
        /// </summary>
        public event Action<string, int> HistoryJumpRequested;

        /// <summary>
        /// 老师把卡片拖到新位置并松手时触发（浮层线程）：参数是"贴不贴右边"和竖直百分比。
        /// 宿主把这两个值写进设置，下次启动就从这个位置显示。
        /// </summary>
        public event Action<bool, int> PositionChanged;

        public void Start() { thread.Start(); }

        volatile string lastError = "";

        /// <summary>
        /// Why the card could not be created, if it could not be. Swallowing creation failures
        /// silently is how this window once spent a whole test run not existing while every
        /// focus check happily reported "focus was not stolen".
        /// </summary>
        public string LastError { get { return lastError; } }

        void Fail(string where)
        {
            int code = Marshal.GetLastWin32Error();
            lastError = where + " 失败，Win32 错误码 " + code;
        }

        public bool IsCreated { get { return hwnd != IntPtr.Zero; } }

        /// <summary>
        /// One-line state dump. Diagnostics matter here: when the card is wrong on a teacher's
        /// screen there is no debugger to attach, only a log file.
        /// </summary>
        public string Diag()
        {
            var pill = CollapsedPill();
            CardState now;
            lock (gate) { now = pending; }
            return string.Format(
                "hwnd=0x{0:X} pump=0x{1:X} expanded={2} dpi={3} card={4}x{5} tab={6} pill={7}x{8}@{9} workRight={10} workTop={11} workH={12} paint={13}{14}{15}",
                hwnd.ToInt64(), pumpHwnd.ToInt64(), expanded, dpi,
                cardWidth, cardHeight, tabWidth,
                pill.R - pill.L, pill.B - pill.T, pill.T,
                screenRight, screenTop, screenHeight,
                paintCount,
                paintError.Length > 0 ? "  << paint error: " + paintError : "",
                lastError.Length > 0 ? "  << " + lastError : "")
                + " | state: shown=" + shown + " deck=" + now.DeckOpen + " suiteOnly=" + now.SuiteOnly
                + " showing=" + now.Showing + " applies=" + applyCount + " file=" + now.FileName
                // hist/prev 是「历史 · N 次」入口与「第 N 页」的来源：入口看不见时，先看这两个数
                // 是不是 0（键查不到台账），再看 card 高度有没有随 hist>0 长高（排版算错）。
                + " hist=" + HistoryTotal(now) + "/" + HistoryRows(now)
                + " files=" + (now == null || now.Files == null ? 0 : now.Files.Count) + "/" + FileRows(now)
                + " prev=" + (now == null ? 0 : now.Previous) + (historyPanel ? " panel" : "") + (filePanel ? " filesPanel" : "")
                + " stage=" + stage
                + " | look: " + look.Describe() + (fontNote.Length > 0 ? "  << " + fontNote : "");
        }

        /// <summary>
        /// Number of completed paints. A visible window that never painted is the failure that
        /// looks like "the plugin does nothing", so this counter is the first thing to check.
        /// </summary>
        int paintCount;
        string paintError = "";

        // 最近一次绘制出来的可点区域（客户区坐标）与当时是不是在放映 —— 点击要按这个判定。
        Rect32 jumpHit;
        Rect32 collapseHit;      // 小窗右上角那个"收回"箭头，点它就把卡片贴回屏幕边
        bool showingCard;
        // 状态推送比窗口早到时记一笔（见 Wake / Run）：窗口一建好就补一次 ApplyPending。
        volatile bool wakePending;
        // ApplyPending 跑过几次（诊断用）：卡片不显示时，用来区分"状态没送到"和"送到了但判成不显示"。
        int applyCount;
        // 已经真正应用过的那份状态的哈希。唤醒消息（PostMessage）万一丢了，引擎按哈希去重不会再推，
        // 卡片就永远停在旧状态（2026-10-17 老师报"开 WPS 卡片出不来"）——每秒的自检心跳拿它兜底。
        int appliedHash = int.MinValue;
        // 状态变化的日志出口（由插件服务接到台账 app.log）：卡片该出现却没出现时只看日志。
        public Action<string> Log;
        // 浮层线程走到哪一步了（诊断用）：卡片线程一旦死在半路，窗口会留在屏幕上但什么都不画。
        volatile string stage = "new";

        public void Configure(CardAppearance appearance, bool showOnStart, bool autoHide, int seconds)
        {
            look = appearance ?? CardAppearance.Resolve(null, null);
            autoShow = showOnStart;
            autoHideEnabled = autoHide;
            autoHideSeconds = seconds < 5 ? 5 : (seconds > 600 ? 600 : seconds);
            RestartHideTimer();
            Wake(WM_APP_APPLY);   // 设置页里刚改的颜色/大小要立刻可见，不能等下一次翻页
        }

        /// <summary>
        /// 运行期换外观（老师在设置页里改了颜色、大小、位置）。真正的重排在浮层线程里做，
        /// 这里只把新值挂上去并叫醒消息循环——跨线程直接摆弄窗口属性是自找麻烦。
        /// </summary>
        public void SetAppearance(CardAppearance appearance)
        {
            if (appearance == null) return;
            look = appearance;
            Wake(WM_APP_APPLY);
        }

        public void Apply(CardState st)
        {
            lock (gate) { pending = st; }
            Wake(WM_APP_APPLY);
        }

        public void ShowStatus(string text)
        {
            lock (gate) { statusText = text ?? ""; }
            Wake(WM_APP_APPLY);
        }

        public void Expand()
        {
            // 设置页的"预览"就在走这条路：即使此刻没有课件，也给 20 秒把卡片显示出来，
            // 让老师当场看到自己刚调的颜色和大小。
            previewUntil = DateTime.UtcNow.AddSeconds(20);
            Wake(WM_APP_APPLY);
        }

        void Wake(int msg)
        {
            IntPtr target = pumpHwnd != IntPtr.Zero ? pumpHwnd : hwnd;
            if (target != IntPtr.Zero) { PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero); return; }
            // 窗口还没建出来（引擎的第一次状态推送常常比卡片线程早几个毫秒）。**不能就这样丢掉**：
            // 引擎按状态哈希去重，同一个状态不会推第二次，丢掉之后卡片会一直停在初始状态
            // （停在 DeckOpen=false → 屏幕上永远不显示），表现就是老师报的"我开 PPT 卡片没出来"。
            // 记一笔，等窗口建好立刻补一次（见 Run）。
            wakePending = true;
        }

        // ---- overlay thread ---------------------------------------------------

        void Run()
        {
            try
            {
                taskbarCreated = RegisterWindowMessage("TaskbarCreated");
                var hInst = GetModuleHandle(null);

                var wc = new WNDCLASSEX();
                wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = proc;
                wc.hInstance = hInst;
                wc.hCursor = LoadCursor(IntPtr.Zero, IDC_HAND);
                wc.lpszClassName = className;
                if (RegisterClassEx(ref wc) == 0) { Fail("RegisterClassEx"); return; }
                classRoots.Add(proc);   // 钉住委托：类活着，回调指针就必须一直有效

                // A message-only window keeps the pump alive even while the card is collapsed.
                pumpHwnd = CreateWindowEx(0, className, "pump", WS_POPUP,
                    0, 0, 0, 0, new IntPtr(-3) /*HWND_MESSAGE*/, IntPtr.Zero, hInst, IntPtr.Zero);
                if (pumpHwnd == IntPtr.Zero) { Fail("CreateWindowEx(pump)"); return; }

                ApplyWorkArea(WorkingArea());

                // WS_EX_LAYERED：GDI 没有逐图元 alpha，整窗混合是唯一能让卡片半透明、
                // 从而与 ClassIsland 主界面（黑底 50%）观感一致的办法。
                hwnd = CreateWindowEx(
                    WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED,
                    className, "课件续页", WS_POPUP,
                    screenRight - tabWidth, screenTop + (screenHeight - cardHeight) / 2,
                    cardWidth, cardHeight, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) { Fail("CreateWindowEx(card)"); return; }

                // Only now does Windows know which monitor this window landed on, so this is the
                // first moment the real DPI can be asked for; the window was created with the
                // 96 dpi sizes and is resized immediately afterwards.
                dpi = DpiOf(hwnd);
                ApplyMetrics();
                ApplyLook();

                expanded = false;
                Place();
                // 建窗时不显示：待命可能在上课前 1 分钟就开始，而那时还没有任何课件 ——
                // 第一次状态推送（ApplyPending）会决定它要不要露出来。
                ShowWindow(hwnd, SW_HIDE);
                shown = false;

                // 补一次：如果状态推送比窗口早到（被 Wake 记成 wakePending），它得在这里落地，
                // 否则卡片会一直不显示（引擎按哈希去重，不会再推同一个状态）。
                wakePending = false;
                try { ApplyPending(); }
                catch (Exception ex)
                {
                    // 这一下有闪失不能让消息循环起不来 —— 卡片线程一死就再也没有第二次机会。
                    lastError = "首次应用状态失败：" + ex.GetType().Name + " " + ex.Message;
                }

                // 自检心跳：丢一次唤醒不至于让卡片永远不显示（见 OnConsistencyCheck）。
                consistencyTimer = new Timer(OnConsistencyCheck, null, 1000, 1000);

                stage = "loop";
                MSG msg;
                int got = 0;
                while (!quit && (got = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                stage = "loop exited: quit=" + quit + " GetMessage=" + got;
            }
            catch (Exception ex)
            {
                // The overlay must never take the host application down with it, but it must also
                // not vanish without a trace: LastError is what the host logs.
                lastError = "浮层线程异常：" + ex.GetType().Name + " " + ex.Message;
            }
        }

        void ApplyWorkArea(Rect32 wa)
        {
            screenLeft = wa.L;
            screenRight = wa.Right;
            screenTop = wa.T;
            screenHeight = wa.Height;
        }

        void Place()
        {
            int x = look.OnRight
                ? (expanded ? screenRight - cardWidth : screenRight - tabWidth)
                : (expanded ? screenLeft : screenLeft + tabWidth - cardWidth);
            int y = screenTop + (int)Math.Round((screenHeight - cardHeight) * look.VerticalPercent / 100.0);
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, cardWidth, cardHeight, SWP_NOACTIVATE);
        }

        /// <summary>
        /// 把外观落到窗口上：装字体、按缩放算尺寸、按圆角裁窗口形状、设整窗透明度。
        /// 每次状态更新都重算一遍，是为了让设置页里的改动立刻生效——代价只是几个 Win32 调用。
        /// </summary>
        void ApplyLook()
        {
            EnsureFont();
            ApplyMetrics();
            CardState st;
            lock (gate) { st = pending; }
            ApplyContentHeight(st);
            ApplyWindowRegion();
            ApplyAlpha();
        }

        static int HistoryTotal(CardState st)
        {
            return st == null ? 0 : st.HistoryTotal;
        }

        /// <summary>面板二显示哪份文件的历史：从文件夹进来的用被点的那份，否则用卡片的当前课件。</summary>
        List<CardHistoryItem> HistoryItems(CardState st)
        {
            if (historyPanel && selectedFile != null && selectedFile.History != null) return selectedFile.History;
            return st == null ? null : st.History;
        }

        int HistoryRows(CardState st)
        {
            var rows = HistoryItems(st);
            return rows == null ? 0 : Math.Min(rows.Count, MaxHistoryRows);
        }

        /// <summary>面板一实际画几行。状态里最多 8 份，卡片再兜一层，防止上层以后放宽了数量。</summary>
        int FileRows(CardState st)
        {
            if (st == null || st.Files == null) return 0;
            return Math.Min(st.Files.Count, MaxFileRows);
        }

        /// <summary>
        /// 展开态正文需要的高度，单位是 96 dpi 的稿子像素。这张表必须和 <see cref="Paint"/> 里
        /// 那串 <c>y += step…</c> 一一对应：以前这里拿 look.CardHeight（外观估算值）再加常数，
        /// 一旦 Paint 增删一行、或者老师关掉某一行（ShowContext/ShowStatusLine）、或者缩放不是 1，
        /// 两边就会差几像素。差在最后一行上，表现就是「历史 · N 次」被挤出可视区
        /// （用户 2026-10-18 在 125% 缩放的机器上报"看不到这个入口"）。
        /// 现在排版只有这一份行高表，DPI 只在最后乘一次。
        /// </summary>
        int ContentHeight96(CardState st)
        {
            int h = 10;
            // 面板的抬头自己就是标题（面板二是被点的那份文件名），再画一行“当前课件”会张冠李戴；
            // 旧的单文件历史面板保持原样，仍然在顶部显示当前文件名。
            bool showTopName = look.ShowFileName && !filePanel && !(historyPanel && selectedFile != null);
            if (showTopName) h += 22;
            if (filePanel)
            {
                // 抬头 + 每份课件一行（空台账也留一行“还没有记录”）+ 返回 + 下边距
                return h + 26 + Math.Max(1, FileRows(st)) * 26 + 26 + 10;
            }
            if (historyPanel)
            {
                // 抬头 + 每条一行 + 返回 + 下边距
                return h + 26 + HistoryRows(st) * 26 + 26 + 10;
            }
            h += 50;                                                    // 大页码
            h += 22;                                                    // 副标题
            if (look.ShowJumpLine && st != null && st.Previous > 0) h += 26;
            if (BitsText(st).Length > 0) h += 18;
            if (HistoryTotal(st) > 0) h += 26;                          // 历史入口
            // 状态行按“留了一行”算，不按此刻有没有字算：每次 ShowStatus 都改高度的话，
            // 卡片会在点跳转的瞬间长高／缩回去（老师眼前一跳一跳的）。
            if (look.ShowStatusLine) h += 18;
            // 文件夹图标挪到右上角、收起箭头左边（2026-10-18 用户反馈“太高了，文件夹往上放放”），
            // 不再为它单留一行，卡片高度回到加图标之前。
            return h + 10;
        }

        /// <summary>正文下方那行小字（课表上下文 · 提示）。高度计算和绘制共用，免得两边不一致。</summary>
        string BitsText(CardState st)
        {
            if (st == null || !look.ShowContext) return "";
            var bits = st.Context.Length > 0 ? st.Context : "";
            if (st.Hint.Length > 0) bits += (bits.Length > 0 ? "  ·  " : "") + st.Hint;
            return bits;
        }

        /// <summary>
        /// 卡片高度随内容走：展开态按实际要画的行数算，历史面板打开时按条数继续长高。
        /// 上限是工作区高度——再高就会有一截画到屏幕外，老师点不到。
        /// 收起态保持原高，贴边药丸的大小和位置不受历史入口影响。
        /// </summary>
        void ApplyContentHeight(CardState st)
        {
            int h96 = look.CardHeight;
            if (expanded)
            {
                int need = ContentHeight96(st);
                // 面板是“一屏列表”，高度就是内容高度：多留一截空白会让面板看着像没画完。
                // 普通卡片则取大值：look.CardHeight 含老师的缩放（CardScale），内容表是 96dpi 行高；
                // 放大卡片时窗口跟着变大，缩小时也不会把内容挤掉。
                if (historyPanel || filePanel) h96 = need;
                else if (need > h96) h96 = need;
            }
            int h = Scaled(h96);
            if (screenHeight > 0 && h > screenHeight) h = screenHeight;
            if (h > 0) cardHeight = h;
        }

        /// <summary>
        /// 用 ClassIsland 自带的 HarmonyOS Sans SC 做卡片字体。GDI 不认 Avalonia 的 avares 资源，
        /// 只能把磁盘上的 ttf 以 FR_PRIVATE 私有加载进本进程；失败就退回系统字体，
        /// 并把原因留在 Diag 里——"字体不对"是卡片看起来不像 ClassIsland 最常见的原因。
        /// </summary>
        void EnsureFont()
        {
            if (fontInstalled || look.FontFile.Length == 0) return;
            try
            {
                int added = AddFontResourceExW(look.FontFile, FR_PRIVATE, IntPtr.Zero);
                if (added > 0) { fontInstalled = true; fontNote = ""; }
                else fontNote = "字体加载失败(" + look.FontFile + ")";
            }
            catch (Exception ex) { fontNote = "字体加载异常 " + ex.GetType().Name + " " + ex.Message; }
        }

        /// <summary>
        /// 把窗口本身裁成圆角矩形，圆角外的角落才是真透明（否则是内存位图里的黑角）。
        /// 展开时贴着屏幕边缘的那一侧必须保持直角，不然边角会漏出底下的桌面或放映画面。
        /// </summary>
        void ApplyWindowRegion()
        {
            if (hwnd == IntPtr.Zero) return;
            int r = Scaled(look.Radius);
            try
            {
                // 收起态窗口里只剩下贴在屏幕边缘的那个小药丸；其余部分连"存在"都不存在，
                // 所以裁掉之后既不挡桌面，也不会抢走点击（展开态则整窗可见）。
                Rect32 visible = expanded ? new Rect32(0, 0, cardWidth, cardHeight) : CollapsedPill();
                IntPtr rgn = r > 0
                    ? CreateRoundRectRgn(visible.L, visible.T, visible.R + 1, visible.B + 1, r * 2, r * 2)
                    : CreateRectRgn(visible.L, visible.T, visible.R + 1, visible.B + 1);
                if (rgn == IntPtr.Zero) return;

                if (r > 0 && visible.B - visible.T > r * 2)
                {
                    // 紧贴屏幕边缘的那一侧保持直角，不然边角会漏出底下的放映画面。
                    IntPtr square = look.OnRight
                        ? CreateRectRgn(visible.R - r, visible.T, visible.R + 1, visible.B + 1)
                        : CreateRectRgn(visible.L, visible.T, visible.L + r + 1, visible.B + 1);
                    if (square != IntPtr.Zero)
                    {
                        CombineRgn(rgn, rgn, square, RGN_OR);
                        DeleteObject(square);
                    }
                }
                SetWindowRgn(hwnd, rgn, true);
                // 交给系统之后不能再 DeleteObject，那个 region 已经归窗口所有。
            }
            catch { }
        }

        /// <summary>
        /// 收起态露在屏幕边缘的那一小条（“把手”）。故意不占满整张卡片的高度：
        /// 一根竖直贯通半屏的黑条比一个小把手吓人得多，老师会以为屏幕坏了。
        /// </summary>
        Rect32 CollapsedPill()
        {
            int ph = Scaled((int)Math.Round(96 * look.CardScale));
            int min = Scaled(44);
            if (ph < min) ph = min;
            int cap = cardHeight * 2 / 3;
            if (ph > cap) ph = cap;
            int top = (cardHeight - ph) / 2;
            return look.OnRight
                ? new Rect32(0, top, tabWidth, top + ph)
                : new Rect32(cardWidth - tabWidth, top, cardWidth, top + ph);
        }

        void ApplyAlpha()
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                // 展开的卡片用设置里的不透明度（默认 90%，基本实心，和 ClassIsland 主界面的浅灰白
                // 一个观感）；贴边收起后那条小药丸单独再透一档——它只有几十像素宽，透一点才不显眼，
                // 用户 2026-10-23 就是因为药丸"又不透明了"才提的。
                int alpha = look.BgAlpha;
                if (!expanded)
                {
                    // 贴边收起后的药丸：直接用 ClassIsland 主界面那层透明（50%），不再跟展开卡片
                    // 共用一个值。用户 2026-10-24 的原话：「打开悬浮窗是还是这个亮度，缩成药丸时
                    // 为 classisland 一样的透明」。
                    alpha = 128;
                }

                SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Max(20, Math.Min(255, alpha)), LWA_ALPHA);
            }
            catch { }
        }

        /// <summary>The monitor's DPI for this window, with a screen-wide fallback.</summary>
        static int DpiOf(IntPtr hWnd)
        {
            try
            {
                int d = GetDpiForWindow(hWnd);
                if (d >= 48 && d <= 960) return d;
            }
            catch
            {
                // GetDpiForWindow exists from Windows 10 1607; older systems get the fallback.
            }
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                int d = GetDeviceCaps(dc, 88 /*LOGPIXELSX*/);
                if (d >= 48 && d <= 960) return d;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return 96;
        }

        void ApplyMetrics()
        {
            // look 里的宽高已经含用户缩放，这里只负责把 96 dpi 的稿子放大到本机 DPI。
            cardWidth = Scaled(look.CardWidth);
            cardHeight = Scaled(look.CardHeight);
            tabWidth = Scaled(look.TabWidth);
        }

        void DoExpand()
        {
            if (expanded) return;
            expanded = true;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();
            CardState st;
            lock (gate) { st = pending; }
            ApplyContentHeight(st);   // 历史入口那一行只在展开态占高度
            Place();
            ApplyWindowRegion();   // 展开态整窗可见，收起态只有那个小把手
            ApplyAlpha();          // 药丸 50% / 展开卡片按设置：状态一变就得跟着变，不能等下一拍 ApplyPending
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        void DoCollapse()
        {
            if (!expanded) return;
            expanded = false;
            // 面板是临时菜单：收起来之后下次展开应该回到正常卡片，而不是停在文件/历史列表上。
            ResetPanels();
            CardState st;
            lock (gate) { st = pending; }
            ApplyContentHeight(st);
            Place();
            ApplyWindowRegion();
            ApplyAlpha();          // 收起成药丸立刻回到 50% 透明，否则要等下一次状态推送才变
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        /// <summary>把两层菜单状态一起清掉。收起卡片、课件关掉时都走这里，避免残留的选中项。</summary>
        void ResetPanels()
        {
            filePanel = false;
            historyPanel = false;
            selectedFile = null;
            historyBackToFiles = false;
        }

        /// <summary>面板开/关/换层共用的重排：高度、贴边位置、圆角 region、重画一次都做齐。</summary>
        void Relayout()
        {
            CardState st;
            lock (gate) { st = pending; }
            ApplyContentHeight(st);
            Place();
            ApplyWindowRegion();
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        /// <summary>打开「打开过的课件」面板。台账为空也打开：显示一行“还没有记录”比点了没反应好。</summary>
        void OpenFilePanel()
        {
            if (filePanel) return;
            filePanel = true;
            historyPanel = false;
            selectedFile = null;
            historyBackToFiles = false;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();   // 老师主动点开的面板，15 秒倒计时重新开始
            Relayout();
        }

        void CloseFilePanel()
        {
            if (!filePanel) return;
            filePanel = false;
            selectedFile = null;
            Relayout();
        }

        /// <summary>从面板一某一行进入那份文件的历史（面板二）。</summary>
        void OpenFileHistory(CardState st, int index)
        {
            if (st == null || st.Files == null || index < 0 || index >= FileRows(st)) return;
            var f = st.Files[index];
            if (f == null) return;
            filePanel = false;
            historyPanel = true;
            selectedFile = f;
            historyBackToFiles = true;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();
            Relayout();
        }

        void OpenHistoryPanel()
        {
            if (historyPanel) return;
            // 卡片底部「历史 · N 次」入口：看的是当前课件的单文件历史，返回直接回卡片。
            historyPanel = true;
            filePanel = false;
            selectedFile = null;
            historyBackToFiles = false;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();   // 老师主动点开的面板，15 秒倒计时重新开始
            Relayout();
        }

        void CloseHistoryPanel()
        {
            if (!historyPanel) return;
            if (historyBackToFiles)
            {
                // 面板二是从文件夹面板进来的：返回应该回面板一，让老师继续翻别的课件。
                historyPanel = false;
                filePanel = true;
                historyBackToFiles = false;
            }
            else
            {
                historyPanel = false;
                selectedFile = null;
            }
            Relayout();
        }

        void RestartHideTimer()
        {
            if (hideTimer == null) hideTimer = new Timer(OnHideTimer, null, Timeout.Infinite, Timeout.Infinite);
            if (!autoHideEnabled) { hideTimer.Change(Timeout.Infinite, Timeout.Infinite); return; }
            hideTimer.Change(TimeSpan.FromSeconds(autoHideSeconds), Timeout.InfiniteTimeSpan);
        }

        void OnHideTimer(object _)
        {
            try
            {
                if (!autoHideEnabled || !expanded) return;
                if ((DateTime.UtcNow - lastActivity).TotalSeconds < autoHideSeconds - 0.5) return;
                Wake(WM_APP_HIDE);
            }
            catch { }
        }

        /// <summary>
        /// 自检心跳（1 秒一次）：状态说"该显示"，窗口却还是藏着的，就补一条 WM_APP_APPLY。
        /// 2026-10-15 的真事故：引擎的状态推送比卡片窗口早几十毫秒时，那次唤醒会被丢掉
        /// （引擎按哈希去重，不会再推一遍），卡片就永远停在"隐藏"—— 老师看到的就是
        /// "我开 PPT 卡片没出来，重开一次有时又好了"。靠这条心跳，丢消息最多晚一秒。
        /// 状态一致时这里什么也不做，不产生任何窗口操作。
        /// </summary>
        void OnConsistencyCheck(object _)
        {
            try
            {
                CardState st;
                lock (gate) { st = pending; }
                bool want = st.DeckOpen || DateTime.UtcNow < previewUntil;
                // 2026-10-17：状态变了却没被应用（唤醒消息丢了、或者 ApplyPending 里出过异常），
                // 这里补一条。引擎按哈希去重不会再推第二遍，没有这道兜底卡片就永远停在旧状态。
                if (st.Hash() != appliedHash) { Wake(WM_APP_APPLY); return; }
                if (want != shown) { Wake(WM_APP_APPLY); return; }
                // 课件刚打开、本该弹小窗，却还缩在边上（同样是被丢消息的后果）。
                if (want && !expanded && !expandedForDeck) Wake(WM_APP_APPLY);
            }
            catch { }
        }

        // ---- window procedure -------------------------------------------------

        IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_MOUSEACTIVATE:
                        lastActivity = DateTime.UtcNow;
                        return (IntPtr)MA_NOACTIVATE;   // deliver the click, never take focus

                    case WM_SETCURSOR:
                        SetCursor(LoadCursor(IntPtr.Zero, IDC_HAND));
                        return (IntPtr)1;

                    case WM_DPICHANGED:
                        // Dragged to (or started on) a monitor with a different scale factor:
                        // recompute the card's metrics and re-hang it on that monitor's edge.
                        dpi = (int)(wParam.ToInt64() & 0xFFFF);
                        ApplyWorkArea(WorkingArea());
                        // ApplyLook 会按新 DPI 重算宽度、内容高度和窗口 region；原来这里只调
                        // ApplyMetrics，圆角区域会停留在旧尺寸上。
                        ApplyLook();
                        Place();
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;

                    case WM_PAINT:
                        Paint(hWnd);
                        return IntPtr.Zero;

                    case WM_ERASEBKGND:
                        return (IntPtr)1;

                    // 一次物理点击会同时送来 DOWN 和 UP：早期两个都触发动作，
                    // 结果“点一下”等于“展开并立刻跳页 / 跳两次”，看起来就是点什么都没反应。
                    case WM_LBUTTONDOWN:
                        lastActivity = DateTime.UtcNow;
                        RestartHideTimer();
                        BeginDrag(hWnd);
                        return IntPtr.Zero;

                    // 按住拖动。鼠标可能已经跑到窗口外面，所以按下时 SetCapture，
                    // 后续的 MOUSEMOVE / BUTTONUP 才会继续送到这个窗口。
                    case WM_MOUSEMOVE:
                        if (dragArmed)
                        {
                            POINT p;
                            if (GetCursorPos(out p))
                            {
                                int dx = p.X - dragStartCursor.X, dy = p.Y - dragStartCursor.Y;
                                if (!dragging && (Math.Abs(dx) > Scaled(4) || Math.Abs(dy) > Scaled(4))) dragging = true;
                                if (dragging)
                                {
                                    lastActivity = DateTime.UtcNow;
                                    SetWindowPos(hWnd, HWND_TOPMOST, dragStartRect.Left + dx, dragStartRect.Top + dy,
                                        cardWidth, cardHeight, SWP_NOACTIVATE);
                                }
                            }
                        }
                        return IntPtr.Zero;

                    case WM_LBUTTONUP:
                        ReleaseCapture();
                        if (dragArmed && dragging)
                        {
                            dragArmed = false;
                            dragging = false;
                            DropCard();
                            return IntPtr.Zero;   // 拖完这一下不当点击，免得顺手把放映跳走
                        }
                        dragArmed = false;
                        dragging = false;
                        OnClick((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                        return IntPtr.Zero;

                    case WM_APP_APPLY:
                        ApplyPending();
                        return IntPtr.Zero;

                    case WM_APP_HIDE:
                        DoCollapse();
                        return IntPtr.Zero;

                    case WM_CLOSE:
                        DestroyWindow(hWnd);
                        return IntPtr.Zero;

                    case WM_DESTROY:
                        if (hWnd == pumpHwnd || hWnd == hwnd) { quit = true; PostQuitMessage(0); }
                        return IntPtr.Zero;
                }

                if (taskbarCreated != 0 && msg == (int)taskbarCreated)
                {
                    SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    return IntPtr.Zero;
                }
            }
            catch { }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        void ApplyPending()
        {
            applyCount++;
            CardState st;
            lock (gate) { st = pending; }
            // 只有"有课件"（正在放映，或者打开着某份课件）时卡片才该出现。待命期间（比如上课前
            // 1 分钟）还没有课件，屏幕上必须干干净净；设置页的"预览"给 20 秒豁免。
            bool hasDeck = st.DeckOpen;
            // 2026-10-23 用户报：「显示出来后，正在识别课件时拖动悬浮窗会直接隐藏」。识别那一两秒
            // 里 DeckOpen 会抖（COM 还没回话、标题兜底还没命中），卡片就跟着闪没了。认出过一次
            // 之后保持 8 秒，抖动不算"课件没了"。
            if (hasDeck)
            {
                deckLatchUntil = DateTime.UtcNow.AddSeconds(8);
            }
            else if (DateTime.UtcNow < deckLatchUntil)
            {
                hasDeck = true;
            }
            bool preview = DateTime.UtcNow < previewUntil;
            bool shouldShow = hasDeck || preview;
            // 拖动过程中无论如何不能把自己藏掉——否则一松手就找不着了。
            if (dragging || dragArmed)
            {
                shouldShow = true;
            }
            // 课件关了、预览到点了：面板不能继续留在屏幕上。
            if (!shouldShow) ResetPanels();
            // 单文件历史面板的数据源被清空时也收起来；文件夹进来的面板二看的是快照，
            // 台账后来被清空也不该把老师正在翻的那一页抽走。
            else if (historyPanel && !historyBackToFiles && HistoryTotal(st) == 0) historyPanel = false;

            appliedHash = st.Hash();
            ApplyLook();
            // 外观可能刚刚变过（换边、改垂直位置、改缩放），所以每次都要重新摆一遍。
            Place();

            if (shouldShow != shown)
            {
                shown = shouldShow;
                ShowWindow(hwnd, shouldShow ? SW_SHOWNOACTIVATE : SW_HIDE);
                var log = Log;
                if (log != null)
                {
                    try { log("show=" + shouldShow + " deck=" + hasDeck + " preview=" + preview + " file=" + st.FileName); }
                    catch { }
                }
            }
            if (!shouldShow)
            {
                // 屏幕上已经什么都没有了（课件关了、或者预览 20 秒到点了），下一轮出现要重新
                // 弹小窗。不在这里清掉的话，"先在设置页点过预览"会让之后打开课件只显示把手。
                expandedForDeck = false;
                return;
            }

            // 打开课件就把小窗弹出来（用户 2026-10-13 要求："打开课件 → 先弹小窗，15 秒后
            // 自己贴边收回"）。每份课件只自动弹这一次：老师手动点了收回箭头之后，
            // 不能因为他动一下鼠标又弹回来，那样比不弹还烦。
            // 设置页的"预览"也算一轮：老师改完颜色/大小，得看到整张卡片，不是光一个把手。
            if (autoShow && (st.Showing || hasDeck || preview))
            {
                // 2026-10-16 用户报的 bug（第 4 条）：放映中每翻一页引擎都会重推状态（哈希变了），
                // 原来这里在 st.Showing 时无条件 DoExpand()，老师刚点收起箭头，下一页又自己弹开。
                // 现在一份课件只自动弹一次（expandedForDeck），之后翻页只改数字，不再动展开状态。
                // 2026-10-23（用户第 2 条）：换课件时把"这一轮已经弹过"的记号复位，于是新课件
                // 会再弹一次；同一份课件里翻页仍然不受影响（记号还是 true）。
                string deckKey = st.FileName.Length > 0 ? (st.FileName + "|" + st.SlideCount) : "";
                if (deckKey.Length > 0)
                {
                    bool switchedDeck = lastDeckKey.Length > 0 && !string.Equals(deckKey, lastDeckKey, StringComparison.Ordinal);
                    lastDeckKey = deckKey;
                    if (switchedDeck) expandedForDeck = false;
                }

                if (!expandedForDeck)
                {
                    expandedForDeck = true;
                    DoExpand();
                }
            }
            else if (!hasDeck)
            {
                expandedForDeck = false;   // 课件关掉了，下次打开是新的一轮
                lastDeckKey = "";          // 同一份课件重新打开也算新的一轮，要再弹一次
            }
            // A slideshow window is topmost, and so are apps like the browser a teacher left open.
            // Being topmost is not enough -- the card has to be re-raised into the front of that
            // band every time it changes, or the show (or that browser) ends up covering it.
            if (expanded)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            // 展开 ⇄ 收起的透明度不一样（见 ApplyAlpha），而 ApplyAlpha 以前只在初始化时算过一次，
            // 所以药丸一直沿用展开时那档亮度——2026-10-23 用户报「缩成药丸时还是这个亮度」就是这个。
            ApplyAlpha();
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        void BeginDrag(IntPtr hWnd)
        {
            try
            {
                dragArmed = true;
                dragging = false;
                if (!GetCursorPos(out dragStartCursor) || !GetWindowRect(hWnd, out dragStartRect))
                {
                    dragArmed = false;
                    return;
                }
                SetCapture(hWnd);
            }
            catch { dragArmed = false; }
        }

        /// <summary>
        /// 松手落地：按卡片中心落在屏幕哪半边决定贴左还是贴右，竖直位置换算成百分比。
        /// 位置只保存这两个值（贴边 + 百分比），和设置页里那两个设置完全一致 ——
        /// “拖动”和“改设置”不会各存一套互相打架，卡片也只认这两个值来摆位。
        /// </summary>
        void DropCard()
        {
            try
            {
                RECT r;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r)) return;
                int cx = (r.Left + r.Right) / 2;
                bool onRight = cx >= (screenLeft + screenRight) / 2;
                int winH = r.Bottom - r.Top;
                int span = screenHeight - winH;
                int pct = span > 0 ? (int)Math.Round((r.Top - screenTop) * 100.0 / span) : 50;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;

                // 先就地生效，别等宿主回推外观（那一趟要跨线程，卡片会先闪回旧位置）。
                look.OnRight = onRight;
                look.VerticalPercent = pct;
                Place();
                ApplyWindowRegion();
                InvalidateRect(hwnd, IntPtr.Zero, false);
                lastActivity = DateTime.UtcNow;
                RestartHideTimer();

                var handler = PositionChanged;
                if (handler != null) handler(onRight, pct);
            }
            catch { }
        }

        void OnClick(int x, int y)
        {
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();
            if (!expanded) { DoExpand(); return; }
            // 收回箭头比跳页优先：它就画在卡片里，点错了把放映跳走是事故。
            if (Inside(collapseHit, x, y)) { DoCollapse(); return; }

            // 文件夹面板（面板一）：点某份课件进它的历史，点“返回”回正常卡片。
            if (filePanel)
            {
                CardState fs;
                lock (gate) { fs = pending; }
                for (int i = 0; i < MaxFileRows; i++)
                {
                    if (!Inside(fileRowHits[i], x, y)) continue;
                    OpenFileHistory(fs, i);
                    return;
                }
                if (Inside(backHit, x, y)) { CloseFilePanel(); return; }
                return;   // 面板里点空白处：不替老师把面板收掉
            }

            // 历史面板：点某条记录就跳到那一页，点“返回”回上一层。这是老师自己点开的
            // 显式菜单，所以不受 ClickToJump / showingCard 的限制（那两条是防误触整卡跳页的）。
            if (historyPanel)
            {
                CardState hs;
                lock (gate) { hs = pending; }
                var items = HistoryItems(hs);
                for (int i = 0; i < MaxHistoryRows; i++)
                {
                    int slide = historyRowSlides[i];
                    if (slide <= 0 || !Inside(historyRowHits[i], x, y)) continue;
                    // 面板二里每条记录自己就带着文件路径（可能和当前课件不是同一份），
                    // 跳页必须按“这条记录的文件 + 页码”发，不能只发页码。
                    string filePath = items != null && i < items.Count ? items[i].FilePath : "";
                    CloseHistoryPanel();
                    var jump = HistoryJumpRequested;
                    if (jump != null) jump(filePath ?? "", slide);
                    return;
                }
                if (Inside(backHit, x, y)) { CloseHistoryPanel(); return; }
                return;   // 面板里点空白处：不跳页，也不替老师把面板收掉
            }

            // 右下角文件夹图标：进“打开过的全部课件”。必须排在整卡跳页之前，
            // 否则放映中 showingCard=true，这个图标永远点不开。
            if (folderHit.R > folderHit.L && Inside(folderHit, x, y)) { OpenFilePanel(); return; }

            // 入口行要排在“整卡点击跳页”之前：放映中 showingCard=true，后面那段会把
            // 任何不在跳转行上的点击都拦掉，历史入口就永远点不开了。
            if (historyHit.R > historyHit.L && Inside(historyHit, x, y)) { OpenHistoryPanel(); return; }

            if (!look.ClickToJump) return;
            // 正在放映时只有“上次停在第 N 页”那一行可点：老师随手点在卡片别处就把放映跳走，
            // 是比不响应更糟的事故。
            if (showingCard && !Inside(jumpHit, x, y)) return;
            var h = JumpRequested;
            if (h != null) h();
        }

        /// <summary>
        /// 右上角那个文件夹图标：实心黄色文件夹（2026-10-18 用户给了图样，要求“用这个样子”），
        /// 左上小 tab + 夹身，叠一层更亮的夹盖，全用 GDI 圆角矩形拼，不引图片资源。
        /// 尺寸从传入矩形算（约 24 逻辑像素），96/144 dpi 下形状一致，只是整体放大。
        /// </summary>
        void DrawFolderIcon(GdiCanvas g, Rect32 r, Color32 c)
        {
            int s = Math.Max(1, (r.B - r.T) / 12);   // 24px 时 s=2
            var body = Color32.FromArgb(255, 0xF7, 0xC3, 0x3F);   // 文件夹主色（暖黄）
            var lid = Color32.FromArgb(255, 0xFF, 0xDD, 0x7A);    // 夹盖更亮一点，做出层次
            var edge = Color32.FromArgb(255, 0xE0, 0xA5, 0x1E);   // 描边，深一档

            // 夹身：左上角被 tab 抬起，所以整块从 y+4s 开始，圆角 2s
            g.FillRounded(body, new Rect32(r.L + 2 * s, r.T + 4 * s, r.R - 2 * s, r.B - 2 * s), 2 * s, true, true);
            // tab：夹身左上那一小块
            g.FillRounded(body, new Rect32(r.L + 2 * s, r.T + 2 * s, r.L + 7 * s, r.T + 6 * s), s, true, true);
            // 夹盖：一条更亮的横带，压住 tab 与夹身的接缝
            g.FillRounded(lid, new Rect32(r.L + 3 * s, r.T + 5 * s, r.R - 3 * s, r.T + 7 * s), 0, false, false);
            // 描边：给图标一个轮廓，白底卡片上也不至于糊成一团
            g.StrokeRounded(edge, new Rect32(r.L + 2 * s, r.T + 4 * s, r.R - 2 * s, r.B - 2 * s), 2 * s, true, true);
        }

        static bool Inside(Rect32 r, int x, int y)
        {
            return x >= r.L && x < r.R && y >= r.T && y < r.B;
        }

        // ---- GDI painting -----------------------------------------------------

        void Paint(IntPtr hWnd)
        {
            PAINTSTRUCT ps;
            IntPtr hdc = BeginPaint(hWnd, out ps);
            if (hdc == IntPtr.Zero) return;
            try
            {
              try
              {
                RECT rc;
                GetClientRect(hWnd, out rc);
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                if (w <= 0 || h <= 0) return;

                CardState st;
                string status;
                lock (gate) { st = pending; status = statusText; }

                IntPtr memDc = CreateCompatibleDC(hdc);
                IntPtr bmp = CreateCompatibleBitmap(hdc, w, h);
                IntPtr oldBmp = SelectObject(memDc, bmp);
                try
                {
                    using (var g = new GdiCanvas(memDc, w, h, dpi, look))
                    {
                        // 配色全部来自 ClassIsland 主题（见 CardAppearance），插件不另立一套色板。
                        var bg = Color32.FromArgb(255, (byte)look.BgR, (byte)look.BgG, (byte)look.BgB);
                        var accent = Color32.FromArgb(255, (byte)look.AccentR, (byte)look.AccentG, (byte)look.AccentB);
                        var text = Color32.FromArgb(255, (byte)look.TextR, (byte)look.TextG, (byte)look.TextB);
                        var muted = Color32.FromArgb(255, (byte)look.MutedR, (byte)look.MutedG, (byte)look.MutedB);
                        var jumpFill = Color32.Mix(bg, accent, 0.22);
                        var hairline = Color32.Mix(bg, text, 0.12);
                        int radius = Scaled(look.Radius);
                        g.Clear(bg);

                        // 命中区每次重画都先清空：分页/历史一变，旧矩形不能继续可点。
                        historyHit = new Rect32(0, 0, 0, 0);
                        backHit = new Rect32(0, 0, 0, 0);
                        for (int i = 0; i < MaxHistoryRows; i++)
                        {
                            historyRowHits[i] = new Rect32(0, 0, 0, 0);
                            historyRowSlides[i] = 0;
                        }
                        folderHit = new Rect32(0, 0, 0, 0);
                        for (int i = 0; i < MaxFileRows; i++) fileRowHits[i] = new Rect32(0, 0, 0, 0);

                        if (!expanded)
                        {
                            // 收起态：贴屏幕边缘的一个小把手（像本体的侧边条），箭头用主题色点睛。
                            var pill = CollapsedPill();
                            jumpHit = new Rect32(0, 0, w, h);   // 收起时点标签任意处都是“展开”
                            collapseHit = new Rect32(0, 0, 0, 0);
                            showingCard = st.Showing;
                            bool rl = look.OnRight, rr = !look.OnRight;
                            // 收起把手：白底 + 纯黑箭头（2026-10-17 用户要求“背景白色，箭头改为纯黑”）。
                            // 白底上的黑箭头对比最强，不用再描边。
                            g.FillRounded(bg, pill, radius, rl, rr);
                            g.StrokeRounded(hairline, pill, radius, rl, rr);
                            // 箭头永远指向"屏幕中间"：贴右边就是 ❮（往左拉出来），贴左边就是 ❯。
                            // 和展开态右上角那个"收回"箭头正好相反，一进一出不会看错。
                            g.Text(look.OnRight ? "\u276E" : "\u276F", g.FontHuge, Color32.FromArgb(255, 0, 0, 0), pill, Align.Center, true);
                        }
                        else
                        {
                            bool rl = !look.OnRight, rr = look.OnRight;
                            g.StrokeRounded(hairline, new Rect32(0, 0, w - 1, h - 1), radius, rl, rr);

                            int pad = Scaled(14);
                            int inner = w - pad * 2;
                            int y = Scaled(10);
                            int lineSmall = Scaled(20), stepSmall = Scaled(22);
                            int lineBig = Scaled(50), stepBig = Scaled(50);
                            int lineTiny = Scaled(18), stepTiny = Scaled(18);
                            int lineJump = Scaled(22), stepJump = Scaled(26);

                            // 面板二（某份文件的历史）自己就是标题，不再画顶部“当前课件”那一行：
                            // 否则老师点开的是另一份课件，标题却写着当前这份，会看错。
                            bool panelTwo = historyPanel && selectedFile != null;
                            if (look.ShowFileName && !filePanel && !panelTwo)
                            {
                                string title = st.FileName.Length > 0 ? st.FileName : (st.Showing ? "正在放映" : "课件续页");
                                // 右边留出收回箭头的位置，长文件名不会从箭头底下穿过去。
                                g.Text(g.Ellipsis(title, g.FontSmall, inner - Scaled(22)), g.FontSmall, muted,
                                       new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                y += stepSmall;
                            }

                            if (filePanel)
                            {
                                // 面板一：台账里打开过的课件。抬头不显示路径，老师不需要看 c:\...。
                                // 2026-10-23 用户第 3 条（"全部课件历史记录长的太丑"）重排：
                                // 抬头压一条分隔线、文件名升到 FontSmall、当前行整行淡强调底 + 左侧竖条、
                                // 返回改成居中弱化文字。行高和面板总高都不变，只换排版。
                                g.Text("课件库", g.FontTiny, muted,
                                       new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                int sepY = y + lineSmall - Scaled(2);
                                g.FillRounded(hairline, new Rect32(pad, sepY, w - pad, sepY + Math.Max(1, Scaled(1))), 0, false, false);
                                y += stepJump;

                                int rows = FileRows(st);
                                if (rows == 0)
                                {
                                    // 台账为空也把面板打开：显示一句“还没有记录”，老师知道是没记录，
                                    // 而不是图标坏了。这一层只留“返回”。
                                    g.Text("还没有记录", g.FontTiny, muted,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                    y += stepJump;
                                }
                                for (int i = 0; i < rows; i++)
                                {
                                    var f = st.Files[i];
                                    if (f == null) continue;
                                    var row = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                    fileRowHits[i] = row;
                                    // 正在讲的这份：整行淡强调底 + 左边一小条，扫一眼就知道“现在在哪份”。
                                    if (f.IsCurrent)
                                    {
                                        g.FillRounded(Color32.Mix(bg, accent, 0.10), row, Scaled(6), true, true);
                                        g.FillRounded(accent, new Rect32(row.L + Scaled(2), y + Scaled(4), row.L + Scaled(4), y + lineJump - Scaled(4)), Scaled(1), true, true);
                                    }
                                    // 行尾的「第 N 页 · 3 小时前」右对齐；文件名先按剩余宽度截断，两段不会叠。
                                    int detailW = g.TextWidth(f.Detail, g.FontTiny);
                                    int nameW = inner - detailW - Scaled(14);
                                    if (nameW < Scaled(24)) nameW = inner;   // 细节太长时宁可截细节，也不把文件名挤没
                                    g.Text(g.Ellipsis(f.DisplayName, g.FontSmall, nameW), g.FontSmall,
                                           f.IsCurrent ? accent : text,
                                           new Rect32(pad, y, w - pad, y + lineJump - Scaled(1)), Align.Left, false);
                                    if (detailW > 0 && detailW < inner - Scaled(24))
                                        g.Text(f.Detail, g.FontTiny, muted,
                                               new Rect32(pad, y, w - pad, y + lineJump - Scaled(1)), Align.Right, false);
                                    y += stepJump;
                                }

                                var fileBack = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                backHit = fileBack;
                                int backSep = y + lineJump - Scaled(2);
                                g.FillRounded(hairline, new Rect32(pad, backSep, w - pad, backSep + Math.Max(1, Scaled(1))), 0, false, false);
                                g.Text("\u2039 返回", g.FontTiny, muted,
                                       new Rect32(pad, y, w - pad, y + lineJump), Align.Center, false);
                            }
                            else if (historyPanel)
                            {
                                // 面板二：整块替换正常卡片正文。不做滚动，最多列 6 条。
                                // 抬头是被点的那份文件名（从文件夹进来时），旧的底部入口仍然显示“本课件历史”。
                                var items = HistoryItems(st);
                                int total = selectedFile != null ? selectedFile.Total : HistoryTotal(st);
                                string head = selectedFile != null && !string.IsNullOrEmpty(selectedFile.DisplayName)
                                    ? selectedFile.DisplayName
                                    : (total > 0 ? "本课件历史 · " + total + " 次" : "本课件历史");
                                // 收起箭头在右上角，抬头也要给它让位，不然两段字会叠在一起。
                                g.Text(g.Ellipsis(head, g.FontSmall, inner - Scaled(22)), g.FontSmall, muted,
                                       new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                y += stepJump;

                                int rows = HistoryRows(st);
                                for (int i = 0; i < rows; i++)
                                {
                                    var item = items[i];
                                    var hit = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                    historyRowHits[i] = hit;
                                    historyRowSlides[i] = item.Slide;
                                    // 最近一条用强调色，其余弱化——一眼看出“上次停在哪”。
                                    g.Text(g.Ellipsis(item.Label, g.FontTiny, inner), g.FontTiny,
                                           item.IsLatest ? accent : muted,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                    y += stepJump;
                                }

                                var back = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                backHit = back;
                                g.Text("\u2039 返回", g.FontTiny, accent,
                                       new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                            }
                            else
                            {
                                int page = st.Showing ? st.Current : st.Previous;
                                string pageText = page > 0 ? page.ToString() : "—";
                                g.Text(pageText, g.FontBig, st.Showing ? accent : text,
                                       new Rect32(pad, y, w - pad, y + lineBig), Align.Left, false);
                                y += stepBig;

                                string caption = st.Showing
                                    ? (look.ShowSlideCount && st.SlideCount > 0 ? "正在放映 · 共 " + st.SlideCount + " 页" : "正在放映")
                                    : (st.Previous > 0 ? "上次停在这一页"
                                       : (st.SuiteOnly ? "正在识别课件…" : "等待放映"));
                                g.Text(g.Ellipsis(caption, g.FontSmall, inner), g.FontSmall, muted,
                                       new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                y += stepSmall;

                                // “上次停在第 N 页”是这插件存在的理由，所以它单占一行、带主题色底，
                                // 而且只有这一行可点（见 OnClick）。
                                showingCard = st.Showing || st.SuiteOnly;   // 等课件时整张卡片都不可点（只有收回箭头能点）
                                jumpHit = new Rect32(0, 0, 0, 0);
                                if (look.ShowJumpLine && st.Previous > 0)
                                {
                                    string jump = st.Showing
                                        ? "上次停在第 " + st.Previous + " 页 · 点这里跳过去"
                                        : "点击从第 " + st.Previous + " 页开始放映";
                                    var row = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                    g.FillRounded(jumpFill, row, Scaled(6), true, true);
                                    g.Text(g.Ellipsis(jump, g.FontSmall, inner), g.FontSmall, accent,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                    jumpHit = row;
                                    y += stepJump;
                                }

                                // 和高度计算共用同一份“要不要画这行”的判断（见 BitsText）。
                                string bits = BitsText(st);
                                if (bits.Length > 0)
                                {
                                    g.Text(g.Ellipsis(bits, g.FontTiny, inner), g.FontTiny, muted,
                                           new Rect32(pad, y, w - pad, y + lineTiny), Align.Left, false);
                                    y += stepTiny;
                                }

                                // 历史入口：弱化色，和上面带底色的跳转行区分开——它是“打开一个列表”，
                                // 不是“立刻跳页”，误点的后果不一样。
                                int total = HistoryTotal(st);
                                if (total > 0)
                                {
                                    string hist = "历史 · " + total + " 次 \u203A";
                                    historyHit = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                    g.Text(g.Ellipsis(hist, g.FontTiny, inner), g.FontTiny, muted,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                    y += stepJump;
                                }

                                // 状态行就算这一秒没字也要占位（见 ContentHeight96），文件夹图标才能稳定贴在右下角。
                                if (look.ShowStatusLine)
                                {
                                    if (status.Length > 0)
                                        g.Text(g.Ellipsis(status, g.FontTiny, inner), g.FontTiny, accent,
                                               new Rect32(pad, y, w - pad, y + lineTiny), Align.Left, false);
                                    y += stepTiny;
                                }
                            }

                            // 右上角的"收回"箭头：点一下就把卡片贴回屏幕边（老师上完课想清屏时用）。
                            // 箭头指的方向 = 卡片要缩去的那一边，所以右边贴边的卡片是"❯"。
                            // 历史面板里也保留它：面板开着时老师照样能一键清屏。
                            var arrow = new Rect32(w - pad - Scaled(16), Scaled(6), w - pad + Scaled(2), Scaled(6) + Scaled(18));
                            collapseHit = arrow;
                            g.Text(look.OnRight ? "\u276F" : "\u276E", g.FontSmall, muted, arrow, Align.Center, false);

                            // 文件夹图标 → 打开过的全部课件（2026-10-18 用户要求）。原先贴在右下角单占一行，
                            // 老师嫌卡片太高，改成塞进右上角、放在收起箭头左边，垂直与箭头对齐。
                            // 自绘（tab + 开口的夹身）不引图片资源；命中矩形四周加 6px 免瞄准。
                            int icon = Scaled(FolderIconSize);
                            int iconX = arrow.L - Scaled(10) - icon;
                            int iconY = Scaled(6) + (Scaled(18) - icon) / 2;
                            var iconRect = new Rect32(iconX, iconY, iconX + icon, iconY + icon);
                            DrawFolderIcon(g, iconRect, muted);
                            folderHit = new Rect32(iconX - Scaled(6), iconY - Scaled(6), iconX + icon + Scaled(6), iconY + icon + Scaled(6));
                        }
                    }

                    BitBlt(hdc, 0, 0, w, h, memDc, 0, 0, 0x00CC0020 /*SRCCOPY*/);
                }
                finally
                {
                    SelectObject(memDc, oldBmp);
                    DeleteObject(bmp);
                    DeleteDC(memDc);
                }
              }
              catch (Exception ex) { paintError = ex.GetType().Name + " " + ex.Message; }
              paintCount++;
            }
            finally
            {
                EndPaint(hWnd, ref ps);
            }
        }

        struct RECT { public int Left, Top, Right, Bottom; }

        static Rect32 WorkingArea()
        {
            RECT r;
            if (SystemParametersInfo(0x0030 /*SPI_GETWORKAREA*/, 0, out r, 0))
                return new Rect32(r.Left, r.Top, r.Right, r.Bottom);
            return new Rect32(0, 0, 1920, 1040);
        }

        public void Dispose()
        {
            try
            {
                if (hideTimer != null) hideTimer.Dispose();
                if (consistencyTimer != null) consistencyTimer.Dispose();
                if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                else if (pumpHwnd != IntPtr.Zero) PostMessage(pumpHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        // ---- interop ----------------------------------------------------------

        // 外观相关的 Win32 常量：整窗透明（layered）、圆角裁形（window region）、私有字体加载。
        const int WS_EX_LAYERED = 0x00080000;
        const uint LWA_ALPHA = 0x00000002;
        const int RGN_OR = 2;
        const uint FR_PRIVATE = 0x10;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern void PostQuitMessage(int nExitCode);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG lpmsg);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // 拖拽用（2026-10-16 用户第 3 条）：光标位置 + 鼠标捕获 + 窗口矩形。
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        struct POINT { public int X, Y; }
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string lpString);
        // kernel32, not user32: declaring this one in user32.dll throws
        // EntryPointNotFoundException at call time, which silently killed the whole overlay thread.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
        [DllImport("user32.dll")]
        static extern IntPtr SetCursor(IntPtr hCursor);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);
        [DllImport("user32.dll")]
        static extern int GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")]
        static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")]
        static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
        // gdi32, 不能在 user32：这个类在 P/Invoke 上已经栽过两次（GetModuleHandle、DrawTextW），
        // 每次都是整个浮层默默失效。
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
            uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision,
            uint iQuality, uint iPitchAndFamily, string pszFaceName);
        [DllImport("gdi32.dll")]
        static extern int SetTextColor(IntPtr hdc, int crColor);
        [DllImport("gdi32.dll")]
        static extern int SetBkMode(IntPtr hdc, int iBkMode);
        // DrawText 在 user32.dll，不在 gdi32.dll（这里写错过一次，结果整张卡片一个字都画不出来，
        // 而且异常被消息循环吞掉，看起来就是“插件什么都没做”）。CharSet.Unicode 也不能少，
        // 否则中文按 ANSI 编组会画成乱码。
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int DrawTextW(IntPtr hdc, string lpchText, int nCount, ref RECT lprc, uint uFormat);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(int crColor);
        [DllImport("gdi32.dll")]
        static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreatePen(int iStyle, int cWidth, int crColor);
        [DllImport("gdi32.dll")]
        static extern bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")]
        static extern IntPtr GetStockObject(int i);

        static int Rgb(byte r, byte g, byte b) { return r | (g << 8) | (b << 16); }

        struct Color32
        {
            public byte A, R, G, B;
            public static Color32 FromArgb(byte a, byte r, byte g, byte b) { return new Color32 { A = a, R = r, G = g, B = b }; }
            public static Color32 White { get { return FromArgb(255, 255, 255, 255); } }

            /// <summary>
            /// 混合两个颜色：GDI 没有逐图元 alpha，"半透明高亮底色"只能提前混好再按不透明色画。
            /// </summary>
            public static Color32 Mix(Color32 a, Color32 b, double t)
            {
                return FromArgb(255,
                    (byte)Math.Round(a.R * (1 - t) + b.R * t),
                    (byte)Math.Round(a.G * (1 - t) + b.G * t),
                    (byte)Math.Round(a.B * (1 - t) + b.B * t));
            }
        }

        struct Rect32
        {
            public int L, T, R, B;
            public Rect32(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; }
            public int Right { get { return R; } }
            public int Top { get { return T; } }
            public int Height { get { return B - T; } }
        }

        enum Align { Left, Center, Right }

        /// <summary>Minimal GDI drawing surface: fill, stroke and text. No framework types.</summary>
        sealed class GdiCanvas : IDisposable
        {
            readonly IntPtr dc;
            readonly int width, height;
            readonly IntPtr fontSmall, fontBig, fontTiny, fontHuge;
            readonly IntPtr nullPen;

            const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
            const uint CLEARTYPE = 5, ANTIALIASED = 4, DEFAULT_CHARSET = 1, DEFAULT_PITCH = 0;
            const int TRANSPARENT = 1;
            const uint DT_LEFT = 0x00000000, DT_CENTER = 0x00000001, DT_RIGHT = 0x00000002, DT_VCENTER = 0x00000004,
                       DT_SINGLELINE = 0x00000020, DT_END_ELLIPSIS = 0x00008000, DT_WORDBREAK = 0x00000010,
                       DT_CALCRECT = 0x00000400;

            readonly int savedBkMode, savedTextColor;
            readonly IntPtr savedFont;

            /// <summary>Font point sizes are authored at 96 dpi and scaled like everything else.</summary>
            static int FontHeight(int points, int dpi) { return -(int)Math.Round(points * dpi / 96.0); }

            public GdiCanvas(IntPtr dc, int w, int h, int dpi, CardAppearance look)
            {
                this.dc = dc; width = w; height = h;
                // 字体族和字号都取自 ClassIsland 的主题（HarmonyOS Sans SC + 它的字号体系），
                // 老师换字体或调字号时卡片跟着变，而不是插件自己另立一套。
                string face = string.IsNullOrWhiteSpace(look.FontFamily) ? "Microsoft YaHei UI" : look.FontFamily;
                int d = look.FontDelta;
                int small = Math.Max(9, look.FontSecondary + d);
                int tiny = Math.Max(8, look.FontSecondary + d - 2);
                int big = Math.Max(18, look.FontLarge + d + 18);
                int huge = Math.Max(12, look.FontBody + d + 6);
                fontSmall = CreateFontW(FontHeight(small, dpi), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontBig = CreateFontW(FontHeight(big, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontTiny = CreateFontW(FontHeight(tiny, dpi), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontHuge = CreateFontW(FontHeight(huge, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                savedBkMode = SetBkMode(dc, TRANSPARENT);
                savedTextColor = SetTextColor(dc, 0xFFFFFF);
                savedFont = SelectObject(dc, fontSmall);
                // GDI 的 Rectangle / RoundRect 是"用画刷填、用画笔描边"：内存 DC 默认那支笔是 1px 黑笔，
                // 于是每个填充都会在边上留一条黑线——收起把手右边那条竖线（r.R - radius 处）就是这么来的
                // （2026-10-17 用户报的「有条线是bug」）。换成空笔，填充就只填不描边；
                // 真正要描边的地方由 StrokeRounded 自己选笔、画完再还回来。
                nullPen = CreatePen(5 /*PS_NULL*/, 0, 0);
                SelectObject(dc, nullPen);
            }

            public IntPtr FontSmall { get { return fontSmall; } }
            public IntPtr FontBig { get { return fontBig; } }
            public IntPtr FontTiny { get { return fontTiny; } }
            public IntPtr FontHuge { get { return fontHuge; } }

            public void Clear(Color32 c)
            {
                using (var b = new Brush(dc, c)) Rectangle(dc, 0, 0, width, height);
            }

            public void FillRounded(Color32 c, Rect32 r, int radius, bool roundLeft, bool roundRight)
            {
                if (c.A < 255) { FillSolid(c, r); return; }   // GDI has no per-primitive alpha
                using (var b = new Brush(dc, c))
                {
                    if (roundLeft || roundRight) RoundRect(dc, r.L, r.T, r.R, r.B, radius * 2, radius * 2);
                    else Rectangle(dc, r.L, r.T, r.R, r.B);
                }
                if (roundLeft != roundRight)
                {
                    // Square off the side that must stay flush with the screen edge.
                    using (var b = new Brush(dc, c))
                    {
                        if (roundRight) Rectangle(dc, r.L, r.T, r.L + radius, r.B);
                        else Rectangle(dc, r.R - radius, r.T, r.R, r.B);
                    }
                }
            }

            void FillSolid(Color32 c, Rect32 r)
            {
                using (var b = new Brush(dc, c)) Rectangle(dc, r.L, r.T, r.R, r.B);
            }

            public void StrokeRounded(Color32 c, Rect32 r, int radius, bool roundLeft, bool roundRight)
            {
                IntPtr pen = CreatePen(0, 1, Rgb(c.R, c.G, c.B));
                IntPtr old = SelectObject(dc, pen);
                IntPtr oldBrush = SelectObject(dc, GetStockObject(5 /*NULL_BRUSH*/));
                if (roundLeft || roundRight) RoundRect(dc, r.L, r.T, r.R, r.B, radius * 2, radius * 2);
                else Rectangle(dc, r.L, r.T, r.R, r.B);
                SelectObject(dc, oldBrush);
                SelectObject(dc, old);
                DeleteObject(pen);
            }

            public void Text(string text, IntPtr font, Color32 c, Rect32 r, Align align, bool centerVertically)
            {
                IntPtr old = SelectObject(dc, font);
                SetTextColor(dc, Rgb(c.R, c.G, c.B));
                uint f = DT_SINGLELINE | DT_END_ELLIPSIS;
                if (align == Align.Center) f |= DT_CENTER;
                else if (align == Align.Right) f |= DT_RIGHT;
                else f |= DT_LEFT;
                if (centerVertically) f |= DT_VCENTER;
                var rc = new RECT { Left = r.L, Top = r.T, Right = r.R, Bottom = r.B };
                DrawTextW(dc, text, text.Length, ref rc, f);
                SelectObject(dc, old);
            }

            public string Ellipsis(string text, IntPtr font, int maxWidth)
            {
                if (string.IsNullOrEmpty(text)) return "";
                if (MeasureWidth(text, font) <= maxWidth) return text;
                for (int cut = text.Length - 1; cut > 1; cut--)
                {
                    var t = text.Substring(0, cut) + "…";
                    if (MeasureWidth(t, font) <= maxWidth) return t;
                }
                return text;
            }

            /// <summary>量一段文字的像素宽：文件列表要把“第 N 页 · 3 小时前”右对齐，先得知道它多宽。</summary>
            public int TextWidth(string text, IntPtr font)
            {
                return string.IsNullOrEmpty(text) ? 0 : MeasureWidth(text, font);
            }

            int MeasureWidth(string text, IntPtr font)
            {
                IntPtr old = SelectObject(dc, font);
                var rc = new RECT { Left = 0, Top = 0, Right = 10000, Bottom = 100 };
                DrawTextW(dc, text, text.Length, ref rc, DT_SINGLELINE | DT_CALCRECT);
                SelectObject(dc, old);
                return rc.Right - rc.Left;
            }

            sealed class Brush : IDisposable
            {
                readonly IntPtr dc, brush, old;
                public Brush(IntPtr dc, Color32 c)
                {
                    this.dc = dc;
                    brush = CreateSolidBrush(Rgb(c.R, c.G, c.B));
                    old = SelectObject(dc, brush);
                }
                public void Dispose()
                {
                    SelectObject(dc, old);
                    DeleteObject(brush);
                }
            }

            public void Dispose()
            {
                SelectObject(dc, savedFont);
                SetBkMode(dc, savedBkMode);
                SetTextColor(dc, savedTextColor);
                DeleteObject(nullPen);
                DeleteObject(fontSmall);
                DeleteObject(fontBig);
                DeleteObject(fontTiny);
                DeleteObject(fontHuge);
            }
        }
    }
}
