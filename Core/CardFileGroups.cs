using System;
using System.Collections.Generic;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 把「打开过的课件」按科目分组、筛选、分页。卡片只负责画，这些规则放在这里，
    /// 是因为它们全是纯函数：课堂上没法调试，能单测的部分就别塞进 GDI 的绘制代码里。
    ///
    /// 顺序规则（2026-10-24 用户第 3 条：全部课件历史太乱，要按科目分组）：
    ///   组按「该组最近一份课件的时间」排，组内也按最近时间排 —— 也就是整份列表的
    ///   最近时间顺序不变，只是把同科目的挪到一起。老师找课件时先看科目，再看新鲜度。
    /// </summary>
    public static class CardFileGroups
    {
        /// <summary>台账里那些不知道科目的记录（老版本写的、或者上课时不在课表里）都归到这一组。</summary>
        public const string Unmarked = "未标注科目";

        /// <summary>屏幕上的一组：科目名 + 这一组里的课件（顺序已经排好）。</summary>
        public sealed class FileGroup
        {
            public string Subject = "";
            public List<CardFileItem> Items = new List<CardFileItem>();
        }

        /// <summary>课件显示用的科目名：去空白；没有科目的返回「未标注科目」，卡片上不会出现空标题。</summary>
        public static string SubjectOf(CardFileItem f)
        {
            if (f == null) return Unmarked;
            string s = (f.Subject ?? "").Trim();
            return s.Length > 0 ? s : Unmarked;
        }

        /// <summary>
        /// 按科目归并，返回排好序的组。组顺序 = 该组最上面那份课件在入参里的位置，
        /// 所以入参是「最近优先」时，组也是「最近用过的科目在前」。
        /// </summary>
        public static List<FileGroup> Group(List<CardFileItem> files)
        {
            var groups = new List<FileGroup>();
            if (files == null) return groups;

            var index = new Dictionary<string, FileGroup>(StringComparer.Ordinal);
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (f == null) continue;
                string subject = SubjectOf(f);
                FileGroup? g;
                if (!index.TryGetValue(subject, out g) || g == null)
                {
                    g = new FileGroup { Subject = subject };
                    index[subject] = g;
                    groups.Add(g);
                }
                g.Items.Add(f);
            }

            // 组内按最近时间排：入参一般已经是最近优先（CardFileList.Build 排过），
            // 但测试和以后的调用方不该依赖这一点，排一遍只要几微秒。
            for (int i = 0; i < groups.Count; i++) groups[i].Items.Sort(CompareRecent);
            return groups;
        }

        /// <summary>分组后的线性顺序（组按最近、组内按最近）。分页必须按这个顺序切，不能按原始顺序。</summary>
        public static List<CardFileItem> Flatten(List<CardFileItem> files)
        {
            var groups = Group(files);
            var all = new List<CardFileItem>();
            for (int i = 0; i < groups.Count; i++) all.AddRange(groups[i].Items);
            return all;
        }

        /// <summary>筛选条上的科目（去重、保持分组顺序）。「全部」不在里面，它是筛选条的固定第一项。</summary>
        public static List<string> Subjects(List<CardFileItem> files)
        {
            var groups = Group(files);
            var names = new List<string>();
            for (int i = 0; i < groups.Count; i++) names.Add(groups[i].Subject);
            return names;
        }

        /// <summary>
        /// 按科目筛选。<paramref name="subject"/> 为空 = 全部。返回的顺序和 <see cref="Flatten"/> 一致，
        /// 于是「筛选 → 分页 → 分组标题」三步能拼在一起而不需要再排一次。
        /// </summary>
        public static List<CardFileItem> Filter(List<CardFileItem> files, string? subject)
        {
            var all = Flatten(files);
            string want = (subject ?? "").Trim();
            if (want.Length == 0) return all;
            var picked = new List<CardFileItem>();
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(SubjectOf(all[i]), want, StringComparison.Ordinal)) picked.Add(all[i]);
            }
            return picked;
        }

        /// <summary>总页数。count 为 0 时仍然算 1 页：空台账也要显示「还没有记录」，不能算成 0 页。</summary>
        public static int PageCount(int count, int pageSize)
        {
            if (pageSize <= 0) pageSize = 1;
            if (count <= 0) return 1;
            return (count + pageSize - 1) / pageSize;
        }

        /// <summary>把页码夹进有效范围：筛选后条数变少、或者台账被清掉时，旧页码不能留在外面。</summary>
        public static int ClampPage(int page, int count, int pageSize)
        {
            int pages = PageCount(count, pageSize);
            if (page < 0) return 0;
            if (page > pages - 1) return pages - 1;
            return page;
        }

        /// <summary>
        /// 取某一页要画的分组。跨页的那一组会在新一页上再画一次科目标题 ——
        /// 一页里出现没有标题的课件行，老师会不知道它是哪个科目的。
        /// </summary>
        public static List<FileGroup> Page(List<CardFileItem> filtered, int page, int pageSize)
        {
            var groups = new List<FileGroup>();
            if (filtered == null || filtered.Count == 0) return groups;
            if (pageSize <= 0) pageSize = 1;
            page = ClampPage(page, filtered.Count, pageSize);

            int start = page * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            for (int i = start; i < end; i++)
            {
                var f = filtered[i];
                string subject = SubjectOf(f);
                FileGroup? g = groups.Count > 0 ? groups[groups.Count - 1] : null;
                if (g == null || !string.Equals(g.Subject, subject, StringComparison.Ordinal))
                {
                    g = new FileGroup { Subject = subject };
                    groups.Add(g);
                }
                g.Items.Add(f);
            }
            return groups;
        }

        /// <summary>一页里的课件行数（不含科目标题行）。</summary>
        public static int CountItems(List<FileGroup> groups)
        {
            int n = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g != null && g.Items != null) n += g.Items.Count;
            }
            return n;
        }

        static int CompareRecent(CardFileItem? a, CardFileItem? b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            int c = b.LastAt.CompareTo(a.LastAt);
            return c != 0 ? c : string.CompareOrdinal(a.DisplayName ?? "", b.DisplayName ?? "");
        }
    }
}
