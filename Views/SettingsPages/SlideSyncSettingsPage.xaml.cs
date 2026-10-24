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

[SettingsPageInfo("classslidesync.settings", "课件续页", "\uE7C3", "\uE7C3")]
public partial class SlideSyncSettingsPage : SettingsPageBase
{
    public Plugin Plugin { get; }

    public SyncSettings Settings { get { return Plugin.Settings; } }

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
            try { Plugin.Service?.RecomputeAppearance(); } catch { }
            Appearance.Refresh();
            RefreshSubjects();
        };
    }

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
        }
    }

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
        }
    }

    void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (Plugin.Service != null)
        {
            Plugin.Service.ShowCardNow();
        }
    }

    void OnStartTutorialClick(object sender, RoutedEventArgs e)
    {
        if (TutorialGuide.Start())
        {
            GuideHint.IsVisible = false;
            return;
        }

        GuideHint.Text = "没能启动引导：当前 ClassIsland 版本可能不支持内置教程。"
                       + "可以先看 README 的「怎么用」一节，功能本身不受影响。";
        GuideHint.IsVisible = true;
    }

    void OnAddSubjectFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings.SubjectFolders.Add(new SubjectFolder());
        }
        catch { }
    }

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
