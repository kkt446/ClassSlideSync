using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Ui
{
    /// <summary>
    /// Linux 上的浮层卡片：一个无边框、置顶、不占任务栏的 Avalonia <see cref="Window"/>。
    ///
    /// 版式是 Windows 卡片（Ui/NativeOverlayCard.cs，96 dpi 下的 236×176 像素）的 1:1 复刻：
    /// 标题行 + 右上齿轮、大页码 + 共 N 页 + 还剩 X 页 · 约 Y 分钟、实心「从第 N 页接着讲」按钮、
    /// 底部进度条、底部「历史 · N 次」。**所有尺寸/字号用的都是 DIP**，Avalonia 会按
    /// TopLevel.RenderScaling 自动换算成物理像素 —— 这正是 Windows 那边 `Scaled(v) = v*dpi/96`
    /// 的等价物，所以 96 dpi 下是 236×176，144 dpi 下是 354×264，视觉一致。
    ///
    /// 数据来源：Linux 上没有 COM，会话引擎不跑。状态由 SlideSyncService 的 500ms 定时器从
    /// Core/LinuxSlideSource.cs（~/.local/share/classslidesync/current.json）读出来后，
    /// 通过 <see cref="ApplyExternal"/> 推进来。
    ///
    /// 线程模型：所有公开方法都可能被定时器线程调用，内部统一 Post 到 Dispatcher.UIThread；
    /// 窗口对象只在 UI 线程上创建/销毁。任何绘制异常都被吞掉并写日志 —— 卡片坏了不能
    /// 把宿主带下去。
    /// </summary>
    public sealed class LinuxCardWindow : ICardSink
    {
        // ---- 外观 / 状态 -------------------------------------------------------
        CardAppearance look = new CardAppearance();
        bool showOnStart = true;
        bool autoHideEnabled = true;
        bool showPanelButton = true;
        int autoHideSeconds = 15;

        // 最近一帧数据（ApplyExternal / Apply 写，UI 线程读）。
        volatile string deck = "";
        volatile int pageNo;
        volatile int totalPages;
        volatile bool showing;
        volatile int historyTotal;
        volatile string status = "";
        volatile int remainingPages;
        volatile int remainingMinutes;
        volatile bool hasPrevious;

        // ---- 窗口 --------------------------------------------------------------
        Window? win;
        Border? root;
        TextBlock? titleText;
        TextBlock? pageText;
        TextBlock? totalText;
        TextBlock? remainText;
        TextBlock? footText;
        TextBlock? buttonText;
        Border? button;
        Border? barFill;

        volatile bool created;
        volatile bool hidden;
        volatile bool disposed;
        // Diag() 的窗口部分：只在 UI 线程写，任意线程读（见 Diag 注释）。
        volatile string lastDiag = "linux-avalonia window=not-created";
        DateTime previewUntil = DateTime.MinValue;
        DispatcherTimer? hideTimer;
        // 跳页等待：按钮点下后记下目标页；5 秒内页码没变就在状态行提示「等放映软件响应」。
        volatile int pendingJumpPage;
        DispatcherTimer? jumpWaitTimer;

        // ---- 拖动 --------------------------------------------------------------
        bool dragging;
        bool dragMoved;
        Point dragStartPoint;
        PixelPoint dragStartPos;

        const double BaseWidth = 236.0;
        const double BaseHeight = 176.0;

        // Windows 卡片的固定底轨颜色（NativeOverlayCard.ProgressTrack 写成常量 #CDD0D5）。
        static readonly Color ProgressTrack = Color.FromRgb(0xCD, 0xD0, 0xD5);

        public LinuxCardWindow()
        {
            look = new CardAppearance();
        }

        // ======================= ICardSink =======================

#pragma warning disable CS0067 // Linux 卡片暂不提供历史面板/外部打开器，事件先保留以对齐接口。
        public event Action? JumpRequested;
        public event Action? PanelRequested;
        public event Action<bool, int>? PositionChanged;
        public event Action<string, int>? HistoryJumpRequested;
        public event Action<string, int>? OpenDeckRequested;
#pragma warning restore CS0067

        /// <summary>
        /// 按钮点下时带「卡片上写着的页码」触发。ICardSink.JumpRequested 是参数less的接口
        /// 事件（Ui/NativeOverlayCard.cs 也实现它，不能改），而 Linux 跳页必须把页码交给
        /// 服务写进 command.json，否则 500ms 后读数一变，跳页目标就漂了。
        /// 服务订阅这个事件；JumpRequested 只在「还没读到页码」时触发，用来显示提示。
        /// </summary>
        public event Action<int>? JumpRequestedPage;

        public Action<string>? Log { get; set; }

        public bool IsCreated { get { return created && win != null; } }

        public bool IsHidden { get { return hidden; } }

        public string Diag()
        {
            // 这条会被服务的延迟任务/看门狗在**别的线程**上调用：Avalonia 的任何窗口属性
            // 从非 UI 线程读都会抛 "Call from invalid thread"。所以窗口那一半用 UI 线程
            // 缓存下来的 lastDiag，状态那一半全是 volatile 字段。
            try
            {
                return lastDiag
                    + " hidden=" + hidden
                    + " side=" + (look.OnRight ? "right" : "left")
                    + " v=" + look.VerticalPercent + "%"
                    + " | deck=" + (deck.Length > 0 ? deck : "(none)")
                    + " page=" + pageNo + "/" + totalPages
                    + " showing=" + showing
                    + " hist=" + historyTotal;
            }
            catch (Exception ex)
            {
                return "linux-avalonia diag failed: " + ex.Message;
            }
        }

        public void Start()
        {
            if (disposed) return;
            Post(() =>
            {
                try
                {
                    EnsureWindow();
                    Note("start");
                }
                catch (Exception ex) { Note("start failed: " + ex.Message); }
            });
        }

        public void Configure(CardAppearance appearance, bool showOnStart, bool autoHide, int autoHideSeconds, bool showPanel = true)
        {
            look = appearance ?? new CardAppearance();
            this.showOnStart = showOnStart;
            autoHideEnabled = autoHide;
            this.autoHideSeconds = Math.Clamp(autoHideSeconds, 5, 600);
            showPanelButton = showPanel;
            Post(() =>
            {
                try
                {
                    if (win == null) EnsureWindow();
                    BuildStructure();
                    UpdateVisuals();
                    Place();
                }
                catch (Exception ex) { Note("configure failed: " + ex.Message); }
            });
        }

        public void SetAppearance(CardAppearance appearance)
        {
            look = appearance ?? new CardAppearance();
            Post(() =>
            {
                try
                {
                    if (win == null) EnsureWindow();
                    BuildStructure();
                    UpdateVisuals();
                    Place();
                }
                catch (Exception ex) { Note("appearance failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// 外部状态驱动（Linux 上没有 SessionEngine）：课件名、当前页、总页数、是否在放映。
        /// 页码来自 current.json，由服务每 500ms 读一次、变化时才推。
        /// </summary>
        public void ApplyExternal(string deckName, int page, int total, bool isShowing)
        {
            int prevPage = pageNo;
            deck = deckName ?? "";
            pageNo = page < 0 ? 0 : page;
            totalPages = total < 0 ? 0 : total;
            showing = isShowing;
            historyTotal = 0;
            remainingPages = Math.Max(0, totalPages - pageNo);
            // TODO(等台账/页速接上): Linux 端还没有「每页花多久」的历史，先按课堂常见节奏
            // 1.5 分钟/页估算；Ledger 的停留时长准备好后换成真实值。
            remainingMinutes = remainingPages > 0 ? EstimateMinutes(remainingPages) : 0;
            hasPrevious = false;

            // 跳页有了结果：页码真的变了（prev != 新页）就撤销等待计时器，并清掉
            // 「正在跳…/已请求…」这类临时状态，底部回到「第 N / M 页」。
            bool pageMoved = prevPage != pageNo;
            if (pageMoved || (pendingJumpPage > 0 && pageNo != pendingJumpPage))
            {
                Post(FinishJumpWait);
            }
            if (pageMoved) ClearTransientJumpStatus();
            Post(RenderNow);
        }

        public void Apply(CardState state)
        {
            if (state == null) return;
            deck = state.FileName ?? "";
            pageNo = state.Current;
            totalPages = state.SlideCount;
            showing = state.Showing;
            historyTotal = state.HistoryTotal;
            status = state.Status ?? "";
            remainingPages = state.RemainingPages > 0 ? state.RemainingPages : Math.Max(0, totalPages - pageNo);
            remainingMinutes = state.RemainingMinutes;
            hasPrevious = state.Previous > 0;
            Post(RenderNow);
        }

        public void ShowStatus(string text)
        {
            status = text ?? "";
            Post(RenderNow);
        }

        public void Show()
        {
            hidden = false;
            Post(() =>
            {
                try
                {
                    EnsureWindow();
                    UpdateVisuals();
                    Place();
                    ShowWindow();
                    RestartHideTimer();
                }
                catch (Exception ex) { Note("show failed: " + ex.Message); }
            });
        }

        public void Hide()
        {
            hidden = true;
            Post(() =>
            {
                try
                {
                    hideTimer?.Stop();
                    win?.Hide();
                }
                catch (Exception ex) { Note("hide failed: " + ex.Message); }
            });
        }

        public void Expand()
        {
            previewUntil = DateTime.UtcNow.AddSeconds(20);
            Show();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Post(() =>
            {
                try
                {
                    hideTimer?.Stop();
                    hideTimer = null;
                    try { jumpWaitTimer?.Stop(); } catch { }
                    jumpWaitTimer = null;
                    var w = win;
                    win = null;
                    created = false;
                    try { w?.Close(); } catch { }
                    Note("disposed");
                }
                catch (Exception ex) { Note("dispose failed: " + ex.Message); }
            });
        }

        // ======================= 窗口 / 版式 =======================

        void EnsureWindow()
        {
            if (win != null) return;
            var w = new Window
            {
                SystemDecorations = SystemDecorations.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true,
                ShowInTaskbar = false,
                CanResize = false,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                Background = Brushes.Transparent,
                Width = Math.Max(120, look.CardWidth > 0 ? look.CardWidth : BaseWidth),
                Height = Math.Max(80, look.CardHeight > 0 ? look.CardHeight : BaseHeight),
            };
            w.Opened += (_, _) =>
            {
                try { Place(); } catch { }
            };
            w.Closed += (_, _) => { created = false; };
            win = w;
            created = true;
            BuildStructure();
            Note("window created " + w.Width + "x" + w.Height);
        }

        /// <summary>重建整棵可视树（外观、贴边方向、尺寸变化时调用）。</summary>
        void BuildStructure()
        {
            if (win == null) return;
            try
            {
                var a = look;
                double W = Math.Max(120, a.CardWidth > 0 ? a.CardWidth : BaseWidth);
                double H = Math.Max(80, a.CardHeight > 0 ? a.CardHeight : BaseHeight);
                win.Width = W;
                win.Height = H;

                var bg = Color.FromArgb((byte)Math.Clamp(a.BgAlpha, 0, 255), (byte)a.BgR, (byte)a.BgG, (byte)a.BgB);
                var text = Color.FromRgb((byte)a.TextR, (byte)a.TextG, (byte)a.TextB);
                var muted = Color.FromRgb((byte)a.MutedR, (byte)a.MutedG, (byte)a.MutedB);
                var accent = Color.FromRgb((byte)a.AccentR, (byte)a.AccentG, (byte)a.AccentB);

                IBrush bgBrush = new SolidColorBrush(bg);
                IBrush textBrush = new SolidColorBrush(text);
                IBrush mutedBrush = new SolidColorBrush(muted);
                IBrush accentBrush = new SolidColorBrush(accent);
                IBrush whiteBrush = new SolidColorBrush(Colors.White);
                IBrush hairline = new SolidColorBrush(Mix(bg, text, 0.12));

                bool onRight = a.OnRight;
                // Windows：贴右时内容左内边距 14、右 28（给外层竖带让位），贴左时对调。
                double padL = onRight ? 14 : 28;
                double padR = onRight ? 28 : 14;
                double inner = Math.Max(40, W - padL - padR);
                double gearX = W - (onRight ? 28 : 14) - 24;
                double titleW = Math.Max(20, gearX - padL - 4);
                // 右列固定从 padL+70 开始（Windows 那边页码列宽是 46..70 之间按数字宽度浮动，
                // 这里取上限：右列是右对齐的，右边距一致，视觉上没差别，改页码时也不用重排）。
                double rightX = padL + 70;
                double rightW = Math.Max(20, W - padR - rightX);

                var c = new Canvas { Width = W, Height = H };

                titleText = new TextBlock
                {
                    FontSize = Math.Max(9.0, 12.5 + a.FontDelta),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = textBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Add(c, new Border { Width = titleW, Height = 20, Child = titleText }, padL, 12);

                // 右上齿轮（用向量拼：8 个齿 + 圆环，避免字体里没有 ⚙ 时变成豆腐块）。
                var gear = BuildGear(24, mutedBrush);
                var gearHost = new Border
                {
                    Width = 24,
                    Height = 24,
                    Background = Brushes.Transparent,
                    Child = gear,
                };
                Add(c, gearHost, gearX, 10);
                gearHost.PointerReleased += (_, _) =>
                {
                    if (!showPanelButton || dragMoved) return;
                    try { PanelRequested?.Invoke(); } catch { }
                    RestartHideTimer();
                };

                // 大页码 + 右列（共 N 页 / 还剩 …）
                pageText = new TextBlock
                {
                    FontSize = Math.Max(20.0, 40.0 + a.FontDelta),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = textBrush,
                    TextAlignment = TextAlignment.Left,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Add(c, new Border
                {
                    Width = Math.Max(30, rightX - padL - 4),
                    Height = 54,
                    Child = pageText,
                }, padL, 32);

                totalText = new TextBlock
                {
                    FontSize = Math.Max(8.0, 10.5 + a.FontDelta),
                    Foreground = mutedBrush,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Add(c, new Border { Width = rightW, Height = 20, Child = totalText }, rightX, 34);

                remainText = new TextBlock
                {
                    FontSize = Math.Max(8.5, 11.5 + a.FontDelta),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = textBrush,
                    TextAlignment = TextAlignment.Right,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Add(c, new Border { Width = rightW, Height = 30, Child = remainText }, rightX, 56);

                // 实心按钮
                buttonText = new TextBlock
                {
                    FontSize = Math.Max(9.0, 12.5 + a.FontDelta),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = whiteBrush,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                button = new Border
                {
                    Width = inner,
                    Height = 32,
                    CornerRadius = new CornerRadius(8),
                    Background = accentBrush,
                    Child = buttonText,
                };
                Add(c, button, padL, 94);
                button.PointerReleased += (_, _) =>
                {
                    if (dragMoved) return;
                    int page = pageNo;
                    if (page > 0)
                    {
                        // 先记下「我请求的是第 N 页」，再发事件。服务写 command.json 是同步的，
                        // 但适配器跳页要等下一轮轮询（最长约 1 秒），这 5 秒窗口里页码没动
                        // 就把状态行换成「已请求…等放映软件响应」。
                        ArmJumpWait(page);
                        try { JumpRequestedPage?.Invoke(page); } catch { }
                    }
                    else
                    {
                        try { JumpRequested?.Invoke(); } catch { }
                    }
                    RestartHideTimer();
                };

                // 进度条
                Add(c, new Border
                {
                    Width = inner,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(ProgressTrack),
                }, padL, 134);
                barFill = new Border
                {
                    Width = 0,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = accentBrush,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                };
                Add(c, barFill, padL, 134);

                // 底部一行（历史 / 状态）
                footText = new TextBlock
                {
                    FontSize = Math.Max(8.0, 10.5 + a.FontDelta),
                    Foreground = mutedBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                Add(c, new Border { Width = inner, Height = 18, Child = footText }, padL, 146);

                // 外层竖带（Windows 的 rail：收起把手）。点它 = 收起卡片。
                var railArrow = new TextBlock
                {
                    Text = onRight ? "\u276F" : "\u276E",
                    FontSize = Math.Max(9, a.FontSecondary + a.FontDelta),
                    Foreground = mutedBrush,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                var rail = new Border
                {
                    Width = 18,
                    Height = H,
                    Background = new SolidColorBrush(Mix(bg, text, 0.10)),
                    CornerRadius = onRight
                        ? new CornerRadius(0, Math.Max(0, a.Radius), Math.Max(0, a.Radius), 0)
                        : new CornerRadius(Math.Max(0, a.Radius), 0, 0, Math.Max(0, a.Radius)),
                    Child = new Border { Width = 18, Height = H, Child = railArrow },
                };
                Add(c, rail, onRight ? W - 18 : 0, 0);
                rail.PointerReleased += (_, _) =>
                {
                    if (dragMoved) return;
                    Hide();
                };

                root = new Border
                {
                    Background = bgBrush,
                    BorderBrush = hairline,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(Math.Max(0, a.Radius)),
                    ClipToBounds = true,
                    Child = c,
                };

                // 拖动：按在任意位置都能拖；松手时决定贴左/贴右与竖直百分比。
                // 按下时**不**捕获指针，否则按钮/齿轮的 PointerReleased 收不到（点击会失效）；
                // 只有真的拖过 4 DIP 才 Capture。
                root.PointerPressed += (_, e) =>
                {
                    try
                    {
                        if (win == null) return;
                        if (!e.GetCurrentPoint(win).Properties.IsLeftButtonPressed) return;
                        dragging = true;
                        dragMoved = false;
                        dragStartPoint = e.GetPosition(win);
                        dragStartPos = win.Position;
                    }
                    catch { dragging = false; }
                };
                root.PointerMoved += (_, e) =>
                {
                    try
                    {
                        if (!dragging || win == null) return;
                        if (!e.GetCurrentPoint(win).Properties.IsLeftButtonPressed) return;
                        var d = e.GetPosition(win) - dragStartPoint;
                        if (!dragMoved && (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4))
                        {
                            dragMoved = true;
                            try { e.Pointer.Capture(root); } catch { }
                        }
                        if (!dragMoved) return;
                        double scaling = ScalingOf();
                        win.Position = new PixelPoint(
                            dragStartPos.X + (int)Math.Round(d.X * scaling),
                            dragStartPos.Y + (int)Math.Round(d.Y * scaling));
                    }
                    catch { }
                };
                root.PointerReleased += (_, _) =>
                {
                    try
                    {
                        if (!dragging) return;
                        dragging = false;
                        if (dragMoved) DropCard();
                    }
                    catch { }
                };

                win.Content = root;
            }
            catch (Exception ex)
            {
                Note("build layout failed: " + ex.Message);
            }
        }

        void RenderNow()
        {
            if (disposed) return;
            try
            {
                if (win == null) EnsureWindow();
                UpdateVisuals();
                Place();
                bool hasData = totalPages > 0 && pageNo > 0;
                if (showOnStart && hasData && !hidden)
                {
                    ShowWindow();
                    RestartHideTimer();
                }
            }
            catch (Exception ex)
            {
                Note("render failed: " + ex.Message);
            }
        }

        void UpdateVisuals()
        {
            if (win == null) return;
            if (titleText == null) BuildStructure();
            if (titleText == null) return;

            titleText!.Text = string.IsNullOrWhiteSpace(deck)
                ? (showing ? "正在放映" : "课件续页")
                : deck;

            pageText!.Text = pageNo > 0 ? pageNo.ToString() : "";
            totalText!.Text = totalPages > 0 ? "共 " + totalPages + " 页" : "";

            int rem = RemainingCount();
            string remain;
            if (!showing && pageNo > 0 && hasPrevious) remain = "上次停在这一页";
            else if (showing && totalPages > 0 && rem == 0) remain = "已经讲到最后一页";
            else if (totalPages > 0 && pageNo > 0) remain = "还剩 " + rem + " 页 · 约 " + RemainingMinutesEstimate() + " 分钟";
            else remain = "等待放映";
            remainText!.Text = remain;

            if (buttonText != null && button != null)
            {
                if (pageNo > 0)
                {
                    buttonText.Text = "从第 " + pageNo + " 页接着讲";
                    button.Background = new SolidColorBrush(
                        Color.FromRgb((byte)look.AccentR, (byte)look.AccentG, (byte)look.AccentB));
                    buttonText.Foreground = new SolidColorBrush(Colors.White);
                }
                else
                {
                    buttonText.Text = showing ? "正在识别课件…" : "等待放映";
                    button.Background = new SolidColorBrush(Mix(
                        Color.FromRgb((byte)look.BgR, (byte)look.BgG, (byte)look.BgB),
                        Color.FromRgb((byte)look.TextR, (byte)look.TextG, (byte)look.TextB), 0.06));
                    buttonText.Foreground = new SolidColorBrush(
                        Color.FromRgb((byte)look.MutedR, (byte)look.MutedG, (byte)look.MutedB));
                }
            }

            if (barFill != null)
            {
                double inner = Math.Max(40, win.Width - (look.OnRight ? 14 : 28) - (look.OnRight ? 28 : 14));
                barFill.Width = ProgressWidth(inner, pageNo, totalPages);
            }

            if (footText != null)
            {
                if (historyTotal > 0) footText.Text = "历史 · " + historyTotal + " 次 \u203A";
                else if (!string.IsNullOrWhiteSpace(status)) footText.Text = status;
                else footText.Text = totalPages > 0 ? "第 " + pageNo + " / " + totalPages + " 页" : "等待放映";
            }

            try { UpdateDiag(ScreenFor(win)); } catch { }
        }

        int RemainingCount()
        {
            int rem = remainingPages > 0 ? remainingPages : Math.Max(0, totalPages - pageNo);
            return Math.Max(0, Math.Min(rem, totalPages > 0 ? totalPages - pageNo : rem));
        }

        int RemainingMinutesEstimate()
        {
            if (remainingMinutes > 0) return remainingMinutes;
            return EstimateMinutes(RemainingCount());
        }

        static int EstimateMinutes(int pages)
        {
            return Math.Max(1, (int)Math.Round(pages * 1.5));
        }

        /// <summary>与 NativeOverlayCard.ProgressFillWidth 同一套算法（含「1 页也画 2px」）。</summary>
        static double ProgressWidth(double width, int page, int total)
        {
            if (width <= 0 || total <= 0 || page <= 0) return 0;
            double ratio = page >= total ? 1.0 : page / (double)total;
            double w = Math.Round(width * ratio);
            if (w <= 0) w = Math.Min(width, 2);
            if (w > width) w = width;
            return w;
        }

        void Place()
        {
            if (win == null) return;
            try
            {
                var scr = ScreenFor(win);
                if (scr == null) return;
                var wa = scr.WorkingArea;
                double s = scr.Scaling <= 0 ? 1.0 : scr.Scaling;
                double dw = wa.Width / s, dh = wa.Height / s;
                double dx = wa.X / s, dy = wa.Y / s;
                double W = win.Width, H = win.Height;
                double x = look.OnRight ? dx + dw - W : dx;
                double span = Math.Max(0, dh - H);
                double y = dy + span * Math.Clamp(look.VerticalPercent, 0, 100) / 100.0;
                win.Position = new PixelPoint((int)Math.Round(x * s), (int)Math.Round(y * s));
                UpdateDiag(scr);
            }
            catch (Exception ex) { Note("place failed: " + ex.Message); }
        }

        /// <summary>
        /// 找窗口所在屏幕。窗口还没 Show 过时 ScreenFromWindow 会返回 null（X11 上实测），
        /// 这时退回 Primary；WSL/Weston 下 Primary 也可能要等窗口映射之后才拿得到。
        /// </summary>
        static Screen? ScreenFor(Window w)
        {
            try { return w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary; }
            catch { return null; }
        }

        void UpdateDiag(Screen? scr)
        {
            var w = win;
            if (w == null) { lastDiag = "linux-avalonia window=not-created"; return; }
            string vis;
            try { vis = w.IsVisible ? "yes" : "no"; } catch { vis = "?"; }
            double scale = scr != null && scr.Scaling > 0 ? scr.Scaling : 0;
            lastDiag = "linux-avalonia window=yes created=" + created
                + " visible=" + vis
                + " scale=" + scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " card=" + w.Width + "x" + w.Height
                + " pos=" + w.Position.X + "," + w.Position.Y;
        }

        void ShowWindow()
        {
            if (win == null) return;
            if (!win.IsVisible) win.Show();
            win.Topmost = true;
            created = true;
            // Show 之后窗口才有 X11 句柄/所在屏幕：这一刻再摆一次位置，
            // 否则 X11 会把还没映射的窗口按默认层叠位置（+32+32）放出来。
            Place();
            Post(() => { try { Place(); } catch { } });
        }

        double ScalingOf()
        {
            var scr = win == null ? null : ScreenFor(win);
            return scr != null && scr.Scaling > 0 ? scr.Scaling : 1.0;
        }

        void DropCard()
        {
            if (win == null) return;
            var scr = ScreenFor(win);
            if (scr == null) return;
            var wa = scr.WorkingArea;
            double s = scr.Scaling <= 0 ? 1.0 : scr.Scaling;
            int cx = win.Position.X + (int)Math.Round(win.Width * s / 2.0);
            bool onRight = cx >= wa.X + wa.Width / 2;
            int span = wa.Height - (int)Math.Round(win.Height * s);
            int pct = span > 0 ? (int)Math.Round((win.Position.Y - wa.Y) * 100.0 / span) : 50;
            pct = Math.Clamp(pct, 0, 100);

            // 先就地生效，别等设置回推（那一趟会先闪回旧位置）——和 Windows DropCard 一致。
            look.OnRight = onRight;
            look.VerticalPercent = pct;
            BuildStructure();
            UpdateVisuals();
            Place();

            try { PositionChanged?.Invoke(onRight, pct); } catch { }
            Note("moved: side=" + (onRight ? "right" : "left") + " v=" + pct + "%");
        }

        /// <summary>
        /// 按钮点下后最多等 5 秒：这段时间里 current.json 的页码一直没变（适配器没跑、
        /// 放映软件不响应、或者本来就在这一页），状态行就换成「已请求…等放映软件响应」，
        /// 而不是让老师盯着一个再也没变化的「正在跳…」。
        /// </summary>
        void ArmJumpWait(int page)
        {
            pendingJumpPage = page;
            try
            {
                jumpWaitTimer?.Stop();
                jumpWaitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                jumpWaitTimer.Tick += (_, _) =>
                {
                    try { jumpWaitTimer?.Stop(); } catch { }
                    if (disposed) return;
                    if (pageNo == pendingJumpPage)
                    {
                        status = "已请求跳到第 " + pendingJumpPage + " 页，等放映软件响应…";
                        RenderNow();
                        Note("jump wait: 第 " + pendingJumpPage + " 页 5 秒内没变化");
                    }
                };
                jumpWaitTimer.Start();
            }
            catch (Exception ex) { Note("jump wait failed: " + ex.Message); }
        }

        void FinishJumpWait()
        {
            pendingJumpPage = 0;
            try { jumpWaitTimer?.Stop(); } catch { }
            jumpWaitTimer = null;
        }

        /// <summary>跳页成功（页码变了）时把临时状态清掉，底部回到底部页码行。</summary>
        void ClearTransientJumpStatus()
        {
            string s = status ?? "";
            if (s.StartsWith("正在跳到", StringComparison.Ordinal)
                || s.StartsWith("已请求跳到", StringComparison.Ordinal))
            {
                status = "";
            }
        }

        void RestartHideTimer()
        {
            if (win == null) return;
            if (!autoHideEnabled)
            {
                hideTimer?.Stop();
                return;
            }
            if (hideTimer == null)
            {
                hideTimer = new DispatcherTimer();
                hideTimer.Tick += (_, _) =>
                {
                    try
                    {
                        hideTimer?.Stop();
                        if (win != null && win.IsVisible) { win.Hide(); Note("auto-hidden"); }
                    }
                    catch { }
                };
            }
            hideTimer.Stop();
            int seconds = Math.Clamp(autoHideSeconds, 5, 600);
            if (previewUntil > DateTime.UtcNow)
                seconds = Math.Max(seconds, (int)Math.Ceiling((previewUntil - DateTime.UtcNow).TotalSeconds));
            hideTimer.Interval = TimeSpan.FromSeconds(seconds);
            hideTimer.Start();
        }

        void Post(Action action)
        {
            try
            {
                if (Dispatcher.UIThread.CheckAccess()) action();
                else Dispatcher.UIThread.Post(() => { try { action(); } catch { } });
            }
            catch { }
        }

        void Note(string message)
        {
            try { Log?.Invoke("[linux-card] " + message); } catch { }
        }

        static void Add(Canvas c, Control child, double x, double y)
        {
            Canvas.SetLeft(child, x);
            Canvas.SetTop(child, y);
            c.Children.Add(child);
        }

        static Control BuildGear(double size, IBrush brush)
        {
            var c = new Canvas { Width = size, Height = size };
            double cx = size / 2.0, cy = size / 2.0;
            double ring = size * 0.46;
            double toothW = size * 0.13, toothH = size * 0.22;
            for (int i = 0; i < 8; i++)
            {
                var tooth = new Border
                {
                    Width = toothW,
                    Height = toothH,
                    Background = brush,
                    CornerRadius = new CornerRadius(size * 0.03),
                };
                Canvas.SetLeft(tooth, cx - toothW / 2.0);
                Canvas.SetTop(tooth, cy - ring / 2.0 - toothH * 0.62);
                tooth.RenderTransform = new RotateTransform(i * 45.0, cx, cy);
                c.Children.Add(tooth);
            }
            var ellipse = new Ellipse
            {
                Width = ring,
                Height = ring,
                Stroke = brush,
                StrokeThickness = Math.Max(1.4, size * 0.10),
            };
            Canvas.SetLeft(ellipse, cx - ring / 2.0);
            Canvas.SetTop(ellipse, cy - ring / 2.0);
            c.Children.Add(ellipse);
            return c;
        }

        static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(255,
                (byte)Math.Round(a.R + (b.R - a.R) * t),
                (byte)Math.Round(a.G + (b.G - a.G) * t),
                (byte)Math.Round(a.B + (b.B - a.B) * t));
        }
    }
}
