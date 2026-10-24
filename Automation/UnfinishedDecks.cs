using System;
using System.Globalization;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 台账查询：自动化要用的「这份课件算不算没讲完」「记录是不是太旧了」「最近一条是哪条」。
    ///
    /// 为什么单独放一个类，而不是在 Action 和 Rule 里各写一遍：两边必须用同一把尺子。
    /// 规则说“成立”、行动却找不到课件（或反过来），老师看到的是自相矛盾的自动化，
    /// 且这种偏差只会在边界数据（EndSlide=0、22 天前）上出现，最难排查。
    /// </summary>
    public static class UnfinishedDecks
    {
        /// <summary>超过 21 天的记录只当历史：再自动打开，多半是打开上学期的东西。</summary>
        public const int MaxAgeDays = 21;

        /// <summary>
        /// 没讲完 = 知道总页数，且停下的页在总页数之前。
        /// 注意这里刻意不要求 EndSlide &gt; 0：规则按用户给的判定条件（SlideCount &gt; 0 &amp;&amp;
        /// EndSlide &lt; SlideCount）原样执行。EndSlide=0 的坏记录（手改/导入的台账）行动那边
        /// 照样会打开课件，只是没有页码可跳（<see cref="JumpPage"/> 返回 0，PendingJump 不启动）。
        /// </summary>
        public static bool IsUnfinished(SessionRecord? record)
        {
            if (record == null) return false;
            return record.SlideCount > 0 && record.EndSlide < record.SlideCount;
        }

        /// <summary>
        /// 行动打开课件后要跳的页码；0 = 没有可用页码。
        /// 台账里的 EndSlide 是自由数据（可能被手改成 0 或负数），交给 Process/COM 之前得先问一遍。
        /// </summary>
        public static int JumpPage(SessionRecord? record)
        {
            if (record == null) return 0;
            return record.EndSlide > 0 ? record.EndSlide : 0;
        }

        /// <summary>
        /// 这条记录“最后一次被用到”的时间：有结束时间就用结束时间，否则用开始时间。
        /// 和 <see cref="Ledger"/> 里 Prune 的保留期用的是同一个口径，避免“刚清过的记录还被规则认”。
        /// </summary>
        public static DateTime LastActivity(SessionRecord? record)
        {
            if (record == null) return DateTime.MinValue;
            DateTime t;
            if (!string.IsNullOrEmpty(record.EndedAt)
                && DateTime.TryParse(record.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            if (!string.IsNullOrEmpty(record.StartedAt)
                && DateTime.TryParse(record.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return DateTime.MinValue;
        }

        public static bool IsFresh(SessionRecord? record, DateTime now)
        {
            var last = LastActivity(record);
            if (last == DateTime.MinValue) return false;
            // 时间往后偏（时区/手工改台账）不算过期：宁可多认一次，也不要让“刚刚讲的课”被判成旧记录。
            return now - last <= TimeSpan.FromDays(MaxAgeDays);
        }

        /// <summary>
        /// 台账里的“最近一条记录”（不管讲没讲完）。规则用它判断最近那条是否没讲完。
        /// 返回 null = 台账为空。
        /// </summary>
        public static SessionRecord? NewestRecord(Ledger? ledger)
        {
            if (ledger?.ByFile == null) return null;
            SessionRecord? best = null;
            foreach (var kv in ledger.ByFile)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var record = list[i];
                    if (record == null) continue;
                    if (best == null || IsNewer(record, best)) best = record;
                }
            }
            return best;
        }

        /// <summary>
        /// 最近一条「没讲完」的记录。
        /// </summary>
        /// <param name="requireOpenableFile">
        /// 行动要真的去 Process.Start，所以只挑磁盘上还在、且不是「希沃:xxx」伪键的记录；
        /// 规则只做判断，不传这个条件。
        /// </param>
        public static SessionRecord? NewestUnfinished(Ledger? ledger, DateTime now, bool requireOpenableFile)
        {
            if (ledger?.ByFile == null) return null;
            SessionRecord? best = null;
            foreach (var kv in ledger.ByFile)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var record = list[i];
                    if (!IsUnfinished(record) || !IsFresh(record, now)) continue;
                    if (requireOpenableFile && ResolveExistingFile(record!.FilePath).Length == 0) continue;
                    if (best == null || IsNewer(record!, best))
                        best = record;
                }
            }
            return best;
        }

        /// <summary>
        /// candidate 是不是比 best 更“最近”。
        ///
        /// 以前这里（以及 Ledger.Newest）用的是纯字符串比较 StartedAt，注释还写着“和 Ledger.Newest
        /// 口径一致”。但 StartedAt 在台账里是自由文本：引擎写 "2026-09-08 10:00:00"，手改/导入的
        /// 记录可能写成 "2026/9/8 10:00:00"。字符串序在第一个不同的字符上就分胜负，'/' (47) 比
        /// '-' (45) 大，于是 "2026/9/8" 会被判成比 "2026-09-30" 更新 —— “最近一条”取错，规则和行动
        /// 会指向两份不同的课件。CardFileList.CompareDesc 早就因为同样的原因改成解析后比较了，
        /// 这里补上（Ledger.cs 不在本次改动范围内，只能在读取侧纠正）。
        /// </summary>
        public static bool IsNewer(SessionRecord candidate, SessionRecord best)
        {
            if (candidate == null) return false;
            if (best == null) return true;

            var a = SortTime(candidate);
            var b = SortTime(best);
            if (a != b) return a > b;

            // 同一秒、或两条的时间都解析不出来：退回字符串序保证确定性（不能依赖字典枚举顺序，
            // 否则同样的台账在不同次调用里可能选出不同的“最近一条”）。
            int c = string.CompareOrdinal(candidate.StartedAt ?? "", best.StartedAt ?? "");
            if (c != 0) return c > 0;
            return string.CompareOrdinal(candidate.FilePath ?? "", best.FilePath ?? "") > 0;
        }

        /// <summary>排序用的时间：StartedAt 优先，坏了才退 EndedAt（和 CardFileList.CompareDesc 同思路）。</summary>
        static DateTime SortTime(SessionRecord record)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(record.StartedAt)
                && DateTime.TryParse(record.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            if (!string.IsNullOrEmpty(record.EndedAt)
                && DateTime.TryParse(record.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return DateTime.MinValue;
        }

        /// <summary>
        /// 把台账里的路径变成可以交给 Process.Start 的绝对路径；不是磁盘文件（希沃伪键、
        /// 文件已被移走、空路径）就返回空串，让调用方安静地放弃而不是抛异常。
        /// </summary>
        public static string ResolveExistingFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string raw = path!.Trim();
            if (SeewoBridge.IsSeewoKey(raw)) return "";
            try
            {
                string full = System.IO.Path.GetFullPath(raw);
                return System.IO.File.Exists(full) ? full : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
