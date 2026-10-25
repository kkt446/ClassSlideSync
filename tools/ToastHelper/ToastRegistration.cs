using System;
using System.IO;
using Microsoft.Win32;

namespace ClassSlideSync.ToastHelper
{
    /// <summary>
    /// 把「一个未打包的 Win32 程序也能弹 toast」所需的登记做完。全部在 HKCU 里，不需要管理员。
    ///
    /// 需要两样东西，缺一不可：
    ///   1) HKCU\Software\Classes\AppUserModelId\&lt;AUMID&gt;
    ///        DisplayName（必填，通知上显示的应用名）
    ///        IconUri（可选，通知左上角的小图标；路径要能被 shell 读到）
    ///      有了它，系统设置 → 通知里才会出现「课件续页」这一项，用户能自己关。
    ///   2) 开始菜单快捷方式，且带 System.AppUserModel.ID = AUMID（见 ShellLink.cs）
    ///      没有它，ToastNotificationManager 认为这个 AUMID 不属于任何已安装应用，Show() 抛异常。
    ///
    /// 每一步都幂等：快捷方式已经指向本 exe 且 AUMID 对得上就只读不写，
    /// 所以插件每次弹通知都调一遍也不会有额外开销。
    /// </summary>
    internal static class ToastRegistration
    {
        internal static bool Ensure(string aumid, string helperExe, string? iconPath, Log log, out string why)
        {
            why = "";
            if (string.IsNullOrWhiteSpace(aumid))
            {
                why = "AUMID 为空";
                return false;
            }

            if (!EnsureAppUserModelIdKey(aumid, iconPath, log, out why)) return false;

            string lnk = Shortcut.Path;
            if (!Shortcut.IsCurrent(lnk, helperExe, aumid, log))
            {
                if (!Shortcut.Create(lnk, helperExe, aumid, iconPath, log))
                {
                    why = "开始菜单快捷方式创建失败";
                    return false;
                }
                log.Write("已创建开始菜单快捷方式: " + lnk);
            }
            else
            {
                log.Write("开始菜单快捷方式已就绪: " + lnk);
            }

            if (!File.Exists(lnk))
            {
                why = "开始菜单快捷方式不存在: " + lnk;
                return false;
            }

            return true;
        }

        static bool EnsureAppUserModelIdKey(string aumid, string? iconPath, Log log, out string why)
        {
            why = "";
            string subKey = @"Software\Classes\AppUserModelId\" + aumid;
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKey, true))
                {
                    if (key == null)
                    {
                        why = "HKCU\\" + subKey + " 打不开";
                        return false;
                    }

                    key.SetValue("DisplayName", Program.DisplayName, RegistryValueKind.String);
                    if (iconPath != null)
                    {
                        // IconUri 只认绝对路径；用 helper 自己目录里的 icon.png（只读，不往插件目录写）。
                        key.SetValue("IconUri", iconPath, RegistryValueKind.String);
                    }

                    // 读回来确认：权限被策略挡住时 SetValue 可能不抛但也不生效。
                    string? name = key.GetValue("DisplayName") as string;
                    if (!string.Equals(name, Program.DisplayName, StringComparison.Ordinal))
                    {
                        why = "HKCU\\" + subKey + " 写进去了但读回不对";
                        return false;
                    }

                    log.Write("已登记 HKCU\\" + subKey + "（DisplayName=" + name +
                              ", IconUri=" + (iconPath ?? "无") + "）");
                    return true;
                }
            }
            catch (Exception ex)
            {
                why = "写 HKCU\\" + subKey + " 异常: " + ex.Message;
                return false;
            }
        }
    }
}
