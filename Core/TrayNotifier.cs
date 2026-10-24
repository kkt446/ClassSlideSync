using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 弹一条 Windows 系统通知（右下角气泡/通知中心），不依赖打包、不依赖 NuGet。
    /// 做法是最小化的 Shell_NotifyIcon(NIF_INFO)：造一个 message-only 窗口 + 临时托盘图标，
    /// 把标题和正文挂在气泡上；气泡自己会消失，图标由定时器在调用方指定的时长后摘掉。
    /// 不走 ToastNotificationManager 的原因：那个要 net8.0-windows10.0.x 目标框架或
    /// Microsoft.Toolkit 依赖，而插件的目标框架固定为 net8.0-windows，不能为一个通知改 TFM。
    /// </summary>
    internal static class TrayNotifier
    {
        const int NIM_ADD = 0;
        const int NIM_DELETE = 2;

        const int NIF_MESSAGE = 0x1;
        const int NIF_ICON = 0x2;
        const int NIF_TIP = 0x4;
        const int NIF_INFO = 0x10;

        const int NIIF_INFO = 0x1;

        const int IDI_INFORMATION = 32516;

        const int WM_APP = 0x8000;

        static readonly object gate = new object();
        static bool classReady;
        static ushort classAtom;

        // 窗口类注册一次就活到进程结束，WndProc 必须用静态字段钉住，否则 GC 一收就崩
        // （这个坑在 NativeOverlayCard 上踩过一次：FailFast "callback was made on a garbage collected delegate"）。
        static WndProcDelegate classProc;
        static int seq;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string? lpModuleName);

        /// <summary>
        /// 弹通知。标题过长会被系统截断，正文建议控制在一句话内。
        /// <paramref name="durationSeconds"/> 同时写进 uTimeoutOrVersion 并作为摘图标的延时：
        /// Windows Vista 以后气泡的实际停留由系统「通知」设置决定（uTimeout 被忽略），
        /// 但托盘图标的保留时间仍按这里走，勾掉置顶时老师能感知到「停留时长」的差别。
        /// 任何失败都静默返回 false——通知不是主功能，不能因为它影响录制。
        /// </summary>
        internal static bool Show(string title, string text, int durationSeconds = 15)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            int seconds = Math.Clamp(durationSeconds, 3, 120);
            int timeoutMs = seconds * 1000;
            try
            {
                lock (gate)
                {
                    IntPtr hwnd = CreateMessageOnlyWindow();
                    if (hwnd == IntPtr.Zero) return false;

                    var data = new NOTIFYICONDATAW
                    {
                        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                        hWnd = hwnd,
                        uID = 1,
                        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO,
                        uCallbackMessage = WM_APP + 1,
                        hIcon = LoadIconW(IntPtr.Zero, new IntPtr(IDI_INFORMATION)),
                        szTip = "课件续页",
                        szInfo = Trim(text, 250),
                        uTimeoutOrVersion = timeoutMs,
                        szInfoTitle = Trim(title, 60),
                        dwInfoFlags = NIIF_INFO,
                    };

                    if (!Shell_NotifyIconW(NIM_ADD, ref data))
                    {
                        DestroyWindow(hwnd);
                        return false;
                    }

                    // 气泡由系统自己收尾，图标必须我们自己摘，否则托盘里会攒一串小图标。
                    var timer = new Timer(_ => Retire(hwnd), null, timeoutMs, Timeout.Infinite);
                    GC.KeepAlive(timer);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void Retire(IntPtr hwnd)
        {
            try
            {
                lock (gate)
                {
                    var data = new NOTIFYICONDATAW
                    {
                        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                        hWnd = hwnd,
                        uID = 1,
                    };
                    Shell_NotifyIconW(NIM_DELETE, ref data);
                    DestroyWindow(hwnd);
                }
            }
            catch (Exception)
            {
            }
        }

        static IntPtr CreateMessageOnlyWindow()
        {
            if (!classReady)
            {
                IntPtr hInstance = GetModuleHandleW(null);
                classProc = DefWindowProcW;
                var wc = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(classProc),
                    hInstance = hInstance,
                    lpszClassName = "ClassSlideSyncToast" + Interlocked.Increment(ref seq),
                };
                classAtom = RegisterClassExW(ref wc);
                if (classAtom == 0) return IntPtr.Zero;
                classReady = true;
            }

            // HWND_MESSAGE(-3)：只收消息、不上屏、不进 Alt+Tab。
            return CreateWindowExW(0, "ClassSlideSyncToast" + seq, "", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        }

        /// <summary>按字符截断并补省略号；代理项不能从中间切开。</summary>
        static string Trim(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s!.Length <= max) return s;
            int cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut) + "…";
        }
    }
}
