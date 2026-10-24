using System;
using System.Collections.Generic;
using System.IO;

namespace ClassSlideSync.Plugin.Core
{
    public sealed class CardFileItem
    {
        public string Key = "";
        public string FilePath = "";
        public string DisplayName = "";
        public string FolderHint = "";
        public string Detail = "";
        public bool IsCurrent;
        public int Total;
        public List<CardHistoryItem> History = new List<CardHistoryItem>();
        internal DateTime LastAt;
        internal string Folder = "";
    }

    public static class CardFileList
    {
        public const int MaxFiles = 8;

        public static List<CardFileItem> Build(Ledger ledger, string? currentKey, int maxFiles, int maxHistory)
        {
            var result = new List<CardFileItem>();
            if (ledger == null || ledger.ByFile == null || maxFiles <= 0) return result;
            if (maxHistory < 0) maxHistory = 0;

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
                    item.Detail = "还没有可跳的页码";
                }

                for (int i = 0; i < g.Records.Count && item.History.Count < maxHistory; i++)
                {
                    var r = g.Records[i];
                    if (r.EndSlide <= 0) continue;
                    item.History.Add(new CardHistoryItem
                    {
                        FilePath = Normalize(string.IsNullOrEmpty(r.FilePath) ? g.BucketKey : r.FilePath),
                        Slide = r.EndSlide,
                        Label = SessionEngine.HistoryLabel(r),
                        IsLatest = item.History.Count == 0
                    });
                }

                result.Add(item);
            }

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

            result.Sort((a, b) =>
            {
                int c = b.LastAt.CompareTo(a.LastAt);
                return c != 0 ? c : string.CompareOrdinal(a.DisplayName, b.DisplayName);
            });
            if (result.Count > maxFiles) result.RemoveRange(maxFiles, result.Count - maxFiles);
            return result;
        }

        public static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string? seewo = SeewoName(path);
            return seewo != null ? SeewoBridge.KeyPrefix + seewo : path.Trim();
        }

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
