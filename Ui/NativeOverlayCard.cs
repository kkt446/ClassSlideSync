using System;
using System.Globalization;
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
    public sealed class NativeOverlayCard : IDisposable, ICardSink
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
        // 分辨率和色彩位深变了（插拔投影仪、切换镜像/扩展、改分辨率）：dpi 常常不变，
        // 所以收不到 WM_DPICHANGED，只能靠这条消息重算工作区。
        const int WM_DISPLAYCHANGE = 0x007E;
        // 系统级设置广播：任务栏换边/自动隐藏、显示缩放设置变化都会走它。
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_ERASEBKGND = 0x0014;
        const int WM_MOUSEMOVE = 0x0200;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_LBUTTONDBLCLK = 0x0203;
        const int WM_SETCURSOR = 0x0020;
        // 捕获被抢（或用 ReleaseCapture 主动放开）时系统送的：拖动状态机靠它收尾，
        // 否则丢了 WM_LBUTTONUP 就会一直以为自己在拖。注意别和 WM_SETCURSOR 的 0x0020 混了。
        const int WM_CAPTURECHANGED = 0x0215;
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
        /// <summary>
        /// 收起药丸里那两行页码（蓝「已翻 N」/ 红「共 M」）的稿子行高。字号用 fontTiny（默认 12pt，
        /// 96dpi 下字高 16px），行框给 18 是为了数字和中文都不贴边、不被 DrawText 裁掉。
        /// </summary>
        const int PillLineStep96 = 18;
        /// <summary>药丸宽度下限（96dpi 稿值）：再窄放不下「已翻 24」这一行。真实宽度见 PillWidth()。</summary>
        const int PillMinWidth96 = 30;
        /// <summary>历史面板最多列几条。卡片不做滚动，多出来的旧记录就不显示了。</summary>
        const int MaxHistoryRows = 6;
        /// <summary>「打开过的课件」一页最多列几行。第 9 份起翻页（见 FilePageSize）。</summary>
        const int MaxFileRows = 8;
        /// <summary>筛选条最多记几个 chip 的命中区。一行 208 逻辑像素放得下的还不到这个数。</summary>
        const int MaxFilterChips = 6;
        /// <summary>
        /// 面板一的行高（96 dpi 稿子像素）：抬头 / 筛选条 / 科目标题 / 课件行 / 分页条 / 返回。
        /// 这些常数是 ContentHeight96 和 Paint 共用的唯一一份行高表 —— 以前两边各写一套，
        /// 一改 Paint 高度就对不上，最后一行会被挤出可视区。
        /// </summary>
        const int FileHeadStep = 26;
        const int FileFilterStep = 26;
        const int FileGroupStep = 18;
        const int FileRowStep = 26;
        const int FilePagerStep = 26;
        const int FileBackStep = 26;
        /// <summary>
        /// 「哪一份？」面板一行的高度：比课件库的行高两档（26 → 40）。
        /// 为什么不用 26 —— 236 宽的卡片里，「…\桌面\classisland插件」和「第 8 页 · 10 小时前」
        /// 挤在同一行时两边都只剩十几个像素，最后变成「classislan…」+「第 8 页 · 10 小…」，
        /// 老师照样分不清是哪一份。所以一行拆两截：folder 一行、页码/时间一行。
        /// </summary>
        const int SameNameRowStep = FileRowStep + 14;
        /// <summary>右上角那个图标（原来是文件夹，2026-10-24 换成齿轮）的稿子尺寸（96 dpi），实际绘制走 Scaled()。</summary>
        const int FolderIconSize = 24;

        // ---- 2026-10-24 卡片改版（A 方案）：整张卡片固定 236×176 ----------------------
        // 老师原话：「1：课件进度条现在缩成药丸也竖屏显示进度条……2：历史记录放最下面。
        // 3：这个删了，原位置改成设置，把缩小的跟最早一版一样放在悬浮窗靠左竖着」。
        // 新版式就是五行固定：文件名 → 大页码 + 右侧两行 → 实心跳转按钮 → 进度条 → 历史行。
        // 下面这张表是 ContentHeight96 和 Paint 共用的唯一一份行高表，加起来必须正好 176：
        // 12 + 20 + 54 + 8 + 32 + 8 + 4 + 8 + 18 + 12 = 176。
        const int CardPadTop96 = 12;
        const int TitleStep96 = 20;
        const int PageBlockStep96 = 54;
        const int BtnGapTop96 = 8;
        const int BtnHeight96 = 32;
        const int BarGapTop96 = 8;
        const int BarHeight96 = 4;
        const int HistGapTop96 = 8;
        const int HistStep96 = 18;
        const int CardPadBottom96 = 12;
        /// <summary>正常卡片的高度（96 dpi 稿）。固定值：老师的版式里这五行一条都不能少，少一行整张卡就会抖。</summary>
        const int CardContent96 = CardPadTop96 + TitleStep96 + PageBlockStep96 + BtnGapTop96 + BtnHeight96
                                + BarGapTop96 + BarHeight96 + HistGapTop96 + HistStep96 + CardPadBottom96;
        /// <summary>卡片左边缘那条竖向收起把手的宽度（96 dpi）。它整条高度都能点，18 宽并不难点。</summary>
        const int HandleWidth96 = 18;
        /// <summary>正文左边距 = 把手宽 + 10 空隙；右边距 14。整张卡因此左边比右边宽一指，视觉上把把手让出来。</summary>
        const int ContentPadLeft96 = HandleWidth96 + 10;
        const int ContentPadRight96 = 14;
        /// <summary>大页码那一列（96 dpi）：三位数的 40px 数字也放得下，右边还有 124 给两行小字。</summary>
        const int PageColumn96 = 70;
        /// <summary>进度条轨道色（ClassIsland 课程时间条底下那条浅灰），填充用主题 accent。</summary>
        static readonly Color32 ProgressTrack = Color32.FromArgb(255, 0xCD, 0xD0, 0xD5);
        int dpi = 96;

        int Scaled(int v) { return (int)Math.Round(v * dpi / 96.0); }

        int cardWidth = BaseCardWidth;
        int cardHeight = BaseCardHeight;
        int tabWidth = BaseTabWidth;
        // 收起药丸沿屏幕边缘那条边的长度（普通宽度不动它，见 PillWidth / CollapsedPill）。
        // 2026-10-24 起药丸里要竖着放「已翻 N」「共 M」两行字，实测后它比 tabWidth 还宽，单独记一份。
        int pillWidth;
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
        // 2026-10-24 控制面板：右上角文件夹图标左边的齿轮按钮。卡片是 WS_EX_NOACTIVATE，
        // 键盘焦点永远在放映窗口那边，搜索框只能放进独立窗口；这个齿轮就是入口。
        Rect32 panelHit;
        bool showPanelButton = true;
        readonly Rect32[] fileRowHits = new Rect32[MaxFileRows];

        // 2026-10-24 功能2（按科目分组 + 筛选 + 分页）：课件一多，平铺一列就找不着了。
        // fileFilter = "" 是「全部」，否则是选中的科目名；filePage 从 0 起。
        // 这两个都只是浮层上的临时状态，不进设置 —— 老师下次打开面板该看到的是全部课件。
        string fileFilter = "";
        int filePage;
        /// <summary>命中行对应的课件归并键。分组+筛选+分页之后，第 i 行已经不等于 st.Files[i] 了。</summary>
        readonly string[] fileRowKeys = new string[MaxFileRows];
        /// <summary>筛选条每个 chip 的命中区，和它对应的科目（"" = 全部）。</summary>
        readonly Rect32[] filterHits = new Rect32[MaxFilterChips];
        readonly string?[] filterKeys = new string?[MaxFilterChips];
        /// <summary>分页条的「上一页 / 下一页」命中区；不需要分页时是空矩形，点不到。</summary>
        Rect32 pagePrevHit;
        Rect32 pageNextHit;

        // 面板三（2026-10-24 功能9）：「同名不同课件，问一下」。
        // 课件库的一行背后可能是两份内容不同的同名文件，点之前先问「哪一份？」。
        // 候选快照在 CardFileItem.SameName 里（引擎算好），卡片只读；点开的这一份按归并键
        // 每帧重新找一遍（SameNameTarget），台账刷新后那份文件没了也不会画出一张空面板。
        bool sameNamePanel;
        CardFileItem? sameNameFile;
        readonly Rect32[] sameNameRowHits = new Rect32[CardFileList.MaxSameName];
        // 行 → 路径 / 页码。和 historyRowSlides 一个思路：命中区里不存对象，
        // 只存跳页要的两个值，点下去就发 HistoryJumpRequested。
        readonly string?[] sameNameRowPaths = new string?[CardFileList.MaxSameName];
        readonly int[] sameNameRowSlides = new int[CardFileList.MaxSameName];

        // 课前预备态（2026-10-24 功能7/8）：「打开并接着讲」按钮的命中区。预备态整张卡都能点
        // （见 OnClick），这个矩形只用来让按钮本身落点更精确，不参与"是不是预备态"的判断。
        Rect32 readyOpenHit;

        // 拖拽（2026-10-16 用户要求：悬浮窗可以鼠标拖着走，松手记住位置）。
        // 按下时先只记"预备"，移动超过几个像素才算真拖 —— 否则每一次点击都会被当成抖动的拖拽，
        // 跳页就再也点不动了。
        bool dragArmed;
        bool dragging;
        POINT dragStartCursor;
        RECT dragStartRect;

        // 自动化「隐藏悬浮卡片」的抑制位：只影响"要不要显示"，不影响引擎继续记台账。
        // 打开新课件或自动化再点"显示"时清掉。
        bool suppressShow;

        // 这一份课件打开后是不是已经自动弹过一次小窗了。老师手动收回之后不能再弹回来，
        // 只有"关掉课件再打开一份"才算新的一轮（用户 2026-10-13 要求：打开课件先弹小窗，
        // 15 秒后自己贴边收回）。
        bool expandedForDeck;
        // 2026-10-23 用户第 2 条：从一份课件换到另一份（WPS 里打开别的文件、希沃里换课件）时，
        // 卡片要"再弹一次"，而不是因为他上一份已经收起过就一直是把手。判据是"文件名 + 页数"，
        // 换文件一定变；同一份课件被改过（页数变了）也当新的一轮，反正只是弹一次。
        string lastDeckKey = "";
        // 2026-10-24 审出：上面那个 lastDeckKey 只由"文件名 + 页数"组成，而 CardState 里根本没有
        // 文件路径（CardFileList 的 IsCurrent 对同名两份同时为真），所以"关掉 A、马上打开同名的 B"
        // 时键完全不变，switchedDeck 判不出来，新课件只能显示把手。路径这条路走不通，就改判
        // "课件从关到开"这个边沿：只要上一轮收到的是 DeckOpen=false、这一轮变成 true，
        // 不管文件叫什么，都算新的一轮（同一份课件被重新打开也确实该再弹一次）。
        bool deckWasOpen;
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
            // 平台护栏：SetApartmentState 在非 Windows 上抛 PlatformNotSupportedException ——
            // 也就是说 Linux 上「构造这张卡片」本身就会炸。这里只在 Windows 上设 STA；
            // 真正的窗口创建由 Start() 拦住（非 Windows 不会走进那个线程）。
            if (PlatformGuard.IsWindows) thread.SetApartmentState(ApartmentState.STA);
        }

        /// <summary>Raised on the overlay thread when the teacher clicks the page number.</summary>
        public event Action? JumpRequested;

        /// <summary>
        /// 历史面板里点了一条记录（浮层线程）。参数是「哪份文件 + 要跳到的页码」；真正的
        /// COM / MCP 调用由服务转给引擎的工作线程，浮层线程不碰。
        /// 文件夹面板可以跨文件翻历史，所以文件必须跟着页码一起走。
        /// </summary>
        public event Action<string, int>? HistoryJumpRequested;

        /// <summary>
        /// 老师把卡片拖到新位置并松手时触发（浮层线程）：参数是"贴不贴右边"和竖直百分比。
        /// 宿主把这两个值写进设置，下次启动就从这个位置显示。
        /// </summary>
        public event Action<bool, int>? PositionChanged;

        /// <summary>
        /// 点了右上角的齿轮（浮层线程）。服务收到后切到 Avalonia UI 线程打开控制面板：
        /// 卡片跑在自己的 Win32 线程上，不能在这里直接 new Window（会抛 Call from invalid thread）。
        /// </summary>
        public event Action? PanelRequested;

        /// <summary>
        /// 课前预备态里点了「打开并接着讲」（或者整张预备态卡片）：参数是课件真实路径与上次停下
        /// 的页码（浮层线程）。打开文件交给服务走系统文件关联（Process.Start + UseShellExecute）——
        /// 那是外部课件，不是插件宿主导航，卡片线程不碰进程。
        /// </summary>
        public event Action<string, int>? OpenDeckRequested;

        public void Start()
        {
            // 平台护栏（要求 4 的「NativeOverlayCard 的窗口创建」）：窗口、消息循环、
            // GDI 绘制全部是 user32/gdi32。非 Windows 上一行都不做，只留一行日志 ——
            // 卡片线程不启动，IsCreated 保持 false，Dispose 也就没有窗口可 Post。
            if (!PlatformGuard.IsWindows)
            {
                lastError = "platform: skipped (non-windows) overlay-card";
                PlatformGuard.Announce("overlay-card:start", m => Log?.Invoke(m));
                return;
            }
            thread.Start();
        }

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
            // 2026-10-24 审出的真 bug：这里直接读 now.DeckOpen，而服务端要等引擎第一次推状态
            // 才会 Apply()，Arm 之后 1.5 秒就有人来调 Diag() 写诊断日志 —— pending 还是 null，
            // 于是 Diag() 本身抛 NullReferenceException，最需要日志的时候日志正好没了。
            CardState s = now ?? new CardState();
            return string.Format(
                "hwnd=0x{0:X} pump=0x{1:X} expanded={2} dpi={3} card={4}x{5} tab={6} pill={7}x{8}@{9} workRight={10} workTop={11} workH={12} paint={13}{14}{15}",
                hwnd.ToInt64(), pumpHwnd.ToInt64(), expanded, dpi,
                cardWidth, cardHeight, tabWidth,
                pill.R - pill.L, pill.B - pill.T, pill.T,
                screenRight, screenTop, screenHeight,
                paintCount,
                paintError.Length > 0 ? "  << paint error: " + paintError : "",
                lastError.Length > 0 ? "  << " + lastError : "")
                + " | state: shown=" + shown + " deck=" + s.DeckOpen + " suiteOnly=" + s.SuiteOnly
                + " showing=" + s.Showing + " applies=" + applyCount + " file=" + s.FileName
                // hist/prev 是「历史 · N 次」入口与「第 N 页」的来源：入口看不见时，先看这两个数
                // 是不是 0（键查不到台账），再看 card 高度有没有随 hist>0 长高（排版算错）。
                + " hist=" + HistoryTotal(s) + "/" + HistoryRows(s)
                + " files=" + (s.Files == null ? 0 : s.Files.Count) + "/" + FileRows(s)
                + " prev=" + s.Previous + (historyPanel ? " panel" : "") + (filePanel ? " filesPanel" : "")
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

        public void Configure(CardAppearance appearance, bool showOnStart, bool autoHide, int seconds, bool showPanel = true)
        {
            look = appearance ?? CardAppearance.Resolve(null, null);
            autoShow = showOnStart;
            autoHideEnabled = autoHide;
            autoHideSeconds = seconds < 5 ? 5 : (seconds > 600 ? 600 : seconds);
            showPanelButton = showPanel;
            RestartHideTimer();
            Wake(WM_APP_APPLY);   // 设置页里刚改的颜色/大小要立刻可见，不能等下一次翻页
        }

        internal struct TopIconLayout
        {
            public Rect32 GearIcon, GearHit;
        }

        /// <summary>
        /// 右上角齿轮（控制面板入口）的几何（纯函数；测试工程反射调用，在 96/144 dpi 下验命中区）。
        /// 2026-10-24 改版：文件夹图标删了（卡片上不再有「课件库」入口，代码保留但暂时不可达），
        /// 齿轮占它原来的右上角位置；右上角那个收起箭头也搬去了左边缘的竖带（见 RailRect）。
        /// 图标顶端对齐标题行中线，命中区四周外扩 6px 免瞄准，但顶端仍压在客户区里（144 dpi 也不为负）。
        /// </summary>
        internal static TopIconLayout ComputeTopIcons(int width, int dpi, bool showPanel, bool onRight = false)
        {
            int S(int v) { return (int)Math.Round(v * dpi / 96.0); }
            var layout = new TopIconLayout();
            if (!showPanel || width <= 0) return layout;
            int icon = S(FolderIconSize);
            int iconY = S(CardPadTop96) + (S(TitleStep96) - icon) / 2;
            int iconX = width - S(onRight ? ContentPadLeft96 : ContentPadRight96) - icon;
            layout.GearIcon = new Rect32(iconX, iconY, iconX + icon, iconY + icon);
            layout.GearHit = new Rect32(iconX - S(6), iconY - S(6), iconX + icon + S(6), iconY + icon + S(6));
            return layout;
        }

        /// <summary>
        /// 贴着卡片左边缘、竖贯整张卡片的那条收起把手，也是「缩小成药丸」的按钮
        /// （老师原话：「把他放在悬浮窗最左边竖着跟悬浮窗的宽一样长」）。
        /// 整条高度都能点；拖动位置仍然是移动窗口的手势（超过 4px 就算拖动，不算点击）。
        /// </summary>
        internal static Rect32 RailRect(int width, int height, int dpi, bool onRight = false)
        {
            int S(int v) { return (int)Math.Round(v * dpi / 96.0); }
            if (width <= 0 || height <= 0) return new Rect32(0, 0, 0, 0);
            int railW = Math.Min(S(HandleWidth96), width);
            // 2026-10-24：竖带放在卡片「靠屏幕外侧」那条边上——贴右时贴右边缘、贴左时贴左边缘。
            // 这样收起后的药丸既能和竖带落在同一条竖线，又能紧贴屏幕边（老师的两条要求同时成立）。
            return onRight
                ? new Rect32(width - railW, 0, width, height)
                : new Rect32(0, 0, railW, height);
        }

        /// <summary>进度比例 = 已讲页数 / 总页数，夹到 0..1：超过总页数（老师手动翻到片尾）算 100%。</summary>
        internal static double ProgressRatio(int page, int total)
        {
            if (total <= 0 || page <= 0) return 0;
            if (page >= total) return 1;
            return page / (double)total;
        }

        /// <summary>展开卡片底部那条横向进度条的填充宽度（从左往右）。</summary>
        internal static int ProgressFillWidth(int width, int page, int total)
        {
            if (width <= 0) return 0;
            int w = (int)Math.Round(width * ProgressRatio(page, total));
            if (w < 0) w = 0;
            if (w > width) w = width;
            // 讲了一页也要看得见：算出 0 但确实讲过时至少留 2px，否则会被圆角整段吃掉。
            if (w == 0 && page > 0 && total > 0) w = Math.Min(width, 2);
            return w;
        }

        /// <summary>收起药丸里那条竖向进度条的填充高度（从下往上）。</summary>
        internal static int PillFillHeight(int height, int page, int total)
        {
            if (height <= 0) return 0;
            int hh = (int)Math.Round(height * ProgressRatio(page, total));
            if (hh < 0) hh = 0;
            if (hh > height) hh = height;
            if (hh == 0 && page > 0 && total > 0) hh = Math.Min(height, 2);
            return hh;
        }

        /// <summary>
        /// 药丸里画不画那两行竖排数字（2026-10-25 用户第 3 条「设置里记得加悬浮窗缩成药丸时页码关闭」）。
        /// textLines 是两行页码的字符总数：一页都没打开时当然也不画。
        /// 单独抽成纯函数，好让离线 harness 直接断言"开关关掉就一个字都不画"。
        /// </summary>
        internal static bool PillPageTextEnabled(CardAppearance look, int textLines)
        {
            if (look == null || !look.ShowPillPageNumbers) return false;
            return textLines > 0;
        }

        /// <summary>
        /// 这一帧药丸进度条该用哪个颜色：返回 {r,g,b}；返回 null = 没有匹配到科目，
        /// 调用方用主题强调色。2026-10-25 用户第 9 条「每科改颜色」。
        /// </summary>
        internal static int[] PillBarAccent(CardAppearance look, CardState st)
        {
            if (look == null || st == null) return null;
            int r, g, b;
            if (!look.PillAccentFor(PillSubject(st), out r, out g, out b)) return null;
            return new int[] { r, g, b };
        }

        /// <summary>
        /// 当前课件属于哪一科。不改 SessionEngine 的字段（别的改动正在动它），只用现成的两处信息：
        /// 1) CardState.Files 里 IsCurrent 的那一项 —— Subject 是台账里可靠的科目名；
        /// 2) 退一步用 CardState.Context —— LessonTag 拼的「科目 · 第 N 节」，取 " · " 之前那截。
        /// 都拿不到就返回空串，调用方回退主题强调色（"未标注科目"也算拿不到）。
        /// </summary>
        internal static string PillSubject(CardState st)
        {
            if (st == null) return "";
            var files = st.Files;
            if (files != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    CardFileItem f = files[i];
                    if (f == null || !f.IsCurrent) continue;
                    string subject = f.Subject == null ? "" : f.Subject.Trim();
                    // "未标注科目"是 CardFileGroups 的占位名，不是一个真的科目，别拿它染色；
                    // 但也别就此放弃 —— 往下走，Context 里可能还带着真正的科目名。
                    if (string.Equals(subject, CardFileGroups.Unmarked, StringComparison.Ordinal)) break;
                    if (subject.Length > 0) return subject;
                }
            }
            string ctx = st.Context == null ? "" : st.Context.Trim();
            int cut = ctx.IndexOf(" · ", StringComparison.Ordinal);
            if (cut > 0) return ctx.Substring(0, cut).Trim();
            // 课表没排到节次时 Context 就是光秃秃的科目名（LessonTag.Context 只有 Subject 时才非空），
            // 这时整串就是科目；"第 3 节"这种时段名会被 LooksLikeSubject 挡掉。
            if (ctx.Length > 0 && CardFileList.LooksLikeSubject(ctx)) return ctx;
            return "";
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
            suppressShow = false;
            previewUntil = DateTime.UtcNow.AddSeconds(20);
            Wake(WM_APP_APPLY);
        }

        /// <summary>
        /// 自动化「隐藏悬浮卡片」：只把卡片收起来，引擎照旧记台账。
        /// 之前这条走 Disarm，卡片一收这节课从此不记，用户报过"点了隐藏就不记了"。
        /// </summary>
        public void HideForAutomation()
        {
            lock (gate) { suppressShow = true; }
            Wake(WM_APP_APPLY);
        }

        /// <summary>卡片当前是否真的显示在屏幕上（自动化用来决定"显示"还是"隐藏"）。</summary>
        public bool IsHidden
        {
            get { lock (gate) { return suppressShow; } }
        }

        // ---- ICardSink 的语义化别名 -------------------------------------------
        // 服务只跟 ICardSink 说话；Windows 这边的 Show/Hide 就落在上面两个既有方法上，
        // 绘制、消息循环、自动隐藏计时一行没动（Linux 卡片见 Ui/LinuxCardWindow.cs）。

        void ICardSink.Show() { Expand(); }

        void ICardSink.Hide() { HideForAutomation(); }

        Action<string>? ICardSink.Log
        {
            get { return Log; }
            set { Log = value ?? (_ => { }); }
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
            // 2026-10-24 改版：收起后的药丸要和展开态那条左边缘竖带落在同一条线上
            // （老师原话「把他放在悬浮窗最左边竖着跟悬浮窗的宽一样长」），所以窗口 x 在两种状态
            // 下都不动，收起时只是把可见区域收窄到 tabWidth（见 ApplyWindowRegion）。
            // 代价：贴右（默认）时收起药丸停在卡片左边缘、离右屏边还有 cardWidth-tabWidth，
            // 不再像以前那样紧贴屏幕边——这是「药丸和竖带同一条线」的直接后果，报告里写明了。
            if (!expanded)
            {
                // 收起态窗口就是药丸本身：宽度窄、长度按设置（可超过卡片高度）。
                var pillRect = CollapsedPill();
                int pw = pillRect.R - pillRect.L, ph = pillRect.B - pillRect.T;
                int px = look.OnRight ? screenRight - pw : screenLeft;
                int py = screenTop + (int)Math.Round((screenHeight - ph) * look.VerticalPercent / 100.0);
                if (py < screenTop) py = screenTop;
                SetWindowPos(hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
                return;
            }
            int x = look.OnRight ? screenRight - cardWidth : screenLeft;
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

        /// <summary>
        /// 面板二（历史）的正文。2026-10-24 功能3 从 Paint 里整块搬出来，为的是能在测试工程里
        /// 直接对着内存 DC 画一遍、存成 PNG 逐张看排版 —— 卡片是原生窗口画的，平时只能靠老师
        /// 上课时瞄一眼，动文案最容易在这里把行挤爆。GdiCanvas / Color32 都是私有嵌套类型，
        /// 所以这里只能是私有方法，测试工程用反射调（bindingflags NonPublic | Instance）。
        ///
        /// 抬高位、行高、返回位一律由调用方按 ContentHeight96 那套算好传进来，这里不许自己加行：
        /// 面板高度是提前算出来的（命中矩形、贴边位置、15 秒自动收起都指着它）。
        /// </summary>
        void DrawHistoryPanel(GdiCanvas g, CardState st, int w, int pad, int inner,
                              int y, int lineSmall, int lineJump, int stepJump,
                              Color32 accent, Color32 muted)
        {
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
            // 行框右端会压在竖带上（w-pad+7 对 rail.L），而 OnClick 先判竖带：
            // 不裁的话点历史行末尾等于"收起卡片"，行本身点不中（2026-10-24 审出，同 ClipHit）。
            var rail = RailRect(w, cardHeight, dpi, look.OnRight);
            var gearHit = ComputeTopIcons(w, dpi, showPanelButton, look.OnRight).GearHit;
            for (int i = 0; i < rows; i++)
            {
                var item = items[i];
                var hit = ClipHit(new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2)), rail, gearHit);
                historyRowHits[i] = hit;
                historyRowSlides[i] = item.Slide;
                // 最近一条用强调色，其余弱化——一眼看出“上次停在哪”。
                g.Text(FitHistory(item, g, inner), g.FontTiny, item.IsLatest ? accent : muted,
                       new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                y += stepJump;
            }

            var back = ClipHit(new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2)), rail, gearHit);
            backHit = back;
            g.Text("\u2039 返回", g.FontTiny, accent,
                   new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
        }

        /// <summary>
        /// 挑一个塞得进 inner 像素宽的历史行写法：按 <see cref="CardHistoryItem.Labels"/> 从最全
        /// 往最省试（先砍时分、节次和页码必须留着），都塞不进才截断最全那条。
        /// 一行就一行，绝不换行、绝不溢出到卡片外面。
        /// </summary>
        string FitHistory(CardHistoryItem item, GdiCanvas g, int inner)
        {
            if (item == null) return "";
            var variants = item.Labels;
            if (variants != null)
            {
                for (int i = 0; i < variants.Length; i++)
                {
                    string v = variants[i];
                    if (string.IsNullOrEmpty(v)) continue;
                    if (g.TextWidth(v, g.FontTiny) <= inner) return v;
                }
            }
            string all = item.Label;
            if (string.IsNullOrEmpty(all) && variants != null && variants.Length > 0) all = variants[0];
            if (string.IsNullOrEmpty(all)) return "";
            return g.Ellipsis(all, g.FontTiny, inner);
        }

        /// <summary>
        /// 「还剩 N 页 · 约 M 分钟」在右列放不下时逐级退让（2026-10-24）：先砍「约」，再把「还剩」
        /// 缩成「剩」，都塞不进才交给 Ellipsis 截断。
        /// 起因是出图看到 124px 的右列把「还剩 17 页 · 约 17 分钟」截成了「还剩 17 页 · 约 17…」——
        /// 剩下半句「分钟」才是老师判断要不要快进的关键，宁可按级减字，也不能截在数字后面。
        /// </summary>
        string FitRemain(GdiCanvas g, string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text ?? "";
            if (g.TextWidth(text, g.FontSub2) <= maxWidth) return text;

            string t = text.Replace(" \u00B7 约 ", " \u00B7 ");
            if (g.TextWidth(t, g.FontSub2) <= maxWidth) return t;

            if (t.StartsWith("还剩 ", StringComparison.Ordinal))
            {
                t = "剩 " + t.Substring(3);
                if (g.TextWidth(t, g.FontSub2) <= maxWidth) return t;
            }
            return t;
        }

        /// <summary>面板一里一共几份课件（不是一页几行）。快照上限由 CardFileList.MaxSnapshot 决定。</summary>
        int FileRows(CardState st)
        {
            if (st == null || st.Files == null) return 0;
            return Math.Min(st.Files.Count, CardFileList.MaxSnapshot);
        }

        // ---- 面板一的分组 / 筛选 / 分页（2026-10-24 功能2） ----------------------

        List<string> Subjects(CardState st)
        {
            if (st == null || st.Files == null) return new List<string>();
            return CardFileGroups.Subjects(st.Files);
        }

        /// <summary>筛选后（按科目分组、组内最近优先）的完整列表。分页只在这个列表上切。</summary>
        List<CardFileItem> FileItems(CardState st)
        {
            if (st == null || st.Files == null) return new List<CardFileItem>();
            return CardFileGroups.Filter(st.Files, fileFilter);
        }

        /// <summary>筛选条要不要画：只有一种科目时没有可筛的东西，画出来只是白占一行高度。</summary>
        bool HasFilterBar(CardState st)
        {
            return Subjects(st).Count >= 2;
        }

        int ChipWidth(GdiCanvas g, string label)
        {
            int wpx = g.TextWidth(label, g.FontTiny) + Scaled(14);
            int max = Scaled(84);
            return wpx > max ? max : wpx;
        }

        /// <summary>
        /// 筛选条实际放哪几项：从「全部」开始横着塞，塞不下就停在最后一个塞得下的 chip 上 ——
        /// 不换行、也不做「更多」（多一层弹窗在这个 208 像素宽的面板里没法看）。
        /// 例外：当前选中的科目要是被挤到可见范围外，就用它顶掉最后一个 chip，
        /// 否则老师选了科目之后就再也点不回「全部」了。
        /// </summary>
        List<string> FilterChips(GdiCanvas g, List<string> subjects, int inner)
        {
            var keys = new List<string>();
            if (subjects.Count < 2) return keys;   // 只有一种科目：没有可筛的
            keys.Add("");
            int used = ChipWidth(g, "全部");
            int gap = Scaled(4);
            bool selectedVisible = fileFilter.Length == 0;
            for (int i = 0; i < subjects.Count && keys.Count < MaxFilterChips; i++)
            {
                int cw = ChipWidth(g, subjects[i]);
                if (used + gap + cw > inner) break;
                used += gap + cw;
                keys.Add(subjects[i]);
                if (string.Equals(subjects[i], fileFilter, StringComparison.Ordinal)) selectedVisible = true;
            }
            if (!selectedVisible && fileFilter.Length > 0)
            {
                if (keys.Count > 1) keys[keys.Count - 1] = fileFilter;
                else keys.Add(fileFilter);
            }
            return keys;
        }

        /// <summary>
        /// 面板一的总高度（96 dpi）。行高全部来自 File*Step，Paint 里的 <c>y += …</c> 必须和它一一对应。
        /// <paramref name="rows"/> 是课件行数，<paramref name="groups"/> 是科目标题行数，
        /// <paramref name="pages"/> 决定要不要分页条。
        /// </summary>
        int FilePanelHeight96(int rows, int groups, int pages, bool filterBar)
        {
            return 10
                 + FileHeadStep
                 + (filterBar ? FileFilterStep : 0)
                 + Math.Max(1, rows) * FileRowStep   // 空台账也留一行「还没有记录」
                 + groups * FileGroupStep
                 + (pages > 1 ? FilePagerStep : 0)
                 + FileBackStep
                 + 10;
        }

        /// <summary>
        /// 「哪一份？」面板的高度（96 dpi 稿子像素）。和 FilePanelHeight96 同一套写法：
        /// 上边距 10 + 抬头一行 + 每行 SameNameRowStep + 返回 + 下边距 10。
        /// 没有筛选条也没有分页条 —— 候选最多 <see cref="CardFileList.MaxSameName"/> 份，
        /// 一屏放得下，翻页只会让老师多点一次。
        /// </summary>
        int SameNameHeight96(int rows)
        {
            return 10
                 + FileHeadStep
                 + Math.Max(1, rows) * SameNameRowStep   // 候选算不出来时也留一行，别把窗口压成一条线
                 + FileBackStep
                 + 10;
        }

        /// <summary>
        /// 「哪一份？」面板此刻到底在问哪一行：按归并键从最新快照里重新找一遍。
        /// 为什么不直接信 sameNameFile 那个引用 —— 台账一刷新 BuildState 就造一批新的
        /// CardFileItem，旧引用上的 SameName 还是上一轮的候选（文件可能刚被删掉，点下去打不开）。
        /// 找不到、或者候选已经不足两份，就返回 null，调用方据此把面板收掉。
        /// </summary>
        CardFileItem? SameNameTarget(CardState? st)
        {
            if (!sameNamePanel || sameNameFile == null || st == null || st.Files == null) return null;
            string key = sameNameFile.Key ?? "";
            for (int i = 0; i < st.Files.Count; i++)
            {
                var f = st.Files[i];
                if (f == null) continue;
                if (!string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                // 另一份已经没了（文件被删/被移走）：没必要再问，直接回课件库。
                if (f.SameName == null || f.SameName.Count < 2) return null;
                return f;
            }
            return null;
        }

        /// <summary>
        /// 「哪一份？」面板左边那截文件夹路径。FolderHint 是「…\桌面\classisland插件」这种两段式，
        /// 宽度不够时先退到只剩最后一段：同名文件之间唯一的区别就是位置，而位置里最有区分度的
        /// 是最后一段。所以宁可把前面省掉，也不能用尾部省略号把最后一段吃掉 ——
        /// 「…\桌面\class…」和「…\课件\class…」在面板上会长得一模一样，老师只能靠猜。
        /// </summary>
        string FitFolderHint(GdiCanvas g, string hint, int width)
        {
            if (string.IsNullOrEmpty(hint) || width <= 0) return "";
            if (g.TextWidth(hint, g.FontSmall) <= width) return hint;
            // 放不下就一级一级拆父目录：「…\桌面\classisland插件」→「…\classisland插件」。
            // 从保留两段开始试（尽量多留信息），最后只剩末级目录名；中间的分隔符统一换成 "…"，
            // 让老师一眼看出"前面还有路径，被省掉了"。
            string[] parts = hint.Split('\\');
            int keepMax = Math.Min(2, parts.Length);
            for (int keep = keepMax; keep >= 1; keep--)
            {
                string tail = "\u2026\\" + string.Join("\\", parts, parts.Length - keep, keep);
                if (g.TextWidth(tail, g.FontSmall) <= width) return tail;
            }
            // 连末级目录名都放不下（路径本身就长得夸张）：正常省略号截断，至少开头几个字还在。
            return g.Ellipsis("\u2026\\" + parts[parts.Length - 1], g.FontSmall, width);
        }

        /// <summary>
        /// 一页放几行：正常就是 8 行（用户要求「每页最多 8 条」）。只有一种极端情况会减行 ——
        /// 屏幕工作区比整块面板还矮（多科目 + 小屏 + 高缩放）。与其让最后几行画到屏幕外面去，
        /// 不如一页少放几行、多翻一页；工作区够用时这个函数永远返回 8，分页边界不受影响。
        /// </summary>
        int FilePageSize(CardState st)
        {
            var all = FileItems(st);
            int subjects = Subjects(st).Count;
            bool filterBar = HasFilterBar(st);
            for (int size = MaxFileRows; size >= 1; size--)
            {
                int pages = CardFileGroups.PageCount(all.Count, size);
                int h = FilePanelHeight96(size, Math.Min(size, subjects), pages, filterBar);
                if (screenHeight <= 0 || Scaled(h) <= screenHeight) return size;
            }
            return 1;
        }

        /// <summary>
        /// 当前这一页要画的分组（页码已经夹进有效范围）。Paint 和 ContentHeight96 都走这里，
        /// 于是「高度表」和「实际画了哪些行」不可能各算一套。
        /// </summary>
        List<CardFileGroups.FileGroup> FilePageGroups(CardState st)
        {
            var all = FileItems(st);
            int size = FilePageSize(st);
            filePage = CardFileGroups.ClampPage(filePage, all.Count, size);
            return CardFileGroups.Page(all, filePage, size);
        }

        /// <summary>
        /// 台账变了之后把筛选和页码夹回有效范围：课件被清掉、科目从课表里消失时，
        /// 旧的筛选会让面板显示一张空表，老师会以为课件丢了。
        /// </summary>
        void ClampFileView(CardState st)
        {
            if (fileFilter.Length > 0 && !Subjects(st).Contains(fileFilter)) fileFilter = "";
            var all = FileItems(st);
            filePage = CardFileGroups.ClampPage(filePage, all.Count, FilePageSize(st));
        }

        /// <summary>翻页：只改页码，不动筛选；到边界就不动（按钮画成灰的，点了也不会跳到页外）。</summary>
        void TurnFilePage(CardState st, int delta)
        {
            var all = FileItems(st);
            int size = FilePageSize(st);
            int page = CardFileGroups.ClampPage(filePage + delta, all.Count, size);
            if (page == filePage) return;
            filePage = page;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();
            Relayout();
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
            // 功能7（2026-10-24）：课前预备态的行高表，和 Paint 里的 DrawReady 一一对应：
            // 上边距 12 + 标题 20 + 「上次停在第 N 页」22 +（有剩余页时）「还有 M 页没讲」22
            // + 按钮间距 8 + 按钮 32 + 下边距 12。96dpi 下是 128 / 106，都在 210 的上限以内。
            // 刻意不跟 look.CardHeight / CardScale 走：预备态的行数比正常卡片少，跟着老师给
            // 正常卡片调的高度走会在按钮下面留一条白；窗口高必须正好等于 Scaled(它)。
            if (st != null && st.ReadyOnly)
                return 12 + TitleStep96 + 22 + (st.ReadyRemaining > 0 ? 22 : 0) + BtnGapTop96 + BtnHeight96 + 12;

            int h = 10;
            // 面板的抬头自己就是标题（面板二是被点的那份文件名，面板三问的是「哪一份」），
            // 再画一行“当前课件”会张冠李戴；旧的单文件历史面板保持原样，仍在顶部显示当前文件名。
            bool showTopName = look.ShowFileName && !filePanel && !sameNamePanel && !(historyPanel && selectedFile != null);
            if (showTopName) h += 22;
            if (sameNamePanel)
            {
                // 抬头 + 每份候选一行 + 返回 + 下边距（高度算法见 SameNameHeight96）
                var target = SameNameTarget(st);
                return SameNameHeight96(target == null ? 0 : target.SameName.Count);
            }
            if (filePanel)
            {
                // 抬头 +（有得筛时的）筛选条 + 每页课件行（空台账也留一行「还没有记录」）
                // + 科目标题行 +（多页时的）分页条 + 返回 + 下边距
                var page = FilePageGroups(st);
                int rows = CardFileGroups.CountItems(page);
                int pages = CardFileGroups.PageCount(FileItems(st).Count, FilePageSize(st));
                return FilePanelHeight96(rows, page.Count, pages, HasFilterBar(st));
            }
            if (historyPanel)
            {
                // 抬头 + 每条一行 + 返回 + 下边距
                return h + 26 + HistoryRows(st) * 26 + 26 + 10;
            }
            // 正常卡片（2026-10-24 A 方案改版）：整张卡就是五行固定版式——
            // 文件名 → 大页码 + 右侧两行 → 实心跳转按钮 → 进度条 → 最底下一行历史。
            // 高度取常量表相加的 176，不再跟着 ShowJumpLine / ShowStatusLine / 有没有历史增减：
            // 老师的版式里这五行一条都不能少，缺哪行就留一块白；跟着内容变高变矮还会在点跳转的
            // 瞬间让卡片一跳一跳。没有历史时那一行改显示课表上下文或提示（BitsText），
            // 诊断信息不丢，只是换了文案。
            return CardContent96;
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
                // 预备态（功能7）和面板一样：它的行数比正常卡片少，跟着 CardHeight 走会留白。
                if (historyPanel || filePanel || sameNamePanel || (st != null && st.ReadyOnly)) h96 = need;
                // 两边的单位不同：need 是 96dpi 行高表，h96 已经含老师的 CardScale。统一乘一遍 dpi 再比。
                else if (Scaled(need) > Scaled(h96)) h96 = need;
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
                    // 收起态只有贴左时药丸还压在屏幕左边缘；贴右时药丸停在卡片左边缘、离屏边
                    // 还有 cardWidth-tabWidth（见 Place），两边都不贴边，整颗胶囊保持全圆角。
                    IntPtr square = IntPtr.Zero;
                    if (look.OnRight)
                        square = CreateRectRgn(visible.R - r, visible.T, visible.R + 1, visible.B + 1);
                    else if (!look.OnRight)
                        square = CreateRectRgn(visible.L, visible.T, visible.L + r + 1, visible.B + 1);
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
        /// 收起态露出来的那条药丸。2026-10-24 老师要求「缩成药丸时默认长一点」，
        /// 长度从写死的 96 改成设置里的 CollapsedTabLength（默认 200，96..480 可调），
        /// 沿屏幕边缘方向；垂直于屏幕边缘那条边是 PillWidth()。
        /// 仍然不占满整张卡片：上下各留 Scaled(8) 给圆角和描边，药丸不会和卡片等高。
        /// 它固定贴窗口左边缘（贴左时就是屏幕左边缘），和展开态那条收起竖带同一条线。
        /// </summary>
        /// <summary>
        /// 收起态的药丸：窗口本身就缩成药丸大小，所以坐标永远从 (0,0) 起。
        /// 长度不再被卡片高度卡住（以前上限是 cardHeight-8，设置里调 200 也只到 168，
        /// 老师看到的就是「又短又宽」），现在最长可以到 320（96 dpi 稿值）。
        /// </summary>
        int PillHeight()
        {
            int len = look.TabLength > 0 ? look.TabLength : 200;
            int ph = Scaled((int)Math.Round(len * look.CardScale));
            int min = Scaled(120), max = Scaled(320);
            if (ph < min) ph = min;
            if (ph > max) ph = max;
            return ph;
        }

        Rect32 CollapsedPill()
        {
            int pw = PillWidth();
            int ph = PillHeight();
            // 细长条的宽度上限：再宽就不像贴在边上的一条线了。
            int cap = Scaled(60);
            if (pw > cap) pw = cap;
            return new Rect32(0, 0, pw, ph);
        }

        /// <summary>
        /// 药丸宽度（含兜底）：ApplyMetrics 还没跑过时 Diag() 也会来问，那时 pillWidth 还是 0，
        /// 用「设置宽度 / 58」顶上，绝不能让 CollapsedPill 返回 0 宽（窗口会被裁成一条缝）。
        /// </summary>
        int PillWidth()
        {
            if (pillWidth > 0) return pillWidth;
            return Math.Max(tabWidth, Scaled(PillMinWidth96));
        }

        /// <summary>
        /// 收起药丸的宽度（垂直于屏幕边缘那条边）。药丸里要竖着放「已翻 N」「共 M」两行中文，
        /// 光靠 TabWidth（32）或者 58 都装不下 12pt 的「已翻 888」（离屏实测：96dpi 49px、
        /// 144dpi 63px），再加上左右各 6px 内边距，所以取 设置宽度 / 58 下限 / 实测文字宽 三者最大。
        /// 文字按三位数预留，换一份课件时药丸不会忽宽忽窄。只在 ApplyMetrics 里算一次（见 pillWidth）。
        /// </summary>
        int ComputePillWidth()
        {
            // 2026-10-24 晚：药丸改成「细长条 + 竖排数字」，每行只有一个字，
            // 所以宽度按单个数字量，不再按整句「已翻 888」预留。
            int need = MeasurePillText("8");
            // 左右各 6px 内边距，再多给 2px：三位数时内宽刚好等于字宽的话，字体 hinting 差一像素
            // DrawTextW 就会给你加个省略号（「已翻 8…」），留一点余量更稳。
            need += Scaled(6) * 2 + Scaled(2);
            int w = Math.Max(tabWidth, Scaled(PillMinWidth96));
            if (need > w) w = need;
            // 再宽就不像贴在屏幕边上的东西了：卡片总共才 cardWidth 宽。
            int cap = cardWidth - Scaled(8);
            if (w > cap) w = cap;
            return Math.Max(w, Math.Min(Scaled(PillMinWidth96), cap));
        }

        /// <summary>
        /// 药丸里那两行字的文案：蓝字「已翻 N」= 这份课件已经翻到第几页，红字「共 M」= 总页数。
        /// </summary>
        static string PillPageText(bool shown, int page)
        {
            return (shown ? "已翻 " : "共 ") + page;
        }

        /// <summary>
        /// 量一行药丸文字在当前 dpi 下的像素宽度（和 GdiCanvas 同一个字体族、同一种字高）。
        /// 只给 ComputePillWidth 用：文字比药丸宽会被 DrawTextW 截成省略号，先量再定宽最省事。
        /// </summary>
        int MeasurePillText(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string face = string.IsNullOrWhiteSpace(look.FontFamily) ? "Microsoft YaHei UI" : look.FontFamily;
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return 0;
            IntPtr font = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                font = CreateFontW(GdiCanvas.FontHeight(GdiCanvas.PillPointSize(look), dpi), 0, 0, 0, 400,
                    0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 5 /*CLEARTYPE*/, 0, face);
                if (font == IntPtr.Zero) return 0;
                old = SelectObject(dc, font);
                RECT rc = new RECT { Left = 0, Top = 0, Right = 4000, Bottom = 200 };
                // DT_SINGLELINE | DT_CALCRECT：这次只量不画，rc 回来就是包住这行字的最小矩形。
                DrawTextW(dc, text, text.Length, ref rc, 0x00000020 | 0x00000400);
                return rc.Right - rc.Left;
            }
            catch { return 0; }
            finally
            {
                // 先还字体再删：被选着的 GDI 对象删不掉，句柄就漏了（见 GdiCanvas.savedPen 的注释）。
                if (font != IntPtr.Zero) { if (old != IntPtr.Zero) SelectObject(dc, old); DeleteObject(font); }
                DeleteDC(dc);
            }
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
                    // 药丸的透明度跟着设置走（默认 80%）：药丸里有两行页码，50% 时白底字几乎看不见。
                alpha = look.PillAlpha > 0 ? look.PillAlpha : 204;
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
            // 药丸宽度只有收起态用，但它必须在这里定稿：Paint 画的和 ApplyWindowRegion 裁的
            // 是同一个数，两处各算一次迟早会差一像素（文字就被圆角切掉一条）。
            pillWidth = ComputePillWidth();
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

        /// <summary>
        /// 把两层菜单状态一起清掉。收起卡片、课件关掉时都走这里，避免残留的选中项。
        /// 筛选和页码也在这里复位：老师收起卡片再打开，要看到的是「全部课件」第一页。
        /// </summary>
        void ResetPanels()
        {
            filePanel = false;
            historyPanel = false;
            sameNamePanel = false;
            sameNameFile = null;
            selectedFile = null;
            historyBackToFiles = false;
            fileFilter = "";
            filePage = 0;
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
            sameNamePanel = false;
            sameNameFile = null;
            selectedFile = null;
            historyBackToFiles = false;
            fileFilter = "";   // 每次从文件夹图标进来都从「全部 / 第 1 页」开始
            filePage = 0;
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

        /// <summary>
        /// 面板三「哪一份？」（2026-10-24 功能9）：这一行背后有两份以上内容可能不同的同名文件。
        /// 默认跳转路径是 ChooseJumpPath 挑出来的，凭什么挑，老师并不知道；猜错就是把他扔到
        /// 另一份课件的同一页上。所以这里把候选列出来让他自己点。
        /// 候选在引擎里已经筛过（存在、有页码、指纹证明不是同一份拷贝），这里只负责显示。
        /// </summary>
        void OpenSameNamePanel(CardFileItem f)
        {
            if (f == null || f.SameName == null || f.SameName.Count < 2) return;
            sameNamePanel = true;
            sameNameFile = f;
            filePanel = false;      // 面板三顶掉课件库；返回时再回那一页（筛选/页码都不动）
            historyPanel = false;
            selectedFile = null;
            historyBackToFiles = false;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();     // 老师主动点开的面板，15 秒倒计时重新开始
            Relayout();
        }

        /// <summary>
        /// 「‹ 返回」：回课件库，而且筛选和页码原样保留 —— 老师刚才就是从那一页点进来的，
        /// 回来还被扔回「全部 / 第 1 页」的话，找同一份课件得再翻一次。所以直接置 filePanel，
        /// 不走 OpenFilePanel（那个函数会按"从文件夹图标进来"的语义把筛选复位）。
        /// </summary>
        void CloseSameNamePanel()
        {
            if (!sameNamePanel) return;
            sameNamePanel = false;
            sameNameFile = null;
            filePanel = true;
            Relayout();
        }

        /// <summary>
        /// 按归并键在快照里找那一行。分组 + 筛选 + 分页之后，行号早就不是 st.Files 的下标了，
        /// 只认键；找不到（台账刚被清掉）就当没点。
        /// </summary>
        CardFileItem? FileByKey(CardState st, string key)
        {
            if (st == null || st.Files == null || string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < st.Files.Count; i++)
            {
                var f = st.Files[i];
                if (f != null && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        /// <summary>
        /// 从面板一某一行进入那份文件的历史（面板二）。按归并键找，不按下标：
        /// 分组+筛选+分页之后，第 i 行早就不是 st.Files[i] 了，按下标会打开错的那份课件。
        /// </summary>
        void OpenFileHistory(CardState st, string key)
        {
            if (st == null || st.Files == null || string.IsNullOrEmpty(key)) return;
            CardFileItem? f = FileByKey(st, key);
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
                double idle = (DateTime.UtcNow - lastActivity).TotalSeconds;
                if (idle < autoHideSeconds - 0.5)
                {
                    // 2026-10-24 审出的真 bug：Timer 是一次性的（Change 用的 InfiniteTimeSpan），
                    // 这里原来直接 return，等于"这一轮自动收起被吃掉"。老师刚在卡片上点过一下
                    // （lastActivity 被刷新），15 秒的定时器照点响，一看还没闲够就走人 ——
                    // 之后再也没有人重新排，卡片就一直贴在屏幕中间不收了。
                    // 补排一次，而且只排剩下的那点时间，别重新数 15 秒（否则每次点击都往后推）。
                    var t = hideTimer;
                    if (t != null) t.Change(TimeSpan.FromSeconds(autoHideSeconds - idle), Timeout.InfiniteTimeSpan);
                    return;
                }
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
                // 一秒钟一跳的心跳：pending 还是 null 时（引擎没推过状态）就别读了，
                // 否则每秒抛一次 NRE 再被这里 catch 掉，等于这道兜底完全失效。
                if (st == null) return;
                bool want = st.DeckOpen || st.ReadyOnly || DateTime.UtcNow < previewUntil;
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

                    case WM_DISPLAYCHANGE:
                        // 2026-10-24 审出的真 bug：教室里插拔投影仪是最常见的动作，换分辨率（比如
                        // 1920x1080 → 1024x768 镜像）时 Windows 只会广播 WM_DISPLAYCHANGE，
                        // dpi 没变就不会有 WM_DPICHANGED。原来这里没人处理：screenRight 还是旧值，
                        // 卡片按"贴右 = screenRight - cardWidth"算出来直接落在新屏幕外面（x=1684），
                        // 老师那边表现就是"插上投影仪卡片不见了"。
                        ApplyWorkArea(WorkingArea());
                        ApplyLook();
                        Place();
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;

                    case WM_SETTINGCHANGE:
                        // 任务栏自动隐藏、任务栏换边、显示缩放设置变化都会走这条广播。工作区没变就
                        // 什么都不做 —— 这条消息发得很勤，每次 SetWindowPos 会让卡片闪一下。
                        {
                            Rect32 wa = WorkingArea();
                            if (wa.L != screenLeft || wa.T != screenTop ||
                                wa.Right != screenRight || wa.B != screenTop + screenHeight)
                            {
                                ApplyWorkArea(wa);
                                ApplyLook();
                                Place();
                                InvalidateRect(hWnd, IntPtr.Zero, false);
                            }
                            return IntPtr.Zero;
                        }

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
                    {
                        // 2026-10-24 审出的真 bug：原来先 ReleaseCapture() 再判 dragging。
                        // ReleaseCapture 会同步送回一条 WM_CAPTURECHANGED，那条消息里会把
                        // dragArmed/dragging 清掉（见下面的分支）；顺序反了，拖动就永远判不出来，
                        // 松手那一下会被当成点击，把放映跳走。所以先把"这一下算什么"记下来再放开捕获。
                        bool wasArmed = dragArmed;
                        bool wasDrag = dragArmed && dragging;
                        dragArmed = false;
                        dragging = false;
                        ReleaseCapture();
                        if (wasDrag)
                        {
                            DropCard();
                            return IntPtr.Zero;   // 拖完这一下不当点击，免得顺手把放映跳走
                        }
                        if (wasArmed)
                        {
                            OnClick((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                        }
                        return IntPtr.Zero;
                    }

                    case WM_CAPTURECHANGED:
                        // 捕获被别的窗口抢走（宿主弹窗、Alt+Tab、放映窗口抢前台……）时，
                        // 后面那条 WM_LBUTTONUP 就永远不会来了。以前这里没人处理：dragArmed 卡在
                        // true，ApplyPending 里"拖动中无论如何不许隐藏"那条判断（1465 行）就一直成立
                        // —— 课件关了卡片还赖在屏幕上，而且之后每次点击都被当成拖动、跳页再也点不动。
                        // 拖到一半被抢，就把此刻的位置当成一次正常落点提交（DropCard 会记住位置）。
                        if (!quit && (dragArmed || dragging))
                        {
                            bool wasDrag2 = dragging;
                            dragArmed = false;
                            dragging = false;
                            if (wasDrag2) DropCard();
                        }
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
            // 2026-10-24 审出：引擎第一次推状态之前 pending 就是 null（Run 里刚建好窗口就会
            // 调一次 ApplyPending），原来这里直接 st.DeckOpen 抛 NRE，被窗口过程/Run 的
            // catch 吞掉，只剩 lastError 一行字。没有状态就当没这回事，等 Apply() 再来。
            if (st == null) return;
            // 只有"有课件"（正在放映，或者打开着某份课件）时卡片才该出现。待命期间（比如上课前
            // 1 分钟）还没有课件，屏幕上必须干干净净；设置页的"预览"给 20 秒豁免。
            // 功能7（2026-10-24）：课前预备态也属于"该露面"——它没有课件，但正是要提示老师
            // 打开哪份、从第几页接着讲。所以把 ReadyOnly 并进来，而不是让服务端去伪造 DeckOpen。
            bool hasDeck = st.DeckOpen || st.ReadyOnly;
            // 这一轮收到的"课件是不是开着"，和上一轮比出来的上升沿要在下面用，所以先记下来。
            // 必须用 st.DeckOpen 本身的边沿，不能用 hasDeck：hasDeck 有 8 秒防抖闩锁，
            // 会把这个"关→开"的瞬间糊掉（A 关掉后 8 秒内打开 B，hasDeck 一直是 true）。
            bool deckOpenNow = st.DeckOpen;
            bool deckJustOpened = deckOpenNow && !deckWasOpen;
            deckWasOpen = deckOpenNow;
            // 预备态和面板不共存：面板是上一份课件留下的临时菜单，课前预备时不该还停在列表上。
            if (st.ReadyOnly && (filePanel || historyPanel || sameNamePanel)) ResetPanels();
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
            // 自动化「隐藏悬浮卡片」只收卡片，不拆引擎：suppressShow 为真时即便课件还开着也不显示。
            // 以前这条走的是 Disarm，卡片一收整节课都不再记台账，属于把"收起"当成了"停用"。
            if (deckJustOpened) suppressShow = false;
            bool shouldShow = (hasDeck || preview) && !suppressShow;
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
            // 台账或科目变了之后，旧的筛选/页码可能已经越界（那门课被清掉、新记录换了科目）。
            // 必须在算高度之前夹回有效范围：夹晚了，高度按不存在的页算，面板就会空一大截。
            if (filePanel) ClampFileView(st);
            // 功能9：正在问「哪一份？」的那一行如果变了（另一份被删掉、已经不用问了），
            // 就把面板收回到课件库 —— 否则老师会盯着一张没有候选的空面板，只有一个「返回」。
            else if (sameNamePanel && SameNameTarget(st) == null)
            {
                sameNamePanel = false;
                sameNameFile = null;
                filePanel = true;
            }

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
                // 同名的另一份（或者同一份被关掉又打开）文件名和页数都一样，上面判不出来，
                // 这里补一条"课件从关到开"的上升沿。老师手动点过收回箭头之后，
                // 只要这份课件还开着，deckJustOpened 就一直是 false，不会又弹回来。
                if (deckJustOpened) expandedForDeck = false;

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

            // 控制面板齿轮：不管卡片正开着哪个面板都要能点，所以排在 filePanel/historyPanel
            // 两个分支之前（那两个分支末尾都会 return，放进里面就会被吞掉）。
            if (panelHit.R > panelHit.L && Inside(panelHit, x, y))
            {
                var panelEvent = PanelRequested;
                if (panelEvent != null) panelEvent();
                return;
            }

            // 课前预备态（2026-10-24 功能7/8）：整张卡都是「打开并接着讲」——按钮画得再大也只有
            // 32px 高，课前那两分钟老师是瞄一眼就点，不该让他对准按钮。齿轮和竖带在上面已经判过，
            // 所以这里不会把控制面板/收起抢掉。也不受 ClickToJump 开关限制：这不是"跳页"，
            // 而是打开老师自己的课件（外部文件关联，见服务里的 OpenDeckRequested 处理）。
            CardState readyState;
            lock (gate) { readyState = pending; }
            if (readyState != null && readyState.ReadyOnly)
            {
                if (readyState.ReadyFile.Length > 0 && readyState.ReadySlide > 0)
                {
                    var open = OpenDeckRequested;
                    if (open != null) open(readyState.ReadyFile, readyState.ReadySlide);
                }
                return;
            }

            // 面板三「哪一份？」（功能9）：点某一行 = 用那一份自己的路径 + 页码跳过去。
            // 和面板二一样是老师自己点开的显式菜单，不受 ClickToJump / showingCard 限制。
            // 整卡跳页的判定在最后，这里每一条都 return，所以面板开着时点空白处不会跳页。
            if (sameNamePanel)
            {
                CardState ss;
                lock (gate) { ss = pending; }
                CardFileItem? target = SameNameTarget(ss);
                for (int i = 0; i < CardFileList.MaxSameName && target != null && i < target.SameName.Count; i++)
                {
                    string? path = sameNameRowPaths[i];
                    int slide = sameNameRowSlides[i];
                    if (path == null || slide <= 0 || !Inside(sameNameRowHits[i], x, y)) continue;
                    // 先收面板再发事件：服务层处理完会推新状态回来。顺序反了，会出现
                    // 「面板还开着、卡片正文已经换了」的一帧。
                    sameNamePanel = false;
                    sameNameFile = null;
                    filePanel = false;
                    selectedFile = null;
                    historyBackToFiles = false;
                    Relayout();
                    var jumpSame = HistoryJumpRequested;
                    if (jumpSame != null) jumpSame(path, slide);
                    return;
                }
                if (Inside(backHit, x, y)) { CloseSameNamePanel(); return; }
                return;   // 面板里点空白处：不跳页，也不替老师把面板收掉
            }

            // 文件夹面板（面板一）：筛选条 > 课件行 > 翻页 > 返回。
            // 顺序就是从上到下的显示顺序：上面的行先判，点歪到别的行上时按离得近的那个来。
            if (filePanel)
            {
                CardState fs;
                lock (gate) { fs = pending; }
                FilePageGroups(fs);   // 先把页码夹到当前数据上，命中矩形才和画出来的行对得上
                for (int i = 0; i < MaxFilterChips; i++)
                {
                    string? key = filterKeys[i];
                    if (key == null || !Inside(filterHits[i], x, y)) continue;
                    if (!string.Equals(key, fileFilter, StringComparison.Ordinal))
                    {
                        fileFilter = key;
                        filePage = 0;   // 换筛选就必须回第一页，否则可能停在对方没有的页码上
                        lastActivity = DateTime.UtcNow;
                        RestartHideTimer();
                        Relayout();
                    }
                    return;
                }
                for (int i = 0; i < MaxFileRows; i++)
                {
                    string? key = fileRowKeys[i];
                    if (key == null || !Inside(fileRowHits[i], x, y)) continue;
                    // 功能9：这一行背后有几份内容可能不同的同名文件 —— 先问「哪一份？」，
                    // 不替老师挑（ChooseJumpPath 挑的那份未必是他要的，猜错就是讲错页）。
                    var same = FileByKey(fs, key);
                    if (same != null && same.SameName != null && same.SameName.Count > 1)
                    {
                        OpenSameNamePanel(same);
                        return;
                    }
                    OpenFileHistory(fs, key);
                    return;
                }
                if (pagePrevHit.R > pagePrevHit.L && Inside(pagePrevHit, x, y)) { TurnFilePage(fs, -1); return; }
                if (pageNextHit.R > pageNextHit.L && Inside(pageNextHit, x, y)) { TurnFilePage(fs, +1); return; }
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

        /// <summary>
        /// 右上角齿轮（控制面板入口）。GDI 没有多边形/椭圆，用圆角矩形拼：
        /// 8 个齿（上下左右 + 四个斜向）+ 一个实心圆做轮盘，最后用背景色在中间“挖”一个孔。
        /// 尺寸全部按传入矩形算，96/144 dpi 下同形等比，不引图片资源。
        /// </summary>
        void DrawGearIcon(GdiCanvas g, Rect32 r, Color32 c, Color32 bg)
        {
            int size = r.B - r.T;
            if (size < 12) return;
            int cx = (r.L + r.R) / 2, cy = (r.T + r.B) / 2;
            // 2026-10-24 出图复盘：轮盘 0.30、齿宽 0.17 时 24px 下齿太细、盘太小，整颗图标画出来像
            // 雪花（老师要的是"设置"，认不出来就白留这个入口）。盘加粗到 0.34、齿加宽到 0.22、
            // 孔缩到 0.11，齿根被盘盖住的部分更多，chunky 一点才读得出齿轮。
            int disc = Math.Max(3, (int)Math.Round(size * 0.34));    // 轮盘半径
            int hole = Math.Max(1, (int)Math.Round(size * 0.11));    // 中心孔
            int outer = Math.Max(disc + 2, (int)Math.Round(size * 0.47));
            int inner = Math.Max(2, (int)Math.Round(size * 0.29));
            int tw = Math.Max(2, (int)Math.Round(size * 0.22));      // 齿宽

            // 上下左右四个齿
            g.FillRounded(c, new Rect32(cx - tw / 2, cy - outer, cx + tw / 2, cy - inner), tw / 2, true, true);
            g.FillRounded(c, new Rect32(cx - tw / 2, cy + inner, cx + tw / 2, cy + outer), tw / 2, true, true);
            g.FillRounded(c, new Rect32(cx - outer, cy - tw / 2, cx - inner, cy + tw / 2), tw / 2, true, true);
            g.FillRounded(c, new Rect32(cx + inner, cy - tw / 2, cx + outer, cy + tw / 2), tw / 2, true, true);

            // 四个斜齿：沿 45° 方向，中心取内半径和外半径的中点
            int mid = (inner + outer) / 2;
            int d = (int)Math.Round(mid * 0.70710678);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    int px = cx + sx * d, py = cy + sy * d;
                    g.FillRounded(c, new Rect32(px - tw / 2, py - tw / 2, px + tw / 2, py + tw / 2), tw / 2, true, true);
                }
            }

            // 轮盘盖住齿根，最后用背景色挖孔 —— 负空间画法，不需要描边就能看出是齿轮。
            g.FillRounded(c, new Rect32(cx - disc, cy - disc, cx + disc, cy + disc), disc, true, true);
            g.FillRounded(bg, new Rect32(cx - hole, cy - hole, cx + hole, cy + hole), hole, true, true);
        }

        static bool Inside(Rect32 r, int x, int y)
        {
            return x >= r.L && x < r.R && y >= r.T && y < r.B;
        }

        /// <summary>
        /// 把一行的命中区裁到"看得见的内容区"里（2026-10-24 审出的真 bug）。
        /// 行框画的时候左右各外扩 7px 免瞄准（<c>pad-Scaled(7)</c> / <c>w-pad+Scaled(7)</c>），
        /// 贴右时右边那一截 = <c>w-pad+7</c> 已经压到竖带上了（236-14+7=229 对竖带 218 起）：
        /// 老师点行尾那 11px，OnClick 最先判的是竖带 → 卡片直接收起，行根本没被点中。
        /// 齿轮的命中区往下也伸进第一行（GearHit.B=40 对首行 top=36），同样让开。
        /// 菜单行与竖带的裁法原本只在「哪一份？」面板里做，这里抽成一处给所有面板共用。
        /// </summary>
        Rect32 ClipHit(Rect32 row, Rect32 rail, Rect32 gearHit)
        {
            int t = row.T;
            int r = row.R;
            if (gearHit.B > t && gearHit.B < row.B) t = gearHit.B;
            // 竖带在右（贴右）时砍右端；贴左时竖带在最左边，行框本来不压它，这里一并兼容。
            if (rail.L > row.L && rail.L < r) r = rail.L;
            else if (rail.R > row.L && rail.R < row.R) r = rail.R;
            if (r < row.L) r = row.L;
            if (t > row.B) t = row.B;
            return new Rect32(row.L, t, r, row.B);
        }

        // ---- GDI painting -----------------------------------------------------

        /// <summary>
        /// 课前预备态正文（2026-10-24 功能7）。行高必须和 <see cref="ContentHeight96"/> 里
        /// ReadyOnly 那一段一一对应：正文 22×2 → 按钮间距 8 → 按钮 32，底部再留 12。
        /// 少算一行，按钮就会被窗口下边缘裁掉（96dpi 下窗口只有 128 高）。
        /// </summary>
        void DrawReady(GdiCanvas g, CardState st, int w, int pad, int padR, int inner, int y, int btnH,
                       Color32 accent, Color32 text, Color32 muted)
        {
            showingCard = false;          // 预备态还没在放映，不给"整卡跳页"的老逻辑留口子
            jumpHit = new Rect32(0, 0, 0, 0);
            int step = Scaled(22);

            // 「上次停在第 N 页」用正文色：这是老师扫一眼就要读到的数字。
            string pageLine = "上次停在第 " + st.ReadySlide.ToString(CultureInfo.InvariantCulture) + " 页";
            g.Text(g.Ellipsis(pageLine, g.FontSub2, inner), g.FontSub2, text,
                   new Rect32(pad, y, w - padR, y + step), Align.Left, false);
            y += step;

            // 「还有 M 页没讲」：只有页数已知时才画（SlideCount<=0 时服务端给 0）。
            // 用弱化色——它是补充信息，正文里最该被读到的是"停在第几页"。
            if (st.ReadyRemaining > 0)
            {
                string rest = "还有 " + st.ReadyRemaining.ToString(CultureInfo.InvariantCulture) + " 页没讲";
                g.Text(g.Ellipsis(rest, g.FontSub2, inner), g.FontSub2, muted,
                       new Rect32(pad, y, w - padR, y + step), Align.Left, false);
                y += step;
            }

            // 实心 accent 按钮，和正常卡片那颗「从第 N 页接着讲」同一套视觉；文案换成"打开"，
            // 因为这一步真的会拉起演示软件，不只是跳页。
            y += Scaled(BtnGapTop96);
            var btn = new Rect32(pad, y, w - padR, y + btnH);
            g.FillRounded(accent, btn, Scaled(8), true, true);
            g.Text("打开并接着讲", g.FontTitle, Color32.White, btn, Align.Center, true);
            readyOpenHit = btn;
        }

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
                // 2026-10-24：pending 只由 Apply 写，正常永远非空，但 ContentHeight96 早就按 null 兜底了，
                // 这里对齐一下。否则真有人把 null 传进来，异常会被下面的 catch 吞进 paintError，
                // 表现是「卡片只画了半张 / 一片空白」而不是一条错误——这种静默失败最难查。
                if (st == null) st = new CardState();

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
                        var hairline = Color32.Mix(bg, text, 0.12);
                        int radius = Scaled(look.Radius);
                        g.Clear(bg);

                        // 命中区每次重画都先清空：分页/历史一变，旧矩形不能继续可点。
                        readyOpenHit = new Rect32(0, 0, 0, 0);
                        historyHit = new Rect32(0, 0, 0, 0);
                        backHit = new Rect32(0, 0, 0, 0);
                        for (int i = 0; i < MaxHistoryRows; i++)
                        {
                            historyRowHits[i] = new Rect32(0, 0, 0, 0);
                            historyRowSlides[i] = 0;
                        }
                        folderHit = new Rect32(0, 0, 0, 0);
                        panelHit = new Rect32(0, 0, 0, 0);
                        for (int i = 0; i < MaxFileRows; i++) { fileRowHits[i] = new Rect32(0, 0, 0, 0); fileRowKeys[i] = null; }
                        for (int i = 0; i < MaxFilterChips; i++) { filterHits[i] = new Rect32(0, 0, 0, 0); filterKeys[i] = null; }
                        pagePrevHit = new Rect32(0, 0, 0, 0);
                        pageNextHit = new Rect32(0, 0, 0, 0);
                        // 2026-10-24 审：整张卡片的跳页区和竖带原来是各个分支自己赋的（收起分支赋
                        // 全窗、展开分支末尾赋竖带），只有"正常卡"分支会主动把 jumpHit 清成 0。
                        // 面板分支里万一以后加了提前 return，就会留着上一次的跳页区 —— 点到面板
                        // 空白处会把放映跳走。统一在这里先清，各分支再按需覆盖。
                        jumpHit = new Rect32(0, 0, 0, 0);
                        collapseHit = new Rect32(0, 0, 0, 0);
                        for (int i = 0; i < CardFileList.MaxSameName; i++)
                        {
                            sameNameRowHits[i] = new Rect32(0, 0, 0, 0);
                            sameNameRowPaths[i] = null;
                            sameNameRowSlides[i] = 0;
                        }

                        if (!expanded)
                        {
                            // 收起态：一条竖着的药丸，落点和展开态那条左边缘竖带是同一条线。
                            // 老师原话「1：课件进度条现在缩成药丸也竖屏显示进度条」——所以药丸里
                            // 也有一条竖向进度：从下往上按 已讲页数/总页数 填 accent，剩余浅灰，
                            // 扫一眼就知道讲到哪了（和 ClassIsland 课程时间条一个意思，只是转了 90°）。
                            // 2026-10-24 追加：进度条下面加蓝字「已翻 N」，再下面一行红字「共 M」
                            // （老师原话「进度条下面加蓝字(此课件已翻ppt页数），下面再加一行红字(总共ppt页数)」）。
                            // 于是从上到下的顺序是：箭头 → 竖向进度条 → 蓝字 → 红字。
                            var pill = CollapsedPill();
                            jumpHit = new Rect32(0, 0, w, h);   // 收起时点标签任意处都是“展开”
                            collapseHit = new Rect32(0, 0, 0, 0);
                            showingCard = st != null && st.Showing;
                            // 药丸永远贴窗口左边：贴左时它压在屏幕左边缘（那条边保持直角），
                            // 贴右时它离屏边还有一截，整颗胶囊全圆角。
                            bool rl = look.OnRight, rr = !look.OnRight;
                            // 收起把手：白底 + 纯黑箭头（2026-10-17 用户要求“背景白色，箭头改为纯黑”）。
                            // 白底上的黑箭头对比最强，不用再描边。
                            g.FillRounded(bg, pill, radius, rl, rr);
                            g.StrokeRounded(hairline, pill, radius, rl, rr);
                            int capH = Scaled(26);
                            // 箭头指向卡片主体：贴在右边缘时朝左（❮），贴在左边缘时朝右（❯）。
                            g.Text(look.OnRight ? "\u276E" : "\u276F", g.FontHuge, Color32.FromArgb(255, 0, 0, 0),
                                   new Rect32(pill.L, pill.T + Scaled(2), pill.R, pill.T + Scaled(2) + capH), Align.Center, true);

                            // 页码和展开态大页码同一个来源：正在放映取 Current，否则取上次停的 Previous。
                            int pillPage = st == null ? 0 : (st.Showing ? st.Current : st.Previous);
                            int pillTotal = st == null ? 0 : st.SlideCount;

                            int padX = Scaled(6);
                            int lineBottomPad = Scaled(6);        // 离药丸下沿
                            // 2026-10-25 用户反馈：药丸下面那两行数字不要了，药丸里只留箭头 + 进度条。
                            int barBottom = pill.B - lineBottomPad;
                            int barTop = pill.T + Scaled(2) + capH + Scaled(2);
                            int bx = pill.L + padX, bxr = pill.R - padX;
                            // 药丸进度条按科目着色（2026-10-25 用户第 9 条「每科改颜色」）：
                            // 当前课件的科目在设置表里对得上就用那一科的颜色，对不上（或没配）还是主题强调色。
                            var barColor = accent;
                            int[] subjectAccent = PillBarAccent(look, st);
                            if (subjectAccent != null)
                            {
                                barColor = Color32.FromArgb(255, (byte)subjectAccent[0],
                                                            (byte)subjectAccent[1], (byte)subjectAccent[2]);
                            }

                            // 药丸里的竖向进度条：轨道铺满箭头以下、文字以上的剩余高度，科目色从底往上填。
                            if (bxr - bx >= Scaled(4) && barBottom - barTop >= Scaled(8))
                            {
                                int pillRange = barBottom - barTop;
                                // 圆角只给 4px（不超过半宽）：药丸里的轨道又窄又长，用半宽当圆角时
                                // 30% 那一点点填充会画成一颗圆球，看不出「条」的意思（出图确认过）。
                                int barRadius = Math.Min(Scaled(4), (bxr - bx) / 2);
                                g.FillRounded(ProgressTrack, new Rect32(bx, barTop, bxr, barBottom), barRadius, true, true);
                                int fh = PillFillHeight(pillRange, pillPage, pillTotal);
                                if (fh > 0)
                                {
                                    g.FillRounded(barColor, new Rect32(bx, barBottom - fh, bxr, barBottom),
                                                  barRadius, true, true);
                                }
                            }
                        }
                        else
                        {
                            bool rl = !look.OnRight, rr = look.OnRight;
                            // 2026-10-24：传 (0,0,w-1,h-1) 会让 1px 描边内缩一整像素（贴着黑行/黑列内侧画），
                            // 右/下外面反而露出一行/一列底色；传 (0,0,w,h) 描边才正好压在窗口最外一圈像素上。
                            g.StrokeRounded(hairline, new Rect32(0, 0, w, h), radius, rl, rr);

                            // 竖带在哪一边，正文就让出哪一边（贴右时竖带在右，左边就不必让了）。
                            int pad = Scaled(look.OnRight ? ContentPadRight96 : ContentPadLeft96);
                            int padR = Scaled(look.OnRight ? ContentPadLeft96 : ContentPadRight96);
                            int inner = w - pad - padR;
                            int y = Scaled(CardPadTop96);
                            int lineSmall = Scaled(20), stepSmall = Scaled(22);
                            int lineJump = Scaled(22), stepJump = Scaled(26);
                            // 新五行版式（2026-10-24）的四个行高/尺寸，和 ContentHeight96 共用同一张表。
                            int lineTitle = Scaled(TitleStep96);
                            int linePage = Scaled(PageBlockStep96);
                            int btnH = Scaled(BtnHeight96);
                            int barH = Math.Max(2, Scaled(BarHeight96));
                            int lineHist = Scaled(HistStep96);

                            // 右上角齿轮（控制面板）和左边缘那条竖带共用一份几何，Paint 与测试用的是同一套数。
                            var top = ComputeTopIcons(w, dpi, showPanelButton, look.OnRight);
                            var rail = RailRect(w, h, dpi, look.OnRight);

                            // 面板二（某份文件的历史）自己就是标题，不再画顶部“当前课件”那一行：
                            // 否则老师点开的是另一份课件，标题却写着当前这份，会看错。
                            bool panelTwo = historyPanel && selectedFile != null;
                            if (look.ShowFileName && !filePanel && !sameNamePanel && !panelTwo)
                            {
                                string title = st.FileName.Length > 0 ? st.FileName : (st.Showing ? "正在放映" : "课件续页");
                                // 右边只留齿轮（文件夹和右上角箭头都删了），长文件名不会从图标底下穿过去。
                                int iconsLeft = top.GearIcon.R > top.GearIcon.L ? top.GearIcon.L : w - padR;
                                int titleWidth = Math.Max(Scaled(40), iconsLeft - pad - Scaled(4));
                                // 标题 12.5px 加粗深色（视觉规格），竖着居中才和右边齿轮同一条中线。
                                g.Text(g.Ellipsis(title, g.FontTitle, titleWidth), g.FontTitle, text,
                                       new Rect32(pad, y, w - padR, y + lineTitle), Align.Left, true);
                                y += lineTitle;
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
                                y += Scaled(FileHeadStep);

                                // 筛选条（2026-10-24 功能2）：纯鼠标交互。卡片窗口是 WS_EX_NOACTIVATE，
                                // 拿不到键盘焦点，任何输入框/快捷键都会把放映界面顶掉。
                                var subjects = Subjects(st);
                                if (HasFilterBar(st))
                                {
                                    var chips = FilterChips(g, subjects, inner);
                                    int chipH = Scaled(20);
                                    int gap = Scaled(4);
                                    int cx = pad;
                                    for (int i = 0; i < chips.Count; i++)
                                    {
                                        string key = chips[i];
                                        string label = key.Length == 0 ? "全部" : key;
                                        int cw = ChipWidth(g, label);
                                        var chip = new Rect32(cx, y + Scaled(3), cx + cw, y + Scaled(3) + chipH);
                                        bool on = string.Equals(key, fileFilter, StringComparison.Ordinal);
                                        // 选中态用主题色的淡底，未选中用比底色深一档的淡底：都是现有的
                                        // bg/accent/text 混出来的，不引新配色。
                                        g.FillRounded(on ? Color32.Mix(bg, accent, 0.18) : Color32.Mix(bg, text, 0.05),
                                                      chip, chipH / 2, true, true);
                                        g.Text(g.Ellipsis(label, g.FontTiny, cw - Scaled(8)), g.FontTiny,
                                               on ? accent : muted, chip, Align.Center, true);
                                        // 命中区比 chip 高一档（整条筛选行）：鼠标不用瞄准药丸的边缘。
                                        // 贴右时最后一个 chip 可能被"补进选中项"挤出行宽（见 FilterChips），
                                        // 这里统一裁掉压在竖带上的那一截（ClipHit）。
                                        filterHits[i] = ClipHit(new Rect32(chip.L, y, chip.R, y + Scaled(FileFilterStep)), rail, top.GearHit);
                                        filterKeys[i] = key;
                                        cx += cw + gap;
                                    }
                                    y += Scaled(FileFilterStep);
                                }

                                var page = FilePageGroups(st);
                                int rows = CardFileGroups.CountItems(page);
                                if (rows == 0)
                                {
                                    // 空台账也把面板打开：显示一句“还没有记录”，老师知道是没记录，
                                    // 而不是图标坏了。筛选后为空也要说话，否则像筛选把课件弄丢了。
                                    // 这一层只留“返回”。
                                    string empty = fileFilter.Length > 0 ? "这个科目还没有记录" : "还没有记录";
                                    g.Text(g.Ellipsis(empty, g.FontTiny, inner), g.FontTiny, muted,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                    y += Scaled(FileRowStep);
                                }
                                int rowIndex = 0;
                                for (int gi = 0; gi < page.Count; gi++)
                                {
                                    var grp = page[gi];
                                    // 科目分组标题：小号字 + 比正文更弱的颜色，只做分段的记号，不抢课件名。
                                    g.Text(g.Ellipsis(grp.Subject, g.FontTiny, inner), g.FontTiny,
                                           Color32.Mix(bg, muted, 0.80),
                                           new Rect32(pad, y, w - pad, y + Scaled(FileGroupStep)), Align.Left, true);
                                    y += Scaled(FileGroupStep);

                                    for (int k = 0; k < grp.Items.Count; k++)
                                    {
                                        var f = grp.Items[k];
                                        if (f == null) continue;
                                        var row = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                        if (rowIndex < MaxFileRows)
                                        {
                                            // 记的是归并键，不是 st.Files 的下标：分组+筛选+分页之后两者不再对应。
                                            // 行框右端压着竖带那 11px 必须裁掉，否则点行尾会当成"收起"（ClipHit）。
                                            fileRowHits[rowIndex] = ClipHit(row, rail, top.GearHit);
                                            fileRowKeys[rowIndex] = f.Key;
                                            rowIndex++;
                                        }
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
                                        {
                                            // 行尾详情右对齐到"内容右边界"，不是 w-pad：贴右时 pad=14，
                                            // w-pad=222 已经压进竖带（rail.L=218），最后几个字会被竖带盖掉
                                            // （144dpi 的 shots\audit-card-144-right-files.png 一眼可见）。
                                            int detailRight = w - pad;
                                            // 竖带在右边时才需要让开；在左边时（贴左布局 pad=28）行尾离竖带还远。
                                            if (rail.L > pad && rail.L < detailRight) detailRight = rail.L - Scaled(2);
                                            g.Text(f.Detail, g.FontTiny, muted,
                                                   new Rect32(pad, y, detailRight, y + lineJump - Scaled(1)), Align.Right, false);
                                        }
                                        y += Scaled(FileRowStep);
                                    }
                                }

                                // 分页条：只在真的多页时占位置。上一页/下一页各占左右半边，
                                // 中间的「1/3」只做指示、不可点，于是两个命中区不可能重叠。
                                int pageSize2 = FilePageSize(st);
                                int pages = CardFileGroups.PageCount(FileItems(st).Count, pageSize2);
                                if (pages > 1)
                                {
                                    string pageText = (filePage + 1) + "/" + pages;
                                    int midW = g.TextWidth(pageText, g.FontTiny);
                                    int midX = (w - midW) / 2;
                                    var midRect = new Rect32(midX, y, midX + midW, y + Scaled(FilePagerStep));
                                    pagePrevHit = ClipHit(new Rect32(pad - Scaled(7), y - Scaled(2), midX - Scaled(4), y + Scaled(FilePagerStep) - Scaled(2)), rail, top.GearHit);
                                    pageNextHit = ClipHit(new Rect32(midX + midW + Scaled(4), y - Scaled(2), w - pad + Scaled(7), y + Scaled(FilePagerStep) - Scaled(2)), rail, top.GearHit);
                                    bool canPrev = filePage > 0, canNext = filePage < pages - 1;
                                    var off = Color32.Mix(bg, muted, 0.45);
                                    g.Text("\u2039 上一页", g.FontTiny, canPrev ? muted : off,
                                           new Rect32(pad, pagePrevHit.T, pagePrevHit.R, pagePrevHit.B), Align.Left, true);
                                    g.Text(pageText, g.FontTiny, muted, midRect, Align.Center, true);
                                    g.Text("下一页 \u203A", g.FontTiny, canNext ? muted : off,
                                           new Rect32(pageNextHit.L, pageNextHit.T, w - pad, pageNextHit.B), Align.Right, true);
                                    y += Scaled(FilePagerStep);
                                }

                                var fileBack = ClipHit(new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2)), rail, top.GearHit);
                                backHit = fileBack;
                                int backSep = y + lineJump - Scaled(2);
                                g.FillRounded(hairline, new Rect32(pad, backSep, w - pad, backSep + Math.Max(1, Scaled(1))), 0, false, false);
                                g.Text("\u2039 返回", g.FontTiny, muted,
                                       new Rect32(pad, y, w - pad, y + lineJump), Align.Center, false);
                            }
                            else if (sameNamePanel)
                            {
                                // 面板三（2026-10-24 功能9）「哪一份？」：一行一份候选文件。
                                // 左边是它的文件夹（弱化色 —— 同名文件之间唯一的区别就是位置），
                                // 右边是「第 N 页 · 时间」。默认那一份（ChooseJumpPath 挑出来的）
                                // 用强调色 + 淡强调底 + 左侧竖条，和课件库的"当前"行同一套视觉。
                                var same = SameNameTarget(st);
                                if (same != null)
                                {
                                    string head = "哪一份？（同名 " + same.SameName.Count.ToString(CultureInfo.InvariantCulture) + " 份）";
                                    g.Text(g.Ellipsis(head, g.FontSmall, inner), g.FontSmall, text,
                                           new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                    int sameHeadSep = y + lineSmall - Scaled(2);
                                    g.FillRounded(hairline, new Rect32(pad, sameHeadSep, w - pad, sameHeadSep + Math.Max(1, Scaled(1))), 0, false, false);
                                    y += Scaled(FileHeadStep);

                                    for (int i = 0; i < same.SameName.Count && i < CardFileList.MaxSameName; i++)
                                    {
                                        var cand = same.SameName[i];
                                        if (cand == null) continue;
                                        bool current = string.Equals(cand.FilePath, same.FilePath, StringComparison.OrdinalIgnoreCase);
                                        // 一行占 SameNameRowStep（两截）：上面文件夹、下面「第 N 页 · 时间」。
                                        var row = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + Scaled(SameNameRowStep) - Scaled(2));
                                        // 命中区比文字行高一档（整行可点）；
                                        // 但行首会顶到右上角的齿轮、行尾会伸进右侧竖带：这两块在 OnClick 里比面板先判，
                                        // 真点下去其实不会抢，可命中矩形叠着容易被当成"点错行"，所以只把命中区裁开，
                                        // 画出来的行底不动（和课件库的行同一套视觉）。
                                        int hitT = row.T;
                                        int hitR = row.R;
                                        if (top.GearHit.B > hitT) hitT = top.GearHit.B;
                                        if (rail.L > row.L && rail.L < hitR) hitR = rail.L;
                                        sameNameRowHits[i] = new Rect32(row.L, hitT, hitR, row.B);
                                        sameNameRowPaths[i] = cand.FilePath;
                                        sameNameRowSlides[i] = cand.Slide;
                                        if (current)
                                        {
                                            g.FillRounded(Color32.Mix(bg, accent, 0.10), row, Scaled(6), true, true);
                                            g.FillRounded(accent, new Rect32(row.L + Scaled(2), y + Scaled(4), row.L + Scaled(4), y + Scaled(SameNameRowStep) - Scaled(4)), Scaled(1), true, true);
                                        }
                                        // 第一截：文件夹。路径不能用尾部省略号 —— 两份同名文件差的就是路径尾部，
                                        // 「…\桌面\class…」和「…\课件\class…」截完长得一模一样。
                                        g.Text(FitFolderHint(g, cand.FolderHint, inner), g.FontSmall,
                                               current ? accent : text,
                                               new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                        // 第二截：页码 + 距上次多久，弱一档颜色，和第一截错开基线。
                                        g.Text(cand.Detail, g.FontTiny, muted,
                                               new Rect32(pad, y + Scaled(SameNameRowStep - 18), w - pad, y + Scaled(SameNameRowStep)), Align.Left, false);
                                        y += Scaled(SameNameRowStep);
                                    }

                                    var sameBack = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                    // 返回行同理让开竖带，命中矩形不叠在上面。
                                    int sameBackR = sameBack.R;
                                    if (rail.L > sameBack.L && rail.L < sameBackR) sameBackR = rail.L;
                                    backHit = new Rect32(sameBack.L, sameBack.T, sameBackR, sameBack.B);
                                    int sameSep = y + lineJump - Scaled(2);
                                    g.FillRounded(hairline, new Rect32(pad, sameSep, w - pad, sameSep + Math.Max(1, Scaled(1))), 0, false, false);
                                    g.Text("\u2039 返回", g.FontTiny, accent,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Center, false);
                                }
                            }
                            else if (historyPanel)
                            {
                                // 面板二：整块替换正常卡片正文。不做滚动，最多列 6 条。
                                // 正文画在 DrawHistoryPanel 里（2026-10-24 功能3 搬出去，方便离线出图）。
                                DrawHistoryPanel(g, st, w, pad, inner, y, lineSmall, lineJump, stepJump, accent, muted);
                            }
                            else if (st.ReadyOnly)
                            {
                                // 课前预备态（2026-10-24 功能7）：标题行已经在上面画过文件名，
                                // 这里只画「上次停在第 N 页」「还有 M 页没讲」和那颗按钮。
                                DrawReady(g, st, w, pad, padR, inner, y, btnH, accent, text, muted);
                            }
                            else
                            {
                                // 行一：大页码（40px 粗）+ 右侧两行小字。
                                // 「共 N 页」是事实，「还剩 N 页 · 约 M 分钟」才是老师判断要不要快进的依据
                                // （2026-10-24 功能1），所以后者字号大一号、颜色更实。
                                int page = st.Showing ? st.Current : st.Previous;
                                string pageText = page > 0 ? page.ToString(CultureInfo.InvariantCulture) : "\u2014";
                                // 页码用正文字色（视觉规格：「页码 40px 加粗（深色）」）。早先试过给放映中的
                                // 页码上 accent，出图一看 40px 的大橙数字把整张卡的重心全抢走了，按钮反而
                                // 不像主操作，所以两种状态都回正文色。
                                g.Text(pageText, g.FontPage, text,
                                       new Rect32(pad, y, pad + Scaled(PageColumn96), y + linePage), Align.Left, false);

                                // 页码列按数字实际宽度收窄（2026-10-24）：固定 70 时右列只剩 124px，
                                // 「还剩 17 页 · 约 17 分钟」就得退让成「剩 17 页 · 17 分钟」。一位数的页码
                                // 只用 46px 就够了，让出来的宽度刚够右列把话说完；上限仍是 PageColumn96，
                                // 三位数页也不会挤到右列里去。
                                int pageColW = Math.Max(Scaled(46), Math.Min(Scaled(PageColumn96),
                                                         g.TextWidth(pageText, g.FontPage) + Scaled(8)));
                                int rightX = pad + pageColW;
                                int rightW = w - padR - rightX;
                                if (rightW > Scaled(24))
                                {
                                    int totalPages = st.SlideCount;
                                    string totalText = totalPages > 0
                                        ? "共 " + totalPages.ToString(CultureInfo.InvariantCulture) + " 页"
                                        : "页数未知";
                                    g.Text(g.Ellipsis(totalText, g.FontSub, rightW), g.FontSub, muted,
                                           new Rect32(rightX, y + Scaled(2), w - padR, y + Scaled(22)), Align.Right, false);

                                    string remainText;
                                    if (!st.Showing && st.Previous > 0) remainText = "上次停在这一页";
                                    else if (st.RemainingPages > 0)
                                        remainText = "还剩 " + st.RemainingPages.ToString(CultureInfo.InvariantCulture) + " 页 · 约 "
                                                   + st.RemainingMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟";
                                    else if (st.Showing && totalPages > 0) remainText = "已经讲到最后一页";
                                    else remainText = st.SuiteOnly ? "正在识别课件…" : "等待放映";
                                    g.Text(g.Ellipsis(FitRemain(g, remainText, rightW), g.FontSub2, rightW), g.FontSub2, text,
                                           new Rect32(rightX, y + Scaled(24), w - padR, y + linePage), Align.Right, false);
                                }
                                y += linePage;

                                // 行二：实心 accent 圆角按钮，宽 = 卡片内容宽，高 32（96dpi），白字 12.5 粗。
                                // 整块按钮就是「跳页」的命中区（见 OnClick 里的 jumpHit）；没有上次进度
                                // 或者老师关掉这一行时画一个灰掉的同形按钮：位置留着，卡片高度不抖。
                                showingCard = st.Showing || st.SuiteOnly;   // 等课件时整张卡片都不可点（只有把手能点）
                                jumpHit = new Rect32(0, 0, 0, 0);
                                y += Scaled(BtnGapTop96);
                                var btn = new Rect32(pad, y, w - padR, y + btnH);
                                if (look.ShowJumpLine && st.Previous > 0)
                                {
                                    g.FillRounded(accent, btn, Scaled(8), true, true);
                                    string jump = "从第 " + st.Previous.ToString(CultureInfo.InvariantCulture) + " 页接着讲";
                                    g.Text(g.Ellipsis(jump, g.FontTitle, inner - Scaled(12)), g.FontTitle, Color32.White,
                                           btn, Align.Center, true);
                                    jumpHit = btn;
                                }
                                else
                                {
                                    g.FillRounded(Color32.Mix(bg, text, 0.06), btn, Scaled(8), true, true);
                                    string why = st.SuiteOnly ? "正在识别课件…" : (st.Showing ? "还没讲到下一页" : "还没有上次的进度");
                                    g.Text(g.Ellipsis(why, g.FontTitle, inner - Scaled(12)), g.FontTitle, muted,
                                           btn, Align.Center, true);
                                }
                                y += btnH + Scaled(BarGapTop96);

                                // 行三：进度条。4px 高（96dpi）、圆角、轨道 #CDD0D5、填充 accent。
                                // 参照 ClassIsland 的课程时间条：长度本身就是信息，不用再写字。
                                var bar = new Rect32(pad, y, w - padR, y + barH);
                                g.FillRounded(ProgressTrack, bar, barH / 2, true, true);
                                int barFill = ProgressFillWidth(bar.R - bar.L, page, st.SlideCount);
                                if (barFill > 0)
                                    g.FillRounded(accent, new Rect32(bar.L, bar.T, bar.L + barFill, bar.B), barH / 2, true, true);
                                y += barH + Scaled(HistGapTop96);

                                // 行四（整张卡最后一行）：历史入口。老师原话「历史记录放最下面」，
                                // 所以它在卡片最底下一行，弱化色——和上面那块实心按钮区分开：
                                // 一个是「打开列表」，一个是「立刻跳页」，误点的后果不一样。
                                // 没有历史时这一行改显示课表上下文或提示，信息不丢，高度也不变。
                                int histTotal = HistoryTotal(st);
                                if (histTotal > 0)
                                {
                                    string hist = "历史 · " + histTotal.ToString(CultureInfo.InvariantCulture) + " 次 \u203A";
                                    historyHit = new Rect32(pad - Scaled(7), y, w - padR + Scaled(7), y + lineHist);
                                    g.Text(g.Ellipsis(hist, g.FontSub, inner), g.FontSub, muted,
                                           new Rect32(pad, y, w - padR, y + lineHist), Align.Left, true);
                                }
                                else
                                {
                                    string foot = BitsText(st);
                                    bool diagOnly = foot.Length == 0;
                                    if (diagOnly) foot = status;
                                    if (foot.Length > 0)
                                    {
                                        g.Text(g.Ellipsis(foot, g.FontSub, inner), g.FontSub, diagOnly ? accent : muted,
                                               new Rect32(pad, y, w - padR, y + lineHist), Align.Left, true);
                                    }
                                }
                                y += lineHist;
                            }

                            // 左边缘那条竖向把手 = 「缩小成药丸」的按钮（2026-10-24 老师原话
                            // 「把他放在悬浮窗最左边竖着跟悬浮窗的宽一样长」）。整条卡片高度都能点，
                            // 所以原来右上角那个小箭头没了；箭头竖着指（❮）表示往左收进去。
                            // 从卡片左边缘往内拖仍然是移动窗口的手势（超过 4px 就不算点击），不会误触收起。
                            collapseHit = rail;
                            if (rail.R > rail.L)
                            {
                                // 竖带底色比卡片深一档，右边缘压一条 hairline——让「这里能按」看得出来。
                                // 左侧圆角跟着卡片走，右侧是直角，贴住卡片主体。
                                // 0.06 出图时几乎看不见（#EEEEEE 只压到 #E9E9EA），提到 0.10 刚好看得出来
                                // 又不像是选中态；箭头也跟着放大一档（FontSmall），18px 宽的竖带放得下。
                                // 圆角只给卡片外侧那条边，内侧压直角贴住卡片主体；分隔线画在朝内的那一侧。
                                bool railOnRight = look.OnRight;
                                g.FillRounded(Color32.Mix(bg, text, 0.10), rail, radius, !railOnRight, railOnRight);
                                int sepX = railOnRight
                                    ? Math.Min(rail.R, rail.L + Math.Max(1, Scaled(1)))
                                    : Math.Max(rail.L, rail.R - Math.Max(1, Scaled(1)));
                                var sep = railOnRight
                                    ? new Rect32(rail.L, radius, sepX, Math.Max(radius + 1, h - radius))
                                    : new Rect32(sepX, radius, rail.R, Math.Max(radius + 1, h - radius));
                                g.FillRounded(hairline, sep, 0, false, false);
                                // 箭头指向卡片收进去的方向：贴右时朝右（❯），贴左时朝左（❮）。
                                g.Text(railOnRight ? "\u276F" : "\u276E", g.FontSmall, muted, rail, Align.Center, true);
                            }

                            // 控制面板齿轮（2026-10-24：老师让把右上角那个文件夹「这个删了，原位置改成设置」）。
                            // 面板是独立的 Avalonia 窗口，卡片这边只发 PanelRequested，不把面板代码搬进来。
                            if (top.GearIcon.R > top.GearIcon.L)
                            {
                                DrawGearIcon(g, top.GearIcon, muted, bg);
                                panelHit = top.GearHit;
                            }
                            // 文件夹图标已删，folderHit 保持空（上面已清零），OnClick 里那个分支自然进不去；
                            // DrawFolderIcon / OpenFilePanel 和课表面板代码都还在，只是暂时不可达。
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
        // FillRect 只认 lprc 指针，不认画笔：给什么颜色就逐像素填满，不会像 Rectangle 那样
        // 把右/下边界当成"边框"丢掉（见 GdiCanvas.Clear 上的 2026-10-24 注释）。
        [DllImport("user32.dll")]
        static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

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

        internal struct Rect32
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
            // 2026-10-24 五行版式专用的四个字号（规格里写死的 96dpi 稿值，仍然跟随 FontDelta）。
            readonly IntPtr fontTitle, fontSub, fontSub2, fontPage;
            readonly IntPtr nullPen;

            const int FW_NORMAL = 400, FW_SEMIBOLD = 600;
            const uint CLEARTYPE = 5, ANTIALIASED = 4, DEFAULT_CHARSET = 1, DEFAULT_PITCH = 0;
            const int TRANSPARENT = 1;
            const uint DT_LEFT = 0x00000000, DT_CENTER = 0x00000001, DT_RIGHT = 0x00000002, DT_VCENTER = 0x00000004,
                       DT_SINGLELINE = 0x00000020, DT_END_ELLIPSIS = 0x00008000, DT_WORDBREAK = 0x00000010,
                       DT_CALCRECT = 0x00000400;

            readonly int savedBkMode, savedTextColor;
            readonly IntPtr savedFont;
            // 2026-10-24 审出：nullPen 被选进 DC 之后，Dispose 里直接 DeleteObject 是无效的
            // （GDI 拒绝删除"正被选进 DC"的画笔），于是每画一帧就漏一个画笔句柄。
            // 必须先把 memDC 原来那支笔还回去，再删自己的。
            readonly IntPtr savedPen;

            /// <summary>
            /// Font point sizes are authored at 96 dpi and scaled like everything else.
            /// public 是给外面 MeasurePillText 用的：嵌套类的私有成员，外层类看不见（只能反过来）。
            /// </summary>
            public static int FontHeight(double points, int dpi) { return -(int)Math.Round(points * dpi / 96.0); }

            /// <summary>
            /// 药丸里那两行页码（「已翻 N」蓝 /「共 M」红）用的字号：和 fontTiny 同号，
            /// 即比 fontSmall 小一档。定宽（NativeOverlayCard.ComputePillWidth）也读这一个数，
            /// 两边各写一套字号的话量出来的宽度和画出来的就不一样了。
            /// </summary>
            public static int PillPointSize(CardAppearance look)
            {
                return Math.Max(8, look.FontSecondary + look.FontDelta - 2);
            }

            public GdiCanvas(IntPtr dc, int w, int h, int dpi, CardAppearance look)
            {
                this.dc = dc; width = w; height = h;
                // 字体族和字号都取自 ClassIsland 的主题（HarmonyOS Sans SC + 它的字号体系），
                // 老师换字体或调字号时卡片跟着变，而不是插件自己另立一套。
                string face = string.IsNullOrWhiteSpace(look.FontFamily) ? "Microsoft YaHei UI" : look.FontFamily;
                int d = look.FontDelta;
                int small = Math.Max(9, look.FontSecondary + d);
                int tiny = PillPointSize(look);
                int big = Math.Max(18, look.FontLarge + d + 18);
                int huge = Math.Max(12, look.FontBody + d + 6);
                // 12.5 是规格里的标题字号，GDI 的 CreateFontW 收整数字高，所以这里走 double 再取整。
                double titlePt = Math.Max(9.0, 12.5 + d);
                double subPt = Math.Max(8.0, 10.5 + d);
                double sub2Pt = Math.Max(8.5, 11.5 + d);
                double pagePt = Math.Max(20.0, 40.0 + d);
                fontSmall = CreateFontW(FontHeight(small, dpi), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontBig = CreateFontW(FontHeight(big, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontTiny = CreateFontW(FontHeight(tiny, dpi), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontHuge = CreateFontW(FontHeight(huge, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontTitle = CreateFontW(FontHeight(titlePt, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontSub = CreateFontW(FontHeight(subPt, dpi), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontSub2 = CreateFontW(FontHeight(sub2Pt, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                fontPage = CreateFontW(FontHeight(pagePt, dpi), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE, DEFAULT_PITCH, face);
                savedBkMode = SetBkMode(dc, TRANSPARENT);
                savedTextColor = SetTextColor(dc, 0xFFFFFF);
                savedFont = SelectObject(dc, fontSmall);
                // GDI 的 Rectangle / RoundRect 是"用画刷填、用画笔描边"：内存 DC 默认那支笔是 1px 黑笔，
                // 于是每个填充都会在边上留一条黑线——收起把手右边那条竖线（r.R - radius 处）就是这么来的
                // （2026-10-17 用户报的「有条线是bug」）。换成空笔，填充就只填不描边；
                // 真正要描边的地方由 StrokeRounded 自己选笔、画完再还回来。
                nullPen = CreatePen(5 /*PS_NULL*/, 0, 0);
                savedPen = SelectObject(dc, nullPen);
            }

            public IntPtr FontSmall { get { return fontSmall; } }
            public IntPtr FontBig { get { return fontBig; } }
            public IntPtr FontTiny { get { return fontTiny; } }
            public IntPtr FontHuge { get { return fontHuge; } }
            /// <summary>标题 / 实心按钮上的 12.5px 加粗字。</summary>
            public IntPtr FontTitle { get { return fontTitle; } }
            /// <summary>「共 N 页」「历史 · N 次」这类 10.5px 弱化字。</summary>
            public IntPtr FontSub { get { return fontSub; } }
            /// <summary>「还剩 N 页 · 约 M 分钟」这条 11.5px 加粗字，比 FontSub 实一档。</summary>
            public IntPtr FontSub2 { get { return fontSub2; } }
            /// <summary>大页码那一列用的 40px 加粗字。</summary>
            public IntPtr FontPage { get { return fontPage; } }

            public void Clear(Color32 c)
            {
                // 2026-10-24：悬浮卡片「边缘有黑线」的根因就在这里。GDI 的 Rectangle/RoundRect 把 l/t/r/b
                // 当"外接矩形"，即使已经选了 PS_NULL 空笔，也不会写右边界列 x=w-1 和下边界行 y=h-1（被当成边框
                // 剥掉了）；而 Paint 每次都新建 CreateCompatibleBitmap，内容全 0 就是纯黑，于是卡面右下永远各有
                // 1 像素纯黑，90% 不透明合成到屏幕上就是一条黑线（左侧/顶部那 1px 是 StrokeRounded 的 hairline）。
                // 清屏必须用不认画笔、逐像素填满的 FillRect，不能用 Rectangle。
                RECT r = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
                IntPtr b = CreateSolidBrush(Rgb(c.R, c.G, c.B));   // FillRect 直接吃 brush，不用再 SelectObject
                try { FillRect(dc, ref r, b); }
                finally { DeleteObject(b); }
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
                // 先把笔还回 DC，再删：顺序反了 DeleteObject 会失败，句柄就漏了（见 savedPen 的注释）。
                SelectObject(dc, savedPen);
                DeleteObject(nullPen);
                DeleteObject(fontSmall);
                DeleteObject(fontBig);
                DeleteObject(fontTiny);
                DeleteObject(fontHuge);
                DeleteObject(fontTitle);
                DeleteObject(fontSub);
                DeleteObject(fontSub2);
                DeleteObject(fontPage);
            }
        }
    }
}
