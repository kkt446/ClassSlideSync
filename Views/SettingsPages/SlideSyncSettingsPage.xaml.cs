using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Views.SettingsPages;

/// <summary>
/// Settings page shown inside ClassIsland. Every switch binds straight to
/// <see cref="SyncSettings"/>, which saves itself on every change.
/// </summary>
[SettingsPageInfo("classslidesync.settings", "课件续页", "\uE7C3", "\uE7C3")]
public partial class SlideSyncSettingsPage : SettingsPageBase
{
    public Plugin Plugin { get; }

    public SyncSettings Settings { get { return Plugin.Settings; } }

    /// <summary>颜色 / 圆角 / 左右那几个控件用的翻译层，见 <see cref="SlideSyncAppearanceViewModel"/>。</summary>
    public SlideSyncAppearanceViewModel Appearance { get; }

    public SlideSyncSettingsPage(Plugin plugin)
    {
        Plugin = plugin;
        Appearance = new SlideSyncAppearanceViewModel(plugin.Settings, plugin.Service);
        InitializeComponent();
        // XAML 里只给了 x:Name，没写 Click=：这台机器上的 Avalonia XAML 编译器解析不了那个事件
        // 处理器（AVLN3000），改成构造时挂，效果一样且不依赖代码生成。
        if (OpenPanelButton != null) OpenPanelButton.Click += OnOpenControlPanel;
        // 先算好下拉选项，再把 DataContext 挂上去：反过来的话，绑定建立时 Options 还是空的，
        // ComboBox 选不中已有的科目，界面就会显示成空行。
        RefreshSubjects();
        DataContext = this;
        AttachedToVisualTree += (_, _) =>
        {
            // 打开设置页就重算一次：ClassIsland 的主题色是可以中途改的，这里顺势重新探一次。
            try { Plugin.Service?.RecomputeAppearance(); } catch { }
            Appearance.Refresh();
            // 课表可能比设置页晚加载完，每次进来都重新对一遍科目名单。
            RefreshSubjects();
        };
    }

    /// <summary>
    /// 课表里的科目名单，给每行的下拉用。读的是 ClassIsland 档案里的 <c>Subject.Name</c>，
    /// 另外把用户已经填过的名字也并进来——课表里删掉的旧配置不能因此变成空白。
    /// </summary>
    public ObservableCollection<string> SubjectOptions { get; } = new();

    /// <summary>
    /// 科目这一列该显示下拉还是手填框。课表读不出来（没加载、服务缺失、异常）时退回手填框。
    /// 开关写在每一行（<see cref="SubjectFolder.UseDropdown"/>）而不是放在页面上：模板里
    /// $parent[…] 那种写法要 Avalonia 在运行时按程序集名加载类型，插件是动态加载的会直接崩，
    /// 详见 SlideSyncSettingsPage.axaml 里行模板的注释。
    /// </summary>
    void ApplySubjectInput(bool useDropdown)
    {
        foreach (var folder in Settings.SubjectFolders)
        {
            if (folder == null) continue;
            folder.Options = SubjectOptions;
            folder.UseDropdown = useDropdown;
        }
        // 2026-10-25 用户第 9 条：科目配色表那一列也是一样的下拉/手填二选一。
        foreach (var color in Settings.SubjectColors)
        {
            if (color == null) continue;
            color.Options = SubjectOptions;
            color.UseDropdown = useDropdown;
        }
    }

    void RefreshSubjects()
    {
        try
        {
            // ① 课表里已添加的科目：官方途径是 IProfileService.Profile.Subjects（Subject.Name），
            //    见 ClassIsland.Shared.IPC/Abstractions/Services/IPublicProfileService.cs:20 与
            //    ClassIsland.Shared/Models/Profile/Profile.cs:284。
            var fromProfile = new List<string>();
            var loaded = false;
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

            // ② 已配置、但课表里已经没有的科目，也要能显示出来。
            //    这里原样保留（不 Trim）：ComboBox 是按字符串精确匹配选中项的，
            //    存进去的值带空格时，Trim 过的选项反而选不中，看上去就成了空行。
            var all = new List<string>(fromProfile);
            foreach (var folder in Settings.SubjectFolders)
            {
                var name = folder?.Subject;
                if (!string.IsNullOrWhiteSpace(name) && !all.Contains(name))
                {
                    all.Add(name);
                }
            }
            // 配色表里已经填过的科目也要进下拉，否则它那一行会显示成空行。
            foreach (var color in Settings.SubjectColors)
            {
                var name = color?.Subject;
                if (!string.IsNullOrWhiteSpace(name) && !all.Contains(name))
                {
                    all.Add(name);
                }
            }

            var useDropdown = loaded && fromProfile.Count > 0;

            // 只增不删。这里以前是 Clear() 再重填，但 ComboBox 的 SelectedItem 是双向绑定的：
            // 清空的瞬间 SelectedItem 变成 null，等于把 SubjectFolder.Subject 改成了空，
            // 而且立刻被存进 SyncSettings.json——旧配置就这么没了。改成只追加，选中项永远不失效。
            foreach (var name in all)
            {
                if (!SubjectOptions.Contains(name))
                {
                    SubjectOptions.Add(name);
                }
            }

            // 选项备齐之后再把「显示下拉」的开关写到每一行上（行可能是在选项之前建好的）。
            ApplySubjectInput(useDropdown);
        }
        catch
        {
            // 档案还没就绪或者服务拿不到：退化成原来的可编辑文本框，页面不能崩。
            ApplySubjectInput(false);
        }
    }

