using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// User-visible switches. Everything here is settable from the ClassIsland settings page,
    /// and the defaults are what an ordinary lesson needs.
    /// </summary>
    public sealed class SyncSettings : INotifyPropertyChanged
    {
        bool autoShowOverlay = true;
        bool autoHide = true;
        int autoHideSeconds = 15;
        int minSessionSeconds = 10;
        bool recordLedger = true;
        bool confirmBeforeJump = false;
        bool jumpResetsAnimation = false;

        // ---- 运行时机与缓存 ----
        bool autoStartBeforeClass = true;
        int preClassLeadSeconds = 120;
        int postClassGraceSeconds = 120;
        bool armOnDeckOpenOutsideClass = true;
        bool showCardOutsideClass = true;
        int historyRetentionDays = 21;
        bool diagnosticLog = false;

        // ---- 外观（UI 自定义；解析与优先级见 Core/Appearance.cs）----
        bool followClassIslandTheme = true;
        string accentColor = "";
        string cardBackgroundColor = "";
        int cardOpacityPercent = 90;
        int cardCornerRadius = -1;      // -1 = 跟随 ClassIsland 的圆角
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

        // ---- 学科课件文件夹（2026-10-16 用户第 1 条）----
        bool autoOpenSubjectFolder = true;
        ObservableCollection<SubjectFolder> subjectFolders = new ObservableCollection<SubjectFolder>();

        public SyncSettings()
        {
            // 集合内部的增删、以及每一行自己改路径，都要让宿主知道"设置有变化"，
            // 否则 ConfigureFileHelper 不会保存：PropertyChanged 默认只在设置属性被赋值时触发。
            subjectFolders.CollectionChanged += OnFoldersChanged;
        }

        /// <summary>Show the floating card as soon as a show is detected.</summary>
        public bool AutoShowOverlay
        {
            get { return autoShowOverlay; }
            set { Set(ref autoShowOverlay, value); }
        }

        /// <summary>
        /// Slide the card away after a period with no page turn. Defaults to on; the timer
        /// restarts on every page turn (rule A), because the alternative -- counting from the
        /// moment the card appeared -- hides the card in the middle of a normal lesson.
        /// </summary>
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

        /// <summary>Shows shorter than this are treated as mis-clicks and not recorded.</summary>
        public int MinSessionSeconds
        {
            get { return minSessionSeconds; }
            set { Set(ref minSessionSeconds, Clamp(value, 0, 3600)); }
        }

        /// <summary>Write a record for every lesson to the ledger.</summary>
        public bool RecordLedger
        {
            get { return recordLedger; }
            set { Set(ref recordLedger, value); }
        }

        /// <summary>Ask before jumping, for teachers who click the card by accident.</summary>
        public bool ConfirmBeforeJump
        {
            get { return confirmBeforeJump; }
            set { Set(ref confirmBeforeJump, value); }
        }

        /// <summary>
        /// false (default): resume the target slide's animation where it was interrupted
        /// (GotoSlide(index, 0)). true: replay that slide's animation from the start.
        /// </summary>
        public bool JumpResetsAnimation
        {
            get { return jumpResetsAnimation; }
            set { Set(ref jumpResetsAnimation, value); }
        }

        // ---- 运行时机与缓存 -----------------------------------------------------

        /// <summary>
        /// 按 ClassIsland 的课表在上课前自动待命（默认开）。关掉之后插件只在检测到演示软件
        /// 已经打开时才工作——适合没有排课表、或者不想让插件跟着课表走的老师。
        /// </summary>
        public bool AutoStartBeforeClass
        {
            get { return autoStartBeforeClass; }
            set { Set(ref autoStartBeforeClass, value); }
        }

        /// <summary>提前多少秒开始待命（默认 120 秒，也就是"上课前 2 分钟"）。</summary>
        public int PreClassLeadSeconds
        {
            get { return preClassLeadSeconds; }
            set { Set(ref preClassLeadSeconds, Clamp(value, 0, 600)); }
        }

        /// <summary>
        /// 下课后再留多久才彻底退出（默认 120 秒）。这段时间引擎继续盯着课件、把
        /// "最后停在第几页"记完；卡片这时显示不显示，看 <see cref="ShowCardOutsideClass"/>。
        /// </summary>
        public int PostClassGraceSeconds
        {
            get { return postClassGraceSeconds; }
            set { Set(ref postClassGraceSeconds, Clamp(value, 0, 1800)); }
        }

        /// <summary>
        /// 课表时段之外，只要老师自己打开了 PowerPoint/WPS 就照样加载（默认开）。
        /// 自习课、临时加课、学校没排课表时的兜底。
        /// </summary>
        public bool ArmOnDeckOpenOutsideClass
        {
            get { return armOnDeckOpenOutsideClass; }
            set { Set(ref armOnDeckOpenOutsideClass, value); }
        }

        /// <summary>
        /// 课表时段之外，只要检测到演示软件在跑（老师自己打开了课件）就把卡片显示出来
        /// （默认**开**，用户 2026-10-15 要求："检测到 ppt 软件启动时就打开"）。
        /// 关掉它才是严格模式：课表时段之外一律不显示，下课铃一响卡片就隐藏。
        /// </summary>
        public bool ShowCardOutsideClass
        {
            get { return showCardOutsideClass; }
            set { Set(ref showCardOutsideClass, value); }
        }

        /// <summary>历史记录保留多少天（默认 21 天 = 3 周），更久没用的课件记录会被清掉。</summary>
        public int HistoryRetentionDays
        {
            get { return historyRetentionDays; }
            set { Set(ref historyRetentionDays, Clamp(value, 1, 3650)); }
        }

        /// <summary>
        /// 诊断日志：把每次 COM 轮询的结果都写进 app.log。排查问题时打开，平时关着
        /// （开着一节课能写几万行）。
        /// </summary>
        public bool DiagnosticLog
        {
            get { return diagnosticLog; }
            set { Set(ref diagnosticLog, value); }
        }

        // ---- 外观属性 ----------------------------------------------------------

        /// <summary>
        /// 跟随 ClassIsland 的主题色、圆角、字体和主界面底色。默认开；关掉之后下面的自定义值
        /// 才起作用（等于"我不想让它像 ClassIsland，我想自己配"）。
        /// </summary>
        public bool FollowClassIslandTheme
        {
            get { return followClassIslandTheme; }
            set { Set(ref followClassIslandTheme, value); }
        }

        /// <summary>自定义强调色，形如 #FF9D11；空 = 跟随 ClassIsland 主色。</summary>
        public string AccentColor
        {
            get { return accentColor; }
            set { Set(ref accentColor, Normalize(value)); }
        }

        /// <summary>自定义卡片底色，形如 #1E1E1E；空 = 跟随 ClassIsland 主界面底色。</summary>
        public string CardBackgroundColor
        {
            get { return cardBackgroundColor; }
            set { Set(ref cardBackgroundColor, Normalize(value)); }
        }

        /// <summary>卡片不透明度（百分比）。太高挡课件，太低看不清字。</summary>
        public int CardOpacityPercent
        {
            get { return cardOpacityPercent; }
            set { Set(ref cardOpacityPercent, Clamp(value, 20, 100)); }
        }

        /// <summary>卡片圆角（96 dpi 像素）。-1 = 跟随 ClassIsland 的圆角设置。</summary>
        public int CardCornerRadius
        {
            get { return cardCornerRadius; }
            set { Set(ref cardCornerRadius, Clamp(value, -1, 48)); }
        }

        /// <summary>整张卡片（含字号）的缩放倍数。</summary>
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

        /// <summary>字号微调：在主题字号基础上加减几个磅值。</summary>
        public int CardFontSizeDelta
        {
            get { return cardFontSizeDelta; }
            set { Set(ref cardFontSizeDelta, Clamp(value, -4, 12)); }
        }

        /// <summary>自定义字体族名；空 = 跟随 ClassIsland 主界面字体。</summary>
        public string CardFontFamily
        {
            get { return cardFontFamily; }
            set { Set(ref cardFontFamily, value == null ? "" : value.Trim()); }
        }

        /// <summary>卡片贴左边还是右边（false = 右边，和 ClassIsland 主界面同侧）。</summary>
        public bool CardOnLeft
        {
            get { return cardOnLeft; }
            set { Set(ref cardOnLeft, value); }
        }

        /// <summary>卡片在屏幕上的垂直位置，百分比（0 = 最上，50 = 居中）。</summary>
        public int CardVerticalPercent
        {
            get { return cardVerticalPercent; }
            set { Set(ref cardVerticalPercent, Clamp(value, 0, 100)); }
        }

        /// <summary>收起后露在屏幕边缘的标签宽度。</summary>
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

        /// <summary>允许点击卡片跳到"上次停的那一页"。</summary>
        public bool CardClickToJump
        {
            get { return cardClickToJump; }
            set { Set(ref cardClickToJump, value); }
        }

        // ---- 学科课件文件夹（2026-10-16 用户第 1 条）----------------------------

        /// <summary>
        /// 上课时自动打开"当前科目对应的课件文件夹"（默认开）。科目名取自 ClassIsland 课表，
        /// 文件夹在下面这个列表里配。
        /// </summary>
        public bool AutoOpenSubjectFolder
        {
            get { return autoOpenSubjectFolder; }
            set { Set(ref autoOpenSubjectFolder, value); }
        }

        /// <summary>科目 → 课件文件夹。设置页里可以直接增删改。</summary>
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

        /// <summary>
        /// 按科目名找课件文件夹；找不到返回空串。ClassIsland 的科目名可以是复合的
        /// （课表里写"劳动/英语"、简称"劳/英"），所以先整体匹配，再退回去试斜杠分开的每一段。
        /// </summary>
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
            // 某一行改了科目名或路径：也算"设置变了"，让宿主把配置存下来。
            Raise(nameof(SubjectFolders));
        }

        /// <summary>#RRGGBB / #RRGGBBAA / 裸十六进制都接受，统一存成 #RRGGBB 形式的大写串。</summary>
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

        /// <summary>Where the ledger and logs live. Overridable so tests can redirect it.</summary>
        [JsonIgnore]
        public string DataFolder { get; set; }
    }

    /// <summary>
    /// 一条「科目 → 课件文件夹」映射（2026-10-16 用户第 1 条：上课时自动打开这个学科放 PPT 的文件夹）。
    /// ClassIsland 的科目名可以是复合的（课表里"劳动/英语"、简称"劳/英"），
    /// 匹配规则见 <see cref="SyncSettings.FindSubjectFolder"/>。
    /// </summary>
    public sealed class SubjectFolder : INotifyPropertyChanged
    {
        string subject = "";
        string folder = "";

        /// <summary>课表里的科目名，和 ClassIsland 科目列表里显示的一致（例：语文）。</summary>
        public string Subject
        {
            get { return subject; }
            set { Set(ref subject, value == null ? "" : value.Trim()); }
        }

        /// <summary>这个科目放课件的文件夹全路径。</summary>
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
