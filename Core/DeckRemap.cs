using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace ClassSlideSync.Plugin.Core
{
    // ======================================================================================
    // 任务 6 + 任务 10（2026-10-25）：课件「对不上」之后的两个补救动作。
    //
    //   6) 课件被改过（指纹变了、页数也变了）时，把「上次停在第 N 页」映射到新版本的第 M 页。
    //      做法：纯文件解析 .pptx（OpenXML 就是一个 zip，里面有 ppt/slides/slideN.xml），
    //      取出每页的文字，用「字符二元组 Dice 相似度 + 上下文偏移投票」对齐页码。
    //      相似度不够就明确说「课件改过了，页码可能不准」，绝不给一个假页码。
    //
    //  10) 台账里的路径失效（课件被移动/改名）时，去同科目文件夹和原目录里找候选：
    //      文件名相似 + 内容指纹相同（或每页文字高度相似），找到就把历史接过去、记一条日志；
    //      找不到就保持「找不到文件」。
    //
    // 这个文件里所有会碰磁盘的函数都吞掉异常：卡片在 UI 线程上每拍都可能调它，不能因为
    // 一份坏掉的 pptx 把卡片打崩。纯函数（MapPage / ContentSimilarity / NameSimilarity）
    // 不碰磁盘，方便单测。
    // ======================================================================================

    /// <summary>一页课件能拿到的东西：整页文字（若干段用 \n 连）+ 第一段（标题味儿的那行）。</summary>
    public sealed class DeckPage
    {
        public string Text { get; set; } = "";
        public string Title { get; set; } = "";
        public bool IsEmpty { get { return (Text ?? "").Trim().Length == 0; } }
    }

    /// <summary>一份课件的页面快照。Fingerprint 是 <see cref="Ledger.Fingerprint"/> 算的那个"头尾 + 长度"摘要。</summary>
    public sealed class DeckPages
    {
        public string Path { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public List<DeckPage> Pages { get; set; } = new List<DeckPage>();

        public int Count { get { return Pages == null ? 0 : Pages.Count; } }

        public DeckPage? Page(int slide)
        {
            if (Pages == null || slide < 1 || slide > Pages.Count) return null;
            return Pages[slide - 1];
        }
    }

    /// <summary>
    /// 读 .pptx 的每页文字。只用 System.IO.Compression + XmlReader，不引 OpenXML SDK，
    /// 也不启动 WPS/PPT（任务要求：两条 COM 路线一条都不许走）。
    /// </summary>
    public static class DeckReader
    {
        const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        static readonly XmlReaderSettings Settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CloseInput = false,
            CheckCharacters = false,
        };

        /// <summary>能不能解析：只认 OOXML 压缩包；.ppt 是老二进制格式，这里不猜。</summary>
        public static bool CanParse(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext;
            try { ext = System.IO.Path.GetExtension(path).ToLowerInvariant(); } catch { return false; }
            return ext == ".pptx" || ext == ".pptm" || ext == ".ppsx" || ext == ".pptM".ToLowerInvariant();
        }

        /// <summary>
        /// 读出每页文字；读不到（不是 pptx、文件坏了、不存在）返回 null，不抛异常。
        /// 页序以 ppt/presentation.xml 的 &lt;p:sldIdLst&gt; 为准 —— 在 PowerPoint 里删过页的
        /// 课件，slideN.xml 的编号是稀疏的（1、3、4、7…），按文件名排序会把页序搞乱。
        /// </summary>
        public static DeckPages? Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { return null; }
            try
            {
                if (!File.Exists(full)) return null;
                if (!CanParse(full)) return null;
            }
            catch { return null; }

            try
            {
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false))
                {
                    var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        if (e == null) continue;
                        string name = (e.FullName ?? "").Replace('\\', '/');
                        if (name.Length == 0) continue;
                        map[name] = e;
                    }

                    var order = SlideOrder(map);
                    if (order.Count == 0) return null;

                    var pages = new List<DeckPage>(order.Count);
                    foreach (var name in order)
                    {
                        if (!map.TryGetValue(name, out var entry)) continue;
                        DeckPage page;
                        try
                        {
                            using (var s = entry.Open())
                            {
                                page = ReadSlide(s);
                            }
                        }
                        catch
                        {
                            // 单页 XML 坏掉不该让整份课件读不出来：留一个空页，页码不会错位。
                            page = new DeckPage();
                        }
                        pages.Add(page);
                    }
                    if (pages.Count == 0) return null;
                    return new DeckPages { Path = full, Fingerprint = Ledger.Fingerprint(full), Pages = pages };
                }
            }
            catch { return null; }
        }

        static DeckPage ReadSlide(Stream stream)
        {
            var paras = new List<string>();
            var cur = new StringBuilder();
            bool inText = false;
            try
            {
                using (var r = XmlReader.Create(stream, Settings))
                {
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            if (r.LocalName == "t" && r.NamespaceURI == NsA) inText = true;
                            else if (r.LocalName == "br" || (r.LocalName == "tab" && r.NamespaceURI == NsA)) cur.Append(' ');
                            continue;
                        }
                        if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (r.LocalName == "t" && r.NamespaceURI == NsA) { inText = false; continue; }
                            if (r.LocalName == "p" && r.NamespaceURI == NsP)
                            {
                                if (cur.Length > 0) { paras.Add(cur.ToString()); cur.Clear(); }
                            }
                            continue;
                        }
                        if (inText && (r.NodeType == XmlNodeType.Text
                            || r.NodeType == XmlNodeType.Whitespace
                            || r.NodeType == XmlNodeType.SignificantWhitespace
                            || r.NodeType == XmlNodeType.CDATA))
                        {
                            cur.Append(r.Value);
                        }
                    }
                }
            }
            catch { }
            if (cur.Length > 0) paras.Add(cur.ToString());

            var page = new DeckPage();
            var all = new StringBuilder();
            for (int i = 0; i < paras.Count; i++)
            {
                string line = (paras[i] ?? "").Trim();
                if (line.Length == 0) continue;
                if (all.Length > 0) all.Append('\n');
                all.Append(line);
                if (page.Title.Length == 0) page.Title = line;
            }
            page.Text = all.ToString();
            return page;
        }

        static List<string> SlideOrder(Dictionary<string, ZipArchiveEntry> map)
        {
            var order = new List<string>();
            var rels = ReadRels(map, "ppt/presentation.xml");
            if (map.TryGetValue("ppt/presentation.xml", out var pres))
            {
                try
                {
                    using (var s = pres.Open())
                    using (var r = XmlReader.Create(s, Settings))
                    {
                        while (r.Read())
                        {
                            if (r.NodeType != XmlNodeType.Element || r.LocalName != "sldId") continue;
                            string rid = r.GetAttribute("id", NsR) ?? "";
                            if (rid.Length == 0) continue;
                            if (rels.TryGetValue(rid, out var part) && !order.Contains(part)) order.Add(part);
                        }
                    }
                }
                catch { order.Clear(); }
            }

            if (order.Count == 0)
            {
                // 兜底：presentation.xml 读不动时按 slideN.xml 的数字排；这时候稀疏编号会错位，
                // 但总比一页都拿不到强。
                var found = new List<KeyValuePair<int, string>>();
                var re = new Regex(@"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (var kv in map)
                {
                    var m = re.Match(kv.Key);
                    if (!m.Success) continue;
                    if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num)) continue;
                    if (num <= 0) continue;
                    found.Add(new KeyValuePair<int, string>(num, kv.Key));
                }
                found.Sort((a, b) => a.Key.CompareTo(b.Key));
                foreach (var kv in found) order.Add(kv.Value);
            }
            return order;
        }

        static Dictionary<string, string> ReadRels(Dictionary<string, ZipArchiveEntry> map, string part)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            string dir = PartDir(part);
            string relName = dir + "_rels/" + System.IO.Path.GetFileName(part) + ".rels";
            if (!map.TryGetValue(relName, out var entry)) return dict;
            try
            {
                using (var s = entry.Open())
                using (var r = XmlReader.Create(s, Settings))
                {
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                        string id = r.GetAttribute("Id") ?? "";
                        string target = r.GetAttribute("Target") ?? "";
                        string mode = r.GetAttribute("TargetMode") ?? "";
                        if (id.Length == 0 || target.Length == 0) continue;
                        if (string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase)) continue;
                        dict[id] = ResolvePart(dir, target);
                    }
                }
            }
            catch { }
            return dict;
        }

        static string PartDir(string part)
        {
            int slash = part.LastIndexOf('/');
            return slash < 0 ? "" : part.Substring(0, slash + 1);
        }

        static string ResolvePart(string baseDir, string target)
        {
            target = (target ?? "").Replace('\\', '/');
            string combined = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : baseDir + target;
            var parts = new List<string>();
            foreach (var seg in combined.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            return string.Join("/", parts).ToLowerInvariant();
        }
    }

    /// <summary>页码对齐的结论。只有 Exact / Shifted 才允许拿 NewSlide 去自动跳页。</summary>
    public enum RemapKind
    {
        /// <summary>没给页码（oldSlide &lt;= 0），什么也没算。</summary>
        None,
        /// <summary>新版里有文字完全相同的页，页码可信。</summary>
        Exact,
        /// <summary>文字有改动，但相似度和上下文都指向同一页，页码可信。</summary>
        Shifted,
        /// <summary>像是某一页，但证据不足（重复页、相似度偏低）——只能说「可能不准」。</summary>
        Uncertain,
        /// <summary>整份课件被换掉了，旧页在新版里找不到。</summary>
        Replaced,
        /// <summary>没有旧版页面留底（或当前文件读不出来），没法算。</summary>
        Unavailable,
    }

    public sealed class RemapResult
    {
        public RemapKind Kind { get; set; } = RemapKind.None;
        public int OldSlide { get; set; }
        public int NewSlide { get; set; }
        public int OldCount { get; set; }
        public int NewCount { get; set; }
        /// <summary>新页码 - 旧页码；NewSlide 为 0 时是 0。</summary>
        public int Shift { get { return (NewSlide > 0 && OldSlide > 0) ? NewSlide - OldSlide : 0; } }
        public double Confidence { get; set; }
        /// <summary>给卡片直接显示的中文说明。</summary>
        public string Note { get; set; } = "";
        public bool Usable { get { return Kind == RemapKind.Exact || Kind == RemapKind.Shifted; } }
    }

    /// <summary>移动/改名重关联时的一个候选文件。</summary>
    public sealed class RelocateCandidate
    {
        public string Path { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public double NameScore { get; set; }
        /// <summary>每页文字的平均相似度；-1 表示没算（读不到旧版留底或候选不是 pptx）。</summary>
        public double ContentScore { get; set; } = -1;
        public bool SameFingerprint { get; set; }
        public double Score { get; set; }
        public bool Accepted { get; set; }
    }

    public sealed class RelocateResult
    {
        public bool Found { get; set; }
        public string Path { get; set; } = "";
        /// <summary>给卡片直接显示的中文说明（成功：已重新关联；失败：找不到文件）。</summary>
        public string Note { get; set; } = "";
        public List<RelocateCandidate> Candidates { get; set; } = new List<RelocateCandidate>();
    }

    /// <summary>
    /// 任务 6/10 的实现。相似度算法见文件末尾的「算法说明」，报告里也抄了一份。
    /// </summary>
    public static class DeckRemap
    {
        // ---- 阈值（改这里就等于改行为，报告里逐个解释过）----
        /// <summary>单页最佳匹配的接受线：低于它不许自动跳页。</summary>
        public const double MatchThreshold = 0.62;
        /// <summary>最佳页与次佳页至少要差这么多，否则算「有歧义」。</summary>
        public const double MarginThreshold = 0.08;
        /// <summary>邻居页投票的最低相似度。</summary>
        public const double ContextFloor = 0.55;
        /// <summary>投票给出的目标页，自身仍要够像。</summary>
        public const double ContextVerify = 0.45;
        /// <summary>低于它认为整份课件都换了（不是"第 N 页被删"）。</summary>
        public const double ReplaceFloor = 0.30;
        /// <summary>重关联时「内容够像 + 名字一般」的接受线。</summary>
        public const double RelocateContentStrong = 0.85;
        public const double RelocateNameLoose = 0.50;
        /// <summary>重关联时「名字很像 + 内容凑合」的接受线。</summary>
        public const double RelocateContentWeak = 0.70;
        public const double RelocateNameStrong = 0.80;
        /// <summary>没有任何内容证据时，只允许"名字几乎一样"接上。</summary>
        public const double RelocateNameOnly = 0.92;

        // ==================================================================================
        // 6) 页码对齐
        // ==================================================================================

        public static RemapResult MapPage(DeckPages? oldDeck, DeckPages? newDeck, int oldSlide)
        {
            return MapPage(oldDeck == null ? null : oldDeck.Pages, newDeck == null ? null : newDeck.Pages, oldSlide);
        }

        /// <summary>
        /// 纯函数：旧版第 oldSlide 页 → 新版第几页。不碰磁盘，方便单测。
        /// 顺序：① 文字完全相同的页 → ② 上下文偏移投票 → ③ 单页最佳匹配 → ④ 拒绝。
        /// </summary>
        public static RemapResult MapPage(IList<DeckPage>? oldPages, IList<DeckPage>? newPages, int oldSlide)
        {
            var res = new RemapResult
            {
                OldSlide = oldSlide,
                OldCount = oldPages == null ? 0 : oldPages.Count,
                NewCount = newPages == null ? 0 : newPages.Count,
            };
            if (oldSlide <= 0)
            {
                res.Kind = RemapKind.None;
                return res;
            }
            if (oldPages == null || newPages == null || oldPages.Count == 0 || newPages.Count == 0)
            {
                res.Kind = RemapKind.Unavailable;
                res.Note = NoBaselineNote(oldSlide);
                return res;
            }

            // 旧页码超界（例如旧版 12 页、老师停在第 12 页，新版只剩 10 页）：夹到最后一页来找，
            // 但结论里保留原始页码，卡片显示"第 12 页"才是老师记得的那个数。
            int n = Math.Min(oldSlide, oldPages.Count) - 1;
            var oldP = Prepare(oldPages);
            var newP = Prepare(newPages);
            var o = oldP[n];

            // ① 文字完全相同。删页/插页之后仍然活着的那些页会走到这里，最可靠。
            var hits = new List<int>();
            if (o.NText.Length > 0)
            {
                for (int j = 0; j < newP.Count; j++)
                {
                    if (newP[j].NText == o.NText) hits.Add(j);
                }
            }

            // ② 上下文投票：删页/插页会让同一段课件的每一页都平移同样的页数，
            //    取旧第 N 页前后各 3 页，各自在新版里找最像的一页，用"新索引-旧索引"投票。
            Vote vote = VoteFor(oldP, newP, n);

            if (hits.Count == 1)
            {
                int m = hits[0];
                res.Kind = RemapKind.Exact;
                res.NewSlide = m + 1;
                res.Confidence = 1.0;
                res.Note = MovedNote(oldSlide, m + 1, m - n);
                return res;
            }

            if (hits.Count > 1)
            {
                // 新版里有好几页文字一模一样（老师爱把"课堂练习"复制好几页）。
                // 上下文能定下偏移量时按偏移量选，否则明说"可能不准"。
                if (vote.Has && InRange(n + vote.Offset, newP.Count) && hits.Contains(n + vote.Offset))
                {
                    res.Kind = RemapKind.Shifted;
                    res.NewSlide = n + vote.Offset + 1;
                    res.Confidence = Clamp(0.55 * vote.Mean + 0.45 * 1.0, 0, 0.95);
                    res.Note = MovedNote(oldSlide, res.NewSlide, vote.Offset);
                    return res;
                }
                int pick = hits[0];
                for (int i = 1; i < hits.Count; i++)
                {
                    if (Math.Abs(hits[i] - n) < Math.Abs(pick - n)) pick = hits[i];
                }
                res.Kind = RemapKind.Uncertain;
                res.NewSlide = pick + 1;
                res.Confidence = 0.5;
                res.Note = DuplicateNote(oldSlide, res.NewSlide, hits.Count);
                return res;
            }

            if (o.NText.Length == 0)
            {
                // 旧这一页没有文字（纯图片页）。只有邻居投票非常一致时才敢说"整体平移了 k 页"。
                if (vote.Has && vote.Count >= 3 && vote.Mean >= 0.70 && InRange(n + vote.Offset, newP.Count))
                {
                    res.Kind = RemapKind.Shifted;
                    res.NewSlide = n + vote.Offset + 1;
                    res.Confidence = 0.5;
                    res.Note = ShiftOnlyNote(oldSlide, res.NewSlide, vote.Offset);
                }
                else
                {
                    res.Kind = RemapKind.Unavailable;
                    res.Note = BlankPageNote(oldSlide);
                }
                return res;
            }

            // ② 的落地：投票给出的那一页必须自己也够像。
            if (vote.Has)
            {
                int c = n + vote.Offset;
                if (InRange(c, newP.Count))
                {
                    double direct = PageSim(o, newP[c]);
                    if (direct >= ContextVerify)
                    {
                        res.Kind = RemapKind.Shifted;
                        res.NewSlide = c + 1;
                        res.Confidence = Clamp(0.55 * vote.Mean + 0.45 * direct, 0, 0.95);
                        res.Note = MovedNote(oldSlide, c + 1, vote.Offset);
                        return res;
                    }
                }
            }

            // ③ 没有上下文共识（旧版只有一两页、或者整份课件都改过）时，退到单页最佳匹配。
            //    要求"够像"且"明显比第二像的那页更像"，否则宁可说可能不准。
            int best = -1;
            double bestRaw = 0, bestAdj = 0, secondAdj = 0;
            for (int j = 0; j < newP.Count; j++)
            {
                double raw = PageSim(o, newP[j]);
                // 同分时偏向原位置：页码没动的情况最常见。
                double adj = raw + 0.03 * (1.0 - Math.Min(Math.Abs(j - n), 20) / 20.0);
                if (adj > bestAdj)
                {
                    secondAdj = Math.Max(secondAdj, bestAdj);
                    bestAdj = adj;
                    bestRaw = raw;
                    best = j;
                }
                else if (adj > secondAdj)
                {
                    secondAdj = adj;
                }
            }

            if (best >= 0 && bestRaw >= MatchThreshold && (bestAdj - secondAdj) >= MarginThreshold)
            {
                res.Kind = RemapKind.Shifted;
                res.NewSlide = best + 1;
                res.Confidence = bestRaw;
                res.Note = MovedNote(oldSlide, best + 1, best - n);
                return res;
            }
            if (best >= 0 && bestRaw >= ReplaceFloor)
            {
                res.Kind = RemapKind.Uncertain;
                res.NewSlide = best + 1;
                res.Confidence = bestRaw;
                res.Note = WeakNote(oldSlide, best + 1, bestRaw);
                return res;
            }

            res.Kind = RemapKind.Replaced;
            res.NewSlide = 0;
            res.Confidence = bestRaw;
            res.Note = ReplacedNote(oldSlide);
            return res;
        }

        /// <summary>
        /// 从台账记录出发的便捷入口：旧版页面从 <see cref="DeckPageCache"/> 里按记录里的指纹取，
        /// 新版页面现读。算完会把新版也记进缓存（下次再改就有旧版留底了）。
        /// </summary>
        /// <param name="cacheFolder">页面缓存目录，一般传 ledger.Root；为空时用 ledger.Root。</param>
        public static RemapResult RemapFor(Ledger? ledger, SessionRecord? record, string? currentPath, int oldSlide, string? cacheFolder = null)
        {
            string folder = cacheFolder ?? (ledger == null ? "" : ledger.Root);
            var res = new RemapResult { OldSlide = oldSlide };
            if (record == null || string.IsNullOrWhiteSpace(currentPath))
            {
                res.Kind = RemapKind.Unavailable;
                res.Note = NoBaselineNote(oldSlide);
                return res;
            }

            string fpOld = (record.Fingerprint ?? "").Trim();
            var newDeck = DeckReader.Read(currentPath!);
            if (newDeck == null)
            {
                // 读不出当前课件（.ppt / 文件坏了 / 还在写）：不猜，只说可能不准。
                res.Kind = RemapKind.Unavailable;
                res.Note = "课件改过了，页码可能不准：读不出当前课件的页面";
                return res;
            }
            res.NewCount = newDeck.Count;
            res.OldCount = record.SlideCount;

            // 指纹和页数都没变：这份课件根本没被改过，页码可以直接用。
            if (fpOld.Length > 0 && fpOld == newDeck.Fingerprint && record.SlideCount == newDeck.Count)
            {
                PutQuiet(folder, newDeck);
                res.Kind = RemapKind.Exact;
                res.NewSlide = oldSlide;
                res.Confidence = 1.0;
                res.Note = "课件没改过，页码不变";
                return res;
            }

            var oldDeck = folder.Length > 0 && fpOld.Length > 0 ? CacheFor(folder).Get(fpOld) : null;
            if (oldDeck == null || oldDeck.Count == 0)
            {
                // 没有旧版留底。至少把当前版本记下来，下次它再被改就能对齐了。
                PutQuiet(folder, newDeck);
                res.Kind = RemapKind.Unavailable;
                res.Note = NoBaselineNote(oldSlide);
                return res;
            }

            res.OldCount = oldDeck.Count;
            var mapped = MapPage(oldDeck.Pages, newDeck.Pages, oldSlide);
            PutQuiet(folder, newDeck);
            return mapped;
        }

        static void PutQuiet(string folder, DeckPages deck)
        {
            if (string.IsNullOrWhiteSpace(folder) || deck == null || deck.Count == 0) return;
            try { CacheFor(folder).Put(deck); } catch { }
        }

        // ==================================================================================
        // 页面文字缓存（台账旁边一个小 json，key 是文件指纹）
        // ==================================================================================

        static readonly Dictionary<string, DeckPageCache> Registry = new Dictionary<string, DeckPageCache>(StringComparer.OrdinalIgnoreCase);
        static readonly object RegistryGate = new object();

        /// <summary>同一个数据目录共用一份缓存对象（里面已经加载过 json，别每次重读）。</summary>
        public static DeckPageCache CacheFor(string folder)
        {
            string key;
            try { key = System.IO.Path.GetFullPath(folder ?? ""); } catch { key = folder ?? ""; }
            if (key.Length == 0) key = ".";
            lock (RegistryGate)
            {
                if (Registry.TryGetValue(key, out var cache) && cache != null) return cache;
                cache = new DeckPageCache(key);
                Registry[key] = cache;
                return cache;
            }
        }

        /// <summary>记住这份课件当前的每页文字。引擎在 BeginSession 时调一次就够（每份课件每个版本一次）。</summary>
        public static DeckPages? Remember(string folder, string deckPath)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(deckPath)) return null;
            try { return CacheFor(folder).Remember(deckPath); } catch { return null; }
        }

        /// <summary>按指纹取旧版页面留底；没有返回 null。</summary>
        public static DeckPages? Cached(string folder, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fingerprint)) return null;
            try { return CacheFor(folder).Get(fingerprint); } catch { return null; }
        }

        // ==================================================================================
        // 10) 移动/改名重关联
        // ==================================================================================

        /// <summary>
        /// 在候选目录里找"搬走了的那份课件"。纯查找，不动台账；<see cref="Relink"/> 才改台账。
        /// </summary>
        /// <param name="missingPath">台账里那条已经失效的路径。</param>
        /// <param name="fingerprint">台账记录里的内容指纹（可为空）。</param>
        /// <param name="folders">科目文件夹等候选目录；原目录会被自动加上。</param>
        /// <param name="cacheFolder">页面缓存目录（比对每页文字用）；为空时只比指纹和文件名。</param>
        public static RelocateResult FindMoved(string missingPath, string fingerprint, IEnumerable<string>? folders,
            string? cacheFolder = null, int maxFiles = 400)
        {
            var res = new RelocateResult { Note = NotFoundNote() };
            if (string.IsNullOrWhiteSpace(missingPath))
            {
                return res;
            }

            string missingFull;
            try { missingFull = System.IO.Path.GetFullPath(missingPath); } catch { missingFull = missingPath; }
            string fpWanted = (fingerprint ?? "").Trim();

            var dirs = new List<string>();
            Action<string> addDir = d =>
            {
                if (string.IsNullOrWhiteSpace(d)) return;
                try
                {
                    string full = System.IO.Path.GetFullPath(d);
                    if (!Directory.Exists(full)) return;
                    for (int i = 0; i < dirs.Count; i++)
                    {
                        if (string.Equals(dirs[i], full, StringComparison.OrdinalIgnoreCase)) return;
                    }
                    dirs.Add(full);
                }
                catch { }
            };
            if (folders != null)
            {
                foreach (var f in folders) addDir(f);
            }
            try { addDir(System.IO.Path.GetDirectoryName(missingFull) ?? ""); } catch { }
            if (dirs.Count == 0) return res;

            var files = new List<string>();
            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        if (files.Count >= maxFiles) break;
                        string name;
                        try { name = System.IO.Path.GetFileName(f); } catch { continue; }
                        if (name.Length == 0 || name.StartsWith("~$", StringComparison.Ordinal)) continue;
                        if (!DeckReader.CanParse(f)) continue;
                        try
                        {
                            if (string.Equals(System.IO.Path.GetFullPath(f), missingFull, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        catch { }
                        files.Add(f);
                    }
                }
                catch { }
                if (files.Count >= maxFiles) break;
            }
            if (files.Count == 0) return res;

            DeckPages? oldPages = null;
            if (cacheFolder != null && cacheFolder.Length > 0 && fpWanted.Length > 0)
            {
                oldPages = Cached(cacheFolder, fpWanted);
            }

            var cands = new List<RelocateCandidate>(files.Count);
            int contentBudget = 14;   // 每解析一份 pptx 都要解 zip，最多比 14 份
            foreach (var f in files)
            {
                var cand = new RelocateCandidate { Path = f };
                cand.NameScore = NameSimilarity(missingFull, f);
                cand.Fingerprint = Ledger.Fingerprint(f);
                cand.SameFingerprint = fpWanted.Length > 0 && cand.Fingerprint.Length > 0
                    && string.Equals(cand.Fingerprint, fpWanted, StringComparison.OrdinalIgnoreCase);

                if (cand.SameFingerprint)
                {
                    cand.ContentScore = 1.0;
                }
                else if (oldPages != null && oldPages.Count > 0 && contentBudget > 0)
                {
                    contentBudget--;
                    var deck = DeckReader.Read(f);
                    if (deck != null && deck.Count > 0) cand.ContentScore = ContentSimilarity(oldPages.Pages, deck.Pages);
                }

                if (cand.SameFingerprint)
                {
                    // 内容一字节不差：哪怕改了名也认。
                    cand.Score = 1.0 + 0.5 * cand.NameScore;
                    cand.Accepted = true;
                }
                else if (cand.ContentScore >= 0)
                {
                    cand.Score = 0.55 * cand.ContentScore + 0.45 * cand.NameScore;
                    cand.Accepted = (cand.ContentScore >= RelocateContentStrong && cand.NameScore >= RelocateNameLoose)
                        || (cand.ContentScore >= RelocateContentWeak && cand.NameScore >= RelocateNameStrong);
                }
                else
                {
                    // 没有内容证据（读不到旧版留底 / 候选读不出来）：只认"名字几乎一样"，而且排名靠后。
                    cand.Score = 0.25 * cand.NameScore;
                    cand.Accepted = cand.NameScore >= RelocateNameOnly;
                }
                cands.Add(cand);
            }

            cands.Sort((a, b) => b.Score.CompareTo(a.Score));
            res.Candidates = cands;
            var accepted = cands.Where(c => c.Accepted).ToList();
            if (accepted.Count == 0) return res;

            // 两份都像的时候不动：接错了比"找不到"更糟（老师会看到别人的页码）。
            if (accepted.Count >= 2 && (accepted[0].Score - accepted[1].Score) < MarginThreshold)
            {
                res.Found = false;
                res.Note = "找到 " + accepted.Count + " 份都像的课件，先不自动关联";
                return res;
            }

            res.Found = true;
            res.Path = accepted[0].Path;
            res.Note = "已重新关联：" + System.IO.Path.GetFileName(accepted[0].Path);
            return res;
        }

        /// <summary>
        /// 找到就改台账（<see cref="Ledger.Relink"/> 会落盘并记日志），找不到就什么也不做。
        /// 卡片每拍调也不要紧：找不到时不写日志（logMiss 才写）。
        /// </summary>
        public static RelocateResult Relink(Ledger? ledger, string missingPath, IEnumerable<string>? folders, bool logMiss = false)
        {
            var res = new RelocateResult { Note = NotFoundNote() };
            if (ledger == null || string.IsNullOrWhiteSpace(missingPath)) return res;

            var rec = ledger.Last(missingPath);
            string fp = rec == null ? "" : (rec.Fingerprint ?? "");
            try
            {
                res = FindMoved(missingPath, fp, folders, ledger.Root);
            }
            catch (Exception ex)
            {
                ledger.Log("relink: scan failed: " + ex.Message);
                return res;
            }

            if (!res.Found)
            {
                if (logMiss) ledger.Log("relink: not found: " + missingPath);
                return res;
            }

            var best = res.Candidates.FirstOrDefault(c => c.Accepted && string.Equals(c.Path, res.Path, StringComparison.OrdinalIgnoreCase));
            string why = best == null ? "" : (best.SameFingerprint ? "same fingerprint" : "pages " + Percent(best.ContentScore) + " name " + Percent(best.NameScore));
            try
            {
                if (ledger.Relink(missingPath, res.Path))
                {
                    ledger.Log("relink: \"" + missingPath + "\" -> \"" + res.Path + "\" (" + why + ")");
                    res.Note = "已重新关联：" + System.IO.Path.GetFileName(res.Path);
                }
                else
                {
                    res.Found = false;
                    res.Note = NotFoundNote();
                }
            }
            catch (Exception ex)
            {
                ledger.Log("relink failed: " + ex.Message);
                res.Found = false;
                res.Note = NotFoundNote();
            }
            return res;
        }

        // ==================================================================================
        // 相似度：字符二元组 Dice + 标题加成
        // ==================================================================================

        sealed class PreparedPage
        {
            public string NText = "";
            public string NTitle = "";
        }

        static List<PreparedPage> Prepare(IList<DeckPage> pages)
        {
            var list = new List<PreparedPage>(pages.Count);
            for (int i = 0; i < pages.Count; i++) list.Add(Prepare(pages[i]));
            return list;
        }

        static PreparedPage Prepare(DeckPage? page)
        {
            var p = new PreparedPage();
            if (page == null) return p;
            p.NText = Normalize(page.Text);
            p.NTitle = Normalize(page.Title);
            return p;
        }

        /// <summary>
        /// 归一化：小写、去掉空白/标点/符号（换成 \u0001 分隔，免得跨标点拼出假二元组）。
        /// 中文不分词，字符二元组对中文和英文都够用。
        /// </summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length + 4);
            bool lastSep = true;
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsControl(ch))
                {
                    if (!lastSep) { sb.Append('\u0001'); lastSep = true; }
                    continue;
                }
                sb.Append(char.ToLowerInvariant(ch));
                lastSep = false;
            }
            int n = sb.Length;
            while (n > 0 && sb[n - 1] == '\u0001') n--;
            sb.Length = n;
            return sb.ToString();
        }

        static Dictionary<int, int> Bigrams(string s)
        {
            var d = new Dictionary<int, int>();
            if (s.Length == 0) return d;
            if (s.Length == 1)
            {
                d[s[0]] = 1;
                return d;
            }
            for (int i = 0; i + 1 < s.Length; i++)
            {
                int key = (s[i] << 16) | s[i + 1];
                d[key] = d.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            return d;
        }

        /// <summary>两个已归一化字符串的 Dice 系数（按多重集交集算）。空串返回 0（"没证据"不是"完全像"）。</summary>
        public static double Dice(string? a, string? b)
        {
            string x = a ?? "", y = b ?? "";
            if (x.Length == 0 || y.Length == 0) return 0;
            if (string.Equals(x, y, StringComparison.Ordinal)) return 1.0;
            var bx = Bigrams(x);
            var by = Bigrams(y);
            int inter = 0, total = 0;
            foreach (var kv in bx)
            {
                total += kv.Value;
                if (by.TryGetValue(kv.Key, out int c)) inter += Math.Min(c, kv.Value);
            }
            foreach (var kv in by) total += kv.Value;
            if (total == 0) return 0;
            return 2.0 * inter / total;
        }

        /// <summary>单页相似度：正文占 75%，标题占 25%（标题变了但正文没变，仍然算同一页）。</summary>
        static double PageSim(PreparedPage a, PreparedPage b)
        {
            if (a.NText.Length == 0 || b.NText.Length == 0) return 0;
            if (string.Equals(a.NText, b.NText, StringComparison.Ordinal)) return 1.0;
            double full = Dice(a.NText, b.NText);
            double title = (a.NTitle.Length > 0 && b.NTitle.Length > 0) ? Dice(a.NTitle, b.NTitle) : full;
            return 0.75 * full + 0.25 * title;
        }

        /// <summary>
        /// 两份课件的"内容相似度"：从旧版均匀取最多 8 页，每页在新版里找最像的一页，取平均。
        /// 旧版有页在新版里找不到（被删了），那一页贡献接近 0 —— 这正是我们想要的惩罚。
        /// </summary>
        public static double ContentSimilarity(IList<DeckPage>? oldPages, IList<DeckPage>? newPages)
        {
            if (oldPages == null || newPages == null || oldPages.Count == 0 || newPages.Count == 0) return 0;
            var newP = Prepare(newPages);
            int samples = Math.Min(8, oldPages.Count);
            double total = 0;
            int used = 0;
            for (int k = 0; k < samples; k++)
            {
                int i = samples <= 1 ? 0 : (int)Math.Round((double)k * (oldPages.Count - 1) / (samples - 1));
                var a = Prepare(oldPages[i]);
                if (a.NText.Length == 0) continue;
                double best = 0;
                for (int j = 0; j < newP.Count; j++)
                {
                    double s = PageSim(a, newP[j]);
                    if (s > best) best = s;
                }
                total += best;
                used++;
            }
            return used == 0 ? 0 : total / used;
        }

        /// <summary>
        /// 文件名相似度：去掉目录、扩展名和"副本/(1)/- 拷贝/v2"这类另存为尾巴之后，
        /// 先比二元组 Dice；一份是另一份的子串（"课件" vs "课件 - 副本"）时给 0.75 起步。
        /// </summary>
        public static double NameSimilarity(string? a, string? b)
        {
            string x = NormalizeDeckName(a);
            string y = NormalizeDeckName(b);
            if (x.Length == 0 || y.Length == 0) return 0;
            if (string.Equals(x, y, StringComparison.Ordinal)) return 1.0;
            double dice = Dice(x, y);
            if (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal))
            {
                double ratio = (double)Math.Min(x.Length, y.Length) / Math.Max(x.Length, y.Length);
                dice = Math.Max(dice, 0.75 + 0.25 * ratio);
            }
            return dice;
        }

        public static string NormalizeDeckName(string? path)
        {
            string name;
            try { name = System.IO.Path.GetFileNameWithoutExtension(path ?? ""); } catch { name = path ?? ""; }
            name = (name ?? "").ToLowerInvariant();
            // "课件 - 副本"、"课件（1）"、"课件-拷贝"、"作业 copy"、"课件 v2" 都是另存为留下的尾巴。
            name = Regex.Replace(name, @"[\s\-_（(【\[]*(副本|拷贝|复件|copy)[\s\-_）)】\]]*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            name = Regex.Replace(name, @"[\s\-_（(【\[]*\d{1,2}[\s\-_）)】\]]*$", "", RegexOptions.CultureInvariant);
            name = Regex.Replace(name, @"[\s\-_]*v\d+$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Normalize(name);
        }

        // ---- 上下文投票 ----

        struct Vote
        {
            public bool Has;
            public int Offset;
            public double Mean;
            public int Count;
        }

        static Vote VoteFor(List<PreparedPage> oldP, List<PreparedPage> newP, int n)
        {
            var weights = new Dictionary<int, double>();
            var counts = new Dictionary<int, int>();
            var sums = new Dictionary<int, double>();

            for (int k = -3; k <= 3; k++)
            {
                if (k == 0) continue;
                int i = n + k;
                if (i < 0 || i >= oldP.Count) continue;
                var a = oldP[i];
                if (a.NText.Length == 0) continue;

                int best = -1;
                double bestS = 0;
                int lo = Math.Max(0, i - 8);
                int hi = Math.Min(newP.Count - 1, i + 8);
                for (int j = lo; j <= hi; j++)
                {
                    double s = PageSim(a, newP[j]);
                    if (s > bestS) { bestS = s; best = j; }
                }
                if (best < 0 || bestS < ContextFloor) continue;

                int off = best - i;
                weights[off] = weights.TryGetValue(off, out double w) ? w + bestS : bestS;
                counts[off] = counts.TryGetValue(off, out int c) ? c + 1 : 1;
                sums[off] = sums.TryGetValue(off, out double sm) ? sm + bestS : bestS;
            }

            // 偏移量范围锁在 ±8（再多就不像"删了两页"，而像"换了课件"），顺序遍历保证结果稳定。
            int pick = 0;
            double pickW = 0;
            for (int off = -8; off <= 8; off++)
            {
                if (weights.TryGetValue(off, out double w) && w > pickW + 1e-9) { pickW = w; pick = off; }
            }
            var vote = new Vote { Offset = pick };
            if (pickW <= 0) return vote;
            vote.Has = counts[pick] >= 2 && pickW >= 1.1;
            vote.Count = counts[pick];
            vote.Mean = sums[pick] / counts[pick];
            return vote;
        }

        static bool InRange(int index, int count)
        {
            return index >= 0 && index < count;
        }

        static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        static string Percent(double v)
        {
            if (v < 0) return "?";
            return ((int)Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        static string NotFoundNote()
        {
            return "找不到文件";
        }

        static string MovedNote(int oldSlide, int newSlide, int shift)
        {
            if (shift == 0)
            {
                return "课件改过了：" + oldSlide + " 页还在第 " + newSlide + " 页（内容有改动）";
            }
            string dir = shift > 0 ? "后移" : "前移";
            return "课件改过了：第 " + oldSlide + " 页对应新版第 " + newSlide + " 页（" + dir + " " + Math.Abs(shift) + " 页）";
        }

        static string ShiftOnlyNote(int oldSlide, int newSlide, int shift)
        {
            string dir = shift > 0 ? "后移" : "前移";
            return "课件改过了：整套课件" + dir + " " + Math.Abs(shift) + " 页，第 " + oldSlide + " 页对应第 " + newSlide + " 页";
        }

        static string DuplicateNote(int oldSlide, int newSlide, int hits)
        {
            return "课件改过了，页码可能不准：新版里有 " + hits + " 页和第 " + oldSlide
                + " 页内容相同（约第 " + newSlide + " 页）";
        }

        static string WeakNote(int oldSlide, int newSlide, double score)
        {
            return "课件改过了，页码可能不准：第 " + oldSlide + " 页 ≈ 新版第 " + newSlide
                + " 页（相似度 " + Percent(score) + "）";
        }

        static string ReplacedNote(int oldSlide)
        {
            return "课件改过了，页码可能不准：新版里找不到第 " + oldSlide + " 页的内容";
        }

        static string NoBaselineNote(int oldSlide)
        {
            return "课件改过了，页码可能不准：没有旧版第 " + oldSlide + " 页的留底";
        }

        static string BlankPageNote(int oldSlide)
        {
            return "课件改过了，页码可能不准：第 " + oldSlide + " 页没有文字";
        }
    }

    // ======================================================================================
    // 页面文字缓存：ledger.Root 旁边的 deckpages.json，key 是文件指纹。
    // 台账里只有指纹、没有每页文字；旧版被改掉之后原文件就没了，所以只能提前留一份。
    // ======================================================================================

    public sealed class DeckPageCachePage
    {
        public string T { get; set; } = "";
        public string H { get; set; } = "";
    }

    public sealed class DeckPageCacheEntry
    {
        public string Fp { get; set; } = "";
        public string Path { get; set; } = "";
        public string At { get; set; } = "";
        public List<DeckPageCachePage> Pages { get; set; } = new List<DeckPageCachePage>();
    }

    public sealed class DeckPageCacheFile
    {
        public int Version { get; set; } = 1;
        public List<DeckPageCacheEntry> Entries { get; set; } = new List<DeckPageCacheEntry>();
    }

    public sealed class DeckPageCache
    {
        public const int MaxEntries = 80;
        public const int MaxTextChars = 600;
        public const int MaxTitleChars = 200;

        readonly string dir;
        readonly string file;
        readonly string temp;
        readonly object gate = new object();
        readonly Dictionary<string, DeckPages> map = new Dictionary<string, DeckPages>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> at = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        bool loaded;

        public DeckPageCache(string dataFolder)
        {
            dir = string.IsNullOrWhiteSpace(dataFolder) ? "." : dataFolder;
            try { dir = System.IO.Path.GetFullPath(dir); } catch { }
            file = System.IO.Path.Combine(dir, "deckpages.json");
            temp = file + ".tmp";
        }

        public string Root { get { return dir; } }
        public string FilePath { get { return file; } }

        public int Count
        {
            get { lock (gate) { Ensure(); return map.Count; } }
        }

        public DeckPages? Get(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return null;
            lock (gate)
            {
                Ensure();
                return map.TryGetValue(fingerprint, out var deck) ? deck : null;
            }
        }

        /// <summary>记下一页文字并按指纹存档；同一个指纹重复写没关系（内容一样）。</summary>
        public void Put(DeckPages deck)
        {
            if (deck == null || string.IsNullOrEmpty(deck.Fingerprint) || deck.Count == 0) return;
            lock (gate)
            {
                Ensure();
                map[deck.Fingerprint] = Trim(deck);
                at[deck.Fingerprint] = DateTime.Now;
                PruneLocked();
                SaveLocked();
            }
        }

        /// <summary>读一份课件并记住它；读不出来返回 null。</summary>
        public DeckPages? Remember(string deckPath)
        {
            var deck = DeckReader.Read(deckPath);
            if (deck != null) Put(deck);
            return deck;
        }

        /// <summary>清空内存和磁盘（测试用；正常流程不会调）。</summary>
        public void Clear()
        {
            lock (gate)
            {
                map.Clear();
                at.Clear();
                loaded = true;
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        void Ensure()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(file)) return;
                var doc = JsonSerializer.Deserialize<DeckPageCacheFile>(File.ReadAllText(file, Encoding.UTF8));
                if (doc == null || doc.Entries == null) return;
                foreach (var e in doc.Entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Fp) || e.Pages == null || e.Pages.Count == 0) continue;
                    var deck = new DeckPages { Path = e.Path ?? "", Fingerprint = e.Fp };
                    foreach (var p in e.Pages)
                    {
                        if (p == null) continue;
                        deck.Pages.Add(new DeckPage { Text = p.T ?? "", Title = p.H ?? "" });
                    }
                    map[e.Fp] = deck;
                    if (!string.IsNullOrEmpty(e.At)
                        && DateTime.TryParse(e.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
                    {
                        at[e.Fp] = t;
                    }
                }
            }
            catch { }
        }

        void PruneLocked()
        {
            if (map.Count <= MaxEntries) return;
            var drop = map.Keys
                .OrderBy(k => at.TryGetValue(k, out DateTime t) ? t : DateTime.MinValue)
                .Take(map.Count - MaxEntries)
                .ToList();
            foreach (var k in drop)
            {
                map.Remove(k);
                at.Remove(k);
            }
        }

        void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(dir);
                var doc = new DeckPageCacheFile();
                foreach (var kv in map)
                {
                    var e = new DeckPageCacheEntry
                    {
                        Fp = kv.Key,
                        Path = kv.Value.Path ?? "",
                        At = (at.TryGetValue(kv.Key, out DateTime t) ? t : DateTime.Now)
                            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    };
                    foreach (var p in kv.Value.Pages)
                    {
                        e.Pages.Add(new DeckPageCachePage
                        {
                            T = Clip(p.Text, MaxTextChars),
                            H = Clip(p.Title, MaxTitleChars),
                        });
                    }
                    doc.Entries.Add(e);
                }
                string json = JsonSerializer.Serialize(doc);
                File.WriteAllText(temp, json, Encoding.UTF8);
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            catch
            {
                // 缓存写不进去只是"下次没法对齐"，不该影响上课。
            }
        }

        static DeckPages Trim(DeckPages deck)
        {
            var copy = new DeckPages { Path = deck.Path, Fingerprint = deck.Fingerprint };
            foreach (var p in deck.Pages)
            {
                copy.Pages.Add(new DeckPage { Text = Clip(p.Text, MaxTextChars), Title = Clip(p.Title, MaxTitleChars) });
            }
            return copy;
        }

        static string Clip(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s!.Length <= max ? s : s.Substring(0, max);
        }
    }
}
