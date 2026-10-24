using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 历史面板里的一行字（2026-10-24 功能3）。老师原话：每条只有"第 N 页 · 3 小时前"，
    /// 看不出是哪一节课记的。现在写成能定位到某一节课的样子：
    ///
    ///   有课表信息：周三 第 3 节 · 15:20 · 第 5 → 8 页
    ///   没有课表信息（老记录 / 课间 / 没排课）：10-24 15:20 · 第 5 → 8 页
    ///
    /// 纯函数，不碰 GDI、不碰 ClassIsland：绘制在 Ui\NativeOverlayCard.cs 的 DrawHistoryPanel，
    /// 这里只管字。于是"有没有节次 / 跨年 / 页码缺失 / 超长课程名"都能离线测到，不用等上课。
    ///
    /// 时间一律带 InvariantCulture：上一轮就是 ar-SA 文化下时间分隔符不一样，把好记录当成
    /// 坏数据删了。这里连格式串里的 ':' 和 '-' 都转义掉，任何文化下都只会输出这两个字符。
    /// </summary>
    public static class HistoryLine
    {
        static readonly string[] WeekdayNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        /// <summary>历史行的文字（最全的一版）。</summary>
        public static string Format(SessionRecord r)
        {
            return Format(r, DateTime.Now);
        }

        public static string Format(SessionRecord r, DateTime today)
        {
            if (r == null) return "";
            return Format(r.Weekday, r.Period, r.StartedAt, r.StartSlide, r.EndSlide, today);
        }

        public static string Format(int weekday, int period, string startedAt,
                                    int startSlide, int endSlide, DateTime today)
        {
            return Variants(weekday, period, startedAt, startSlide, endSlide, today)[0];
        }

        /// <summary>
        /// 从最全到最省的几种写法，按"放得下"的顺序排列。绘制代码挑第一个宽度够用的
        /// （卡片只有两百多像素宽："周三 第 3 节 · 15:20 · 第 5 → 8 页"放不下时先砍时间，
        /// 节次和页码必须留着）。最后一项永远只是页码，留给 g.Ellipsis 兜底。
        /// </summary>
        public static string[] Variants(int weekday, int period, string startedAt,
                                       int startSlide, int endSlide, DateTime today)
        {
            string slot = Slot(weekday, period);
            string pages = Pages(startSlide, endSlide);
            string clock = Clock(startedAt);
            string whenFull = When(startedAt, today, true);
            string whenDay = When(startedAt, today, false);

            var list = new List<string>(4);
            if (slot.Length > 0)
            {
                // 有节次就不写日期了：周几 + 第几节已经能定位到具体一节课，再写日期是多余的字
                Add(list, Join(slot, clock, pages));
                Add(list, Join(slot, pages));
            }
            else
            {
                Add(list, Join(whenFull.Length > 0 ? whenFull : whenDay, pages));
                Add(list, Join(whenDay, pages));
            }
            Add(list, pages);
            return list.ToArray();
        }

        /// <summary>
        /// "周三 第 3 节"。周几不知道（0）或第几节数不出来（0）时返回空串：只写"周三"
        /// 定位不到一节课，宁可按日期时间写，也不硬凑一个假节次。
        /// </summary>
        public static string Slot(int weekday, int period)
        {
            if (period <= 0) return "";
            string day = weekday >= 1 && weekday <= 7 ? WeekdayNames[weekday % 7] : "";
            string p = "第 " + period.ToString(CultureInfo.InvariantCulture) + " 节";
            return day.Length > 0 ? day + " " + p : p;
        }

        /// <summary>
        /// "第 5 → 8 页"。讲到同一页（或只有一页）时写"第 8 页"，页码全缺失时写"页码未知"。
        /// </summary>
        public static string Pages(int startSlide, int endSlide)
        {
            if (startSlide > 0 && endSlide > startSlide)
            {
                return "第 " + startSlide.ToString(CultureInfo.InvariantCulture)
                     + " → " + endSlide.ToString(CultureInfo.InvariantCulture) + " 页";
            }
            int one = endSlide > 0 ? endSlide : startSlide;
            if (one > 0) return "第 " + one.ToString(CultureInfo.InvariantCulture) + " 页";
            return "页码未知";
        }

        /// <summary>
        /// 只要时分"15:20"。有节次的记录用它（日期由"周三 第 3 节"替代），解析失败返回空串。
        /// </summary>
        public static string Clock(string startedAt)
        {
            if (string.IsNullOrWhiteSpace(startedAt)) return "";
            DateTime t;
            if (!DateTime.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return "";
            return t.ToString(@"HH\:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 记录开始的时间："10-24 15:20"；跨年时带年份"2025-10-24 15:20"（历史会留一年以上，
        /// 不写年份的"10-24"会被当成今年的）。
        /// <paramref name="withClock"/> = false 时只要日期，给窄宽度用。
        /// </summary>
        public static string When(string startedAt, DateTime today, bool withClock)
        {
            if (string.IsNullOrWhiteSpace(startedAt)) return "";
            DateTime t;
            if (!DateTime.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return "";
            string date = t.Year == today.Year
                ? t.ToString(@"MM\-dd", CultureInfo.InvariantCulture)
                : t.ToString(@"yyyy\-MM\-dd", CultureInfo.InvariantCulture);
            return withClock ? date + " " + t.ToString(@"HH\:mm", CultureInfo.InvariantCulture) : date;
        }

        /// <summary>用" · "把非空段连起来（空段直接跳过，不会出现"· ·"）。</summary>
        static string Join(params string[] parts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(p);
            }
            return sb.ToString();
        }

        /// <summary>加一项，和上一项重复就不加（省掉"没有日期时三个候选一模一样"）。</summary>
        static void Add(List<string> list, string s)
        {
            if (s.Length == 0) return;
            if (list.Count > 0 && list[list.Count - 1] == s) return;
            list.Add(s);
        }
    }
}
