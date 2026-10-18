using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
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
        DataContext = this;
        AttachedToVisualTree += (_, _) =>
        {
            // 打开设置页就重算一次：ClassIsland 的主题色是可以中途改的，这里顺势重新探一次。
            try { Plugin.Service?.RecomputeAppearance(); } catch { }
            Appearance.Refresh();
        };
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
}
