using System;
using System.IO;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 「发一条 Windows 提示」的可编辑设置。
    ///
    /// 必须是普通 POCO（公共无参构造 + 公共 get/set）：ClassIsland 的
    /// <c>ActionBase&lt;TSettings&gt;</c> 用 System.Text.Json 把 ActionItem.Settings
    /// 反序列化成这个类型，再通过 SettingsInternal 交给行动实例。属性名就是 JSON 键名，
    /// 改名等于让老师已经存下的自动化丢设置，所以这些名字不要再动。
    /// </summary>
    public sealed class NotifyActionSettings
    {
        /// <summary>标题（对应 ClassIsland 内建通知的 Mask）。默认「课件续页」。</summary>
        public string Mask { get; set; } = "课件续页";

        /// <summary>正文（对应 Content）。支持 {deck} {page} {total} {remaining} 占位符。</summary>
        public string Content { get; set; } = "来自自动化";

        /// <summary>停留时长（秒）。托盘气泡的图标保留这么久；系统横幅的时长由 Windows 决定。</summary>
        public int DurationSeconds { get; set; } = 10;

        /// <summary>
        /// 是否置顶。Windows 通知没有「置顶」这个开关，这里的语义是：勾选后优先走操作中心横幅
        /// （横幅始终盖在窗口上方），关闭时优先走托盘气泡（可以控制停留时长）。
        /// </summary>
        public bool IsTopmostEnabled { get; set; }
    }

    /// <summary>
    /// 通知文案的占位符替换。做成独立的纯函数类：不依赖服务、不读文件、不抛异常，
    /// 反射测试可以直接调它验证「无记录」「有记录」两种边界。
    /// </summary>
    public static class NotifyTemplate
    {
        /// <summary>
        /// 把模板里的 {deck}/{page}/{total}/{remaining} 换成台账记录里的值。
        /// 记录为空或字段无效时替换成空串（不保留 "{page}" 这种字样给老师看）。
        /// </summary>
        public static string Render(string? template, SessionRecord? record)
        {
            if (string.IsNullOrEmpty(template)) return "";

            string text = template!;
            text = text.Replace("{deck}", DeckName(record), StringComparison.OrdinalIgnoreCase);
            text = text.Replace("{page}", PageText(record), StringComparison.OrdinalIgnoreCase);
            text = text.Replace("{total}", TotalText(record), StringComparison.OrdinalIgnoreCase);
            text = text.Replace("{remaining}", RemainingText(record), StringComparison.OrdinalIgnoreCase);
            return text;
        }

        /// <summary>
        /// 取这次通知要用的那条记录：优先当前放映课件的最后一条，其次全台账最新一条。
        /// 任何异常都吞掉返回 null —— 通知取不到数据时宁可少几个字，也不能让自动化报错。
        /// </summary>
        public static SessionRecord? Resolve(Ledger? ledger, string? currentFilePath)
        {
            if (ledger == null) return null;
            try
            {
                if (!string.IsNullOrWhiteSpace(currentFilePath))
                {
                    SessionRecord? current = ledger.Last(currentFilePath!);
                    if (current != null) return current;
                }
                return ledger.Newest();
            }
            catch
            {
                return null;
            }
        }

        static string DeckName(SessionRecord? record)
        {
            string path = record == null ? "" : (record.FilePath ?? "");
            if (path.Length == 0) return "";
            try
            {
                return Path.GetFileNameWithoutExtension(path) ?? "";
            }
            catch
            {
                return "";
            }
        }

        static string PageText(SessionRecord? record)
        {
            int page = record == null ? 0 : record.EndSlide;
            return page > 0 ? page.ToString() : "";
        }

        static string TotalText(SessionRecord? record)
        {
            int total = record == null ? 0 : record.SlideCount;
            return total > 0 ? total.ToString() : "";
        }

        static string RemainingText(SessionRecord? record)
        {
            if (record == null) return "";
            int page = record.EndSlide;
            int total = record.SlideCount;
            if (page <= 0 || total <= 0) return "";
            return Math.Max(0, total - page).ToString();
        }
    }
}
