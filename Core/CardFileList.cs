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
        /// <summary>
        /// 这份课件的科目（ClassIsland 课表里的名字，拿不到就是空串）。分组和筛选用它，
        /// 取的是这条课件最近一条**有科目**的记录 —— 理由见 <see cref="SubjectOf"/>。
        /// </summary>
        public string Subject = "";
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
        /// <summary>
        /// 这一行背后其实是几份**内容可能不同**的同名文件时，把候选列出来（功能9）。
        /// 空 = 不用问，卡片照旧直接进历史。判据见 <see cref="CardFileList.SameNameOf"/>：
        /// 只有候选 ≥ 2、且指纹证明它们不是同一份拷贝时才填。
        /// </summary>
        public List<CardSameNameItem> SameName = new List<CardSameNameItem>();
    }

    /// <summary>
    /// 「哪一份？」面板里的一行（功能9）：一份具体的文件，以及上次停在哪一页。
    /// 名字相同、目录不同、内容还可能不同 —— 卡片只画这一行的上级目录和页码时间，
    /// 让老师自己认，而不是替他猜。
    /// </summary>
    public sealed class CardSameNameItem
    {
        /// <summary>这一份文件的完整路径；点它就跳这条路径。</summary>
        public string FilePath = "";
        /// <summary>弱化的上级目录（"…\桌面\classisland插件"），同名两份靠它区分。</summary>
        public string FolderHint = "";
        /// <summary>行尾信息，格式与课件行一致：「第 8 页 · 3 小时前」。</summary>
        public string Detail = "";
        /// <summary>
        /// 这一份上次停的页码。Detail 里其实也写了，但卡片不该去解析自己拼的文案
        /// （改一次文案就跳错页），所以页码单独递一遍。
        /// </summary>
        public int Slide;
    }

    /// <summary>
    /// 把台账摊成「打开过的课件」列表。
    ///
    /// 台账里同一个课件可能有多把键：旧版把「希沃:课件名」拼成了进程目录下的假路径，
    /// 同一份课件还可能在「桌面\…\测试课件.pptx」和「插件\test\测试课件.pptx」各有一份副本。
    /// 这里统一按**显示名**归并（希沃伪键归并到希沃名，真实文件归并到文件名），
    /// 老师就不会在卡片上看到同一份课件出现两行。副作用是两份同名的不同文件也会合成一行。
    /// 归并本身是有意的取舍（少一行比多一行更不容易点错），但**默认跳哪一份**不能靠猜：
    /// 功能9 起，每行会算出一组同名候选（<see cref="CardFileItem.SameName"/>），
    /// 内容可能不同时卡片先问一句「哪一份？」。
    /// </summary>
    public static class CardFileList
    {
        /// <summary>面板一每页最多列 8 行（卡片不做滚动，多出来的翻页看）。</summary>
        public const int MaxFiles = 8;

        /// <summary>
        /// 快照里最多带几份课件。以前这里和 MaxFiles 是同一个数（8），第 9 份起直接被截掉、
        /// 永远看不到；功能2 加了分页，快照就必须比一页宽，否则「下一页」是个假按钮。
        /// 120 份够一学期用，也把卡片哈希计算的量级挡住。
        /// </summary>
        public const int MaxSnapshot = 120;

        /// <summary>
        /// 「哪一份？」面板最多列 6 行（功能9）。卡片不做滚动，再多了面板会顶到屏幕外面，
        /// 6 份同名课件已经够极端；超出的候选不显示，但默认那一份仍然可点（退化成老行为）。
        /// </summary>
        public const int MaxSameName = 6;

        public static List<CardFileItem> Build(Ledger ledger, string? currentKey, int maxFiles, int maxHistory)
        {
            var result = new List<CardFileItem>();
            if (ledger == null || ledger.ByFile == null || maxFiles <= 0) return result;
            if (maxHistory < 0) maxHistory = 0;

            // 同一个显示名归到一个组；组里可能有多把台账键（旧污染键 + 干净键）。
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            // 台账由引擎线程写（Add 加键、Prune 删键），而这里经常跑在 UI 线程（卡片点击 →
            // Push → BuildState）或服务线程上。先把"当时那一本字典"里的记录抄进 groups，
            // 后面的排序、File.Exists、指纹判断都不再碰台账：
            //   · 不抄的话，枚举正好撞上引擎加键/删键就抛 "Collection was modified"，
            //     从卡片点击的处理器里抛出去等于点一下没反应；
            //   · 抄的时候持锁，顺手拿到的是同一个瞬间的记录集合，不会出现"桶数变了、
            //     记录数没变"的半新半旧快照。
            lock (ledger.Sync)
            {
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
                item.Subject = SubjectOf(g.Records);

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
                    var labels = HistoryLine.Variants(r.Weekday, r.Period, r.StartedAt,
                                                     r.StartSlide, r.EndSlide, DateTime.Now);
                    item.History.Add(new CardHistoryItem
                    {
                        // 每条历史认自己记录里的文件：归并后的组可能横跨两把键（旧污染键/干净键），
                        // 跳页必须落到老师点的那一条所属的文件上。
                        FilePath = Normalize(string.IsNullOrEmpty(r.FilePath) ? g.BucketKey : r.FilePath),
                        Slide = r.EndSlide,
                        Label = labels.Length > 0 ? labels[0] : "",
                        Labels = labels,
                        IsLatest = item.History.Count == 0
                    });
                }

                // 功能9：这一行背后是不是几份同名的不同文件。放在 History 之后算，
                // 因为候选要用的 DaysSince/EndSlide 和上面那行 Detail 是同一套口径。
                item.SameName = SameNameOf(g.Records);

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

        /// <summary>
        /// 算「这一行背后其实是几份同名的不同文件」（功能9）。
        ///
        /// 返回空列表 = 不用问；返回 ≥ 2 条 = 卡片弹「哪一份？」。
        /// 判据分两步，缺一不可：
        ///   1. 组里真的有两个以上**存在的真实文件**（同一份文件的旧污染键、被删掉的文件都不算）；
        ///   2. 指纹证明它们不是同一份拷贝（全同指纹 / 全都不知道 = 当同一份，别打扰老师）。
        ///
        /// 为什么第二步要卡这么死：同名课件绝大多数就是同一份拷贝 —— 老师从桌面拖进插件
        /// 目录、或者换了台电脑重新拷一份，台账里就多出一条路径。每节课都问一次
        /// 「哪一份？」会变成纯噪音，老师闭着眼点第一行，反而更容易点错。
        /// </summary>
        static List<CardSameNameItem> SameNameOf(List<SessionRecord> records)
        {
            var result = new List<CardSameNameItem>();
            if (records == null || records.Count < 2) return result;

            // 先按路径去重：台账里同一份文件可以有多条记录，还留着旧版的污染键。
            // records 已经按时间倒序（Build 里 Sort(CompareDesc) 过），所以某个路径
            // 第一次出现的那条就是它的最新记录 —— 候选顺序天然就是「最新时间倒序」。
            // 这一步不碰磁盘：组里只有一条真实路径时（绝大多数行）下面直接返回，
            // 不会让每次 BuildState 对每个课件都做一次 File.Exists。
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var latestValid = new Dictionary<string, SessionRecord>(StringComparer.OrdinalIgnoreCase);
            var print = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r == null) continue;
                string p = Normalize(r.FilePath);
                // 希沃伪键没有文件可挑（它本来就是一串「希沃:课件名」），空串更不是候选。
                if (p.Length == 0 || SeewoBridge.IsSeewoKey(p)) continue;
                if (seen.Add(p)) paths.Add(p);
                if (!latestValid.ContainsKey(p) && r.EndSlide > 0) latestValid[p] = r;
                // 取这个路径**最新一条有指纹的**记录：老记录是空串，跳过它别当成
                // 「这一份不知道指纹」，否则整组会永远被判成「有指纹 + 无指纹」的混合。
                string fp = (r.Fingerprint ?? "").Trim();
                if (fp.Length > 0 && !print.ContainsKey(p)) print[p] = fp;
            }
            if (paths.Count < 2) return result;

            for (int i = 0; i < paths.Count; i++)
            {
                string p = paths[i];
                SessionRecord? valid = null;
                // 没有可跳页码的那一份列出来也点不动，直接跳过。
                if (!latestValid.TryGetValue(p, out valid) || valid == null) continue;
                // 文件已经不在了，点了也打不开（网络盘掉线、文件被移走都算）。
                bool exists = false;
                try { exists = File.Exists(p); } catch { exists = false; }
                if (!exists) continue;
                result.Add(new CardSameNameItem
                {
                    FilePath = p,
                    FolderHint = FolderOf(p),
                    // 和上面 item.Detail 一个口径：用这一份自己最新一条有效记录的页码与年龄。
                    Detail = "第 " + valid.EndSlide + " 页 · " + SessionEngine.ShortAge(DaysSince(valid)),
                    Slide = valid.EndSlide
                });
            }
            if (result.Count < 2) { result.Clear(); return result; }
            if (result.Count > MaxSameName) result.RemoveRange(MaxSameName, result.Count - MaxSameName);

            // 指纹比对：只看指纹，不看目录。同一份文件复制到两个目录、指纹一样，
            // 点哪一份都是同一套页，不该问；指纹不同、或者一半有一半没有（说不清），
            // 就必须问 —— 猜错了就是把老师扔到另一份课件的同一页上。
            int known = 0;
            string first = "";
            bool differ = false;
            for (int i = 0; i < result.Count; i++)
            {
                string? fp;
                if (!print.TryGetValue(result[i].FilePath, out fp) || string.IsNullOrEmpty(fp)) continue;
                known++;
                if (first.Length == 0) { first = fp; continue; }
                if (!string.Equals(first, fp, StringComparison.Ordinal)) differ = true;
            }
            // 全同指纹 → 同一份拷贝；全都没指纹 → 什么也不知道，按「同一份」处理（不打扰）。
            if (!differ && (known == 0 || known == result.Count)) result.Clear();
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

        /// <summary>
        /// 这份课件算哪个科目：取最近一条**有科目**的记录。
        /// 为什么不死取最近那一条：台账里的 Subject 是后来才加的字段，老记录一律为空；
        /// 如果最近一次上课恰好不在课表里（自习、临时换课），死取最近一条就会把一直在教
        /// 语文的课件扔进「未标注科目」，老师下次就找不着它了。
        /// </summary>
        static string SubjectOf(List<SessionRecord> records)
        {
            string fallback = "";
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r == null) continue;
                string s = (r.Subject ?? "").Trim();
                if (s.Length > 0) return s;
                // 2026-10-24 功能3：Subject 为空的老记录，从 LessonName / PeriodName 里捡一次
                // （以前有 bug 时这两个字段也没写，但更早的版本可能写过课程名）。
                // 只能捡"像科目"的：捡到"第 3 节"/"课间"会把分组弄脏 —— 捡不到就保持
                // 「未标注科目」，宁可分组少一类，也不硬凑一个假科目。
                if (fallback.Length == 0 && LooksLikeSubject(r.PeriodName)) fallback = (r.PeriodName ?? "").Trim();
                if (fallback.Length == 0 && LooksLikeSubject(r.LessonName)) fallback = (r.LessonName ?? "").Trim();
            }
            return fallback;
        }

        /// <summary>
        /// "这个字符串像不像科目名"。科目名都是短词（"数学"、"信息技术"），而台账里另外几种
        /// 常见值是节次标签和课间标签，它们一律不算：
        ///   "第 3 节" / "第3节" / "课间" / "午休" / "自习" / "上课" / "放学" / "分割线" / "行动"。
        /// </summary>
        internal static bool LooksLikeSubject(string? s)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0 || t.Length > 12) return false;
            if (t[0] == '第' && t[t.Length - 1] == '节') return false;
            switch (t)
            {
                case "课间":
                case "午休":
                case "自习":
                case "上课":
                case "放学":
                case "分割线":
                case "行动":
                case "未标注科目":
                    return false;
            }
            return true;
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
            if (r != null && !string.IsNullOrEmpty(r.StartedAt)
                && DateTime.TryParse(r.StartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return t;
            if (r != null && !string.IsNullOrEmpty(r.EndedAt)
                && DateTime.TryParse(r.EndedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return t;
            return DateTime.MinValue;
        }

        static double DaysSince(SessionRecord? r)
        {
            DateTime t;
            string s = r == null ? "" : (string.IsNullOrEmpty(r.EndedAt) ? r.StartedAt : r.EndedAt);
            if (!DateTime.TryParse(s ?? "", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return -1;
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
