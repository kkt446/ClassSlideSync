using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Media;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;
using ClassSlideSync.Plugin.Views.SettingsPages;

namespace ClassSlideSync.Plugin.Ui
{
    /// <summary>
    /// 课件续页的控制面板。卡片是 Win32 的 WS_EX_NOACTIVATE 图层窗口，拿不到键盘焦点，
    /// 所以“搜索课件、翻历史、跳页”这些需要输入的操作放到这个正经的 Avalonia 窗口里。
    ///
    /// 2026-10-24 老师第二轮要求：这个窗口同时是插件的统一入口，顶部三页
    /// 「课件库 / 设置 / 关于」。课件库的数据来自内存里的 Ledger（Ledger.Load 已经在服务启动时做过），
    /// 打开面板和点“刷新”时整栏重建；面板开着时每 2 秒对一次记录数，变了才增量补详情
    /// （见 <see cref="RefreshLive"/>），因为放映过程中台账是每秒都在长的。
    /// 设置页绑定到服务持有的同一个 <see cref="SyncSettings"/>，改完立即生效，
    /// 存盘仍由宿主的 ConfigureFileHelper 负责，面板自己不写配置文件。
    /// </summary>
    public partial class ControlPanel : Window
    {
        readonly Ledger ledger;

        // 管理模式（左栏「管理 → 勾选 → 删除选中」）：只删台账记录，绝不碰课件文件本身。
        bool manageMode;
        bool deleteArmed;
        Avalonia.Threading.DispatcherTimer? deleteArmTimer;
        readonly SyncSettings settings;
        readonly Func<CardState>? stateProvider;
        readonly Action<string>? openFolder;
        readonly SlideSyncService? service;
        readonly ObservableCollection<PanelGroup> groups = new ObservableCollection<PanelGroup>();

        List<CardFileItem> allFiles = new List<CardFileItem>();
        string currentKey = "";
        PanelRow? selectedRow;
        int selectedPage;
        bool loading;
        bool exporting;        // 导出进行中：防止连点打开两个保存对话框
        bool hintShown;        // 状态条那句“还没有记录”只是空台账提示，不是导出/跳页结果
        int shownCount;        // 左栏最近一次列出几份：状态条和增量刷新共用
        int liveRecords;       // 上次看到的台账记录总数：增量刷新靠它判断“有没有变”
        int liveFiles;         // 上次看到的课件份数：多出新课件要整栏重排
        DispatcherTimer? liveTimer;   // 台账在放映中是每秒变的，面板开着时定时补一次
        WindowState beforeFullScreen = WindowState.Normal;   // F11 之前是什么状态，退出全屏要回得去
        bool closed;           // 窗口已关闭：异步对话框回来时不能再往控件/配置里写

        /// <summary>
        /// 设置页的绑定源（和 ClassIsland 设置页是同一个对象）。只读引用，改动通过绑定写回。
        /// </summary>
        public SyncSettings Settings { get { return settings; } }

        /// <summary>颜色 / 圆角 / 左右那几个控件用的翻译层，见 <see cref="SlideSyncAppearanceViewModel"/>。</summary>
        public SlideSyncAppearanceViewModel Appearance { get; }

        /// <summary>科目下拉的选项（课表里的科目 + 已配置过的旧值，只增不删）。</summary>
        public ObservableCollection<string> SubjectOptions { get; } = new ObservableCollection<string>();

        /// <summary>
        /// 面板里点“立即跳转”。参数 = 哪份课件 + 第几页；服务把它交给和卡片相同的
        /// RequestJump(filePath, slide) 链路，不另开一条跳页实现。
        /// </summary>
        public event Action<string, int>? JumpRequested;

        /// <summary>
        /// 必须在本插件的服务里、Avalonia UI 线程上构造（非 UI 线程 new Window 会抛
        /// "Call from invalid thread"）。openFolder 由服务用宿主的 IUriNavigationService 实现；
        /// service 只给“预览卡片 / 外观摘要 / 关于页”取实时信息，允许为空（离线测试时为空）。
        /// </summary>
        public ControlPanel(Ledger ledger, SyncSettings settings, Func<CardState>? stateProvider,
                            Action<string>? openFolder = null, SlideSyncService? service = null)
        {
            this.ledger = ledger ?? new Ledger();
            this.settings = settings ?? new SyncSettings();
            this.stateProvider = stateProvider;
            this.openFolder = openFolder;
            this.service = service;

            // 绑定源要先于 InitializeComponent 之后的 DataContext 赋值准备好。
            Appearance = new SlideSyncAppearanceViewModel(this.settings, service!);

            InitializeComponent();
            DataContext = this;

            GroupList.ItemsSource = groups;
            ApplyPanelBackground();
            this.settings.PropertyChanged += OnSettingsChanged;
            DetailPanel.IsVisible = false;
            EmptyPanel.IsVisible = true;
            JumpButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
            PreviewCardButton.IsEnabled = service != null;

            SearchBox.TextChanged += OnSearchChanged;
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            Tabs.SelectionChanged += OnTabSelectionChanged;
            Opened += OnOpened;
            Closing += OnClosing;
            Closed += OnClosedDetach;

            // 科目名单可能早就存在设置里了；课表还没加载时先退回可编辑文本框，打开窗口时再对一次。
            RefreshSubjectsSafe();
            UpdateAbout();

            // 台账可能是服务启动时就 Load 好的，构造完就该决定“导出记录”能不能点，
            // 不能等 Opened 才第一次判断（否则空台账的提示会晚一拍）。
            UpdateExportState();

            // 老师要求“跟正常软件一样”：尺寸和位置在关窗口时记住，下次打开接着用。
            ApplySavedGeometry();
            // 存下来的坐标可能落在已经拔掉的副屏上：这时候窗口再也看不见，必须先拉回真实屏幕。
            ClampToVisibleScreen();
        }

        // ---- 控制面板底色（老师要求：不要亚克力，就是一块实心颜色，且能改） ----

