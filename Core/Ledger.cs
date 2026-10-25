using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>One recorded slideshow session -- one lesson's progress through one deck.</summary>
    public sealed class SessionRecord
    {
        public string FilePath { get; set; }         // original path as reported by the suite
        public string StartedAt { get; set; }        // local time, ISO-ish
        public string EndedAt { get; set; }
        public int StartSlide { get; set; }
        public int EndSlide { get; set; }
        public int SlideCount { get; set; }
        public string Fingerprint { get; set; }      // changes when the deck is edited
        public int Seconds { get; set; }
        public string Subject { get; set; }          // the ClassIsland subject, when we know it
        public string LessonName { get; set; }       // the time point's name, when we know it

        // 2026-10-24 功能3：历史里要能看出"是哪一节课记的"，所以把课表读数一起存下来。
        // 老台账里没有这三个字段，System.Text.Json 读不到就留默认值（0 / null），
        // 不能让 Load() 因此进 catch —— 那会把整本台账改名成 .corrupt 再清空。
        public int Weekday { get; set; }             // 1 = 周一 … 7 = 周日，0 = 不知道（老记录）
        public int Period { get; set; }              // 当天第几节（1 起），0 = 数不出来（课间/没排课）
        public string PeriodName { get; set; }       // 时段的自己的名字（课间名/午休…），没有就是空
    }

    /// <summary>
    /// Where the deck was left last time, plus enough history to explain it.
    ///
    /// The fingerprint exists because page numbers drift: if the teacher inserts a slide
    /// before the one they stopped on, "slide 17" silently becomes a different slide. We
    /// keep the fingerprint of the deck as it was when the page was recorded, so the card can
    /// say "this file changed since then" instead of jumping to a confidently wrong page.
    /// </summary>
    public sealed class Ledger
    {
        readonly string dir;
        readonly string fileName;
        readonly string tempName;

        /// <summary>
        /// The ledger lives in the plugin's own config folder, which ClassIsland creates and
        /// backs up. It must never live in the plugin install folder: that gets replaced on
        /// update. Tests redirect it through <see cref="SyncSettings.DataFolder"/>.
        /// </summary>
        /// <summary>
        /// 反序列化用的无参构造。没有它 System.Text.Json 会因为找不到公开无参构造而抛异常，
        /// Load() 的 catch 又把异常吞掉 → 每次启动都读到一本空台账（老师会看到“没有记录”）。
        /// </summary>
        public Ledger() : this("data") { }

        public Ledger(string dataFolder)
        {
            dir = string.IsNullOrWhiteSpace(dataFolder) ? "data" : Path.GetFullPath(dataFolder);
            fileName = Path.Combine(dir, "ledger.json");
            tempName = Path.Combine(dir, "ledger.tmp");
        }

        public string Root { get { return dir; } }

        /// <summary>
        /// 保护 <see cref="ByFile"/> 这本字典、以及里面每个 List 的结构（加键/删键/Add/RemoveAll）。
        ///
        /// 为什么需要它：写方只有引擎线程，读方却有三条 ——
        ///   · 服务线程的定时推送（每 2 秒 PushCheapState → SessionEngine.Push → BuildState
        ///     → Last/Count/Recent/FindByName/FindByFingerprint + CardFileList.Build）；
        ///   · 卡片被点击时的 UI 线程（RequestJump → SetStatus → Push，同一条路径）；
        ///   · 服务线程的 Prune()（删记录、删键）。
        /// 不加锁的真实现象有两个，都是"偶发、现场没法复现"的那种：
        ///   1. 枚举撞上结构变化 → InvalidOperationException「Collection was modified」。
        ///      从 SetStatus 抛出去会直接打断卡片的点击处理；从后台推送抛出去，那一次刷新
        ///      就静默少了。
        ///   2. Save() 里 JsonSerializer 正枚举 List，引擎线程刚好 Add 了一条（List 的版本号
        ///      一变，枚举器立刻抛）→ 整次落盘被 catch 吞掉：内存里有、ledger.json 上没有，
        ///      重启后那节课的记录就没了。
        /// 锁顺序固定为 saveLock → Sync（Save 先拿 saveLock 再拿 Sync；Prune 先改内存、
        /// 出了 Sync 才进 Save），两条线程不会反向持锁互等。锁只保护内存结构，
        /// 读写文件都放在 Sync 之外做。
        /// </summary>
        internal readonly object Sync = new object();

        Dictionary<string, List<SessionRecord>> byFile = new Dictionary<string, List<SessionRecord>>();

        /// <summary>
        /// 台账本体：键是 <see cref="Key"/> 归出来的桶名，值是按写入顺序排的记录。
        ///
        /// 加键/删键一律换成一本新字典（见 Add/Adopt/Prune），不在旧字典上就地改：
        /// Automation\UnfinishedDecks.cs、Services\SlideSyncService.cs、Ui\ControlPanel.axaml.cs
        /// 都直接 <c>foreach (ledger.ByFile)</c> 遍历（它们拿不到这把锁，也不该为了读一条记录
        /// 去改别人的线程模型）。换字典能让这些遍历永远踩不到结构变化，最多少看一条刚出现的记录。
        /// </summary>
        public Dictionary<string, List<SessionRecord>> ByFile
        {
            get { lock (Sync) return byFile; }
            set { lock (Sync) byFile = value ?? new Dictionary<string, List<SessionRecord>>(); }
        }

        public static Ledger Load(string dataFolder)
        {
            var l = new Ledger(dataFolder);
            try
            {
                if (!File.Exists(l.fileName)) return l;
                var json = File.ReadAllText(l.fileName, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<Ledger>(json);
                if (loaded != null && loaded.ByFile != null) l.ByFile = loaded.ByFile;
            }
            catch
            {
                // A corrupt ledger must never stop ClassIsland from starting; begin clean.
                // 但坏文件不能就这么算了：下一次 Save 会把它整个覆盖，讲了一学期的历史就永久没了。
                // 先改名留档，万一只是一行坏了，老师还能人工拣回几十条。
                try
                {
                    if (File.Exists(l.fileName))
                        File.Move(l.fileName, l.fileName + ".corrupt-"
                            + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
                }
                catch { }
                l.ByFile = new Dictionary<string, List<SessionRecord>>();
            }
            return l;
        }

        static readonly object saveLock = new object();

        public void Save()
        {
            // saveLock 仍然守"两个实例同时写同一本台账"；Sync 守"序列化和内存改动撞车"。
            // 顺序固定 saveLock → Sync，和 Prune 的顺序一致（Prune 不持 Sync 调 Save）。
            lock (saveLock)
            {
                string json;
                try
                {
                    // 序列化必须和 Add/Adopt/Prune 互斥：JsonSerializer 枚举 List/Dictionary 时
                    // 结构一变就抛 InvalidOperationException，整次落盘白做（内存有、盘上没有）。
                    lock (Sync)
                        json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception ex)
                {
                    // 以前这里是 catch {}，连序列化失败都是无声的。至少在 app.log 留一条：
                    // 老师报"记录丢了"时，才能分清是根本没写下去、还是被别的实例覆盖了。
                    Log("ledger save failed (serialize): " + ex.GetType().Name + " " + ex.Message);
                    return;
                }
                try
                {
                    Directory.CreateDirectory(dir);
                    // Write-then-rename: an interrupted save must not leave a half-written ledger.
                    File.WriteAllText(tempName, json, new UTF8Encoding(false));
                    if (File.Exists(fileName)) File.Replace(tempName, fileName, null);
                    else File.Move(tempName, fileName);
                }
                catch (Exception ex)
                {
                    // 磁盘满、目标被别的程序独占、ledger.json 被设成只读 —— 这些都会让这次落盘
                    // 失败而内存里一切正常，重启后整本台账回到上一次成功的状态。留日志是唯一线索。
                    Log("ledger save failed (write): " + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        public void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "app.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture) + message + Environment.NewLine);
            }
            catch { }
        }

        public static string Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string t = path.Trim();
            // 「希沃:课件名」是插件自己造的伪键，不是磁盘路径：交给 GetFullPath 会按进程当前
            // 目录拼出一个和启动方式有关的假键（真实台账里出现过
            // …\classisland\app-2.1.0.1-0\希沃:csp-seewo-test）。换个启动目录就再也查不到这份
            // 课件的记录，卡片上的「历史 · N 次」入口会直接消失。伪键必须原样保存。
            if (t.StartsWith(SeewoBridge.KeyPrefix, StringComparison.Ordinal)) return t.ToLowerInvariant();
            try { return Path.GetFullPath(t).TrimEnd('\\').ToLowerInvariant(); }
            catch { return t.ToLowerInvariant(); }
        }

        /// <summary>
        /// 台账里实际存放这条记录的桶名。<see cref="Key"/> 以前把伪键拼上了进程当前目录，老台账
        /// 里就留着那种键；精确查不到时再按“以伪键结尾”回头找一次，找到哪个桶就继续写进那个桶，
        /// 这样升级后旧记录照样看得见，也不会把同一份课件的历史拆成两半。
        /// </summary>
        string Bucket(string path)
        {
            // 这里要遍历整本字典，是最容易撞上并发加键的地方（见 Sync 的注释）。
            lock (Sync)
            {
                string k = Key(path);
                if (k.Length == 0 || byFile.ContainsKey(k)) return k;
                string suffix = "\\" + k;
                // 旧版本保存“裸文件名”时，Key() 会用当时的进程工作目录把名字补成绝对路径
                // （例如 …\app-2.1.0.1-0\演示文稿1）。那个工作目录现在无从得知，
                // 只能退一步按相对路径尾部认领；否则这批旧记录永远读不出来（页数、历史全丢）。
                string bare = "";
                string raw = (path ?? "").Trim();
                if (raw.Length > 0 && !Path.IsPathRooted(raw) &&
                    !raw.StartsWith(SeewoBridge.KeyPrefix, StringComparison.Ordinal))
                {
                    bare = "\\" + raw.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
                    if (bare == "\\") bare = "";
                }
                string bestKey = k;
                List<SessionRecord> best = null;
                foreach (var kv in byFile)
                {
                    bool hit = kv.Key.EndsWith(suffix, StringComparison.Ordinal) ||
                               (bare.Length > 0 && kv.Key.EndsWith(bare, StringComparison.Ordinal));
                    if (!hit) continue;
                    var list = kv.Value;
                    if (list == null || list.Count == 0) continue;
                    var a = list[list.Count - 1];
                    var b = best == null ? null : best[best.Count - 1];
                    if (best == null || (a != null && (b == null ||
                        string.CompareOrdinal(a.StartedAt ?? "", b.StartedAt ?? "") > 0)))
                    {
                        best = list;
                        bestKey = kv.Key;
                    }
                }
                return bestKey;
            }
        }

        public SessionRecord Last(string path)
        {
            lock (Sync)
            {
                var k = Bucket(path);
                if (k.Length == 0) return null;
                List<SessionRecord> list;
                if (!byFile.TryGetValue(k, out list) || list == null || list.Count == 0) return null;
                return list[list.Count - 1];
            }
        }

        /// <summary>
        /// 这份课件最近几次讲到哪儿，新的在前。卡片上的“历史”面板只放得下几行，
        /// 所以这里按时间排完再截断，而不是直接取列表尾部——台账被手工改过或导入过时，
        /// 文件里的顺序未必等于时间顺序。
        /// </summary>
        public List<SessionRecord> Recent(string filePath, int max)
        {
            var result = new List<SessionRecord>();
            lock (Sync)
            {
                var k = Bucket(filePath);
                if (k.Length == 0 || max <= 0) return result;
                List<SessionRecord> list;
                if (!byFile.TryGetValue(k, out list) || list == null) return result;

                // 跳到一条坏记录（没有结束页）比不跳更糟，先剔掉。
                var idx = new List<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r == null || r.EndSlide <= 0) continue;
                    idx.Add(i);
                }
                idx.Sort((x, y) =>
                {
                    int c = string.CompareOrdinal(list[y].StartedAt ?? "", list[x].StartedAt ?? "");
                    // 同一秒里记下的两条，后写入的是更新的那条。
                    return c != 0 ? c : y.CompareTo(x);
                });
                for (int i = 0; i < idx.Count && result.Count < max; i++) result.Add(list[idx[i]]);
            }
            return result;
        }

        /// <summary>这份课件在台账里的总记录数（历史入口上显示的 N，不截断）。</summary>
        public int Count(string filePath)
        {
            lock (Sync)
            {
                var k = Bucket(filePath);
                if (k.Length == 0) return 0;
                List<SessionRecord> list;
                if (!byFile.TryGetValue(k, out list) || list == null) return 0;
                int n = 0;
                for (int i = 0; i < list.Count; i++) if (list[i] != null) n++;
                return n;
            }
        }

        /// <summary>
        /// The most recently started session across every deck. Needed when the plugin has just
        /// started (or the show has already ended) and there is no current file to look up by —
        /// the teacher still expects to see where they left off.
        /// </summary>
        public SessionRecord Newest()
        {
            SessionRecord best = null;
            lock (Sync)
            {
                foreach (var kv in byFile)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        if (r == null) continue;
                        if (best == null || string.CompareOrdinal(r.StartedAt ?? "", best.StartedAt ?? "") > 0) best = r;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 只拿得到“文档名”（窗口标题里的，比如“测试课件.pptx”）时按文件名找最近一条记录。
        /// 2026-10-17 加：WPS 打开文件的头十几秒 COM 不回话，用标题里的名字就能立刻把卡片点亮。
        /// 同名不同目录时取最近用过的那份（老师刚打开的多半就是它）。
        /// </summary>
        public SessionRecord FindByName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            string want = fileName.Trim();
            SessionRecord best = null;
            lock (Sync)
            {
                foreach (var kv in byFile)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        if (r == null || string.IsNullOrEmpty(r.FilePath)) continue;
                        string name;
                        try { name = Path.GetFileName(r.FilePath); } catch { name = r.FilePath; }
                        if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) continue;
                        if (best == null || string.CompareOrdinal(r.StartedAt ?? "", best.StartedAt ?? "") > 0)
                            best = r;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 台账里所有记录的列表快照。列表本身是新的（记录对象仍是台账里那些），
        /// 任务 10 要扫"路径已经失效"的记录，不能直接拿 <see cref="ByFile"/> 的字典去遍历 ——
        /// 别的线程可能在同一个字典上换键。
        /// </summary>
        public List<SessionRecord> AllRecords()
        {
            var all = new List<SessionRecord>();
            lock (Sync)
            {
                foreach (var kv in byFile)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null) all.Add(list[i]);
                    }
                }
            }
            return all;
        }

        /// <summary>
        /// 按文件内容指纹找最近一条记录。课件被改名或挪了目录之后文件名对不上，
        /// 但内容没变 —— 页码仍然可信，历史也不该忽然消失。
        /// </summary>
        public SessionRecord? FindByFingerprint(string path)
        {
            // 指纹是纯文件读取（可能几百毫秒），放在锁外面算，别把引擎线程的落盘堵住。
            string fp = Fingerprint(path);
            if (fp.Length == 0) return null;
            SessionRecord? best = null;
            lock (Sync)
            {
                foreach (var kv in byFile)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        if (r == null || string.IsNullOrEmpty(r.Fingerprint) || r.Fingerprint != fp) continue;
                        if (best == null || string.CompareOrdinal(r.StartedAt ?? "", best.StartedAt ?? "") > 0) best = r;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 功能7（2026-10-24）：找这一科「上次那份」课件，并且它没讲完、还在保留期内。
        /// 判据是这门课最近用过的那条记录（不是最近一条"没讲完"的）：老师上周用另一份
        /// 讲完了，这周不该还把更早那份没讲完的翻出来让他接着讲。
        /// 页数未知（SlideCount&lt;=0）或一页都没翻过（EndSlide&lt;=0）的记录不算，
        /// 否则预备态会写「上次停在第 0 页」。
        /// </summary>
        public SessionRecord? LastUnfinishedForSubject(string subject, int withinDays)
        {
            if (string.IsNullOrWhiteSpace(subject) || withinDays <= 0) return null;
            string want = subject.Trim();
            var cutoff = DateTime.Now.AddDays(-withinDays);

            SessionRecord? latest = null;
            lock (Sync)
            {
                foreach (var kv in byFile)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var r = list[i];
                        if (r == null || string.IsNullOrEmpty(r.FilePath)) continue;
                        string subj = (r.Subject ?? "").Trim();
                        if (!string.Equals(subj, want, StringComparison.OrdinalIgnoreCase)) continue;
                        if (LastUsed(r) < cutoff) continue;
                        if (latest == null || string.CompareOrdinal(r.StartedAt ?? "", latest.StartedAt ?? "") > 0) latest = r;
                    }
                }
            }
            if (latest == null) return null;
            if (latest.SlideCount <= 0 || latest.EndSlide <= 0 || latest.EndSlide >= latest.SlideCount) return null;
            return latest;
        }

        public void Add(SessionRecord r)
        {
            if (r == null) return;
            lock (Sync)
            {
                // 写和读用同一个桶：老台账里伪键带着旧的进程目录，继续往那个桶里追加，
                // 历史才不会被拆成"升级前 8 条 + 升级后 N 条"两半。
                var k = Bucket(r.FilePath);
                if (k.Length == 0) return;
                List<SessionRecord> list;
                if (!byFile.TryGetValue(k, out list) || list == null)
                {
                    list = new List<SessionRecord>();
                    // 新增一把键要换一本新字典，不能就地加（理由见 ByFile 的注释）：
                    // 另外三条线程正在 foreach 这本字典，就地加键会让它们立刻抛
                    // "Collection was modified"。
                    var next = new Dictionary<string, List<SessionRecord>>(byFile);
                    next[k] = list;
                    byFile = next;
                }
                list.Add(r);
                // Keep the file small: a term of lessons is plenty of history.
                const int keep = 200;
                if (list.Count > keep) list.RemoveRange(0, list.Count - keep);
            }
        }

        /// <summary>
        /// 改名/挪目录续接：把旧路径的整段历史迁到新路径名下。调用方先用
        /// <see cref="FindByFingerprint"/> 确认内容没变（课件真被编辑过就不该继承页码）。
        /// 2026-10-24 第二轮 F3：以前 BeginSession 对改名后的路径直接另起一条，同一份课件
        /// 在台账里拆成两条互不相干的记录，卡片也就不再提示"上次停在第 5 页"。
        /// </summary>
        internal bool Adopt(string oldPath, string newPath)
        {
            lock (Sync)
            {
                string oldKey = Bucket(oldPath);
                string newKey = Key(newPath);
                if (oldKey.Length == 0 || newKey.Length == 0 || oldKey == newKey) return false;

                List<SessionRecord> moving;
                if (!byFile.TryGetValue(oldKey, out moving) || moving == null || moving.Count == 0) return false;

                List<SessionRecord> dest;
                if (byFile.TryGetValue(newKey, out dest) && dest != null)
                {
                    dest.AddRange(moving);
                }
                else
                {
                    dest = moving;
                }
                // FilePath 也要改到新路径：卡片和历史面板靠它取显示名，不改会一直挂着旧文件名。
                // 先改对象、再发布新字典：发布之后另一个线程可能立刻按新键取这条记录。
                for (int i = 0; i < dest.Count; i++)
                {
                    if (dest[i] != null) dest[i].FilePath = newPath;
                }
                // 加 newKey + 删 oldKey 分两次就地改，会让别人的 foreach 抛
                // "Collection was modified"；一次换掉整本字典（理由见 ByFile 的注释）。
                var next = new Dictionary<string, List<SessionRecord>>(byFile);
                next[newKey] = dest;
                next.Remove(oldKey);
                byFile = next;
                return true;
            }
        }

        /// <summary>
        /// 任务 10（2026-10-25）：课件被移动/改名后，把旧路径的整段历史接到新路径上。
        /// 与 <see cref="Adopt"/> 的区别只在"多落一次盘、多记一行日志"，迁移语义完全一样：
        /// 调用方（<c>DeckRemap.Relink</c>）必须已经确认新文件就是同一份课件。
        /// 返回 true 表示台账里确实有这段历史并且已经迁过去了。
        /// </summary>
        public bool Relink(string oldPath, string newPath)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return false;
            try
            {
                if (!Adopt(oldPath, newPath)) return false;
                Save();
                Log("relink: history moved " + DisplayName(oldPath) + " -> " + DisplayName(newPath));
                return true;
            }
            catch (Exception ex)
            {
                Log("relink failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 清掉太久没用的课件记录（默认 3 周，见 SyncSettings.HistoryRetentionDays）。
        /// 判据是"最后一次使用"而不是"记录创建时间"：同一份课件只要还在用就一直留着。
        /// 返回清掉的记录条数（路径也跟着一起清）。
        /// </summary>
        public int Prune(int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            int removed = 0;
            try
            {
                lock (Sync)
                {
                    Dictionary<string, List<SessionRecord>> next = null;
                    foreach (var kv in byFile)
                    {
                        var list = kv.Value;
                        if (list == null)
                        {
                            next = next ?? new Dictionary<string, List<SessionRecord>>(byFile);
                            next.Remove(kv.Key);
                            continue;
                        }
                        int before = list.Count;
                        list.RemoveAll(r => LastUsed(r) < cutoff);
                        removed += before - list.Count;
                        if (list.Count == 0)
                        {
                            next = next ?? new Dictionary<string, List<SessionRecord>>(byFile);
                            next.Remove(kv.Key);
                        }
                    }
                    if (next != null) byFile = next;
                }
                // Save 必须在 Sync 之外：Save 内部按 saveLock → Sync 的顺序拿锁，
                // 如果在这里持着 Sync 去等 saveLock，就会和另一条正在 Save 的线程互相等死。
                if (removed > 0) Save();
            }
            catch { }
            return removed;
        }

        /// <summary>
        /// 删掉若干份课件的记录（控制面板「管理」里用）。参数可以是台账键（希沃伪键）或文件路径，
        /// 内部两种形式都试一遍。返回真正删掉的条数；只动台账，不碰课件文件。
        /// </summary>

        /// <summary>
        /// 把桶键 / 路径归一成「老师看到的课件名」：去掉目录、去掉希沃伪键前缀、trim。
        /// 只用于比较，不参与存储。
        /// </summary>
        internal static string DisplayName(string keyOrPath)
        {
            if (string.IsNullOrWhiteSpace(keyOrPath)) return "";
            string s = keyOrPath.Trim();
            int cut = s.LastIndexOfAny(new[] { '\\', '/' });
            if (cut >= 0 && cut + 1 < s.Length) s = s.Substring(cut + 1);
            if (s.StartsWith("希沃:", StringComparison.Ordinal)) s = s.Substring(3);
            return s.Trim();
        }

        public int RemoveMany(IEnumerable<string> keysOrPaths)
        {
            if (keysOrPaths == null) return 0;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in keysOrPaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string one = raw.Trim();
                wanted.Add(one);
                try
                {
                    string k = Key(one);
                    if (k.Length > 0) wanted.Add(k);
                }
                catch { }
            }
            if (wanted.Count == 0) return 0;

            // 2026-10-25 用户报「删记录没删好」：面板给的是课件路径，而台账桶键可能是希沃伪键
            // （希沃:课件名）或被 CWD 污染过的绝对路径，精确匹配会落到别的桶上、只删掉一两条。
            // 这里补一层「按显示名匹配」：两边都去掉目录、去掉「希沃:」前缀、trim 后不区分大小写比较。
            var wantedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var one in wanted)
            {
                string n = DisplayName(one);
                if (n.Length > 0) wantedNames.Add(n);
            }

            int removed = 0;
            try
            {
                lock (Sync)
                {
                    Dictionary<string, List<SessionRecord>> next = null;
                    foreach (var kv in byFile)
                    {
                        if (!wanted.Contains(kv.Key) && !wantedNames.Contains(DisplayName(kv.Key))) continue;
                        next = next ?? new Dictionary<string, List<SessionRecord>>(byFile);
                        next.Remove(kv.Key);
                        removed += kv.Value == null ? 0 : kv.Value.Count;
                    }
                    if (next != null) byFile = next;
                }
                if (removed > 0)
                {
                    Save();
                    Log("history: removed " + removed + " record(s) by hand");
                }
            }
            catch { }
            return removed;
        }

        static DateTime LastUsed(SessionRecord r)
        {
            if (r == null) return DateTime.MinValue;
            DateTime t;
            if (!string.IsNullOrEmpty(r.EndedAt)
                && DateTime.TryParse(r.EndedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return t;
            if (!string.IsNullOrEmpty(r.StartedAt)
                && DateTime.TryParse(r.StartedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) return t;
            return DateTime.MinValue;
        }

        /// <summary>
        /// Cheap "has this deck been edited since" check. Reading a whole lecture deck on
        /// every launch would be wasteful, but the container is rewritten on every save, so
        /// hashing the head and tail plus the length catches edits reliably.
        /// </summary>
        public static string Fingerprint(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sha = SHA256.Create())
                {
                    var len = fs.Length;
                    var buf = new byte[64 * 1024];
                    var collected = new MemoryStream();
                    int read = fs.Read(buf, 0, buf.Length);
                    collected.Write(buf, 0, read);
                    if (len > buf.Length)
                    {
                        fs.Seek(Math.Max(0, len - buf.Length), SeekOrigin.Begin);
                        read = fs.Read(buf, 0, buf.Length);
                        collected.Write(buf, 0, read);
                    }
                    var lenBytes = BitConverter.GetBytes(len);
                    collected.Write(lenBytes, 0, lenBytes.Length);
                    return Convert.ToHexString(sha.ComputeHash(collected.ToArray()));
                }
            }
            catch { return ""; }
        }
    }
}
