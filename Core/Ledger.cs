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
            try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
            catch { return path.Trim().ToLowerInvariant(); }
        }

        public SessionRecord Last(string path)
        {
            var k = Key(path);
            if (k.Length == 0) return null;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list) || list == null || list.Count == 0) return null;
            return list[list.Count - 1];
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

        public void Add(SessionRecord r)
        {
            var k = Key(r.FilePath);
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
