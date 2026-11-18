using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 「用哪个软件打开课件」的唯一出口（2026-11-18 用户要求）。
    ///
    /// 设置是 <see cref="DeckOpenApp"/>：Auto 走系统文件关联（老行为，也是唯一不需要
    /// 猜安装位置的方式）；选了 PowerPoint / WPS 就直接起对应套件的 exe 并把文件交给它，
    /// 这样即使用户把默认程序设成了另一套，插件也按老师刚才选的来。
    ///
    /// 找不到安装位置（没装、注册表键缺失）时**退回系统默认程序**并留日志 ——
    /// 打不开课件比用错软件更糟。进放映和页码跟踪仍由引擎自己附着，和打开方式无关。
    /// </summary>
    public static class DeckLauncher
    {
        /// <summary>当前偏好。由 <see cref="SyncSettings.DeckOpenApp"/> 同步过来（见 SlideSyncService）。</summary>
        public static DeckOpenApp Preferred = DeckOpenApp.Auto;

        /// <summary>
        /// 打开一份课件。失败会抛异常，调用方照旧 try/catch + 日志（与原来 Process.Start 一致）。
        /// </summary>
        public static void Open(string path, Action<string>? log = null)
        {
            string label = "";
            string exe = "";
            if (Preferred == DeckOpenApp.PowerPoint) { label = "PowerPoint"; exe = FindPowerPoint(); }
            else if (Preferred == DeckOpenApp.Wps) { label = "WPS"; exe = FindWps(); }

            if (exe.Length > 0)
            {
                try
                {
                    // 已经开着的套件会把这个文件接过去（和双击文件关联的效果一样）。
                    Process.Start(new ProcessStartInfo(exe, Quote(path)) { UseShellExecute = true });
                    log?.Invoke("open deck: " + label + " (" + exe + ") " + path);
                    return;
                }
                catch (Exception ex)
                {
                    log?.Invoke("open deck: 启动 " + label + " 失败（" + ex.Message + "），改用系统默认程序");
                }
            }
            else if (Preferred != DeckOpenApp.Auto)
            {
                log?.Invoke("open deck: 没找到 " + label + " 的安装位置，改用系统默认程序");
            }

            // Auto，或者指定套件不可用：系统文件关联是最后一道兜底。
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        // ---- exe 定位 ----------------------------------------------------------
        // 只认「官方安装器会写的」几个位置，不做全盘搜索：
        //   · App Paths：Office / WPS 安装器都会写（含 32 位注册表视图）；
        //   · Kingsoft 的 InstallRoot：WPS 自己的升级器刷新，App Paths 偶尔指向统一入口 wps.exe；
        //   · ProgID 的 LocalServer32：经典 Office 注册方式，取第一段可执行文件路径。

        static string FindPowerPoint()
        {
            if (!OperatingSystem.IsWindows()) return "";
            string p = AppPath("POWERPNT.EXE");
            if (p.Length > 0) return p;
            return LocalServerExe("PowerPoint.Application");
        }

        static string FindWps()
        {
            if (!OperatingSystem.IsWindows()) return "";
            string p = AppPath("wpp.exe");
            if (p.Length > 0)
            {
                // WPS 12 的 App Paths 把 wpp.exe 指到统一入口 wps.exe；同目录下的 wpp.exe
                // 才是「直接开演示」的入口，能用就用它（双击 .pptx 的最终效果一样）。
                if (Path.GetFileName(p).Equals("wpp.exe", StringComparison.OrdinalIgnoreCase)) return p;
                string sibling = Path.Combine(Path.GetDirectoryName(p) ?? "", "wpp.exe");
                if (File.Exists(sibling)) return sibling;
                return p;
            }

            try
            {
                string root = Convert.ToString(Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Kingsoft\Office\6.0\common", "InstallRoot", "")) ?? "";
                if (root.Length > 0)
                {
                    p = Path.Combine(root, "office6", "wpp.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }

            // WPS 新版的默认安装目录：LocalAppData\Kingsoft\WPS Office\<版本>\office6\wpp.exe
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Kingsoft", "WPS Office");
                if (Directory.Exists(dir))
                {
                    var versions = Directory.GetDirectories(dir);
                    Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
                    for (int i = versions.Length - 1; i >= 0; i--)
                    {
                        p = Path.Combine(versions[i], "office6", "wpp.exe");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }

            return "";
        }

        /// <summary>读 App Paths：HKCU 优先，然后 HKLM 的 64 / 32 位视图。</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static string AppPath(string exeName)
        {
            string[] paths =
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
            };
            foreach (string key in paths)
            {
                try
                {
                    string v = Convert.ToString(Registry.GetValue(key, "", "")) ?? "";
                    v = v.Trim().Trim('"');
                    if (v.Length > 0 && File.Exists(v)) return v;
                }
                catch { }
            }
            return "";
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static string LocalServerExe(string progId)
        {
            try
            {
                string clsid = Convert.ToString(Registry.GetValue(@"HKEY_CLASSES_ROOT\" + progId + @"\CLSID", "", "")) ?? "";
                if (clsid.Length == 0) return "";
                string cmd = Convert.ToString(Registry.GetValue(
                    @"HKEY_CLASSES_ROOT\CLSID\" + clsid + @"\LocalServer32", "", "")) ?? "";
                if (cmd.Length == 0) return "";
                // 例："C:\...\POWERPNT.EXE /AUTOMATION" → 去掉引号和后面的开关。
                cmd = cmd.Trim();
                string exe;
                if (cmd.StartsWith("\"", StringComparison.Ordinal))
                {
                    int end = cmd.IndexOf('"', 1);
                    exe = end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
                }
                else
                {
                    int sp = cmd.IndexOf(' ');
                    exe = sp > 0 ? cmd.Substring(0, sp) : cmd;
                }
                return File.Exists(exe) ? exe : "";
            }
            catch { return ""; }
        }
    }
}
