using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
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
        DataContext = this;
        RefreshSubjects();
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

    void RefreshSubjects()
    {
        try
        {
            var names = new List<string>();
            var profiles = IAppHost.TryGetService<IProfileService>();
            if (profiles?.Profile?.Subjects != null)
            {
                foreach (var subject in profiles.Profile.Subjects.Values)
                {
                    var name = subject?.Name?.Trim();
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }

            // 已配置、但课表里已经没有的科目，也要能显示出来。
            foreach (var folder in Settings.SubjectFolders)
            {
                var name = folder?.Subject?.Trim();
                if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                {
                    names.Add(name);
                }
            }

            var same = names.Count == SubjectOptions.Count;
            for (var i = 0; i < names.Count && same; i++)
            {
                same = string.Equals(names[i], SubjectOptions[i], StringComparison.Ordinal);
            }

            if (same)
            {
                return;
            }

            SubjectOptions.Clear();
            foreach (var name in names)
            {
                SubjectOptions.Add(name);
            }
        }
        catch
        {
            // 档案还没就绪时静默跳过，下次打开设置页会再试一次。
        }
    }

    /// <summary>路径列：用系统的文件夹选择器，省得手打路径。</summary>
    async void OnPickSubjectFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button b || b.Tag is not SubjectFolder folder)
            {
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null)
            {
                return;
            }

            var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择这个科目的课件文件夹",
                AllowMultiple = false
            });

            var path = picked != null && picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path))
            {
                folder.Folder = path;
            }
        }
        catch
        {
            // 用户取消或平台不支持时不动原值。
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
