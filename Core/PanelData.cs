using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 控制面板历史列表的一行：时间 / 页范围 / 时长。
    /// 和卡片上的 HistoryLabel 不同（那边是一整行字符串，给 GDI 画），面板是三列，
    /// 所以这里单独排一次；时间解析与格式化一律用 InvariantCulture。
    /// </summary>
    public sealed class PanelHistoryRow
    {
        public string TimeText { get; set; } = "";
        public string PageText { get; set; } = "";
        public string DurationText { get; set; } = "";
        /// <summary>这一条最后停在哪一页，面板上的“立即跳转”用它。</summary>
        public int Slide { get; set; }
        /// <summary>这一条属于哪份课件。历史行可以点着跳页，必须带着路径走，否则只会跳到当前选中的那份。</summary>
        public string FilePath { get; set; } = "";
        public bool IsLatest { get; set; }
    }

    /// <summary>
    /// 控制面板的数据层：搜索、分组、详情文案、状态条。全是纯函数，不碰 UI 线程，方便测试。
    /// 分组直接调用 CardFileGroups —— 卡片和面板必须用同一套分组顺序和行文案，不能各写一份。
    /// </summary>
    public static class PanelData
    {
        /// <summary>面板历史最多显示多少条（台账本身每个文件最多 200 条）。</summary>
        public const int MaxHistoryRows = 200;

        /// <summary>
        /// 按关键词过滤课件：空串 = 全部；课件名、科目、所在目录名都参与匹配；大小写不敏感。
        /// 空关键词不做 Trim 之外的任何处理，保证“空串 = 全部”。
        /// </summary>
        public static List<CardFileItem> Search(IEnumerable<CardFileItem>? files, string? query)
        {
            var result = new List<CardFileItem>();
            if (files == null) return result;
            string q = (query ?? "").Trim();
            foreach (var f in files)
            {
                if (f == null) continue;
                if (q.Length == 0 || Matches(f, q)) result.Add(f);
            }
            return result;
        }

        static bool Matches(CardFileItem f, string q)
        {
            // 科目用界面上实际显示的那个（CardFileGroups.SubjectOf 会把空科目显示成“未标注科目”）。
            // 以前这里比的是 f.Subject 原值：分组标题明明写着“未标注科目”，老师照着搜却搜不到 ——
            // 界面上的字必须能被搜到，否则“搜不到”会被当成数据丢了。
            return Contains(f.DisplayName, q)
                || Contains(CardFileGroups.SubjectOf(f), q)
                || Contains(f.FolderHint, q);
        }

        static bool Contains(string? text, string q)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>按科目分组。顺序 / 组内排序 / “未标注科目”都复用卡片那一套。</summary>
        public static List<CardFileGroups.FileGroup> Group(List<CardFileItem>? files)
        {
            return CardFileGroups.Group(files ?? new List<CardFileItem>());
        }

        /// <summary>面板底部状态条：台账文件 + 总记录数。</summary>
        public static string StatusLine(string? dataFolder, int records)
        {
            string file = dataFolder ?? "";
            if (file.Length > 0)
            {
                try { file = Path.Combine(file, "ledger.json"); }
                catch { file = "ledger.json"; }
            }
            else file = "ledger.json";
            return string.Format(CultureInfo.InvariantCulture, "数据文件：{0} · 记录 {1} 条", file, records);
        }

        /// <summary>
        /// 某份课件的历史，新的在前。直接读台账，不用卡片那 6 行的小快照
        /// （CardFileList.Build 的 maxHistory 是给卡片用的）。
        /// </summary>
        public static List<PanelHistoryRow> History(Ledger? ledger, CardFileItem? item, int max)
        {
            var rows = new List<PanelHistoryRow>();
            if (ledger == null || item == null || max <= 0) return rows;
            var records = NewestFirst(ledger, item, max);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r == null) continue;
                rows.Add(new PanelHistoryRow
                {
                    TimeText = TimeText(r.StartedAt),
                    PageText = RangeText(r),
                    DurationText = DurationText(r.Seconds),
                    Slide = r.EndSlide > 0 ? r.EndSlide : r.StartSlide,
                    FilePath = r.FilePath ?? "",
                    IsLatest = rows.Count == 0
                });
            }
            return rows;
        }

        /// <summary>
        /// 这份课件的记录，按“解析后的时间”从新到旧，最多 max 条。
        ///
        /// 不能直接用 Ledger.Recent 的顺序：它按 StartedAt 的字符串排序（内部用 string.CompareOrdinal），
        /// 而台账里的时间是自由文本（引擎写 "2026-09-08 10:00:00"，手改/导入的可能写 "2026/9/8 10:00:00"）。
        /// 字符串序在第一个不同的字符上分胜负，'/' 比 '-' 大，于是 9 月 8 日会被排到 9 月 30 日前面；
        /// 面板把 history[0].Slide 当成“上次停在哪一页”（立即跳转就用它），排错就会跳到旧页。
        /// Ledger.cs 不在本次改动范围，只能在读取侧重排。
        /// </summary>
        static List<SessionRecord> NewestFirst(Ledger? ledger, CardFileItem item, int max)
        {
            var result = new List<SessionRecord>();
            if (ledger == null || max <= 0) return result;

            List<SessionRecord> raw;
            try
            {
                // CardFileItem.Key 是卡片用的归并身份（file:xxx.pptx），不是台账桶名；
                // 查历史必须用 FilePath（Build 已经挑好了这份课件该跳的那个路径）。
                // max 传 int.MaxValue：Recent 自己会截断，这里要先拿全再重排，最后才截断。
                // Recent 已经过滤掉 EndSlide<=0 的无效记录、并带同名后缀桶回退，这两点必须保留。
                string key = string.IsNullOrEmpty(item.FilePath) ? item.Key : item.FilePath;
                raw = ledger.Recent(key, int.MaxValue);
            }
            catch { return result; }

            var order = new List<int>(raw.Count);
            for (int i = 0; i < raw.Count; i++) order.Add(i);
            order.Sort(delegate (int x, int y)
            {
                int c = SortTime(raw[y]).CompareTo(SortTime(raw[x]));
                // 同一秒的两条记录：Recent 已经把“后写入的那条”排在前面，这里按原始下标升序保持它。
                return c != 0 ? c : x.CompareTo(y);
            });
            for (int i = 0; i < order.Count && result.Count < max; i++) result.Add(raw[order[i]]);
            return result;
        }

        /// <summary>排序用的时间：StartedAt 能解析就用它，坏了按“最旧”处理（排到最后）。</summary>
        static DateTime SortTime(SessionRecord? r)
        {
            DateTime t;
            if (r != null && !string.IsNullOrEmpty(r.StartedAt)
                && DateTime.TryParse(r.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return DateTime.MinValue;
        }

        /// <summary>
        /// 这份课件的总页数。CardFileItem.Total 是“台账里有几条记录”，不是页数，
        /// 所以从最近一条有效记录的 SlideCount 取；老记录没有 SlideCount 时退回结束页。
        /// “最近一条”必须和 <see cref="History"/> 用同一个顺序，否则详情里的“共 N 页”
        /// 和上面列表第一行对不上。
        /// </summary>
        public static int TotalSlides(Ledger? ledger, CardFileItem? item)
        {
            if (ledger == null || item == null) return 0;
            var newest = NewestFirst(ledger, item, 1);
            if (newest.Count == 0) return 0;
            var r = newest[0];
            if (r.SlideCount > 0) return r.SlideCount;
            // 台账是可以被手改/导入的：负数页数只会让界面显示“共 -2 页”。
            return Math.Max(0, r.EndSlide);
        }

        /// <summary>大红页码。0 表示还没有可跳的页，界面显示破折号。</summary>
        public static string PageText(int slide)
        {
            return slide > 0 ? slide.ToString(CultureInfo.InvariantCulture) : "—";
        }

        public static string TotalText(int total)
        {
            return total > 0
                ? "共 " + total.ToString(CultureInfo.InvariantCulture) + " 页"
                : "页数未知";
        }

        public static string LastText(int slide)
        {
            return slide > 0
                ? "上次停在第 " + slide.ToString(CultureInfo.InvariantCulture) + " 页"
                : "还没有可跳的页码";
        }

        /// <summary>台账里一条记录的页范围，例如“第 3–7 页”。</summary>
        public static string RangeText(SessionRecord? r)
        {
            if (r == null) return "页码未知";
            int a = r.StartSlide, b = r.EndSlide;
            if (b <= 0) b = a;
            if (b <= 0) return "页码未知";
            // a > b 是坏数据（台账被手改/导入，引擎不会写出这种记录）：别显示“第 10–3 页”，
            // 只报最后停下的那一页 —— 面板只需要一个能让老师对上位置的数字。
            if (a <= 0 || a == b || a > b) return "第 " + b.ToString(CultureInfo.InvariantCulture) + " 页";
            return "第 " + a.ToString(CultureInfo.InvariantCulture) + "–"
                 + b.ToString(CultureInfo.InvariantCulture) + " 页";
        }

        /// <summary>台账里的 StartedAt 是本地时间的字符串，按 InvariantCulture 解析再格式化。</summary>
        public static string TimeText(string? startedAt)
        {
            DateTime t;
            if (!string.IsNullOrWhiteSpace(startedAt)
                && DateTime.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            {
                return t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            return string.IsNullOrWhiteSpace(startedAt) ? "时间未知" : startedAt!;
        }

        /// <summary>一条记录讲了多久。口径和卡片 HistoryLabel 的 Duration 一致。</summary>
        public static string DurationText(int seconds)
        {
            if (seconds <= 0 || seconds < 60) return "不到 1 分钟";
            if (seconds < 3600) return (seconds / 60).ToString(CultureInfo.InvariantCulture) + " 分钟";
            int hours = seconds / 3600;
            int minutes = (seconds % 3600) / 60;
            return minutes > 0
                ? hours.ToString(CultureInfo.InvariantCulture) + " 小时 " + minutes.ToString(CultureInfo.InvariantCulture) + " 分"
                : hours.ToString(CultureInfo.InvariantCulture) + " 小时";
        }
    }
}
