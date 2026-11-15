using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClassIsland.Core;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 以管理员身份运行的 ClassIsland 会被 Windows 挡在 Office 的 COM 外面（跨完整性级别），
    /// 插件读不到课件、跳不了页。唯一的根治办法是让宿主本身回到普通权限运行 —— 这里做三件事：
    ///
    ///   1. 判断"我们是不是被抬权了"（令牌完整性级别 High，见 <see cref="IsElevated"/>）；
    ///   2. 拿一个普通权限（Medium）的令牌：优先自身令牌的 linked token，退回 explorer.exe 的主令牌；
    ///   3. 用这个令牌拉起一个隐藏的 cmd 等待器（.cmd 脚本），它等本进程写下"要重启"的标记并退出后，
    ///      把 ClassIsland 重新以普通权限启动，然后我们自己关掉。
    ///
    /// 为什么必须绕一层脚本：ClassIsland 是单实例（命名 Mutex），新实例必须在旧进程完全退出后才能
    /// 起来；而本进程一旦退出就没法再启动任何东西了。mid 层 cmd 是系统自带、任何教师机都有，
    /// 不需要随插件再带一个 .NET 辅助 exe（ClassIsland 是自包含发布，普通教师机没有 .NET 运行时）。
    ///
    /// 防死循环：降权拉起时给新实例带环境变量 CLASSSLIDESYNC_DEELEVATED=1，并在配置目录写
    /// deelevate.last 时间戳；两者任一命中就跳过，绝不来回重启。
    ///
    /// 全部路径都用 P/Invoke 完成，不依赖 Office / WPS，也不改 ClassIsland 的任何文件。
    /// </summary>
    internal static class DeElevation
    {
        public const string SkipEnv = "CLASSSLIDESYNC_DEELEVATED";
        const string MarkerName = "deelevate.last";
        const string ScriptPrefix = "deelevate-";

        /// <summary>当前进程是否被「以管理员身份运行」抬到了 High 完整性级别。</summary>
        public static bool IsElevated()
        {
            if (!PlatformGuard.IsWindows) return false;
            return PptBridge.OurIntegrity() >= 0x3000;
        }

        /// <summary>
        /// 宿主启动完成后调用。满足条件时在后台准备降权重启，成功后由 UI 线程关闭本进程。
        /// 任何失败都只写一行日志，绝不让插件启动失败、也绝不把宿主关掉再起不来。
        /// </summary>
        public static void MaybeRestart(SyncSettings settings, string configDir, Ledger ledger)
        {
            try
            {
                if (!PlatformGuard.IsWindows) return;
                if (settings == null || !settings.AutoDeElevate) return;
                if (!IsElevated()) return;

                // 本次就是降权重启后的实例：必须停在这里，否则会无限重启。
                if (Environment.GetEnvironmentVariable(SkipEnv) == "1")
                {
                    ledger?.Log("deelevate: skip (already relaunched, " + SkipEnv + "=1)");
                    return;
                }
                if (AppBase.Current != null && AppBase.Current.IsMsix)
                {
                    ledger?.Log("deelevate: skip (msix package)");
                    return;
                }

                string marker = Path.Combine(configDir, MarkerName);
                if (File.Exists(marker)
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromSeconds(150))
                {
                    ledger?.Log("deelevate: skip (restarted less than 150s ago)");
                    return;
                }

                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    ledger?.Log("deelevate: skip (no process path)");
                    return;
                }

                var t = new Thread(() => Restart(exe, configDir, marker, ledger))
                {
                    IsBackground = true,
                    Name = "ClassSlideSync.DeElevation",
                };
                t.Start();
            }
            catch (Exception ex)
            {
                ledger?.Log("deelevate: schedule failed " + ex.GetType().Name + " " + ex.Message);
            }
        }

        static void Restart(string exe, string configDir, string marker, Ledger ledger)
        {
            try
            {
                // 给宿主留出画完首帧、跑完插件启动的时间；开机自启时老师看不见这段。
                Thread.Sleep(2500);

                string workDir = Path.GetDirectoryName(exe) ?? "";
                string script = Path.Combine(configDir, ScriptPrefix + Environment.ProcessId + ".cmd");
                try
                {
                    foreach (var old in Directory.GetFiles(configDir, ScriptPrefix + "*.cmd"))
                    {
                        if (!string.Equals(old, script, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(old); } catch { }
                        }
                    }
                    File.WriteAllText(script, Script, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    ledger?.Log("deelevate: cannot write script " + ex.GetType().Name + " " + ex.Message);
                    return;
                }

                IntPtr token = GetMediumToken(out string via, out string err);
                if (token == IntPtr.Zero)
                {
                    ledger?.Log("deelevate: no medium token (" + err + ")");
                    return;
                }

                IntPtr env = IntPtr.Zero;
                try
                {
                    env = BuildEnvironment(new Dictionary<string, string>
                    {
                        ["CLASSSLIDESYNC_SCRIPT"] = script,
                        ["CLASSSLIDESYNC_RELAUNCH"] = exe,
                        ["CLASSSLIDESYNC_WORKDIR"] = workDir,
                        ["CLASSSLIDESYNC_OLD_PID"] = Environment.ProcessId.ToString(),
                        ["CLASSSLIDESYNC_IMAGE"] = Path.GetFileName(exe),
                        ["CLASSSLIDESYNC_MARKER"] = marker,
                        [SkipEnv] = "1",
                    });

                    var si = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = @"winsta0\default",
                    };
                    // 脚本路径通过环境变量传（变量展开不会被 cmd 再当成特殊字符解析），
                    // 命令行本身永远是纯 ASCII，不随安装目录里的空格/中文/& 变化。
                    string cmd = "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /c \"%CLASSSLIDESYNC_SCRIPT%\"";
                    const uint flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;
                    bool ok = CreateProcessWithTokenW(token, LOGON_WITH_PROFILE, null, cmd, flags,
                        env, workDir, ref si, out PROCESS_INFORMATION pi);
                    if (!ok)
                    {
                        ledger?.Log("deelevate: spawn failed err=" + Marshal.GetLastWin32Error() + " via " + via);
                        return;
                    }
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                }
                finally
                {
                    if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
                    CloseHandle(token);
                }

                try { File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine); }
                catch { }

                ledger?.Log("deelevate: restarting as normal user (pid " + Environment.ProcessId
                            + " -> relaunch via " + Path.GetFileName(exe) + ", token " + via + ")");

                // 等 cmd 真正跑起来再关自己。AppBase.Stop() 自己会把退出流程丢到 UI 线程，
                // 并且会正常保存设置 / 档案 / 组件配置（反编译 App.Stop 确认过，没有确认框）。
                Thread.Sleep(1200);
                try { AppBase.Current?.Stop(); }
                catch (Exception ex) { ledger?.Log("deelevate: stop failed " + ex.GetType().Name + " " + ex.Message); }
            }
            catch (Exception ex)
            {
                ledger?.Log("deelevate: failed " + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// 普通权限令牌：先要 linked token（UAC 过滤出来的那个，最贴近"同一用户、正常权限"），
        /// 失败再借 explorer.exe 的主令牌（老师已登录时一定有）。拿到后必须确认不是 High ——
        /// 拿错令牌会变成"重启完还是管理员"，那就是无限重启。
        /// </summary>
        static IntPtr GetMediumToken(out string via, out string err)
        {
            via = "";
            err = "";

            IntPtr self = IntPtr.Zero;
            if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out self))
            {
                try
                {
                    IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        if (GetTokenInformation(self, TokenLinkedToken, buf, IntPtr.Size, out _))
                        {
                            IntPtr linked = Marshal.ReadIntPtr(buf);
                            if (linked != IntPtr.Zero)
                            {
                                try
                                {
                                    IntPtr dup = DuplicateMedium(linked, out err);
                                    if (dup != IntPtr.Zero) { via = "linked"; return dup; }
                                }
                                finally { CloseHandle(linked); }
                            }
                        }
                        else err = "linked token " + Marshal.GetLastWin32Error();
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(self); }
            }
            else err = "self token " + Marshal.GetLastWin32Error();

            int session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Id);
                    if (hp == IntPtr.Zero) { err = "explorer process " + Marshal.GetLastWin32Error(); continue; }
                    try
                    {
                        IntPtr ht;
                        if (!OpenProcessToken(hp, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out ht))
                        {
                            err = "explorer token " + Marshal.GetLastWin32Error();
                            continue;
                        }
                        try
                        {
                            IntPtr dup = DuplicateMedium(ht, out string why);
                            if (dup != IntPtr.Zero) { via = "explorer"; return dup; }
                            err = "explorer duplicate " + why;
                        }
                        finally { CloseHandle(ht); }
                    }
                    finally { CloseHandle(hp); }
                }
                finally { p.Dispose(); }
            }

            if (err.Length == 0) err = "no explorer in session";
            return IntPtr.Zero;
        }

        static IntPtr DuplicateMedium(IntPtr src, out string err)
        {
            err = "";
            // 实测：CreateProcessWithTokenW 要的访问权不能少，优先 TOKEN_ALL_ACCESS；
            // 拿不到再退回"原样继承访问权"（探测程序里这个组合也成功过）。
            int[] levels = { 2 /*SecurityImpersonation*/, 3 /*SecurityDelegation*/ };
            uint[] accesses = { TOKEN_ALL_ACCESS, 0, MAXIMUM_ALLOWED };
            foreach (uint access in accesses)
            {
                foreach (int level in levels)
                {
                    if (!DuplicateTokenEx(src, access, IntPtr.Zero, level, TokenPrimary, out IntPtr dup))
                    {
                        err = "DuplicateTokenEx err=" + Marshal.GetLastWin32Error();
                        continue;
                    }
                    int il = TokenIntegrity(dup);
                    if (il >= 0x3000)
                    {
                        err = "token still high 0x" + il.ToString("X");
                        CloseHandle(dup);
                        continue;
                    }
                    return dup;
                }
            }
            return IntPtr.Zero;
        }

        static int TokenIntegrity(IntPtr token)
        {
            int size = 128;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buf, size, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf);
                if (sid == IntPtr.Zero) return -1;
                byte count = Marshal.ReadByte(sid, 1);
                if (count == 0) return -1;
                return Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
            }
            catch { return -1; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static IntPtr BuildEnvironment(Dictionary<string, string> overrides)
        {
            var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                string k = e.Key as string;
                if (!string.IsNullOrEmpty(k)) all[k] = e.Value as string ?? "";
            }
            foreach (var kv in overrides) all[kv.Key] = kv.Value;

            var sb = new StringBuilder();
            foreach (var kv in all)
            {
                if (kv.Key.IndexOf('=') >= 0) continue;
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            }
            sb.Append('\0');
            return Marshal.StringToHGlobalUni(sb.ToString());
        }

        /// <summary>
        /// 等待器脚本。只做两件事：等本进程写下 deelevate.last（= 我们真的要重启了）并退出，
        /// 再以普通权限把它拉起来（最多试两次）。
        ///
        /// 为什么要等标记文件而不是只等进程退出：插件拉起等待器后还有一两秒才真正关闭宿主，
        /// 这段时间里老师如果自己点了退出，只等 PID 的话脚本会把已经关掉的应用又拉起来。
        /// 标记文件只由本类在"确定要重启"的那一刻写，老师手动退出时它不会出现，脚本静默结束。
        ///
        /// 全部纯 ASCII —— .cmd 文件的字节是由 cmd 按控制台代码页（中文机器是 936）解码的，
        /// 写中文进去在别的区域设置下会乱码；路径一律通过环境变量传，变量展开不会把空格、
        /// 中文、&amp; 当成语法。
        /// </summary>
        const string Script = """
            @echo off
            setlocal
            set "T=%CLASSSLIDESYNC_RELAUNCH%"
            set "D=%CLASSSLIDESYNC_WORKDIR%"
            set "P=%CLASSSLIDESYNC_OLD_PID%"
            set "I=%CLASSSLIDESYNC_IMAGE%"
            set "M=%CLASSSLIDESYNC_MARKER%"
            if "%T%"=="" goto end
            if "%M%"=="" goto end
            if not exist "%SystemRoot%\System32\tasklist.exe" goto end
            set /a N=0
            :arm
            if exist "%M%" goto armed
            set /a N+=1
            if %N% GEQ 20 goto end
            %SystemRoot%\System32\ping.exe -n 2 127.0.0.1 >nul
            goto arm
            :armed
            set /a N=0
            :wait
            %SystemRoot%\System32\tasklist.exe /FI "PID eq %P%" /NH /FO CSV 2>nul | %SystemRoot%\System32\findstr.exe /C:"\"%P%\"" >nul
            if errorlevel 1 goto gone
            set /a N+=1
            if %N% GEQ 120 goto end
            %SystemRoot%\System32\ping.exe -n 2 127.0.0.1 >nul
            goto wait
            :gone
            %SystemRoot%\System32\ping.exe -n 2 127.0.0.1 >nul
            set /a K=0
            :again
            start "" /D "%D%" "%T%"
            %SystemRoot%\System32\ping.exe -n 4 127.0.0.1 >nul
            set /a K+=1
            if %K% GEQ 2 goto end
            %SystemRoot%\System32\tasklist.exe /FI "IMAGENAME eq %I%" /NH 2>nul | %SystemRoot%\System32\findstr.exe /I /C:"%I%" >nul
            if errorlevel 1 goto again
            :end
            del "%~f0" >nul 2>&1
            """;

        // ---- native ----------------------------------------------------------

        const int TOKEN_QUERY = 0x0008;
        const int TOKEN_DUPLICATE = 0x0002;
        const int TOKEN_ASSIGN_PRIMARY = 0x0001;
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int TokenLinkedToken = 19;
        const int TokenIntegrityLevel = 25;
        const int TokenPrimary = 1;
        const uint TOKEN_ALL_ACCESS = 0x000F01FF;
        const uint MAXIMUM_ALLOWED = 0x02000000;
        const uint LOGON_WITH_PROFILE = 0x00000001;
        const uint CREATE_NO_WINDOW = 0x08000000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr existing, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessWithTokenW(IntPtr hToken, uint logonFlags, string? app, string cmd, uint flags,
            IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    }
}