    /// <summary>
    /// 路径列：用 ClassIsland 自己的文件夹选择器（<c>PlatformServices.FilePickerService</c>），
    /// 和本体设置页里的用法一致，见 ClassIsland/Views/DataTransferPage.axaml.cs:550、557
    /// （<c>TopLevel.GetTopLevel(this)</c> + <c>OpenFoldersPickerAsync(new FolderPickerOpenOptions(){…}, topLevel)</c>）。
    /// 用户取消（返回空列表）时保持原值。
    /// </summary>
    async void OnPickSubjectFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button b || b.Tag is not SubjectFolder folder)
            {
                return;
            }

            // SettingsPageBase 就是 UserControl（ClassIsland.Core/Abstractions/Controls/SettingsPageBase.cs:11），
            // 所以 this 本身是 Visual，可以直接拿 TopLevel。
            var top = TopLevel.GetTopLevel(this);
            if (top == null)
            {
                return;
            }

            var picked = await PlatformServices.FilePickerService.OpenFoldersPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "选择这个科目的课件文件夹",
                    AllowMultiple = false
                }, top);

            if (picked == null || picked.Count <= 0)
            {
                return;
            }

            var path = picked[0];
            if (!string.IsNullOrWhiteSpace(path))
            {
                folder.Folder = path;
            }
        }
        catch
        {
            // 平台不支持（比如某些 Linux 环境）或用户取消时不动原值，也不能把设置页带崩。
        }
    }

    void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (Plugin.Service != null)
        {
            Plugin.Service.ShowCardNow();
        }
    }

    /// <summary>
    /// 新手引导：走 ClassIsland 自带的教程机制（TeachingTip 一步步指），
    /// 内容和「教程中心」里那一条是同一份，见 <see cref="TutorialGuide"/>。
    /// </summary>
    void OnStartTutorialClick(object sender, RoutedEventArgs e)
    {
        if (TutorialGuide.Start())
        {
            GuideHint.IsVisible = false;
            return;
        }

        // 起不来（比如宿主的教程服务不认我们的 JSON）也要说一声，不能让按钮点了像没点。
        GuideHint.Text = "没能启动引导：当前 ClassIsland 版本可能不支持内置教程。"
                       + "可以先看 README 的「怎么用」一节，功能本身不受影响。";
        GuideHint.IsVisible = true;
    }

    /// <summary>学科文件夹表：加一行（用户 2026-10-16 第 1 条）。</summary>
    void OnAddSubjectFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings.SubjectFolders.Add(new SubjectFolder());
            // 新行的科目是空的，但顺手把课表里的科目再对一遍：万一档案是打开设置页之后才加载完的，
            // 这里也能把下拉补上，不用等老师重新进一次设置页。
            RefreshSubjects();
        }
        catch { }
    }

    /// <summary>学科文件夹表：删掉这一行（按钮的 Tag 里带着那一行）。</summary>
    void OnRemoveSubjectFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button b && b.Tag is SubjectFolder folder)
            {
                Settings.SubjectFolders.Remove(folder);
            }
        }
        catch { }
    }

    /// <summary>科目配色表：加一行（用户 2026-10-25 第 9 条）。</summary>
    void OnAddSubjectColor(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings.SubjectColors.Add(new SubjectColor());
            // 顺手把课表里的科目再对一遍：新行的下拉不能是空的（理由同 OnAddSubjectFolder）。
            RefreshSubjects();
        }
        catch { }
    }

    /// <summary>科目配色表：删掉这一行（按钮的 Tag 里带着那一行）。删掉的科目就用主题强调色。</summary>
    void OnRemoveSubjectColor(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button b && b.Tag is SubjectColor color)
            {
                Settings.SubjectColors.Remove(color);
            }
        }
        catch { }
    }

    /// <summary>
    /// 科目配色表：一键恢复内置预设色。清空后由 SyncSettings.SeedDefaultSubjectColors 重灌，
    /// 于是"改乱了想回到出厂观感"不用一个个手调。
    /// </summary>
    void OnResetSubjectColors(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings.SubjectColors.Clear();
            Settings.SeedDefaultSubjectColors();
            RefreshSubjects();
        }
        catch { }
    }

    /// <summary>
    /// 「打开控制面板」：设置页里直接开面板，省得先让卡片露出来再点齿轮。
    /// 服务是单例，卡片没显示时也能开；拿不到服务就安静返回，不在设置页里抛异常。
    /// </summary>
    void OnOpenControlPanel(object? sender, RoutedEventArgs e)
    {
        try
        {
            var svc = IAppHost.TryGetService<ClassSlideSync.Plugin.Services.SlideSyncService>();
            svc?.OpenPanelForAutomation(null);
        }
        catch
        {
            // 面板打不开不该把设置页搞崩。
        }
    }
}

/// <summary>
/// 设置界面里「#AARRGGBB 字符串 ↔ Avalonia Color」的翻译层。
/// 两个设置页（官方设置页 + 控制面板设置页签）共用这一份，所以放在设置页的命名空间里。
/// 解析失败时回退纯白：宁可显示白色，也不让设置页因为一个脏字符串崩掉。
/// </summary>
public sealed class HexColorConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return Colors.White;
        string t = text!.Trim();
        if (t.Length > 0 && t[0] != '#') t = "#" + t;
        Color color;
        return Color.TryParse(t, out color) ? color : Colors.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c)
        {
            // 统一写成 #AARRGGBB：Avalonia 的 Color.ToString() 在 255 时只给 6 位，
            // 而我们的设置字段是按 8 位读的（Core\Appearance.cs 里解析），写短了会丢 alpha。
            return "#" + ((uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B).ToString("X8", CultureInfo.InvariantCulture);
        }
        return value as string ?? "#FFFFFFFF";
    }
}
