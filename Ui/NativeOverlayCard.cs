using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Ui
{
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

        bool shown;
        DateTime previewUntil = DateTime.MinValue;

        Thread thread;
        IntPtr hwnd;
        IntPtr pumpHwnd;
        uint taskbarCreated;
        WndProcDelegate proc;

        static readonly System.Collections.Generic.List<WndProcDelegate> classRoots =
            new System.Collections.Generic.List<WndProcDelegate>();

        static int classSeq;
        readonly string className = "ClassSlideSyncCard" + System.Threading.Interlocked.Increment(ref classSeq);
        bool quit;

        const int BaseCardWidth = 236;
        const int BaseCardHeight = 150;
        const int BaseTabWidth = 32;
        const int MaxHistoryRows = 6;
        const int MaxFileRows = 8;
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

        CardAppearance look = CardAppearance.Resolve(null, null);
        bool fontInstalled;
        string fontNote = "";
        bool expanded;

        bool historyPanel;
        Rect32 historyHit;
        Rect32 backHit;
        readonly Rect32[] historyRowHits = new Rect32[MaxHistoryRows];
        readonly int[] historyRowSlides = new int[MaxHistoryRows];
        CardFileItem? selectedFile;
        bool historyBackToFiles;

        bool filePanel;
        Rect32 folderHit;
        readonly Rect32[] fileRowHits = new Rect32[MaxFileRows];

        bool dragArmed;
        bool dragging;
        POINT dragStartCursor;
        RECT dragStartRect;

        bool expandedForDeck;
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

        public event Action JumpRequested;

        public event Action<string, int> HistoryJumpRequested;

        public event Action<bool, int> PositionChanged;

        public void Start() { thread.Start(); }

        volatile string lastError = "";

        public string LastError { get { return lastError; } }

        void Fail(string where)
        {
            int code = Marshal.GetLastWin32Error();
            lastError = where + " 失败，Win32 错误码 " + code;
        }

        public bool IsCreated { get { return hwnd != IntPtr.Zero; } }

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
                + " hist=" + HistoryTotal(now) + "/" + HistoryRows(now)
                + " files=" + (now == null || now.Files == null ? 0 : now.Files.Count) + "/" + FileRows(now)
                + " prev=" + (now == null ? 0 : now.Previous) + (historyPanel ? " panel" : "") + (filePanel ? " filesPanel" : "")
                + " stage=" + stage
                + " | look: " + look.Describe() + (fontNote.Length > 0 ? "  << " + fontNote : "");
        }

        int paintCount;
        string paintError = "";

        Rect32 jumpHit;
        Rect32 collapseHit;      // 小窗右上角那个"收回"箭头，点它就把卡片贴回屏幕边
        bool showingCard;
        volatile bool wakePending;
        int applyCount;
        int appliedHash = int.MinValue;
        public Action<string> Log;
        volatile string stage = "new";

        public void Configure(CardAppearance appearance, bool showOnStart, bool autoHide, int seconds)
        {
            look = appearance ?? CardAppearance.Resolve(null, null);
            autoShow = showOnStart;
            autoHideEnabled = autoHide;
            autoHideSeconds = seconds < 5 ? 5 : (seconds > 600 ? 600 : seconds);
            RestartHideTimer();
            Wake(WM_APP_APPLY);
        }

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
            previewUntil = DateTime.UtcNow.AddSeconds(20);
            Wake(WM_APP_APPLY);
        }

        void Wake(int msg)
        {
            IntPtr target = pumpHwnd != IntPtr.Zero ? pumpHwnd : hwnd;
            if (target != IntPtr.Zero) { PostMessage(target, msg, IntPtr.Zero, IntPtr.Zero); return; }
            wakePending = true;
        }

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
                classRoots.Add(proc);

                pumpHwnd = CreateWindowEx(0, className, "pump", WS_POPUP,
                    0, 0, 0, 0, new IntPtr(-3) , IntPtr.Zero, hInst, IntPtr.Zero);
                if (pumpHwnd == IntPtr.Zero) { Fail("CreateWindowEx(pump)"); return; }

                ApplyWorkArea(WorkingArea());

                hwnd = CreateWindowEx(
                    WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED,
                    className, "课件续页", WS_POPUP,
                    screenRight - tabWidth, screenTop + (screenHeight - cardHeight) / 2,
                    cardWidth, cardHeight, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) { Fail("CreateWindowEx(card)"); return; }

                dpi = DpiOf(hwnd);
                ApplyMetrics();
                ApplyLook();

                expanded = false;
                Place();
                ShowWindow(hwnd, SW_HIDE);
                shown = false;

                wakePending = false;
                try { ApplyPending(); }
                catch (Exception ex)
                {
                    lastError = "首次应用状态失败：" + ex.GetType().Name + " " + ex.Message;
                }

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

        int FileRows(CardState st)
        {
            if (st == null || st.Files == null) return 0;
            return Math.Min(st.Files.Count, MaxFileRows);
        }

        int ContentHeight96(CardState st)
        {
            int h = 10;
            bool showTopName = look.ShowFileName && !filePanel && !(historyPanel && selectedFile != null);
            if (showTopName) h += 22;
            if (filePanel)
            {
                return h + 26 + Math.Max(1, FileRows(st)) * 26 + 26 + 10;
            }
            if (historyPanel)
            {
                return h + 26 + HistoryRows(st) * 26 + 26 + 10;
            }
            h += 50;
            h += 22;
            if (look.ShowJumpLine && st != null && st.Previous > 0) h += 26;
            if (BitsText(st).Length > 0) h += 18;
            if (HistoryTotal(st) > 0) h += 26;
            if (look.ShowStatusLine) h += 18;
            return h + 10;
        }

        string BitsText(CardState st)
        {
            if (st == null || !look.ShowContext) return "";
            var bits = st.Context.Length > 0 ? st.Context : "";
            if (st.Hint.Length > 0) bits += (bits.Length > 0 ? "  ·  " : "") + st.Hint;
            return bits;
        }

        void ApplyContentHeight(CardState st)
        {
            int h96 = look.CardHeight;
            if (expanded)
            {
                int need = ContentHeight96(st);
                if (historyPanel || filePanel) h96 = need;
                else if (need > h96) h96 = need;
            }
            int h = Scaled(h96);
            if (screenHeight > 0 && h > screenHeight) h = screenHeight;
            if (h > 0) cardHeight = h;
        }

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

        void ApplyWindowRegion()
        {
            if (hwnd == IntPtr.Zero) return;
            int r = Scaled(look.Radius);
            try
            {
                Rect32 visible = expanded ? new Rect32(0, 0, cardWidth, cardHeight) : CollapsedPill();
                IntPtr rgn = r > 0
                    ? CreateRoundRectRgn(visible.L, visible.T, visible.R + 1, visible.B + 1, r * 2, r * 2)
                    : CreateRectRgn(visible.L, visible.T, visible.R + 1, visible.B + 1);
                if (rgn == IntPtr.Zero) return;

                if (r > 0 && visible.B - visible.T > r * 2)
                {
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
            }
            catch { }
        }

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
                int alpha = look.BgAlpha;
                if (!expanded)
                {
                    alpha = 128;
                }

                SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Max(20, Math.Min(255, alpha)), LWA_ALPHA);
            }
            catch { }
        }

        static int DpiOf(IntPtr hWnd)
        {
            try
            {
                int d = GetDpiForWindow(hWnd);
                if (d >= 48 && d <= 960) return d;
            }
            catch
            {
            }
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                int d = GetDeviceCaps(dc, 88 );
                if (d >= 48 && d <= 960) return d;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return 96;
        }

        void ApplyMetrics()
        {
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
            ApplyContentHeight(st);
            Place();
            ApplyWindowRegion();
            ApplyAlpha();
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        void DoCollapse()
        {
            if (!expanded) return;
            expanded = false;
            ResetPanels();
            CardState st;
            lock (gate) { st = pending; }
            ApplyContentHeight(st);
            Place();
            ApplyWindowRegion();
            ApplyAlpha();
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        void ResetPanels()
        {
            filePanel = false;
            historyPanel = false;
            selectedFile = null;
            historyBackToFiles = false;
        }

        void Relayout()
        {
            CardState st;
            lock (gate) { st = pending; }
            ApplyContentHeight(st);
            Place();
            ApplyWindowRegion();
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        void OpenFilePanel()
        {
            if (filePanel) return;
            filePanel = true;
            historyPanel = false;
            selectedFile = null;
            historyBackToFiles = false;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();
            Relayout();
        }

        void CloseFilePanel()
        {
            if (!filePanel) return;
            filePanel = false;
            selectedFile = null;
            Relayout();
        }

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
            historyPanel = true;
            filePanel = false;
            selectedFile = null;
            historyBackToFiles = false;
            lastActivity = DateTime.UtcNow;
            RestartHideTimer();
            Relayout();
        }

        void CloseHistoryPanel()
        {
            if (!historyPanel) return;
            if (historyBackToFiles)
            {
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

        void OnConsistencyCheck(object _)
        {
            try
            {
                CardState st;
                lock (gate) { st = pending; }
                bool want = st.DeckOpen || DateTime.UtcNow < previewUntil;
                if (st.Hash() != appliedHash) { Wake(WM_APP_APPLY); return; }
                if (want != shown) { Wake(WM_APP_APPLY); return; }
                if (want && !expanded && !expandedForDeck) Wake(WM_APP_APPLY);
            }
            catch { }
        }

        IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_MOUSEACTIVATE:
                        lastActivity = DateTime.UtcNow;
                        return (IntPtr)MA_NOACTIVATE;

                    case WM_SETCURSOR:
                        SetCursor(LoadCursor(IntPtr.Zero, IDC_HAND));
                        return (IntPtr)1;

                    case WM_DPICHANGED:
                        dpi = (int)(wParam.ToInt64() & 0xFFFF);
                        ApplyWorkArea(WorkingArea());
                        ApplyLook();
                        Place();
                        InvalidateRect(hWnd, IntPtr.Zero, false);
                        return IntPtr.Zero;

                    case WM_PAINT:
                        Paint(hWnd);
                        return IntPtr.Zero;

                    case WM_ERASEBKGND:
                        return (IntPtr)1;

                    case WM_LBUTTONDOWN:
                        lastActivity = DateTime.UtcNow;
                        RestartHideTimer();
                        BeginDrag(hWnd);
                        return IntPtr.Zero;

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
                            return IntPtr.Zero;
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
            bool hasDeck = st.DeckOpen;
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
            if (dragging || dragArmed)
            {
                shouldShow = true;
            }
            if (!shouldShow) ResetPanels();
            else if (historyPanel && !historyBackToFiles && HistoryTotal(st) == 0) historyPanel = false;

            appliedHash = st.Hash();
            ApplyLook();
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
                expandedForDeck = false;
                return;
            }

            if (autoShow && (st.Showing || hasDeck || preview))
            {
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
                expandedForDeck = false;
                lastDeckKey = "";          // 同一份课件重新打开也算新的一轮，要再弹一次
            }
            if (expanded)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
            if (Inside(collapseHit, x, y)) { DoCollapse(); return; }

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
                return;
            }

            if (historyPanel)
            {
                CardState hs;
                lock (gate) { hs = pending; }
                var items = HistoryItems(hs);
                for (int i = 0; i < MaxHistoryRows; i++)
                {
                    int slide = historyRowSlides[i];
                    if (slide <= 0 || !Inside(historyRowHits[i], x, y)) continue;
                    string filePath = items != null && i < items.Count ? items[i].FilePath : "";
                    CloseHistoryPanel();
                    var jump = HistoryJumpRequested;
                    if (jump != null) jump(filePath ?? "", slide);
                    return;
                }
                if (Inside(backHit, x, y)) { CloseHistoryPanel(); return; }
                return;
            }

            if (folderHit.R > folderHit.L && Inside(folderHit, x, y)) { OpenFilePanel(); return; }

            if (historyHit.R > historyHit.L && Inside(historyHit, x, y)) { OpenHistoryPanel(); return; }

            if (!look.ClickToJump) return;
            if (showingCard && !Inside(jumpHit, x, y)) return;
            var h = JumpRequested;
            if (h != null) h();
        }

        void DrawFolderIcon(GdiCanvas g, Rect32 r, Color32 c)
        {
            int s = Math.Max(1, (r.B - r.T) / 12);   // 24px 时 s=2
            var body = Color32.FromArgb(255, 0xF7, 0xC3, 0x3F);
            var lid = Color32.FromArgb(255, 0xFF, 0xDD, 0x7A);
            var edge = Color32.FromArgb(255, 0xE0, 0xA5, 0x1E);

            g.FillRounded(body, new Rect32(r.L + 2 * s, r.T + 4 * s, r.R - 2 * s, r.B - 2 * s), 2 * s, true, true);
            g.FillRounded(body, new Rect32(r.L + 2 * s, r.T + 2 * s, r.L + 7 * s, r.T + 6 * s), s, true, true);
            g.FillRounded(lid, new Rect32(r.L + 3 * s, r.T + 5 * s, r.R - 3 * s, r.T + 7 * s), 0, false, false);
            g.StrokeRounded(edge, new Rect32(r.L + 2 * s, r.T + 4 * s, r.R - 2 * s, r.B - 2 * s), 2 * s, true, true);
        }

        static bool Inside(Rect32 r, int x, int y)
        {
            return x >= r.L && x < r.R && y >= r.T && y < r.B;
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

                IntPtr memDc = CreateCompatibleDC(hdc);
                IntPtr bmp = CreateCompatibleBitmap(hdc, w, h);
                IntPtr oldBmp = SelectObject(memDc, bmp);
                try
                {
                    using (var g = new GdiCanvas(memDc, w, h, dpi, look))
                    {
                        var bg = Color32.FromArgb(255, (byte)look.BgR, (byte)look.BgG, (byte)look.BgB);
                        var accent = Color32.FromArgb(255, (byte)look.AccentR, (byte)look.AccentG, (byte)look.AccentB);
                        var text = Color32.FromArgb(255, (byte)look.TextR, (byte)look.TextG, (byte)look.TextB);
                        var muted = Color32.FromArgb(255, (byte)look.MutedR, (byte)look.MutedG, (byte)look.MutedB);
                        var jumpFill = Color32.Mix(bg, accent, 0.22);
                        var hairline = Color32.Mix(bg, text, 0.12);
                        int radius = Scaled(look.Radius);
                        g.Clear(bg);

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
                            var pill = CollapsedPill();
                            jumpHit = new Rect32(0, 0, w, h);
                            collapseHit = new Rect32(0, 0, 0, 0);
                            showingCard = st.Showing;
                            bool rl = look.OnRight, rr = !look.OnRight;
                            g.FillRounded(bg, pill, radius, rl, rr);
                            g.StrokeRounded(hairline, pill, radius, rl, rr);
                            g.Text(look.OnRight ? "\u276E" : "\u276F", g.FontHuge, Color32.FromArgb(255, 0, 0, 0), pill, Align.Center, true);
                        }
                        else
                        {
                            bool rl = !look.OnRight, rr = look.OnRight;
                            g.StrokeRounded(hairline, new Rect32(0, 0, w, h), radius, rl, rr);

                            int pad = Scaled(14);
                            int inner = w - pad * 2;
                            int y = Scaled(10);
                            int lineSmall = Scaled(20), stepSmall = Scaled(22);
                            int lineBig = Scaled(50), stepBig = Scaled(50);
                            int lineTiny = Scaled(18), stepTiny = Scaled(18);
                            int lineJump = Scaled(22), stepJump = Scaled(26);

                            bool panelTwo = historyPanel && selectedFile != null;
                            if (look.ShowFileName && !filePanel && !panelTwo)
                            {
                                string title = st.FileName.Length > 0 ? st.FileName : (st.Showing ? "正在放映" : "课件续页");
                                g.Text(g.Ellipsis(title, g.FontSmall, inner - Scaled(22)), g.FontSmall, muted,
                                       new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                y += stepSmall;
                            }

                            if (filePanel)
                            {
                                g.Text("课件库", g.FontTiny, muted,
                                       new Rect32(pad, y, w - pad, y + lineSmall), Align.Left, false);
                                int sepY = y + lineSmall - Scaled(2);
                                g.FillRounded(hairline, new Rect32(pad, sepY, w - pad, sepY + Math.Max(1, Scaled(1))), 0, false, false);
                                y += stepJump;

                                int rows = FileRows(st);
                                if (rows == 0)
                                {
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
                                    if (f.IsCurrent)
                                    {
                                        g.FillRounded(Color32.Mix(bg, accent, 0.10), row, Scaled(6), true, true);
                                        g.FillRounded(accent, new Rect32(row.L + Scaled(2), y + Scaled(4), row.L + Scaled(4), y + lineJump - Scaled(4)), Scaled(1), true, true);
                                    }
                                    int detailW = g.TextWidth(f.Detail, g.FontTiny);
                                    int nameW = inner - detailW - Scaled(14);
                                    if (nameW < Scaled(24)) nameW = inner;
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
                                var items = HistoryItems(st);
                                int total = selectedFile != null ? selectedFile.Total : HistoryTotal(st);
                                string head = selectedFile != null && !string.IsNullOrEmpty(selectedFile.DisplayName)
                                    ? selectedFile.DisplayName
                                    : (total > 0 ? "本课件历史 · " + total + " 次" : "本课件历史");
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

                                showingCard = st.Showing || st.SuiteOnly;
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

                                string bits = BitsText(st);
                                if (bits.Length > 0)
                                {
                                    g.Text(g.Ellipsis(bits, g.FontTiny, inner), g.FontTiny, muted,
                                           new Rect32(pad, y, w - pad, y + lineTiny), Align.Left, false);
                                    y += stepTiny;
                                }

                                int total = HistoryTotal(st);
                                if (total > 0)
                                {
                                    string hist = "历史 · " + total + " 次 \u203A";
                                    historyHit = new Rect32(pad - Scaled(7), y - Scaled(2), w - pad + Scaled(7), y + lineJump - Scaled(2));
                                    g.Text(g.Ellipsis(hist, g.FontTiny, inner), g.FontTiny, muted,
                                           new Rect32(pad, y, w - pad, y + lineJump), Align.Left, false);
                                    y += stepJump;
                                }

                                if (look.ShowStatusLine)
                                {
                                    if (status.Length > 0)
                                        g.Text(g.Ellipsis(status, g.FontTiny, inner), g.FontTiny, accent,
                                               new Rect32(pad, y, w - pad, y + lineTiny), Align.Left, false);
                                    y += stepTiny;
                                }
                            }

                            var arrow = new Rect32(w - pad - Scaled(16), Scaled(6), w - pad + Scaled(2), Scaled(6) + Scaled(18));
                            collapseHit = arrow;
                            g.Text(look.OnRight ? "\u276F" : "\u276E", g.FontSmall, muted, arrow, Align.Center, false);

                            int icon = Scaled(FolderIconSize);
                            int iconX = arrow.L - Scaled(10) - icon;
                            int iconY = Scaled(6) + (Scaled(18) - icon) / 2;
                            var iconRect = new Rect32(iconX, iconY, iconX + icon, iconY + icon);
                            DrawFolderIcon(g, iconRect, muted);
                            folderHit = new Rect32(iconX - Scaled(6), iconY - Scaled(6), iconX + icon + Scaled(6), iconY + icon + Scaled(6));
                        }
                    }

                    BitBlt(hdc, 0, 0, w, h, memDc, 0, 0, 0x00CC0020 );
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
            if (SystemParametersInfo(0x0030 , 0, out r, 0))
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
        [DllImport("user32.dll")]
        static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

        static int Rgb(byte r, byte g, byte b) { return r | (g << 8) | (b << 16); }

        struct Color32
        {
            public byte A, R, G, B;
            public static Color32 FromArgb(byte a, byte r, byte g, byte b) { return new Color32 { A = a, R = r, G = g, B = b }; }
            public static Color32 White { get { return FromArgb(255, 255, 255, 255); } }

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

            static int FontHeight(int points, int dpi) { return -(int)Math.Round(points * dpi / 96.0); }

            public GdiCanvas(IntPtr dc, int w, int h, int dpi, CardAppearance look)
            {
                this.dc = dc; width = w; height = h;
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
                nullPen = CreatePen(5 , 0, 0);
                SelectObject(dc, nullPen);
            }

            public IntPtr FontSmall { get { return fontSmall; } }
            public IntPtr FontBig { get { return fontBig; } }
            public IntPtr FontTiny { get { return fontTiny; } }
            public IntPtr FontHuge { get { return fontHuge; } }

            public void Clear(Color32 c)
            {
                RECT r = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
                IntPtr b = CreateSolidBrush(Rgb(c.R, c.G, c.B));
                try { FillRect(dc, ref r, b); }
                finally { DeleteObject(b); }
            }

            public void FillRounded(Color32 c, Rect32 r, int radius, bool roundLeft, bool roundRight)
            {
                if (c.A < 255) { FillSolid(c, r); return; }
                using (var b = new Brush(dc, c))
                {
                    if (roundLeft || roundRight) RoundRect(dc, r.L, r.T, r.R, r.B, radius * 2, radius * 2);
                    else Rectangle(dc, r.L, r.T, r.R, r.B);
                }
                if (roundLeft != roundRight)
                {
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
                IntPtr oldBrush = SelectObject(dc, GetStockObject(5 ));
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
