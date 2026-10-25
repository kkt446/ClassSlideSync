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
        // 整窗不透明度，绝对百分比：100 = 完全不透，90 只留一点点（默认值就是 90）。
        // 2026-10-24 第二轮起，跟随 ClassIsland 主题时**不再**乘宿主自己的 Opacity ——
        // 底色照旧用实测的 #EEEEEE，但透明度由这个滑块独占：乘上去等于把宿主那层 0.5
        // 再叠一遍，卡片浮在彩色幻灯片上时整块发雾（用户原话「不要虚化」）。
        // 详见 Core\Appearance.cs 里 Resolve 的 alpha 段。
        int cardOpacityPercent = 90;
        int cardCornerRadius = -1;      // -1 = 跟随 ClassIsland 的圆角
        double cardScale = 1.0;
        int cardFontSizeDelta = 0;
        string cardFontFamily = "";
        bool cardOnLeft = false;
        int cardVerticalPercent = 50;
        int collapsedTabWidth = 32;
        // 2026-10-24：药丸默认拉长（原来固定 96×缩放），长度可调。
        int collapsedTabLength = 140;
    // 2026-10-24：药丸里要放「已翻 N / 共 M」两行字，50% 透明时字几乎看不见，
    // 默认提到 80%；老师想要原来的观感可以自己调回 50。
    int collapsedTabOpacityPercent = 80;
    // 2026-10-25 用户第 3 条：药丸里那两行「已翻 N / 共 M」竖排数字默认画；
    // 关掉后药丸只剩箭头 + 进度条（药丸只有几十像素宽，老师嫌挤就会关）。
    bool showPillPageNumbers = true;
        bool cardShowFileName = true;
        bool cardShowSlideCount = true;
        bool cardShowContext = true;
        bool cardShowStatusLine = true;
        bool cardShowJumpLine = true;
        bool cardClickToJump = true;
        // 2026-10-24 控制面板：卡片右上角的齿轮（独立 Avalonia 窗口的入口）默认开。
        bool enableControlPanel = true;
        // 2026-10-24：控制面板不要亚克力，默认纯白；颜色可改（#AARRGGBB）。
        string panelBackgroundColor = "#FFFFFFFF";
        // 2026-10-24 功能4：下课那一刻（且课件已经关掉）弹一条 Windows 通知，说说这一节记到哪了。
        bool classEndNotification = true;
        /// <summary>功能7（2026-10-25）：课前 2 分钟亮预备态。老师不需要就直接关掉。</summary>
        bool readyCardEnabled = true;

        // ---- 学科课件文件夹（2026-10-16 用户第 1 条）----
        bool autoOpenSubjectFolder = true;
        ObservableCollection<SubjectFolder> subjectFolders = new ObservableCollection<SubjectFolder>();

        // ---- 每科的药丸进度条颜色（2026-10-25 用户第 9 条）----
        ObservableCollection<SubjectColor> subjectColors = new ObservableCollection<SubjectColor>();

        public SyncSettings()
        {
            // 集合内部的增删、以及每一行自己改路径，都要让宿主知道"设置有变化"，
            // 否则 ConfigureFileHelper 不会保存：PropertyChanged 默认只在设置属性被赋值时触发。
            subjectFolders.CollectionChanged += OnFoldersChanged;
            subjectColors.CollectionChanged += OnSubjectColorsChanged;
            // 新装（或旧配置里没有这个字段）时先把预设色表灌进去，设置页里直接就能改颜色。
            SeedDefaultSubjectColors();
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

        /// <summary>下课时弹一条系统通知（只有课件已经关掉才弹）。</summary>
        public bool ClassEndNotification
        {
            get { return classEndNotification; }
            set { Set(ref classEndNotification, value); }
        }

        /// <summary>上课前两分钟是否亮「预备态」卡片（功能7）。关掉之后只在真正放映时出现。</summary>
        public bool ReadyCardEnabled
        {
            get { return readyCardEnabled; }
            set { Set(ref readyCardEnabled, value); }
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

        /// <summary>
        /// 卡片不透明度（百分比）。太高挡课件，太低看不清字。
        /// 它是**绝对**的整窗不透明度：100 完全不透、90 只留一点点、往下逐级更透；
        /// 低于 20 会被夹到 20（再低整张卡片就看不见了）。
        /// 注意别按「相对宿主」理解：2026-10-24 第二轮起跟随主题时也不乘 ClassIsland 的
        /// Opacity（乘上去在彩色幻灯片上会整块发雾）。自定义底色带 8 位 alpha
        /// （#RRGGBBAA）时以那个 alpha 为准，这个滑块不参与。
        /// </summary>
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

        /// <summary>收起后药丸的长度（沿屏幕边缘方向，96dpi 逻辑像素）。</summary>
        public int CollapsedTabOpacityPercent
    {
        get { return collapsedTabOpacityPercent; }
        set { Set(ref collapsedTabOpacityPercent, Clamp(value, 30, 100)); }
    }

    public int CollapsedTabLength
        {
            get { return collapsedTabLength; }
            set { Set(ref collapsedTabLength, Clamp(value, 96, 480)); }
        }

        /// <summary>
        /// 药丸里画不画「已翻 N / 共 M」两行竖排数字（默认画）。关掉后药丸只剩箭头 + 进度条，
        /// 药丸宽度不变（免得一开关就重新排一次版），省出来的空间全给进度条。
        /// 2026-10-25 用户第 3 条。
        /// </summary>
        public bool ShowPillPageNumbers
        {
            get { return showPillPageNumbers; }
            set { Set(ref showPillPageNumbers, value); }
        }

        /// <summary>控制面板的底色，默认不透明白（#AARRGGBB）。</summary>
        public string PanelBackgroundColor
        {
            get { return panelBackgroundColor; }
            set
            {
                string v = (value ?? "").Trim();
                if (v.Length == 0) v = "#FFFFFFFF";
                if (v[0] != '#') v = "#" + v;
                Set(ref panelBackgroundColor, v.ToUpperInvariant());
            }
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

        /// <summary>
        /// 在卡片右上角显示控制面板按钮（齿轮）。关掉后卡片上不画它，其余行为不变；
        /// 面板本身仍可由服务打开（设置页的"预览"不会用到）。
        /// </summary>
        public bool EnableControlPanel
        {
            get { return enableControlPanel; }
            set { Set(ref enableControlPanel, value); }
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
        // ---- 控制面板窗口几何（2026-10-24 老师要求"跟正常软件一样"）----
        // 0 = 没有记录过：宽度/高度 < 480/360 时用 axaml 里的默认值，坐标 0,0 视为"没记录"。
        int panelWidth, panelHeight, panelLeft, panelTop;

        public int PanelWidth
        {
            get { return panelWidth; }
            set { Set(ref panelWidth, value); }
        }

        public int PanelHeight
        {
            get { return panelHeight; }
            set { Set(ref panelHeight, value); }
        }

        public int PanelLeft
        {
            get { return panelLeft; }
            set { Set(ref panelLeft, value); }
        }

        public int PanelTop
        {
            get { return panelTop; }
            set { Set(ref panelTop, value); }
        }

        public ObservableCollection<SubjectFolder> SubjectFolders
        {
            get { return subjectFolders; }
            set
            {
                if (value == null || ReferenceEquals(value, subjectFolders)) return;
                // 旧集合里的每一行也要逐个解订阅：它们当初在 OnFoldersChanged / 构造里挂上了
                // OnFolderChanged。只解集合事件的话，被换掉的那些 SubjectFolder 会一直活着
                // （设置页每次重新加载都漏一批），而且它们改一个字段仍会往**新**设置对象上
                // 抛 PropertyChanged(SubjectFolders)，宿主会把一份早就没人看的配置存回去。
                subjectFolders.CollectionChanged -= OnFoldersChanged;
                foreach (SubjectFolder old in subjectFolders) Hook(old, false);
                subjectFolders = value;
                subjectFolders.CollectionChanged += OnFoldersChanged;
                foreach (SubjectFolder f in subjectFolders) Hook(f, true);
                Raise(nameof(SubjectFolders));
            }
        }

        /// <summary>
        /// 科目 → 药丸进度条颜色（2026-10-25 用户第 9 条："每科改颜色，设置可以调每科对应的颜色"）。
        /// 集合为空时用内置的 <see cref="DefaultColorSubjects"/>/<see cref="DefaultColorHexes"/> 兜底，
        /// 表里没有的科目回退成主题强调色（见 CardAppearance 里的配色查询）。
        /// </summary>
        public ObservableCollection<SubjectColor> SubjectColors
        {
            get { return subjectColors; }
            set
            {
                if (value == null || ReferenceEquals(value, subjectColors)) return;
                // 换集合时逐行解订阅，理由同 SubjectFolders：被换掉的行不能继续往新对象上抛事件。
                subjectColors.CollectionChanged -= OnSubjectColorsChanged;
                foreach (SubjectColor old in subjectColors) HookColor(old, false);
                subjectColors = value;
                subjectColors.CollectionChanged += OnSubjectColorsChanged;
                foreach (SubjectColor c in subjectColors) HookColor(c, true);
                Raise(nameof(SubjectColors));
            }
        }

        /// <summary>
        /// 按科目名找课件文件夹；找不到返回空串。ClassIsland 的科目名可以是复合的
        /// （课表里写"劳动/英语"、简称"劳/英"），所以查询和配置条目**两侧都拆段**求交集。
        /// 2026-10-24 第二轮 F2：以前只拆查询侧，存"劳动/英语"时用"劳动"或"英语"查不到；
        /// 现在两个方向都能命中。多个条目同时命中时按配置顺序取第一个，并写一条 ambiguous
        /// 日志留痕（老师自己配了重复科目时，至少能从日志看出为什么打开了这个文件夹）。
        /// </summary>
        public string FindSubjectFolder(string subjectName)
        {
            if (string.IsNullOrWhiteSpace(subjectName) || subjectFolders == null) return "";
            string want = subjectName.Trim();
            string[] wantParts = SplitSubject(want);

            string chosen = "", chosenSubject = "";
            int hits = 0;
            foreach (SubjectFolder f in subjectFolders)
            {
                if (f == null) continue;
                string name = f.Subject == null ? "" : f.Subject.Trim();
                if (name.Length == 0) continue;
                string folder = f.Folder == null ? "" : f.Folder.Trim();
                if (folder.Length == 0) continue;   // 配了科目但没配路径：不算候选
                if (!SegmentOverlap(wantParts, SplitSubject(name))) continue;
                hits++;
                if (chosen.Length == 0) { chosen = folder; chosenSubject = name; }
            }

            if (chosen.Length == 0) return "";
            if (hits > 1 && LogSink != null)
            {
                try
                {
                    LogSink("ambiguous subject folder: " + want + " -> " + chosenSubject +
                        "（" + hits + " 个候选，按配置顺序取第一个）");
                }
                catch { }
            }
            return chosen;
        }

        static readonly char[] SubjectSeps = new char[] { '/', '／', '\\', '、', '|', '｜', '+', '＋' };

        /// <summary>按课表里出现过的分隔符拆段并去空白；空串返回空数组。</summary>
        static string[] SplitSubject(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject)) return new string[0];
            string[] raw = subject.Split(SubjectSeps);
            var parts = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string t = raw[i].Trim();
                if (t.Length > 0) parts.Add(t);
            }
            return parts.ToArray();
        }

        static bool SegmentOverlap(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0) return false;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- 药丸进度条配色（2026-10-25 用户第 9 条）------------------------------

        /// <summary>
        /// 两个科目名是否指同一科：先去空白精确比，再按课表分隔符两侧拆段求交集。
        /// 给 <see cref="DefaultColorSubjects"/> 的兜底查询和 Appearance 侧的色表查询共用。
        /// </summary>
        internal static bool SubjectNamesOverlap(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return SegmentOverlap(SplitSubject(a), SplitSubject(b));
        }

        /// <summary>预设色表的科目名，顺序就是设置页里新装的显示顺序。</summary>
        internal static readonly string[] DefaultColorSubjects = new string[]
        {
            "语文", "数学", "英语", "物理", "化学", "生物", "政治", "历史",
            "地理", "科学", "信息", "音乐", "美术", "体育"
        };

        /// <summary>
        /// 和 <see cref="DefaultColorSubjects"/> 一一对应的一组协调预设色：同一明度/饱和度区间
        /// （Open Color 7 级），白底药丸上都能看清，彼此又不撞色。
        /// </summary>
        internal static readonly string[] DefaultColorHexes = new string[]
        {
            "#E03131", "#1971C2", "#7048E8", "#0CA678", "#F08C00", "#2F9E44", "#C2255C", "#8B5E34",
            "#0B7285", "#5C7CFA", "#3B5BDB", "#AE3EC9", "#A61E4D", "#66A80F"
        };

        /// <summary>
        /// 按科目名找药丸进度条的颜色（"#RRGGBB"）；找不到返回空串，调用方回退主题强调色。
        /// 匹配规则和 <see cref="FindSubjectFolder"/> 一致：先精确命中，再两侧拆段求交集
        /// （课表里"劳动/英语"这种复合科目名，配"劳动"或"英语"都能命中）。
        /// 条目非空时**不再**回落到内置表：用户把某科从表里删掉，就是想让那科用强调色。
        /// </summary>
        public string FindSubjectColor(string subjectName)
        {
            if (string.IsNullOrWhiteSpace(subjectName)) return "";
            string want = subjectName.Trim();

            if (subjectColors != null && subjectColors.Count > 0)
            {
                string[] wantParts = SplitSubject(want);
                string exact = "", loose = "";
                foreach (SubjectColor c in subjectColors)
                {
                    if (c == null) continue;
                    string name = c.Subject == null ? "" : c.Subject.Trim();
                    string color = c.Color == null ? "" : c.Color.Trim();
                    if (name.Length == 0 || color.Length == 0) continue;
                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                    {
                        if (exact.Length == 0) exact = color;
                        continue;
                    }
                    if (loose.Length == 0 && SegmentOverlap(wantParts, SplitSubject(name))) loose = color;
                }
                if (exact.Length > 0) return exact;
                return loose;
            }

            // 集合为空（用户全删了，或旧配置里根本没这个字段）：用内置预设色表兜底。
            for (int i = 0; i < DefaultColorSubjects.Length; i++)
                if (string.Equals(DefaultColorSubjects[i], want, StringComparison.OrdinalIgnoreCase))
                    return DefaultColorHexes[i];
            string[] parts = SplitSubject(want);
            for (int i = 0; i < DefaultColorSubjects.Length; i++)
                if (SegmentOverlap(parts, SplitSubject(DefaultColorSubjects[i])))
                    return DefaultColorHexes[i];
            return "";
        }

        /// <summary>
        /// 把预设色表灌进集合，好让设置页一打开就有 14 行可改的颜色。
        /// 只在集合为空时灌一次：用户自己把行删光是合法操作，查询侧还有内置表兜底。
        /// </summary>
        internal void SeedDefaultSubjectColors()
        {
            if (subjectColors == null || subjectColors.Count > 0) return;
            for (int i = 0; i < DefaultColorSubjects.Length && i < DefaultColorHexes.Length; i++)
                subjectColors.Add(new SubjectColor { Subject = DefaultColorSubjects[i], Color = DefaultColorHexes[i] });
        }

        void OnSubjectColorsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (SubjectColor c in e.NewItems) HookColor(c, true);
            if (e.OldItems != null) foreach (SubjectColor c in e.OldItems) HookColor(c, false);
            Raise(nameof(SubjectColors));
        }

        void HookColor(SubjectColor c, bool on)
        {
            if (c == null) return;
            if (on) c.PropertyChanged += OnColorChanged;
            else c.PropertyChanged -= OnColorChanged;
        }

        void OnColorChanged(object sender, PropertyChangedEventArgs e)
        {
            // 某一行改了科目名或颜色：也算"设置变了"，宿主会重算外观（卡片当场换色）并落盘。
            Raise(nameof(SubjectColors));
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

        /// <summary>
        /// #RGB / #RRGGBB / #RRGGBBAA / 裸十六进制都接受，统一存成大写串。
        /// 8 位的 alpha 必须保留：2026-10-24 第二轮 F1 —— 以前一律截成 6 位，老师填
        /// #10203080 期望半透明，实际 alpha 被静默丢掉（TryParseColor 本身是认 8 位的）。
        /// 3 位按 CSS 简写规则展开（#FFF → #FFFFFF），不影响 6/8 位的原有行为。
        /// </summary>
        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string s = value.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 3 && s.Length != 6 && s.Length != 8) return "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return "";
            }
            s = s.ToUpperInvariant();
            if (s.Length == 3)
            {
                // #RGB → #RRGGBB：每一位复制一遍，和浏览器/设置页的颜色输入习惯一致。
                char[] expanded = new char[6];
                for (int i = 0; i < 3; i++)
                {
                    expanded[i * 2] = s[i];
                    expanded[i * 2 + 1] = s[i];
                }
                s = new string(expanded);
            }
            return "#" + s;
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

        /// <summary>
        /// 写诊断日志的出口（由 <see cref="Services.SlideSyncService"/> 接到台账的 app.log）。
        /// 设置类本身不该持有 Ledger 实例，否则 JSON 往返和单元测试都得为它准备一个数据目录。
        /// </summary>
        [JsonIgnore]
        internal Action<string> LogSink { get; set; }
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

        // ---- 下面两个是给设置页的行模板用的，只活在内存里，不参与 JSON 往返 ----

        bool useDropdown = true;

        /// <summary>
        /// 下拉选项（引用设置页那份共享列表，只增不删）。挂在行对象上而不是让模板去
        /// 往上找页面：插件是动态加载的，Avalonia 在模板里解析 <c>$parent[local:类型]</c>
        /// 这种写法要做运行时程序集加载（XamlTypeResolver.Resolve → Assembly.Load），
        /// 在 ClassIsland 里会抛 FileNotFoundException 把整个设置页打崩。
        /// 绑定到 <c>x:DataType</c> 指明的类型成员则是编译期绑定，不需要运行时解析。
        /// </summary>
        [JsonIgnore]
        internal ObservableCollection<string> Options { get; set; }

        /// <summary>true = 科目这一列显示下拉；false = 退回可编辑文本框（课表没读出来时）。</summary>
        [JsonIgnore]
        public bool UseDropdown
        {
            get { return useDropdown; }
            set
            {
                if (useDropdown == value) return;
                useDropdown = value;
                var h = PropertyChanged;
                if (h == null) return;
                h(this, new PropertyChangedEventArgs(nameof(UseDropdown)));
                h(this, new PropertyChangedEventArgs(nameof(UseTextBox)));
            }
        }

        /// <summary>给 XAML 的 <c>IsVisible</c> 用，取反而已。</summary>
        [JsonIgnore]
        public bool UseTextBox { get { return !useDropdown; } }

        public event PropertyChangedEventHandler PropertyChanged;

        void Set(ref string field, string value, [CallerMemberName] string name = null)
        {
            if (string.Equals(field, value, StringComparison.Ordinal)) return;
            field = value;
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 一条「科目 → 药丸进度条颜色」映射（2026-10-25 用户第 9 条：每科颜色自己调）。
    /// 匹配规则见 <see cref="SyncSettings.FindSubjectColor"/>。
    /// </summary>
    public sealed class SubjectColor : INotifyPropertyChanged
    {
        string subject = "";
        string color = "#1971C2";

        /// <summary>课表里的科目名，和 ClassIsland 科目列表里显示的一致（例：语文）。</summary>
        public string Subject
        {
            get { return subject; }
            set { Set(ref subject, value == null ? "" : value.Trim()); }
        }

        /// <summary>
        /// 这个科目在药丸里那道竖进度条用的颜色，只存 #RRGGBB（药丸本身才透，颜色永不透）。
        /// 非法串直接丢掉保留原值：ColorPicker 拖到一半偶尔回传空串，写进去等于把配置清空。
        /// </summary>
        public string Color
        {
            get { return color; }
            set
            {
                string v = SyncSettings.Normalize(value);
                if (v.Length == 0) return;
                // 8 位的 alpha 一律丢掉（这条条永远不透明），但两个来源的字节序不同：
                // ClassIsland 的配置串是 #RRGGBBAA（alpha 在尾），设置页 ColorPicker 的回传是
                // #AARRGGBB（alpha 在首）。两头都认——带 FF 的那一端就是 alpha。
                if (v.Length == 9)
                {
                    string hex = v.Substring(1);
                    v = hex.EndsWith("FF", StringComparison.OrdinalIgnoreCase)
                        ? "#" + hex.Substring(0, 6)
                        : "#" + hex.Substring(2);
                }
                Set(ref color, v);
            }
        }

        // ---- 下面两个是给设置页的行模板用的，只活在内存里，不参与 JSON 往返 ----
        // （和 SubjectFolder 的理由一样：插件动态加载，模板里不能用 $parent[] 往上找页面。）

        [JsonIgnore]
        internal ObservableCollection<string> Options { get; set; }

        /// <summary>true = 科目这一列显示下拉；false = 退回可编辑文本框（课表没读出来时）。</summary>
        [JsonIgnore]
        public bool UseDropdown
        {
            get { return useDropdown; }
            set
            {
                if (useDropdown == value) return;
                useDropdown = value;
                var h = PropertyChanged;
                if (h == null) return;
                h(this, new PropertyChangedEventArgs(nameof(UseDropdown)));
                h(this, new PropertyChangedEventArgs(nameof(UseTextBox)));
            }
        }

        bool useDropdown = true;

        /// <summary>给 XAML 的 <c>IsVisible</c> 用，取反而已。</summary>
        [JsonIgnore]
        public bool UseTextBox { get { return !useDropdown; } }

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
