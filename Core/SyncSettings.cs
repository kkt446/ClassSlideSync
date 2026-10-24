using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ClassSlideSync.Plugin.Core
{
    public sealed class SyncSettings : INotifyPropertyChanged
    {
        bool autoShowOverlay = true;
        bool autoHide = true;
        int autoHideSeconds = 15;
        int minSessionSeconds = 10;
        bool recordLedger = true;
        bool confirmBeforeJump = false;
        bool jumpResetsAnimation = false;

        bool autoStartBeforeClass = true;
        int preClassLeadSeconds = 120;
        int postClassGraceSeconds = 120;
        bool armOnDeckOpenOutsideClass = true;
        bool showCardOutsideClass = true;
        int historyRetentionDays = 21;
        bool diagnosticLog = false;

        bool followClassIslandTheme = true;
        string accentColor = "";
        string cardBackgroundColor = "";
        int cardOpacityPercent = 90;
        int cardCornerRadius = -1;
        double cardScale = 1.0;
        int cardFontSizeDelta = 0;
        string cardFontFamily = "";
        bool cardOnLeft = false;
        int cardVerticalPercent = 50;
        int collapsedTabWidth = 32;
        bool cardShowFileName = true;
        bool cardShowSlideCount = true;
        bool cardShowContext = true;
        bool cardShowStatusLine = true;
        bool cardShowJumpLine = true;
        bool cardClickToJump = true;

        bool autoOpenSubjectFolder = true;
        ObservableCollection<SubjectFolder> subjectFolders = new ObservableCollection<SubjectFolder>();

        public SyncSettings()
        {
            subjectFolders.CollectionChanged += OnFoldersChanged;
        }

        public bool AutoShowOverlay
        {
            get { return autoShowOverlay; }
            set { Set(ref autoShowOverlay, value); }
        }

        public bool AutoHide
        {
            get { return autoHide; }
            set { Set(ref autoHide, value); }
        }

        public int AutoHideSeconds
        {
            get { return autoHideSeconds; }
            set { Set(ref autoHideSeconds, Clamp(value, 5, 600)); }
        }

        public int MinSessionSeconds
        {
            get { return minSessionSeconds; }
            set { Set(ref minSessionSeconds, Clamp(value, 0, 3600)); }
        }

        public bool RecordLedger
        {
            get { return recordLedger; }
            set { Set(ref recordLedger, value); }
        }

        public bool ConfirmBeforeJump
        {
            get { return confirmBeforeJump; }
            set { Set(ref confirmBeforeJump, value); }
        }

        public bool JumpResetsAnimation
        {
            get { return jumpResetsAnimation; }
            set { Set(ref jumpResetsAnimation, value); }
        }

        public bool AutoStartBeforeClass
        {
            get { return autoStartBeforeClass; }
            set { Set(ref autoStartBeforeClass, value); }
        }

        public int PreClassLeadSeconds
        {
            get { return preClassLeadSeconds; }
            set { Set(ref preClassLeadSeconds, Clamp(value, 0, 600)); }
        }

        public int PostClassGraceSeconds
        {
            get { return postClassGraceSeconds; }
            set { Set(ref postClassGraceSeconds, Clamp(value, 0, 1800)); }
        }

        public bool ArmOnDeckOpenOutsideClass
        {
            get { return armOnDeckOpenOutsideClass; }
            set { Set(ref armOnDeckOpenOutsideClass, value); }
        }

        public bool ShowCardOutsideClass
        {
            get { return showCardOutsideClass; }
            set { Set(ref showCardOutsideClass, value); }
        }

        public int HistoryRetentionDays
        {
            get { return historyRetentionDays; }
            set { Set(ref historyRetentionDays, Clamp(value, 1, 3650)); }
        }

        public bool DiagnosticLog
        {
            get { return diagnosticLog; }
            set { Set(ref diagnosticLog, value); }
        }

        public bool FollowClassIslandTheme
        {
            get { return followClassIslandTheme; }
            set { Set(ref followClassIslandTheme, value); }
        }

        public string AccentColor
        {
            get { return accentColor; }
            set { Set(ref accentColor, Normalize(value)); }
        }

        public string CardBackgroundColor
        {
            get { return cardBackgroundColor; }
            set { Set(ref cardBackgroundColor, Normalize(value)); }
        }

        public int CardOpacityPercent
        {
            get { return cardOpacityPercent; }
            set { Set(ref cardOpacityPercent, Clamp(value, 20, 100)); }
        }

        public int CardCornerRadius
        {
            get { return cardCornerRadius; }
            set { Set(ref cardCornerRadius, Clamp(value, -1, 48)); }
        }

        public double CardScale
        {
            get { return cardScale; }
            set
            {
                double v = value;
                if (v < 0.6) v = 0.6;
                if (v > 2.0) v = 2.0;
                Set(ref cardScale, Math.Round(v, 2));
            }
        }

        public int CardFontSizeDelta
        {
            get { return cardFontSizeDelta; }
            set { Set(ref cardFontSizeDelta, Clamp(value, -4, 12)); }
        }

        public string CardFontFamily
        {
            get { return cardFontFamily; }
            set { Set(ref cardFontFamily, value == null ? "" : value.Trim()); }
        }

        public bool CardOnLeft
        {
            get { return cardOnLeft; }
            set { Set(ref cardOnLeft, value); }
        }

        public int CardVerticalPercent
        {
            get { return cardVerticalPercent; }
            set { Set(ref cardVerticalPercent, Clamp(value, 0, 100)); }
        }

        public int CollapsedTabWidth
        {
            get { return collapsedTabWidth; }
            set { Set(ref collapsedTabWidth, Clamp(value, 16, 96)); }
        }

        public bool CardShowFileName
        {
            get { return cardShowFileName; }
            set { Set(ref cardShowFileName, value); }
        }

        public bool CardShowSlideCount
        {
            get { return cardShowSlideCount; }
            set { Set(ref cardShowSlideCount, value); }
        }

        public bool CardShowContext
        {
            get { return cardShowContext; }
            set { Set(ref cardShowContext, value); }
        }

        public bool CardShowStatusLine
        {
            get { return cardShowStatusLine; }
            set { Set(ref cardShowStatusLine, value); }
        }

        public bool CardShowJumpLine
        {
            get { return cardShowJumpLine; }
            set { Set(ref cardShowJumpLine, value); }
        }

        public bool CardClickToJump
        {
            get { return cardClickToJump; }
            set { Set(ref cardClickToJump, value); }
        }

        public bool AutoOpenSubjectFolder
        {
            get { return autoOpenSubjectFolder; }
            set { Set(ref autoOpenSubjectFolder, value); }
        }

        public ObservableCollection<SubjectFolder> SubjectFolders
        {
            get { return subjectFolders; }
            set
            {
                if (value == null || ReferenceEquals(value, subjectFolders)) return;
                subjectFolders.CollectionChanged -= OnFoldersChanged;
                subjectFolders = value;
                subjectFolders.CollectionChanged += OnFoldersChanged;
                foreach (SubjectFolder f in subjectFolders) Hook(f, true);
                Raise(nameof(SubjectFolders));
            }
        }

        public string FindSubjectFolder(string subjectName)
        {
            if (string.IsNullOrWhiteSpace(subjectName)) return "";
            string want = subjectName.Trim();
            string hit = MatchFolder(want);
            if (hit.Length > 0) return hit;

            char[] seps = new char[] { '/', '／', '\\', '、', '|', '+' };
            if (want.IndexOfAny(seps) >= 0)
            {
                foreach (string part in want.Split(seps))
                {
                    hit = MatchFolder(part.Trim());
                    if (hit.Length > 0) return hit;
                }
            }
            return "";
        }

        string MatchFolder(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return "";
            foreach (SubjectFolder f in subjectFolders)
            {
                if (f == null) continue;
                string name = f.Subject == null ? "" : f.Subject.Trim();
                if (name.Length == 0) continue;
                if (!string.Equals(name, subject, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(f.Folder)) continue;
                return f.Folder.Trim();
            }
            return "";
        }

        void OnFoldersChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (SubjectFolder f in e.NewItems) Hook(f, true);
            if (e.OldItems != null) foreach (SubjectFolder f in e.OldItems) Hook(f, false);
            Raise(nameof(SubjectFolders));
        }

        void Hook(SubjectFolder f, bool on)
        {
            if (f == null) return;
            if (on) f.PropertyChanged += OnFolderChanged;
            else f.PropertyChanged -= OnFolderChanged;
        }

        void OnFolderChanged(object sender, PropertyChangedEventArgs e)
        {
            Raise(nameof(SubjectFolders));
        }

        static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string s = value.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return "";
            }
            return "#" + s.Substring(0, 6).ToUpperInvariant();
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public event PropertyChangedEventHandler PropertyChanged;

        void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        [JsonIgnore]
        public string DataFolder { get; set; }
    }

    public sealed class SubjectFolder : INotifyPropertyChanged
    {
        string subject = "";
        string folder = "";

        public string Subject
        {
            get { return subject; }
            set { Set(ref subject, value == null ? "" : value.Trim()); }
        }

        public string Folder
        {
            get { return folder; }
            set { Set(ref folder, value == null ? "" : value.Trim()); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Set(ref string field, string value, [CallerMemberName] string name = null)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
