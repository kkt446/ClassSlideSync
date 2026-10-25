using System;
using System.Diagnostics;
using System.IO;

namespace ClassSlideSync.ToastHelper
{
    /// <summary>
    /// 独立的小工具：替 ClassIsland 插件「课件续页」弹一条 Windows 操作中心横幅（toast）。
    ///
    /// 为什么拆成 exe 而不是写在插件里：
    ///   1) 插件的 TargetFramework 必须是 net8.0-windows（ClassIsland 动态加载要求），
    ///      toast 用的 Windows.UI.Notifications 投影只有 net8.0-windows10.0.19041.0 才有；
    ///   2) 未打包的 Win32 程序弹 toast 之前要先在 HKCU 登记 AppUserModelID、并建一个带
    ///      System.AppUserModel.ID 的开始菜单快捷方式。这些步骤只该做一次、失败要能安静退出，
    ///      放在插件进程里做等于让宿主（ClassIsland）替它承担风险。
    ///
    /// 用法（插件就是这么调的，见插件 Core\ToastNotifier.cs）：
    ///   ToastHelper.exe --register-only --aumid ClassSlideSync.Plugin.Toast --log &lt;日志&gt;
    ///   ToastHelper.exe --title "标题" --body "正文" --aumid ... --log ... [--timeout-ms 8000]
    ///
    /// 退出码就是插件判断「该不该回退托盘气泡」的唯一依据，0 以外一律回退。
    /// </summary>
    internal static class Program
    {
        /// <summary>默认 AppUserModelID。插件侧 Core\ToastNotifier.cs 里的 Aumid 必须一字不差。</summary>
        internal const string DefaultAumid = "ClassSlideSync.Plugin.Toast";

        internal const string DisplayName = "课件续页";

        /// <summary>契约：插件只认 <see cref="Ok"/>，其他都回退托盘气泡。</summary>
        internal static class ExitCodes
        {
            internal const int Ok = 0;
            /// <summary>命令行看不懂（缺 --body、参数没给值）。</summary>
            internal const int BadArguments = 10;
            /// <summary>注册表 / 开始菜单快捷方式没写成功 —— 调用者应该回退。</summary>
            internal const int RegistrationFailed = 20;
            /// <summary>系统 API 在 Show() 时抛了。</summary>
            internal const int ShowFailed = 30;
            /// <summary>用户在系统设置里把这类通知关掉了（或组策略关掉）。重试没用，回退。</summary>
            internal const int NotificationsDisabled = 31;
            /// <summary>没预料到的异常。</summary>
            internal const int Unexpected = 40;
        }

        [STAThread]
        static int Main(string[] args)
        {
            // 先单独把 --log 捞出来：后面任何一步炸了，日志里都得有痕迹。
            var log = new Log(FindArg(args, "--log"));
            try
            {
                return Run(args, log);
            }
            catch (Exception ex)
            {
                log.Write("未处理异常: " + ex);
                return ExitCodes.Unexpected;
            }
        }

        static int Run(string[] args, Log log)
        {
            if (HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                log.Write("用法: ToastHelper.exe --title <标题> --body <正文> [--aumid <id>] [--log <文件>] | --register-only");
                return ExitCodes.BadArguments;
            }

            string aumid = FindArg(args, "--aumid") is { Length: > 0 } custom ? custom : DefaultAumid;
            bool registerOnly = HasFlag(args, "--register-only");
            // 诊断用：跳过登记直接弹，用来区分「登记不对」和「系统通知本身不通」。
            bool noRegister = HasFlag(args, "--no-register");
            string title = FindArg(args, "--title") ?? "";
            string body = FindArg(args, "--body") ?? "";

            if (!registerOnly && body.Trim().Length == 0)
            {
                log.Write("参数不够：弹横幅至少要 --body（--title 可空）");
                return ExitCodes.BadArguments;
            }

            // Environment.ProcessPath 是 apphost 的完整路径；MainModule 只是兜底（单文件/特殊宿主）。
            string exe = Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule?.FileName
                         ?? "";
            if (exe.Length == 0)
            {
                log.Write("拿不到自己的 exe 路径，无法建快捷方式");
                return ExitCodes.RegistrationFailed;
            }

            string dir = System.IO.Path.GetDirectoryName(exe) ?? "";
            string iconCandidate = System.IO.Path.Combine(dir, "icon.png");
            string? icon = File.Exists(iconCandidate) ? iconCandidate : null;

            log.Write("启动 aumid=" + aumid + " exe=" + exe + " registerOnly=" + registerOnly +
                      " icon=" + (icon ?? "无"));

            // 每次都做一遍「确保登记」：便宜（读两个键 + 看一个 lnk），换来的是用户手动删掉
            // 开始菜单快捷方式之后下一次通知能自己长回来，而不是永远静默失败。
            if (!noRegister && !ToastRegistration.Ensure(aumid, exe, icon, log, out string why))
            {
                log.Write("注册失败，返回 " + ExitCodes.RegistrationFailed + ": " + why);
                return ExitCodes.RegistrationFailed;
            }

            log.Write(noRegister ? "跳过登记（--no-register，诊断模式）" : "注册检查通过（AUMID + 开始菜单快捷方式）");
            if (registerOnly) return ExitCodes.Ok;

            return ToastShow.Show(aumid, title, body, icon, log);
        }

        // ---- 极简命令行解析：只支持 "--名字 值" 与 "--名字=值"，插件只用前者 ----

        internal static string? FindArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Length == 0) continue;
                if (a.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : "";
                if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return a.Substring(name.Length + 1);
            }
            return null;
        }

        internal static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args)
                if (a.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