        /// <summary>
        /// 把设置里的 #AARRGGBB / #RRGGBB 解析成画刷；解析不了就退回兜底色。
        /// 老师手改配置文件写错一位也不能让窗口变成透明（那样看起来像程序崩了）。
        /// </summary>
        static IBrush BrushFrom(string hex, IBrush fallback)
        {
            string s = (hex ?? "").Trim();
            if (s.Length == 0) return fallback;
            if (!s.StartsWith("#", StringComparison.Ordinal)) s = "#" + s;

            // #RRGGBB 与 #AARRGGBB 都按 Avalonia 的口径解析（8 位时 Alpha 在最前面）。
            // 这也是 SyncSettings.PanelBackgroundColor 注释里写的口径，以及设置页 ColorPicker
            // 写回的口径（HexColorConverter 输出恒为 #AARRGGBB），三者必须一致。
            Color color;
            if (!Color.TryParse(s, out color)) return fallback;
            return new SolidColorBrush(color);
        }

        static double RelativeLuminance(Color c)
        {
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        }

        /// <summary>
        /// 底色是老师自己配色，深浅都可能：底色偏暗时把主题切到暗色，免得深底深字看不清。
        /// </summary>
        internal void ApplyPanelBackground()
        {
            var brush = BrushFrom(settings.PanelBackgroundColor, new SolidColorBrush(Colors.White));
            Background = brush;
            var solid = brush as ISolidColorBrush;
            if (solid != null)
            {
                // 阈值 0.45：更暗的底色（#6B7280 以下）用浅色正文，更亮的用深色正文。
                // 0.45 是二分点：中灰 #808080 亮度 0.50 判为浅底，深字对比度约 8.8:1；
                // 若判成深底，白字对 #808080 只有约 3.9:1，反而更差。
                bool dark = RelativeLuminance(solid.Color) < 0.45;
                var want = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                if (RequestedThemeVariant != want)
                {
                    RequestedThemeVariant = want;

                    // 光切变体不够：FluentAvalonia 的设置卡片（SettingsExpander 那一层层）是
                    // 启动时按当时的主题变体解析好、随后就冻住的，窗口上改 RequestedThemeVariant
                    // 不会让它们重算 —— 结果就是深底上出现「浅灰卡片 + 浅色正文」，白字压浅底。
                    // 往窗口资源里塞一个换过值的无关键，会触发一次 ResourceChanged，
                    // 那一批画刷就按新变体重新解析（实测：卡片底色由 #B3FFFFFF 变为深色档）。
                    // 必须排到下一轮：变体本身是异步传播的，同一帧里重算仍然会读到旧变体。
                    Avalonia.Threading.Dispatcher.UIThread.Post(NudgeThemeResources);
                }
            }
        }

        /// <summary>
        /// 强迫一次资源重解析，让跟随主题的资源画刷按当前变体重算。
        /// 只是换一个无关键的值，不参与任何绑定，出错也无所谓。
        /// </summary>
        void NudgeThemeResources()
        {
            try
            {
                Resources["PanelColorThemeNudge"] = Guid.NewGuid().ToString("N");
            }
            catch
            {
                // 资源字典不可写时就算了，退化成「卡片跟随宿主主题」的老行为。
            }
        }

        void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e == null || e.PropertyName != nameof(SyncSettings.PanelBackgroundColor)) return;

