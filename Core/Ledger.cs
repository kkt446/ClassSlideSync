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

        public Dictionary<string, List<SessionRecord>> ByFile { get; set; }
            = new Dictionary<string, List<SessionRecord>>();

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
                l.ByFile = new Dictionary<string, List<SessionRecord>>();
            }
            return l;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                // Write-then-rename: an interrupted save must not leave a half-written ledger.
                File.WriteAllText(tempName, json, new UTF8Encoding(false));
                if (File.Exists(fileName)) File.Replace(tempName, fileName, null);
                else File.Move(tempName, fileName);
            }
            catch { }
        }

        public void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "app.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
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
            string k = Key(path);
            if (k.Length == 0 || ByFile == null || ByFile.ContainsKey(k)) return k;
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
            foreach (var kv in ByFile)
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

        public SessionRecord Last(string path)
        {
            var k = Bucket(path);
            if (k.Length == 0) return null;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list) || list == null || list.Count == 0) return null;
            return list[list.Count - 1];
        }

        /// <summary>
        /// 这份课件最近几次讲到哪儿，新的在前。卡片上的“历史”面板只放得下几行，
        /// 所以这里按时间排完再截断，而不是直接取列表尾部——台账被手工改过或导入过时，
        /// 文件里的顺序未必等于时间顺序。
        /// </summary>
        public List<SessionRecord> Recent(string filePath, int max)
        {
            var result = new List<SessionRecord>();
            var k = Bucket(filePath);
            if (k.Length == 0 || max <= 0 || ByFile == null) return result;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list) || list == null) return result;

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
            return result;
        }

        /// <summary>这份课件在台账里的总记录数（历史入口上显示的 N，不截断）。</summary>
        public int Count(string filePath)
        {
            var k = Bucket(filePath);
            if (k.Length == 0 || ByFile == null) return 0;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list) || list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++) if (list[i] != null) n++;
            return n;
        }

        /// <summary>
        /// The most recently started session across every deck. Needed when the plugin has just
        /// started (or the show has already ended) and there is no current file to look up by —
        /// the teacher still expects to see where they left off.
        /// </summary>
        public SessionRecord Newest()
        {
            SessionRecord best = null;
            if (ByFile == null) return null;
            foreach (var kv in ByFile)
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
            return best;
        }

        /// <summary>
        /// 只拿得到“文档名”（窗口标题里的，比如“测试课件.pptx”）时按文件名找最近一条记录。
        /// 2026-10-17 加：WPS 打开文件的头十几秒 COM 不回话，用标题里的名字就能立刻把卡片点亮。
        /// 同名不同目录时取最近用过的那份（老师刚打开的多半就是它）。
        /// </summary>
        public SessionRecord FindByName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || ByFile == null) return null;
            string want = fileName.Trim();
            SessionRecord best = null;
            string bestPath = "";
            foreach (var kv in ByFile)
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
                    {
                        best = r;
                        bestPath = r.FilePath;
                    }
                }
            }
            if (best != null && best.FilePath != bestPath) best.FilePath = bestPath;
            return best;
        }

        /// <summary>
        /// 按文件内容指纹找最近一条记录。课件被改名或挪了目录之后文件名对不上，
        /// 但内容没变 —— 页码仍然可信，历史也不该忽然消失。
        /// </summary>
        public SessionRecord? FindByFingerprint(string path)
        {
            string fp = Fingerprint(path);
            if (fp.Length == 0 || ByFile == null) return null;
            SessionRecord? best = null;
            foreach (var kv in ByFile)
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
            return best;
        }

        public void Add(SessionRecord r)
        {
            // 写和读用同一个桶：老台账里伪键带着旧的进程目录，继续往那个桶里追加，
            // 历史才不会被拆成"升级前 8 条 + 升级后 N 条"两半。
            var k = Bucket(r.FilePath);
            if (k.Length == 0) return;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list)) { list = new List<SessionRecord>(); ByFile[k] = list; }
            list.Add(r);
            // Keep the file small: a term of lessons is plenty of history.
            const int keep = 200;
            if (list.Count > keep) list.RemoveRange(0, list.Count - keep);
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
                var keys = new List<string>(ByFile.Keys);
                foreach (var k in keys)
                {
                    var list = ByFile[k];
                    if (list == null) { ByFile.Remove(k); continue; }
                    int before = list.Count;
                    list.RemoveAll(r => LastUsed(r) < cutoff);
                    removed += before - list.Count;
                    if (list.Count == 0) ByFile.Remove(k);
                }
                if (removed > 0) Save();
            }
            catch { }
            return removed;
        }

        static DateTime LastUsed(SessionRecord r)
        {
            if (r == null) return DateTime.MinValue;
            DateTime t;
            if (!string.IsNullOrEmpty(r.EndedAt) && DateTime.TryParse(r.EndedAt, out t)) return t;
            if (!string.IsNullOrEmpty(r.StartedAt) && DateTime.TryParse(r.StartedAt, out t)) return t;
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
