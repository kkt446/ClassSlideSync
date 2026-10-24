using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassSlideSync.Plugin.Core
{
    public sealed class SessionRecord
    {
        public string FilePath { get; set; }
        public string StartedAt { get; set; }
        public string EndedAt { get; set; }
        public int StartSlide { get; set; }
        public int EndSlide { get; set; }
        public int SlideCount { get; set; }
        public string Fingerprint { get; set; }
        public int Seconds { get; set; }
        public string Subject { get; set; }
        public string LessonName { get; set; }
    }

    public sealed class Ledger
    {
        readonly string dir;
        readonly string fileName;
        readonly string tempName;

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
            if (t.StartsWith(SeewoBridge.KeyPrefix, StringComparison.Ordinal)) return t.ToLowerInvariant();
            try { return Path.GetFullPath(t).TrimEnd('\\').ToLowerInvariant(); }
            catch { return t.ToLowerInvariant(); }
        }

        string Bucket(string path)
        {
            string k = Key(path);
            if (k.Length == 0 || ByFile == null || ByFile.ContainsKey(k)) return k;
            string suffix = "\\" + k;
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

        public List<SessionRecord> Recent(string filePath, int max)
        {
            var result = new List<SessionRecord>();
            var k = Bucket(filePath);
            if (k.Length == 0 || max <= 0 || ByFile == null) return result;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list) || list == null) return result;

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
                return c != 0 ? c : y.CompareTo(x);
            });
            for (int i = 0; i < idx.Count && result.Count < max; i++) result.Add(list[idx[i]]);
            return result;
        }

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
            var k = Bucket(r.FilePath);
            if (k.Length == 0) return;
            List<SessionRecord> list;
            if (!ByFile.TryGetValue(k, out list)) { list = new List<SessionRecord>(); ByFile[k] = list; }
            list.Add(r);
            const int keep = 200;
            if (list.Count > keep) list.RemoveRange(0, list.Count - keep);
        }

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