            // 配置也可能是宿主在别的线程载入的：碰窗口属性必须先回到 UI 线程。
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { ApplyPanelBackground(); } catch { }
                });
                return;
            }

            try
            {
                ApplyPanelBackground();
            }
            catch (Exception ex)
            {
                Log("panel background failed: " + ex.Message);
            }
        }

        void OnOpened(object? sender, EventArgs e)
        {
            // 打开时读一次台账（构造时不读，老师还在拖卡片时别做多余的事）。
            Reload();
            // 构造那一刻窗口还没交给平台层，屏幕信息可能不全；真正显示出来后再夹一次。
            ClampToVisibleScreen();
            StartLiveRefresh();
            SearchBox.Focus();
            Log("opened");
        }

        /// <summary>
        /// 台账在放映中是每秒都可能追加一条的，而面板是老师盯着看的窗口，不能停在打开那一刻。
        /// 两秒补一次、只在「课件库」页签并且窗口可见时跑，要不要真的刷新由 <see cref="RefreshLive"/> 判断。
        /// </summary>
        void StartLiveRefresh()
        {
            try
            {
                if (liveTimer == null)
                {
                    liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    liveTimer.Tick += OnLiveTick;
                }
                liveTimer.Start();
            }
            catch (Exception ex) { Log("live timer start failed: " + ex.Message); }
        }

        void OnLiveTick(object? sender, EventArgs e)
        {
            // 定时器回调里抛异常就是未观察的 UI 线程异常，会带崩宿主。
            try { RefreshLive(); }
            catch (Exception ex) { Log("live refresh failed: " + ex.Message); }
        }

        /// <summary>
        /// 增量刷新：记录数没变就什么都不做。多出新课件时整栏重排（Rebuild 会保住选中项），
        /// 只是同一份课件又记了一节时只更新右侧详情和状态条 —— 不动左栏，老师的滚动位置和选中项都不跳。
        /// </summary>
        internal void RefreshLive()
        {
            if (!IsVisible) return;
            if (Tabs.SelectedIndex != 0) return;   // 设置页/关于页不需要跟着台账重排

            int records = RecordCount();
            int files = FileCount();
            if (records == liveRecords && files == liveFiles) return;

            bool filesChanged = files != liveFiles;
            liveRecords = records;
            liveFiles = files;

            if (filesChanged)
            {
                Reload();
                return;
            }

            RefreshSelectedDetail();
            UpdateStatus(shownCount);
            UpdateAbout();
        }

        /// <summary>
        /// 右侧详情的增量刷新：同一份课件又记了一节，“上次停在第 N 页”要跟着变。
        /// 历史列表只在内容真的变了才重新赋值，免得把老师的滚动位置顶回顶部。
        /// </summary>
        void RefreshSelectedDetail()
        {
            if (selectedRow == null) return;
            var item = selectedRow.Item;
            var history = PanelData.History(ledger, item, PanelData.MaxHistoryRows);
            int page = history.Count > 0 ? history[0].Slide : 0;

            selectedPage = page;
            DetailPage.Text = PanelData.PageText(selectedPage);
            DetailTotal.Text = PanelData.TotalText(PanelData.TotalSlides(ledger, item));
            DetailLast.Text = PanelData.LastText(selectedPage);
            JumpButton.IsEnabled = true;   // 选中即允许点，页码缺失由 RequestJump 给提示，不靠置灰装死

            var old = HistoryList.ItemsSource as List<PanelHistoryRow>;
            bool same = old != null && old.Count == history.Count;
            for (int i = 0; same && i < history.Count; i++)
            {
                same = string.Equals(old![i].TimeText, history[i].TimeText, StringComparison.Ordinal)
                    && string.Equals(old[i].PageText, history[i].PageText, StringComparison.Ordinal)
                    && string.Equals(old[i].DurationText, history[i].DurationText, StringComparison.Ordinal);
            }
            if (same) return;

            HistoryList.ItemsSource = history;
            HistoryEmpty.IsVisible = history.Count == 0;
        }

        /// <summary>
        /// 关窗时把挂出去的回调全摘掉：定时器不停的话，关掉的窗口会一直每秒跑一次；
        /// 事件订阅同样不留。窗口对象本身是否还被别处引用我们管不了，但至少不能再拖着这些。
        /// </summary>
        void OnClosedDetach(object? sender, EventArgs e)
        {
            try { settings.PropertyChanged -= OnSettingsChanged; } catch { }
            try
            {
                closed = true;
                if (liveTimer != null)
                {
                    liveTimer.Stop();
                    liveTimer.Tick -= OnLiveTick;
                    liveTimer = null;
                }
                SearchBox.TextChanged -= OnSearchChanged;
                Tabs.SelectionChanged -= OnTabSelectionChanged;
                Opened -= OnOpened;
                Closing -= OnClosing;
                Closed -= OnClosedDetach;
                RemoveHandler(KeyDownEvent, OnWindowKeyDown);
            }
            catch (Exception ex) { Log("panel detach failed: " + ex.Message); }
        }

        /// <summary>切页签：不重新读台账；进设置页时重新对一遍课表科目和外观摘要。</summary>
        void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (Tabs.SelectedIndex == 1)
            {
                RefreshSubjectsSafe();
                try { Appearance.Refresh(); } catch { }
                // 进设置页时再对一次底色：官方设置页那边改完，这个窗口可能还没收到事件
                // （比如面板开着、老师在另一个窗口改完回来）。
                ApplyPanelBackground();
                return;
            }

            // 关于页那句“共 N 条记录”是构造时算出来的，切过来的时候台账可能又记了几节。
            if (Tabs.SelectedIndex == 2) UpdateAbout();
        }

        /// <summary>重新从内存台账生成左栏和状态条。刷新按钮、打开窗口、测试都走这里。</summary>
        public void Reload()
        {
            if (loading) return;
            loading = true;
            try
            {
                CardState? st = null;
                try { st = stateProvider != null ? stateProvider() : null; } catch { }
                string key = "";
                if (st != null && st.Files != null)
                {
                    for (int i = 0; i < st.Files.Count; i++)
                    {
                        var f = st.Files[i];
                        if (f != null && f.IsCurrent) { key = f.Key; break; }
                    }
                }

                // maxHistory = 1：左栏只需要“第 N 页 · 3 小时前”，完整历史由 PanelData.History 现读。
                currentKey = key;
                allFiles = CardFileList.Build(ledger, currentKey, 500, 1);
                Rebuild();
                UpdateAbout();
                // 刷新入口也刷一次底色：配置文件被外部改过（导入配置、宿主重载设置）时，
                // PropertyChanged 未必走到这里，刷新一下正好兜住。
                ApplyPanelBackground();
            }
            catch (Exception ex)
            {
                Log("reload failed: " + ex.Message);
            }
            finally
            {
                loading = false;
            }
        }

        /// <summary>按搜索框内容过滤 + 分组，重建左栏；尽量保持原来选中的那一行。</summary>
        void Rebuild()
        {
            string? keepKey = selectedRow?.Item.Key ?? null;
            var filtered = PanelData.Search(allFiles, SearchBox.Text);
            var grouped = PanelData.Group(filtered);

            groups.Clear();
            PanelRow? restore = null;
            foreach (var g in grouped)
            {
                var rows = new List<PanelRow>();
                foreach (var f in g.Items)
                {
                    var row = new PanelRow(f, g.Subject, string.Equals(f.Key, currentKey, StringComparison.OrdinalIgnoreCase));
                    rows.Add(row);
                    if (keepKey != null && string.Equals(f.Key, keepKey, StringComparison.Ordinal)) restore = row;
                }
                groups.Add(new PanelGroup(g.Subject, rows));
            }

            if (restore != null) Select(restore);
            else if (selectedRow != null) ClearSelection();

            // 首次打开 / 搜索后原选中项不在结果里：自动选第一份，右侧不至于一片空白。
            if (selectedRow == null && groups.Count > 0 && groups[0].Rows.Count > 0)
                Select(groups[0].Rows[0]);

            UpdateEmptyState(filtered.Count);
            shownCount = filtered.Count;
            UpdateStatus(shownCount);

            // 增量刷新的基准：这次重建看到的就是“当前已知”的台账规模。
            liveRecords = RecordCount();
            liveFiles = FileCount();
        }

        void UpdateEmptyState(int filteredCount)
        {
            if (selectedRow != null)
            {
                DetailPanel.IsVisible = true;
                EmptyPanel.IsVisible = false;
                return;
            }
            DetailPanel.IsVisible = false;
            EmptyPanel.IsVisible = true;
            EmptyTitle.Text = filteredCount == 0 && allFiles.Count > 0
                ? "没有匹配的课件"
                : "从左侧选一份课件";
        }

        void Select(PanelRow row)
        {
            if (selectedRow != null) selectedRow.IsSelected = false;
            selectedRow = row;
            row.IsSelected = true;

            var item = row.Item;
            var history = PanelData.History(ledger, item, PanelData.MaxHistoryRows);
            selectedPage = history.Count > 0 ? history[0].Slide : 0;

            DetailName.Text = item.DisplayName;
            DetailMeta.Text = string.IsNullOrEmpty(item.FolderHint)
                ? row.Subject
                : row.Subject + " · " + item.FolderHint;
            DetailPage.Text = PanelData.PageText(selectedPage);
            DetailTotal.Text = PanelData.TotalText(PanelData.TotalSlides(ledger, item));
            DetailLast.Text = PanelData.LastText(selectedPage);
            HistoryList.ItemsSource = history;
            HistoryEmpty.IsVisible = history.Count == 0;
            // 两个按钮一律可点：能不能跳、文件夹在不在，由点击后的提示说清楚。
            // 以前这里按 selectedPage / item.Folder 置灰，老师点到灰按钮什么反应都没有，
            // 和“按钮坏了”一模一样（2026-10-31 真机反馈）。
            JumpButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
            DetailPanel.IsVisible = true;
            EmptyPanel.IsVisible = false;
        }

        void ClearSelection()
        {
            if (selectedRow != null) selectedRow.IsSelected = false;
            selectedRow = null;
            selectedPage = 0;
            HistoryList.ItemsSource = null;
            HistoryEmpty.IsVisible = false;
            JumpButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
        }

        void UpdateStatus(int shown)
        {
            StatusText.Text = PanelData.StatusLine(ledger.Root, RecordCount())
                + " · 当前列出 " + shown.ToString(CultureInfo.InvariantCulture) + " 份";

            // 记录数会随刷新变化，按钮的可用态跟着状态条一起更新，不另找时机。
            UpdateExportState();
        }

        /// <summary>台账里的记录总条数（关于页和状态条共用）。</summary>
        int RecordCount()
        {
            int records = 0;
            if (ledger.ByFile != null)
            {
                foreach (var kv in ledger.ByFile)
                {
                    if (kv.Value != null) records += kv.Value.Count;
                }
            }
            return records;
        }

        /// <summary>台账里有几份课件（按文件键算，和左栏的“份”是同一个口径）。</summary>
        int FileCount()
        {
            try
            {
                var byFile = ledger.ByFile;
                return byFile == null ? 0 : byFile.Count;
            }
            catch { return 0; }
        }

        // ---- 导出记录（CSV） -------------------------------------------------------
        //
        // 导出的是“整本台账”，不是当前搜索结果：老师要的是能交给教务或自己归档的完整表格。
        // 拼表、转义、BOM 全在 Core\LedgerCsv.cs 的纯函数里，这里只负责对话框、写盘和状态条。

        /// <summary>顶部“导出记录”：选路径 → 写 UTF-8 BOM 的 CSV → 状态条报结果。</summary>
        async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            if (exporting) return;

            // 先拍快照再弹对话框：对话框是异步的，这段时间老师可能点了刷新、服务也可能又记了一节；
            // 导出的应该是“按下按钮那一刻”的台账，而不是选了路径之后的。
            List<SessionRecord> records;
            try
            {
                records = LedgerCsv.Collect(ledger);
            }
            catch (Exception ex)
            {
                ShowHint("导出失败：" + ex.Message);
                return;
            }

            if (records.Count == 0)
            {
                // 按钮本来就该是禁用的（UpdateExportState）；这里再兜一次，
                // 防止台账在窗口开着的时候被清空、按钮状态还没跟上。
                ShowEmptyHint();
                return;
            }

            exporting = true;
            ExportButton.IsEnabled = false;
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top == null)
                {
                    ShowHint("导出失败：找不到窗口宿主，无法打开保存对话框");
                    return;
                }

                var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "导出记录",
                    SuggestedFileName = LedgerCsv.SuggestedFileName(DateTime.Now),
                    DefaultExtension = "csv",
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("CSV 表格（Excel 可打开）")
                        {
                            Patterns = new[] { "*.csv" }
                        }
                    }
                });

                // 老师点了取消：什么都不做，连状态条都不动（不能把上一次的结果擦掉）。
                if (file == null) return;

                string? path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    // 云盘/移动设备这类非本地存储拿不到真实路径，File API 写不了，直接说清楚。
                    ShowHint("导出失败：这个位置不能直接写入，请选本机磁盘上的文件夹");
                    return;
                }

                WriteExport(path!, records);
            }
            catch (Exception ex)
            {
                // 对话框不受支持、路径没权限、磁盘满……都收成状态条上一句人话。
                // 绝不能让异常从 async void 冒出去：那是未观察的 UI 线程异常，会带崩宿主。
                Log("export failed: " + ex.Message);
                ShowHint("导出失败：" + ex.Message);
            }
            finally
            {
                exporting = false;
                try { UpdateExportState(); } catch { }
            }
        }

        /// <summary>
        /// 真正落盘的那一步。单独拆出来是因为对话框没法在离线测试里点，
        /// 而“写出来的字节对不对”恰恰是最该验的东西：测试可以反射直接调它。
        /// 同步写：一本台账撑死几百 KB，比异步续体在无消息泵的测试里卡住更省事。
        /// </summary>
        void WriteExport(string path, List<SessionRecord> records)
        {
            File.WriteAllBytes(path, LedgerCsv.ToBytes(records));
            ShowHint(LedgerCsv.StatusText(records.Count, path));
            Log("exported " + records.Count.ToString(CultureInfo.InvariantCulture)
                + " records to " + path);
        }

        /// <summary>
        /// 按记录数刷新“导出记录”的可用态：0 条禁用，并把“还没有记录”直接摆在状态条上
        /// （禁用按钮在 Avalonia 里不弹 ToolTip，光靠提示语老师看不到）。
        /// 有数据后只撤掉这句空台账提示，不覆盖上一次的“已导出 N 条到 …”。
        /// </summary>
        void UpdateExportState()
        {
            int n = RecordCount();
            bool has = n > 0;

            ExportButton.IsEnabled = has && !exporting;
            try
            {
                ToolTip.SetTip(ExportButton, has
                    ? "把整本台账导出成 CSV（Excel 可直接打开）"
                    : "还没有记录");
            }
            catch { }

            if (!has)
            {
                hintShown = true;
                HintText.Text = "还没有记录";
                HintText.IsVisible = true;
            }
            else if (hintShown)
            {
                // 之前显示的是“还没有记录”这句：有数据了就收起来，
                // 但如果是“已导出 N 条到 …”这类操作结果，留着让老师还能看见。
                hintShown = false;
                HintText.Text = "";
                HintText.IsVisible = false;
            }
        }

        void ShowEmptyHint()
        {
            hintShown = true;
            HintText.Text = "还没有记录";
            HintText.IsVisible = true;
        }

        /// <summary>
        /// 状态条右侧那一行小字：导出结果、跳页反馈都走这里。
        /// 空字符串等于收起，避免留一条空白把状态条撑高。
        /// </summary>
        void ShowHint(string? text)
        {
            hintShown = false;
            HintText.Text = text ?? "";
            HintText.IsVisible = !string.IsNullOrEmpty(HintText.Text);
        }

        /// <summary>
        /// 服务把跳页/开文件夹的最终结果写回面板底部（引擎在工作线程上报“已跳到第 N 页”
        /// 或“打不开 PowerPoint”）。可能从非 UI 线程调用，所以自己 Post 回 UI 线程。
        /// </summary>
        public void ShowStatus(string? text)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowHint(text);
                return;
            }
            Dispatcher.UIThread.Post(() => ShowHint(text));
        }

        /// <summary>关于页：版本号读插件目录里的 manifest.yml，读不到就写“未知”。</summary>
        void UpdateAbout()
        {
            try
            {
                AboutNameText.Text = "课件续页";
                AboutVersionText.Text = "版本 " + ReadPluginVersion();
                AboutDataFolderText.Text = string.IsNullOrWhiteSpace(ledger.Root)
                    ? "（还没有数据目录）"
                    : ledger.Root;
                AboutRecordsText.Text = string.Format(CultureInfo.InvariantCulture, "共 {0} 条记录", RecordCount());
            }
            catch (Exception ex) { Log("about update failed: " + ex.Message); }
        }

        /// <summary>
        /// 从插件程序集旁边的 manifest.yml 读 version。ClassIsland 加载插件时清单和 dll 同目录，
        /// 所以不额外依赖宿主的插件信息接口；文件缺失/格式不对时返回“未知”。
        /// </summary>
        static string ReadPluginVersion()
        {
            try
            {
                string? dir = Path.GetDirectoryName(typeof(ControlPanel).Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    string path = Path.Combine(dir!, "manifest.yml");
                    if (File.Exists(path))
                    {
                        foreach (string line in File.ReadAllLines(path))
                        {
                            string s = line.Trim();
                            if (!s.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) continue;
                            string v = s.Substring("version:".Length).Trim().Trim('"', '\'');
                            if (v.Length > 0) return v;
                        }
                    }
                }
            }
            catch { }
            return "未知";
        }

        void OnSearchChanged(object? sender, TextChangedEventArgs e)
        {
            if (loading) return;
            // 搜索是纯内存过滤：输入一个字就重排一次，不重新读台账。
            Rebuild();
        }

        // ---- 管理模式：管理 / 全选 / 删除选中 / 完成 ----

    void OnManageClick(object? sender, RoutedEventArgs e)
    {
        SetManageMode(true);
    }

    void OnManageDoneClick(object? sender, RoutedEventArgs e)
    {
        SetManageMode(false);
    }

    void SetManageMode(bool on)
    {
        manageMode = on;
        if (!on)
        {
            // 退出管理态时清掉勾选，避免下次进来还留着上次的选中项
            foreach (var row in AllRows()) row.IsPicked = false;
            DisarmDelete();
        }
        SelectAllCheck.IsChecked = false;
        SelectAllCheck.IsVisible = on;
        ManageTitle.Text = on ? "勾选课件" : "课件库";
        ManageButton.IsVisible = !on;
        ManageDoneButton.IsVisible = on;
        DeleteButton.IsVisible = on;
        foreach (var row in AllRows()) row.ManageMode = on;
        UpdateDeleteButton();
    }

    IEnumerable<PanelRow> AllRows()
    {
        foreach (var group in groups)
        {
            if (group.Rows == null) continue;
            foreach (var row in group.Rows) if (row != null) yield return row;
        }
    }

    List<PanelRow> PickedRows()
    {
        var list = new List<PanelRow>();
        foreach (var row in AllRows()) if (row.IsPicked) list.Add(row);
        return list;
    }

    void UpdateDeleteButton()
    {
        int n = PickedRows().Count;
        DeleteButton.IsEnabled = n > 0;
        DeleteButton.Content = deleteArmed ? "确认删除 " + n + " 份" : "删除选中";
    }

    void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        bool want = SelectAllCheck.IsChecked == true;
        foreach (var row in AllRows()) row.IsPicked = want;
        UpdateDeleteButton();
    }

    void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        var picked = PickedRows();
        if (picked.Count == 0) return;

        // 两步确认：第一次点变成「确认删除 N 份」，十秒内不点就自己复位，免得手滑。
        if (!deleteArmed)
        {
            deleteArmed = true;
            UpdateDeleteButton();
            ShowHint("再点一次「确认删除」就真删了，删的是台账记录，课件文件不动");
            if (deleteArmTimer == null)
            {
                deleteArmTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                deleteArmTimer.Tick += (_, __) => DisarmDelete();
            }
            deleteArmTimer.Stop();
            deleteArmTimer.Start();
            return;
        }

        var keys = new List<string>();
        foreach (var row in picked)
        {
            string path = row.Item?.FilePath ?? "";
            if (path.Length > 0) keys.Add(path);
        }
        int removed = 0;
        try
        {
            removed = ledger.RemoveMany(keys);
        }
        catch (Exception ex)
        {
            Log("panel delete failed: " + ex.Message);
            ShowHint("删除失败：" + ex.Message);
            DisarmDelete();
            return;
        }
        DisarmDelete();
        SetManageMode(false);
        Rebuild();
        ClearSelection();
        UpdateStatus(0);
        ShowHint("已删除 " + removed + " 条记录");
    }

    void DisarmDelete()
    {
        deleteArmed = false;
        if (deleteArmTimer != null) deleteArmTimer.Stop();
        UpdateDeleteButton();
    }

    // ---- 配置导入 / 导出（只含设置，不含台账） ----

    async void OnExportSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            string json = SettingsTransfer.ToJson(settings);
            if (string.IsNullOrEmpty(json))
            {
                ShowHint("设置导出失败：序列化结果为空");
                return;
            }
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null)
            {
                ShowHint("导出失败：拿不到文件对话框");
                return;
            }
            var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "导出课件续页设置",
                SuggestedFileName = SettingsTransfer.SuggestedFileName(DateTime.Now),
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } },
                },
            });
            string? path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            File.WriteAllText(path, json, new UTF8Encoding(false));
            ShowHint("设置已导出到 " + path);
        }
        catch (Exception ex)
        {
            Log("panel export settings failed: " + ex.Message);
            ShowHint("导出失败：" + ex.Message);
        }
    }

    async void OnImportSettingsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null)
            {
                ShowHint("导入失败：拿不到文件对话框");
                return;
            }
            var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "导入课件续页设置",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } },
                },
            });
            if (files == null || files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            string? error = SettingsTransfer.Apply(settings, text);
            if (!string.IsNullOrEmpty(error))
            {
                ShowHint("导入失败：" + error);
                return;
            }
            Reload();
            ShowHint("设置已导入，卡片已按新设置刷新");
        }
        catch (Exception ex)
        {
            Log("panel import settings failed: " + ex.Message);
            ShowHint("导入失败：" + ex.Message);
        }
    }

    // ---- 历史行：点哪一行就跳到那一页 ----

    void OnHistoryRowPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (sender as Control)?.DataContext as PanelHistoryRow;
        if (row == null) return;
        int page = row.Slide;
        DetailPage.Text = PanelData.PageText(page);
        DetailLast.Text = PanelData.LastText(page);
        JumpButton.IsEnabled = true;   // 页码无效时由 RequestJump 给提示，不靠置灰装死
        RequestJump(row.FilePath, row.FilePath, page);
    }

    void OnRefreshClick(object? sender, RoutedEventArgs e)
        {
            Reload();
            Log("refreshed");
        }

        void OnRowPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border border || border.Tag is not PanelRow row) return;
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            if (ReferenceEquals(row, selectedRow)) return;
            Select(row);
            e.Handled = true;
        }

        void OnOpenFolderClick(object? sender, RoutedEventArgs e)
        {
            if (selectedRow == null)
            {
                ShowHint("先在左边选一份课件");
                return;
            }
            string path = selectedRow.Item.FilePath ?? "";
            string name = selectedRow.Name;
            // Item.Folder 是左栏给老师看的省略路径（"…\高一\课件"），不能当真实目录用：
            // Directory.Exists 永远 false，点「打开文件夹」只会得到一句“文件夹不在了”。
            string folder = RealFolderOf(path);
            if (folder.Length == 0)
            {
                ShowHint(SeewoBridge.IsSeewoKey(path)
                    ? "希沃课件没有本机文件夹，台账里只有课件名"
                    : "这份课件没有可打开的文件夹：" + name);
                return;
            }
            if (!Directory.Exists(folder))
            {
                Log("open folder missing: " + folder);
                ShowHint("找不到这份课件的文件夹，可能已被移动：" + folder);
                return;
            }
            var handler = openFolder;
            if (handler == null)
            {
                ShowHint("打开文件夹的通道没接上（插件服务还没就绪），关掉面板重开试试");
                return;
            }
            try
            {
                // 真机上 app.log 就靠这一行定位：panel: open folder <目录>
                Log("open folder " + folder);
                ShowHint("正在打开文件夹…");
                handler(folder);
            }
            catch (Exception ex)
            {
                Log("open folder failed: " + ex.Message);
                ShowHint("打开文件夹失败：" + ex.Message);
            }
        }

        /// <summary>课件文件真正所在的目录；希沃伪键/只有文件名的旧记录没有目录，返回空串。</summary>
        static string RealFolderOf(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return "";
            if (SeewoBridge.IsSeewoKey(filePath)) return "";
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(dir)) return "";
                return Path.GetFullPath(dir);
            }
            catch { return ""; }
        }

        /// <summary>
        /// 跳页的统一入口：右栏「立即跳转」和历史行点击都走这里，校验与提示只写一份。
        /// page &lt;= 0 表示这一条记录没记下停在哪一页，只提示不跳。
        /// 希沃伪键不在面板拦下：交给引擎按 SeewoBridge.GotoSlide 走（见 SessionEngine.DoJump）。
        /// </summary>
        void RequestJump(string path, string name, int page)
        {
            if (page <= 0)
            {
                ShowHint("这条记录没记下停在哪一页，只能点「打开文件夹」自己找");
                return;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowHint("这条记录没留下课件路径：" + name);
                return;
            }
            var handler = JumpRequested;
            if (handler == null)
            {
                // 离线/服务还没起来时必须说话：以前这里直接 return，点了等于没点。
                ShowHint("没接上跳页链路（插件服务还没就绪），关掉面板重开试试");
                return;
            }
            if (!SeewoBridge.IsSeewoKey(path) && !File.Exists(path))
            {
                // 课件被移走/删掉了：引擎拿到一个不存在的路径只会静默失败，老师会以为是面板坏了。
                ShowHint("找不到这份课件的文件（" + name + "），可能已被移动");
                return;
            }
            try
            {
                // 真机上 app.log 就靠这一行定位：panel: jump -> <文件> 第 N 页
                Log("jump -> " + path + " 第 " + page.ToString(CultureInfo.InvariantCulture) + " 页");
                // 别说“已请求”：服务/引擎答不答应由它们回报（ControlPanel.ShowStatus），
                // 未上课时引擎不存在，先报成功等于骗老师（2026-10-31 真机反馈）。
                ShowHint("正在跳转…");
                handler(path, page);
            }
            catch (Exception ex)
            {
                // 服务自己会兜底，但面板不能假设这一点：跳页出问题也要有话说，更不能让异常冒到 UI 线程。
                Log("jump failed: " + ex.Message);
                ShowHint("跳页失败：" + ex.Message);
            }
        }

        void OnJumpClick(object? sender, RoutedEventArgs e)
        {
            if (selectedRow == null)
            {
                ShowHint("先在左边选一份课件");
                return;
            }
            RequestJump(selectedRow.Item.FilePath ?? "", selectedRow.Name, selectedPage);
        }

        void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                // 老师的要求：Esc 先退出全屏，再按一次才关窗口。
                if (WindowState == WindowState.FullScreen)
                {
                    ToggleFullScreen();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
            {
                // Ctrl+F 永远落到课件库的搜索框：在设置/关于页按也要能直接搜课件。
                if (Tabs.SelectedIndex != 0) Tabs.SelectedIndex = 0;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }

        void OnFullScreenClick(object? sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        /// <summary>F11 / 底部“全屏”按钮：在窗口态和全屏态之间切换。</summary>
        void ToggleFullScreen()
        {
            try
            {
                if (WindowState == WindowState.FullScreen)
                {
                    // 退出全屏要回到进全屏之前的样子。老师如果是从最大化按的 F11，
                    // 退全屏却掉回普通窗口，等于顺手把他的窗口改小了。
                    WindowState = beforeFullScreen == WindowState.FullScreen
                        ? WindowState.Normal
                        : beforeFullScreen;
                    return;
                }
                beforeFullScreen = WindowState;
                WindowState = WindowState.FullScreen;
            }
            catch (Exception ex) { Log("fullscreen failed: " + ex.Message); }
        }

        /// <summary>恢复上次的窗口尺寸与位置；没记录过就用 axaml 里的默认值（1000×680 居中）。</summary>
        void ApplySavedGeometry()
        {
            try
            {
                if (settings == null) return;
                if (settings.PanelWidth >= 480 && settings.PanelHeight >= 360)
                {
                    Width = settings.PanelWidth;
                    Height = settings.PanelHeight;
                }
                // 0,0 视为“没记录过”：默认值就是 0，分不清“老师把窗口拖到左上角”和“从没存过”，
                // 按未记录处理更稳妥（居中打开总比贴着任务栏好）。
                bool havePos = settings.PanelLeft != 0 || settings.PanelTop != 0;
                if (havePos && settings.PanelLeft > -20000 && settings.PanelTop > -20000)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = new PixelPoint(settings.PanelLeft, settings.PanelTop);
                }
            }
            catch (Exception ex) { Log("geometry restore failed: " + ex.Message); }
        }

        /// <summary>
        /// 把窗口拉回一块真实存在的屏幕里。掉到屏幕外有两种常见来源：
        /// 上一节课在副屏上关的窗口、这次教室没有副屏；或者换了分辨率/缩放的投影仪，存的尺寸比工作区还大。
        /// 屏幕信息还没准备好（构造得太早）时直接返回，Opened 里会再调一次。
        /// </summary>
        void ClampToVisibleScreen()
        {
            try
            {
                if (WindowState != WindowState.Normal) return;   // 最大化/全屏由系统管尺寸，别去改
                var screens = Screens;
                if (screens == null) return;
                var all = screens.All;
                if (all == null || all.Count == 0) return;

                double scale = 1.0;
                Screen? target = null;
                try
                {
                    target = screens.ScreenFromPoint(Position);
                    if (target == null) target = screens.ScreenFromWindow(this);
                    if (target != null && target.Scaling > 0) scale = target.Scaling;
                }
                catch { }

                int w = ToPixels(double.IsNaN(Width) ? 1000 : Width, scale);
                int h = ToPixels(double.IsNaN(Height) ? 680 : Height, scale);

                if (target == null)
                {
                    // 存的坐标不在任何屏幕上（副屏被拔掉了）：挑一块与窗口矩形有交集的屏。
                    var probe = new PixelRect(Position, new PixelSize(Math.Max(1, w), Math.Max(1, h)));
                    foreach (var s in all)
                    {
                        if (s == null || s.WorkingArea.Width <= 0 || s.WorkingArea.Height <= 0) continue;
                        if (s.WorkingArea.Intersects(probe)) { target = s; break; }
                    }
                }
                if (target == null) target = screens.Primary;
                if (target == null) target = all[0];
                if (target == null) return;

                var work = target.WorkingArea;
                if (work.Width <= 0 || work.Height <= 0) return;
                if (target.Scaling > 0) scale = target.Scaling;

                // 尺寸：不超过工作区，也不低于 MinWidth/MinHeight（缩放换算是像素，宽高是 DIP）。
                int minW = ToPixels(double.IsNaN(MinWidth) ? 720 : MinWidth, scale);
                int minH = ToPixels(double.IsNaN(MinHeight) ? 480 : MinHeight, scale);
                int wantW = Math.Min(Math.Max(w, minW), Math.Max(work.Width, minW));
                int wantH = Math.Min(Math.Max(h, minH), Math.Max(work.Height, minH));
                if (Math.Abs(wantW - w) > 2) Width = wantW / (scale > 0 ? scale : 1.0);
                if (Math.Abs(wantH - h) > 2) Height = wantH / (scale > 0 ? scale : 1.0);

                // 位置只在我们自己恢复过坐标时才动：居中/由系统摆位的情况交给系统，别抢。
                if (WindowStartupLocation != WindowStartupLocation.Manual) return;

                // 标题栏至少要露出来一点，老师才抓得住窗口（左边 120px、上面 40px）。
                int keepX = Math.Min(120, Math.Max(24, wantW / 3));
                int keepY = Math.Min(40, Math.Max(12, wantH / 4));
                int nx = Math.Max(work.X - wantW + keepX, Math.Min(Position.X, work.Right - keepX));
                int ny = Math.Max(work.Y, Math.Min(Position.Y, work.Bottom - keepY));
                if (nx != Position.X || ny != Position.Y)
                {
                    Position = new PixelPoint(nx, ny);
                }
            }
            catch (Exception ex) { Log("geometry clamp failed: " + ex.Message); }
        }

        /// <summary>DIP → 屏幕像素（按屏幕缩放）。用于和 WorkingArea 这类像素矩形比较。</summary>
        static int ToPixels(double dip, double scale)
        {
            if (double.IsNaN(dip) || double.IsInfinity(dip) || dip <= 0) return 0;
            double px = dip * (scale > 0 ? scale : 1.0);
            if (px > int.MaxValue / 4) px = int.MaxValue / 4;
            return (int)Math.Round(px);
        }

        void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            SaveGeometry();
        }

        /// <summary>把当前尺寸和位置写回设置；最大化/全屏时不覆盖（否则还原后尺寸会变成屏幕大小）。</summary>
        void SaveGeometry()
        {
            try
            {
                if (settings == null || WindowState != WindowState.Normal) return;
                // 关窗前宽度可能还没经过布局（程序刚开就关），NaN 写进设置会变成 0，下次就不敢恢复。
                if (double.IsNaN(Width) || double.IsNaN(Height) || Width <= 0 || Height <= 0) return;
                settings.PanelWidth = (int)Math.Round(Width);
                settings.PanelHeight = (int)Math.Round(Height);
                // 只有我们自己摆过位置时才记坐标：居中打开时 Position 还不是真实值。
                if (WindowStartupLocation == WindowStartupLocation.Manual)
                {
                    settings.PanelLeft = Position.X;
                    settings.PanelTop = Position.Y;
                }
            }
            catch (Exception ex) { Log("geometry save failed: " + ex.Message); }
        }

        // ---- 设置页：学科课件文件夹 ------------------------------------------------

        /// <summary>
        /// 课表里的科目名单，给每行的下拉用。读的是 ClassIsland 档案里的 Subject.Name，
        /// 另外把用户已经填过的名字也并进来——课表里删掉的旧配置不能因此变成空白。
        /// 写法照抄 Views\SettingsPages\SlideSyncSettingsPage.xaml.cs 的 RefreshSubjects。
        /// </summary>
        void RefreshSubjects()
        {
            var fromProfile = new List<string>();
            bool loaded = false;
            var profiles = IAppHost.TryGetService<IProfileService>();
            if (profiles?.Profile?.Subjects != null)
            {
                loaded = true;
                foreach (var subject in profiles.Profile.Subjects.Values)
                {
                    var name = subject?.Name?.Trim();
                    if (!string.IsNullOrEmpty(name) && !fromProfile.Contains(name))
                    {
                        fromProfile.Add(name);
                    }
                }
            }

            // 已配置、但课表里已经没有的科目也要能显示出来；不 Trim，ComboBox 按字符串精确匹配选中项。
            var all = new List<string>(fromProfile);
            foreach (var folder in settings.SubjectFolders)
            {
                var name = folder?.Subject;
                if (!string.IsNullOrWhiteSpace(name) && !all.Contains(name))
                {
                    all.Add(name);
                }
            }
            // 2026-10-25 用户第 9 条：配色表里已经填过的科目也要进下拉，否则那一行显示成空行。
            foreach (var color in settings.SubjectColors)
            {
                var name = color?.Subject;
                if (!string.IsNullOrWhiteSpace(name) && !all.Contains(name))
                {
                    all.Add(name);
                }
            }

            bool useDropdown = loaded && fromProfile.Count > 0;

            // 只增不删：清空 Options 的瞬间 ComboBox 会把 SelectedItem 置空，等于把旧配置写没。
            foreach (var name in all)
            {
                if (!SubjectOptions.Contains(name)) SubjectOptions.Add(name);
            }

            ApplySubjectInput(useDropdown);
        }

        /// <summary>
        /// 调 RefreshSubjects 的兜底：面板可能在没有宿主的进程里构造（离线验收），
        /// IAppHost/IProfileService 所在程序集加载失败时退回手填框，窗口不能崩。
        /// </summary>
        void RefreshSubjectsSafe()
        {
            try { RefreshSubjects(); }
            catch { ApplySubjectInput(false); }
        }

        void ApplySubjectInput(bool useDropdown)
        {
            foreach (var folder in settings.SubjectFolders)
            {
                if (folder == null) continue;
                folder.Options = SubjectOptions;
                folder.UseDropdown = useDropdown;
            }
            // 2026-10-25 用户第 9 条：科目配色表那一列同样挂共享的下拉选项。
            foreach (var color in settings.SubjectColors)
            {
                if (color == null) continue;
                color.Options = SubjectOptions;
                color.UseDropdown = useDropdown;
            }
        }

        /// <summary>路径列的“选择…”：用 Avalonia 的 StorageProvider，面板不需要宿主的文件选择器。</summary>
        async void OnPickSubjectFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Button b || b.Tag is not SubjectFolder folder) return;
                var top = TopLevel.GetTopLevel(this);
                if (top == null) return;

                var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择这个科目的课件文件夹",
                    AllowMultiple = false
                });

                // 对话框是异步的：等它回来的时候，这一行可能被删了、窗口也可能已经关了。
                // 往一个已经不在列表里的对象写路径，只会写进一条谁也不看的配置。
                if (closed || !settings.SubjectFolders.Contains(folder)) return;
                if (picked == null || picked.Count <= 0) return;

                var path = picked[0].TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path)) folder.Folder = path!;
            }
            catch (Exception ex)
            {
                // 平台不支持文件夹选择器、或取路径失败时不动原值，也不能把窗口带崩。
                Log("pick subject folder failed: " + ex.Message);
            }
        }

        /// <summary>学科文件夹表：加一行。</summary>
        void OnAddSubjectFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                settings.SubjectFolders.Add(new SubjectFolder());
                RefreshSubjectsSafe();
            }
            catch { }
        }

        /// <summary>学科文件夹表：删掉这一行（按钮的 Tag 里带着那一行）。</summary>
        void OnRemoveSubjectFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button b && b.Tag is SubjectFolder folder)
                {
                    settings.SubjectFolders.Remove(folder);
                }
            }
            catch { }
        }

        /// <summary>科目配色表：加一行（用户 2026-10-25 第 9 条）。</summary>
        void OnAddSubjectColor(object? sender, RoutedEventArgs e)
        {
            try
            {
                settings.SubjectColors.Add(new SubjectColor());
                // 顺手把课表里的科目再对一遍：新行的下拉不能是空的（理由同 OnAddSubjectFolder）。
                RefreshSubjectsSafe();
            }
            catch { }
        }

        /// <summary>科目配色表：删掉这一行（按钮的 Tag 里带着那一行）。删掉的科目就用主题强调色。</summary>
        void OnRemoveSubjectColor(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button b && b.Tag is SubjectColor color)
                {
                    settings.SubjectColors.Remove(color);
                }
            }
            catch { }
        }

        /// <summary>科目配色表：一键恢复内置预设色（清空后由 SyncSettings 重灌默认表）。</summary>
        void OnResetSubjectColors(object? sender, RoutedEventArgs e)
        {
            try
            {
                settings.SubjectColors.Clear();
                settings.SeedDefaultSubjectColors();
                RefreshSubjectsSafe();
            }
            catch { }
        }

        /// <summary>预览卡片：服务在就用它把卡片叫出来（和设置页的按钮同一条链路）。</summary>
        void OnPreviewClick(object? sender, RoutedEventArgs e)
        {
            try { service?.ShowCardNow(); }
            catch (Exception ex) { Log("preview failed: " + ex.Message); }
        }

        // ---- 关于页 ---------------------------------------------------------------

        /// <summary>仓库地址：点开系统浏览器。宿主导航服务拿不到时也还有这一条路。</summary>
        void OnRepoClick(object? sender, RoutedEventArgs e)
        {
            const string url = "https://github.com/KKT446/ClassSlideSync";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { Log("open repo failed: " + ex.Message); }
        }

        /// <summary>打开台账/日志所在的数据目录。</summary>
        void OnAboutOpenFolderClick(object? sender, RoutedEventArgs e)
        {
            string folder = ledger.Root ?? "";
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                if (openFolder != null)
                {
                    openFolder(folder);
                    return;
                }
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add(folder);
                Process.Start(psi);
            }
            catch (Exception ex) { Log("open data folder failed: " + ex.Message); }
        }

        void Log(string message)
        {
            try { settings.LogSink?.Invoke("panel: " + message); } catch { }
        }
    }
}
