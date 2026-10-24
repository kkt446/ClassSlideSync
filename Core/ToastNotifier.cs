using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 操作中心横幅（Windows toast）通知。
    ///
    /// 为什么要有这个文件，而不是像以前一样只用 <see cref="TrayNotifier"/>：
    /// 托盘气泡（Shell_NotifyIcon + NIF_INFO）在 Win11 上只是「通知中心里的一条历史」，
    /// 不弹横幅、也没有声音，下课提醒老师根本看不见。用户 2026-10-24 要的是真正的横幅。
    ///
    /// 为什么横幅要放在一个独立的小工具里（Tools\ToastHelper\ToastHelper.exe）：
    /// 弹 toast 需要 WinRT 投影（Windows.UI.Notifications + Windows.Data.Xml.Dom），
    /// 那是 net8.0-windows10.0.19041.0 起才有的 API；而插件的目标框架被 ClassIsland 的动态加载
    /// 钉死在 net8.0-windows，不能改，也不给插件加 NuGet。所以让 helper 用自己的 TFM 单独编译，
    /// 插件只在命令行上调用它——插件侧一行 WinRT 代码都不沾。
    ///
    /// 为什么 helper 要自注册（HKCU + 开始菜单快捷方式）：
    /// Win32 程序想弹 toast，系统必须能按 AppUserModelID 认出「是哪个应用」——
    /// 即 HKCU\Software\Classes\AppUserModelId\&lt;id&gt; 里的 DisplayName/IconUri，
    /// 再加一个带 System.AppUserModel.ID 属性的开始菜单快捷方式。这两步全在用户态，
    /// 不需要管理员权限，helper 自己首次运行时就做掉（见 Tools\ToastHelper\ToastRegistration.cs）。
    ///
    /// 整个类只做一件事：把「横幅能不能用」判断清楚，能就用，不能就一声不响地返回 false，
    /// 让调用方退回托盘气泡。通知不是主功能，任何失败都不许拖累录制。
    /// </summary>
    internal static class ToastNotifier
    {
        /// <summary>
        /// 标记文件里的协议版本。helper 的注册方式/命令行变了就 +1：
        /// 插件看到版本对不上会重跑一次登记，老师升级插件后不用手动清文件。
        /// </summary>
        const string MarkerVersion = "1";

        /// <summary>连续失败这么多次就本次会话不再尝试，免得每次下课都白起一个进程。</summary>
        const int GiveUpAfterFailures = 3;

        static readonly object gate = new object();

        static string _helperPath = "";
        static string _markerPath = "";
        static string _helperLogPath = "";
        static string _logPath = "";

        /// <summary>本进程里已经确认可用（标记文件命中，或者刚登记成功）。</summary>
        static volatile bool _ready;

        static int _failures;

        /// <summary>横幅可用吗（helper 在、且登记过）。没准备好时调用方直接走托盘气泡。</summary>
        internal static bool IsReady => _ready && File.Exists(_helperPath);

        /// <summary>
        /// 插件初始化时调用一次：算出 helper 路径、后台跑一次自注册、把结果写进标记文件。
        ///
        /// 为什么要开线程：登记要起一个 helper 进程（几十毫秒，冷启动可能一两百毫秒），
        /// 而 Initialize 是在 ClassIsland 启动链上的，不能因为一条通知把启动拖慢。
        /// </summary>
        internal static void InstallOnce(string configFolder)
        {
            try
            {
                ResolvePaths(configFolder);
            }
            catch (Exception ex)
            {
                QuietLog("横幅：算路径失败（忽略，继续用托盘气泡）：" + ex.Message);
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_helperPath))
                    {
                        // 老版本/手工删过：安静回退，不报错、不弹窗。这是最常见的「没打包进去」情形。
                        QuietLog("横幅：helper 不在（" + _helperPath + "），用托盘气泡");
                        return;
                    }

                    if (MarkerIsCurrent())
                    {
                        _ready = true;
                        QuietLog("横幅：标记文件说已经登记过（" + _markerPath + "），跳过");
                        return;
                    }

                    if (RunHelper("--register-only --log " + QuoteArg(_helperLogPath), 15000, out int code) && code == 0)
                    {
                        _ready = true;
                        WriteMarker();
                        QuietLog("横幅：helper 自注册完成（HKCU + 开始菜单快捷方式）");
                    }
                    else
                    {
                        QuietLog("横幅：helper 登记失败 exit=" + code + "，用托盘气泡（详见 " + _helperLogPath + "）");
                    }
                }
                catch (Exception ex)
                {
                    QuietLog("横幅：登记异常（忽略）：" + ex.Message);
                }
            });
        }

        /// <summary>
        /// 弹一条横幅。成功 true；没准备好/被系统关掉/helper 挂了都返回 false（调用方退托盘气泡）。
        /// </summary>
        internal static bool Show(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            if (!IsReady) return false;
            if (Volatile.Read(ref _failures) >= GiveUpAfterFailures) return false;

            lock (gate)
            {
                try
                {
                    if (!File.Exists(_helperPath))
                    {
                        // 插件目录被清过（ClassIsland 卸载/更新）：别每节课都白试一次。
                        _ready = false;
                        QuietLog("横幅：helper 突然不见了，本次会话改用托盘气泡");
                        return false;
                    }

                    if (!RunHelper("--title " + QuoteArg(title) + " --body " + QuoteArg(body)
                                   + " --log " + QuoteArg(_helperLogPath), 8000, out int code))
                    {
                        Interlocked.Increment(ref _failures);
                        QuietLog("横幅：helper 超时/启动失败，用托盘气泡");
                        return false;
                    }

                    if (code == 0)
                    {
                        Volatile.Write(ref _failures, 0);
                        return true;
                    }

                    Interlocked.Increment(ref _failures);
                    if (code == 31)
                    {
                        // 系统的「通知」总开关被关了：横幅永远不会出现，直接退托盘气泡更靠谱。
                        QuietLog("横幅：系统通知被关掉了（helper exit=31），用托盘气泡");
                    }
                    else
                    {
                        QuietLog("横幅：helper 失败 exit=" + code + "，用托盘气泡（详见 " + _helperLogPath + "）");
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failures);
                    QuietLog("横幅：异常（忽略）：" + ex.Message);
                    return false;
                }
            }
        }

        // ---------- 路径与标记文件 ----------

        static void ResolvePaths(string configFolder)
        {
            // 宿主没给配置目录时别用相对路径：那会把标记文件/日志落到 ClassIsland 进程的当前目录里，
            // 多半没写权限，还会把文件撒到别人的目录里。退到 LocalAppData，至少是用户自己的、可写的。
            if (string.IsNullOrWhiteSpace(configFolder))
            {
                configFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClassSlideSync.Plugin");
            }

            _markerPath = Path.Combine(configFolder, "toast-installed.txt");
            _logPath = Path.Combine(configFolder, "toast-plugin.log");
            _helperLogPath = Path.Combine(configFolder, "toast-helper.log");

            // 必须用插件 dll 自己的位置，不能用 AppContext.BaseDirectory：
            // 那是 ClassIsland 进程目录，插件 dll 在 data\Plugins\<插件名>\ 里。
            string dir = Path.GetDirectoryName(typeof(ToastNotifier).Assembly.Location) ?? "";
            _helperPath = Path.Combine(dir, "ToastHelper", "ToastHelper.exe");
        }

        /// <summary>标记文件是不是「本版本、同一个 helper、文件还在」。三条都满足才跳过登记。</summary>
        static bool MarkerIsCurrent()
        {
            try
            {
                if (!File.Exists(_markerPath)) return false;
                string[] lines = File.ReadAllLines(_markerPath);
                string version = "";
                string helper = "";
                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Equals("version", StringComparison.OrdinalIgnoreCase)) version = val;
                    else if (key.Equals("helper", StringComparison.OrdinalIgnoreCase)) helper = val;
                }

                return version == MarkerVersion
                       && helper.Equals(_helperPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        static void WriteMarker()
        {
            try
            {
                File.WriteAllLines(_markerPath, new[]
                {
                    "version=" + MarkerVersion,
                    "helper=" + _helperPath,
                    "installed=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                }, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                QuietLog("横幅：写标记文件失败（不影响使用，下次会再登记一次）：" + ex.Message);
            }
        }

        // ---------- 起进程 ----------

        /// <summary>起 helper 并等它结束。超时直接杀掉：通知宁可晚一拍，也不能把调度线程卡住。</summary>
        static bool RunHelper(string args, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            var psi = new ProcessStartInfo(_helperPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(_helperPath) ?? "",
            };

            using (Process? p = Process.Start(psi))
            {
                if (p == null) return false;
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                exitCode = p.ExitCode;
                return true;
            }
        }

        /// <summary>
        /// 按 Windows 命令行解析规则加引号：文字里可能有中文引号、路径里可能有空格，
        /// 标题里的「"」如果直接塞进去会被拆成两个参数，helper 就会当成参数错误退出。
        /// </summary>
        static string QuoteArg(string s)
        {
            var sb = new StringBuilder("\"");
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\')
                {
                    int j = i;
                    while (j < s.Length && s[j] == '\\') j++;
                    int n = j - i;
                    if (j == s.Length) sb.Append('\\', n * 2);          // 结尾反斜杠要加倍，否则会「吃掉」右引号
                    else if (s[j] == '"') sb.Append('\\', n * 2 + 1);   // 反斜杠紧挨着引号：n*2+1
                    else sb.Append('\\', n);                            // 普通反斜杠原样
                    i = j - 1;
                }
                else if (c == '"') sb.Append("\\\"");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 自己的小日志：插件的 app.log 归 SlideSyncService 管，这里不能依赖它已经初始化。
        /// 只写几百字节，失败一律吞掉——写日志失败绝不能反过来影响通知。
        /// </summary>
        static void QuietLog(string message)
        {
            try
            {
                if (_logPath.Length == 0) return;
                var fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > 512 * 1024) return;   // 防呆：超过 512KB 就不写了
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
