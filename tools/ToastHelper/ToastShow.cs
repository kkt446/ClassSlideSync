using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClassSlideSync.ToastHelper
{
    /// <summary>
    /// 真正弹横幅的地方。用 Windows.UI.Notifications（WinRT 投影），
    /// 不是 Shell_NotifyIcon 的托盘气泡 —— 托盘气泡只在右下角闪一下、不进操作中心，
    /// 用户要的是操作中心里那种横幅。
    ///
    /// 这个类只在 <see cref="ToastRegistration"/> 登记成功之后才会被调到；
    /// 它自己任何异常都转成退出码，绝不把异常抛回插件。
    /// </summary>
    internal static class ToastShow
    {
        internal static int Show(string aumid, string title, string body, string? iconPath, Log log)
        {
            // 三段分开 try：CreateToastNotifier 抛 0x80070490 说明「系统不认这个 AUMID」
            // （快捷方式还没被 shell 收进去），Show 抛 0x80070490 才是通知平台自己的问题。
            // 不分开就永远分不清该修哪一头。
            ToastNotifier notifier;
            try
            {
                notifier = ToastNotificationManager.CreateToastNotifier(aumid);
            }
            catch (Exception ex)
            {
                log.Write("CreateToastNotifier 失败: " + ex.GetType().Name + " hr=0x" + ex.HResult.ToString("X8") +
                          " " + ex.Message + "（AUMID 没被系统认出来）");
                return Program.ExitCodes.ShowFailed;
            }

            try
            {
                // Setting 说明系统允不允许这个应用发通知（用户关了、专注助手、组策略都会体现）。
                // 注意：在「从未发过通知」的 AUMID 上，这个属性在某些 Windows 版本上会抛
                // 0x80070490（ERROR_NOT_FOUND），实测 Show() 本身照样成功。所以它只当参考，
                // 读失败不能当成失败，否则会假回退托盘气泡。
                try
                {
                    NotificationSetting setting = notifier.Setting;
                    log.Write("notifier.Setting=" + setting);
                    if (setting != NotificationSetting.Enabled)
                    {
                        log.Write("系统不允许这类通知（" + setting + "），返回 " + Program.ExitCodes.NotificationsDisabled);
                        return Program.ExitCodes.NotificationsDisabled;
                    }
                }
                catch (Exception setEx)
                {
                    log.Write("读 notifier.Setting 失败（忽略，继续弹）: hr=0x" + setEx.HResult.ToString("X8") +
                              " " + setEx.Message);
                }

                string xml = BuildXml(title, body, iconPath);
                log.Write("toast xml=" + xml);

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                var toast = new ToastNotification(doc);
                // 横幅本身几秒就收起来，操作中心里这条留两小时（老师下课忙完回来还能看到）。
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(2);

                notifier.Show(toast);
                log.Write("Show() 返回，没有抛异常");

                // 证据：真正弹出来的 toast 会进操作中心的历史记录。读得到条数，说明系统确实
                // 接收了这条通知（而不只是「调用了没抛异常」）。读失败只当附加信息，不影响结果。
                try
                {
                    var history = ToastNotificationManager.History.GetHistory(aumid);
                    log.Write("操作中心历史条数=" + (history == null ? "读不到" : history.Count.ToString()));
                }
                catch (Exception histEx)
                {
                    log.Write("读操作中心历史失败（不影响结果）: " + histEx.Message);
                }

                return Program.ExitCodes.Ok;
            }
            catch (Exception ex)
            {
                int hr = ex.HResult;
                log.Write("Show() 失败: " + ex.GetType().Name + " hr=0x" + hr.ToString("X8") + " " + ex.Message);
                return Program.ExitCodes.ShowFailed;
            }
        }

        /// <summary>
        /// ToastGeneric 模板：
        ///   text[0] = 标题（粗体），text[1] = 正文；
        ///   appLogoOverride = 应用小图标（对应 AUMID 的 IconUri，但这里显式给一遍更稳）；
        ///   duration="long" 让横幅多停一会儿（默认 5 秒，下课那一瞬间老师可能没在看屏幕）。
        /// </summary>
        internal static string BuildXml(string title, string body, string? iconPath)
        {
            var sb = new StringBuilder(512);
            sb.Append("<toast duration=\"long\">");
            sb.Append("<visual><binding template=\"ToastGeneric\">");
            sb.Append("<text>").Append(Escape(title)).Append("</text>");
            sb.Append("<text>").Append(Escape(body)).Append("</text>");
            if (iconPath != null && File.Exists(iconPath))
            {
                // file:/// 形式的绝对 URI；路径里有中文/空格也能被 XML 接受（属性值已转义）。
                sb.Append("<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"")
                  .Append(Escape(new Uri(iconPath).AbsoluteUri))
                  .Append("\"/>");
            }
            sb.Append("</binding></visual>");
            sb.Append("<audio src=\"ms-winsoundevent:Notification.Default\"/>");
            sb.Append("</toast>");
            return sb.ToString();
        }

        static string Escape(string? text) => SecurityElement.Escape(text ?? "") ?? "";
    }
}
