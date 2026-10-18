using System;
using System.Collections.Generic;
using System.IO;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 卡片「文件夹」面板里的一行：一份课件，以及它自己的历史。
    ///
    /// 为什么把历史一起塞进这里，而不是等卡片点开再回查台账：卡片画在浮层线程上，
    /// 台账却由引擎线程写；跨线程读 Dictionary 会偶发 "collection was modified"。
    /// 引擎在 BuildState 里一次性算好，卡片只读快照，线程边界就只有一个。
    /// </summary>
    public sealed class CardFileItem
    {
        /// <summary>归并键（file:文件名 / seewo:课件名）。同名不同目录的旧记录靠它合成一行。</summary>
        public string Key = "";
        /// <summary>这份课件默认跳转用的文件；历史行自己还带 <see cref="CardHistoryItem.FilePath"/>。</summary>
        public string FilePath = "";
        /// <summary>老师看得懂的名字：文件名，或「希沃 · 课件名」。</summary>
        public string DisplayName = "";
        /// <summary>两行显示名撞车时才填的上级目录，平时是空的。</summary>
        public string FolderHint = "";
        /// <summary>行尾的弱化信息，例如「第 8 页 · 3 小时前」。</summary>
        public string Detail = "";
        /// <summary>是不是卡片此刻正在讲的那份课件（用强调色标出来）。</summary>
        public bool IsCurrent;
        /// <summary>归并后的总记录数（不截断）。</summary>
        public int Total;
        /// <summary>归并后的最近几条记录，新的在前；最多 6 条。</summary>
        public List<CardHistoryItem> History = new List<CardHistoryItem>();
        /// <summary>最近一次记录的时间，只用于排序；不画到卡片上。</summary>
        internal DateTime LastAt;
        /// <summary>最近一条真实路径的上级目录，给 FolderHint 兜底。</summary>
        internal string Folder = "";
    }

    /// <summary>
    /// 把台账摊成「打开过的课件」列表。
    ///
    /// 台账里同一个课件可能有多把键：旧版把「希沃:课件名」拼成了进程目录下的假路径，
    /// 同一份课件还可能在「桌面\…\测试课件.pptx」和「插件\test\测试课件.pptx」各有一份副本。
    /// 这里统一按**显示名**归并（希沃伪键归并到希沃名，真实文件归并到文件名），
    /// 老师就不会在卡片上看到同一份课件出现两行。副作用是两份同名的不同文件也会合成一行，
    /// 这是有意的取舍：课堂上同名课件基本就是同一份拷贝，少一行比多一行更不容易点错。
    /// </summary>
    public static class CardFileList
    {
        /// <summary>面板最多列 8 行（卡片不做滚动）。</summary>
        public const int MaxFiles = 8;

        public static List<CardFileItem> Build(Ledger ledger, string? currentKey, int maxFiles, int maxHistory)
        {
            var result = new List<CardFileItem>();
            if (ledger == null || ledger.ByFile == null || maxFiles <= 0) return result;
            if (maxHistory < 0) maxHistory = 0;

            // 同一个显示名归到一个组；组里可能有多把台账键（旧污染键 + 干净键）。
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ledger.ByFile)
            {
                var list = kv.Value;
                if (list == null || list.Count == 0) continue;
                string id = Identity(kv.Key);
                if (id.Length == 0) continue;
                Group? g;
                if (!groups.TryGetValue(id, out g) || g == null)
                {
                    g = new Group { Id = id, SeewoName = SeewoName(kv.Key), BucketKey = kv.Key };
                    groups[id] = g;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r != null) g.Records.Add(r);
                }
            }

            string currentId = Identity(currentKey);
            foreach (var g in groups.Values)
            {
                if (g.Records.Count == 0) continue;
                // 文件里的顺序不一定等于时间顺序（手工改过台账/导入过），先按时间排。
                g.Records.Sort(CompareDesc);

                SessionRecord? newest = null;
                SessionRecord? newestValid = null;
                for (int i = 0; i < g.Records.Count; i++)
                {
                    var r = g.Records[i];
                    if (newest == null) newest = r;
                    if (newestValid == null && r.EndSlide > 0) newestValid = r;
                }
                if (newest == null) continue;

                var item = new CardFileItem
                {
                    Key = g.Id,
                    IsCurrent = currentId.Length > 0 && string.Equals(currentId, g.Id, StringComparison.OrdinalIgnoreCase),
                    LastAt = TimeOf(newest)
                };
                item.FilePath = ChooseJumpPath(g.Records);
                if (item.FilePath.Length == 0) item.FilePath = Normalize(g.BucketKey);

                item.DisplayName = DisplayNameFor(g, newest);
                item.Folder = FolderOf(item.FilePath);
                item.Total = g.Records.Count;

                var head = newestValid ?? newest;
                if (newestValid != null)
                {
                    item.Detail = "第 " + newestValid.EndSlide + " 页 · " + SessionEngine.ShortAge(DaysSince(head));
                }
                else
                {
                    // 只有坏记录（没有结束页）时也保留这一份课件：至少让老师知道它存在。
                    item.Detail = "还没有可跳的页码";
                }

                for (int i = 0; i < g.Records.Count && item.History.Count < maxHistory; i++)
                {
                    var r = g.Records[i];
                    if (r.EndSlide <= 0) continue;
                    item.History.Add(new CardHistoryItem
                    {
                        // 每条历史认自己记录里的文件：归并后的组可能横跨两把键（旧污染键/干净键），
                        // 跳页必须落到老师点的那一条所属的文件上。
                        FilePath = Normalize(string.IsNullOrEmpty(r.FilePath) ? g.BucketKey : r.FilePath),
                        Slide = r.EndSlide,
                        Label = SessionEngine.HistoryLabel(r),
                        IsLatest = item.History.Count == 0
                    });
                }

                result.Add(item);
            }

            // 归并后理论上不会再有同名行；保留这段兜底：万一以后改了归并规则（比如按完整路径），
            // 同名两行也能靠上级目录区分，而不是让老师面对两行一模一样的字。
            var byName = new Dictionary<string, List<CardFileItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in result)
            {
                List<CardFileItem>? list;
                if (!byName.TryGetValue(f.DisplayName, out list) || list == null) { list = new List<CardFileItem>(); byName[f.DisplayName] = list; }
                list.Add(f);
            }
            foreach (var kv in byName)
            {
                if (kv.Value.Count < 2) continue;
                foreach (var f in kv.Value) f.FolderHint = f.Folder;
            }

            // 最近打开的在最上面；同一时刻按名字排，刷新时不会两行来回换位。
            result.Sort((a, b) =>
            {
                int c = b.LastAt.CompareTo(a.LastAt);
                return c != 0 ? c : string.CompareOrdinal(a.DisplayName, b.DisplayName);
            });
            if (result.Count > maxFiles) result.RemoveRange(maxFiles, result.Count - maxFiles);
            return result;
        }

        /// <summary>把旧版拼出来的「c:\…\希沃:课件名」还原成干净的伪键；真实路径原样返回。</summary>
        public static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string? seewo = SeewoName(path);
            return seewo != null ? SeewoBridge.KeyPrefix + seewo : path.Trim();
        }

        /// <summary>归并身份：希沃按课件名，真实文件按文件名（不含目录）。</summary>
        static string Identity(string? pathOrKey)
        {
            string? seewo = SeewoName(pathOrKey);
            if (seewo != null) return "seewo:" + seewo.ToLowerInvariant();
            string name = FileNameOf(pathOrKey);
            return name.Length > 0 ? "file:" + name.ToLowerInvariant() : "";
        }

        static string? SeewoName(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.LastIndexOf(SeewoBridge.KeyPrefix, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            string name = s.Substring(i + SeewoBridge.KeyPrefix.Length).Trim();
            return name.Length == 0 ? null : name;
        }

        static string FileNameOf(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path); }
            catch { return path; }
        }

        static string DisplayNameFor(Group g, SessionRecord newest)
        {
            string? seewo = SeewoName(newest.FilePath) ?? g.SeewoName;
            if (seewo != null) return "希沃 · " + seewo;
            string name = FileNameOf(newest.FilePath);
            if (name.Length == 0) name = FileNameOf(g.BucketKey);
            if (name.Length == 0) name = g.BucketKey;
            return name;
        }

        /// <summary>
        /// 组内默认跳哪份文件：优先最近一条存在的文件；旧版本的假路径（进程目录已不存在）
        /// 会让 PptBridge 打不开 PowerPoint，所以宁可退到同组里更早但真实存在的那条。
        /// 希沃只有伪键，直接返回最新的干净键。
        /// </summary>
        static string ChooseJumpPath(List<SessionRecord> records)
        {
            string fallback = "";
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                string p = Normalize(r.FilePath);
                if (p.Length == 0) continue;
                if (SeewoBridge.IsSeewoKey(p)) return p;
                if (fallback.Length == 0) fallback = p;
                bool exists = false;
                try { exists = File.Exists(p); } catch { }
                if (exists) return p;
            }
            return fallback;
        }

        static string FolderOf(string path)
        {
            if (string.IsNullOrEmpty(path) || SeewoBridge.IsSeewoKey(path)) return "";
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return "";
                var parts = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return "";
                int take = Math.Min(2, parts.Length);
                return "…\\" + string.Join("\\", parts, parts.Length - take, take);
            }
            catch { return ""; }
        }

        static DateTime TimeOf(SessionRecord r)
        {
            DateTime t;
            if (r != null && !string.IsNullOrEmpty(r.StartedAt) && DateTime.TryParse(r.StartedAt, out t)) return t;
            if (r != null && !string.IsNullOrEmpty(r.EndedAt) && DateTime.TryParse(r.EndedAt, out t)) return t;
            return DateTime.MinValue;
        }

        static double DaysSince(SessionRecord? r)
        {
            DateTime t;
            string s = r == null ? "" : (string.IsNullOrEmpty(r.EndedAt) ? r.StartedAt : r.EndedAt);
            if (!DateTime.TryParse(s ?? "", out t)) return -1;
            return (DateTime.Now - t).TotalDays;
        }

        static int CompareDesc(SessionRecord? a, SessionRecord? b)
        {
            // 按解析后的时间排，而不是按字符串：台账是手改/导入过的，StartedAt 格式不一定是
            // 同一种宽度，纯字符串比较会把 2026/9/8 排到 2026/10/1 后面。
            int c = TimeOf(b).CompareTo(TimeOf(a));
            if (c != 0) return c;
            return string.CompareOrdinal(b?.StartedAt ?? "", a?.StartedAt ?? "");
        }

        sealed class Group
        {
            public string Id = "";
            public string BucketKey = "";
            public string? SeewoName;
            public readonly List<SessionRecord> Records = new List<SessionRecord>();
        }
    }
}
