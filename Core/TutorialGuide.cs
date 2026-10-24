using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Tutorial;
using ClassIsland.Shared;

namespace ClassSlideSync.Plugin.Core;

internal static class TutorialGuide
{
    public const string GroupId = "ClassSlideSync.Plugin.Tutorials";

    public const string TutorialId = "slidesync.getStarted";

    const string ResourceName = "ClassSlideSync.Plugin.Assets.Tutorials.getStarted.json";

    static readonly string[] PreferredHosts =
    {
        "ClassIsland.Views.SettingsWindowNew",
        "ClassIsland.Views.TutorialCenterWindow",
        "ClassIsland.MainWindow",
    };

    public static void Register()
    {
        try
        {
            var group = Load();
            if (group == null || string.IsNullOrWhiteSpace(group.Id))
            {
                Log("教程 JSON 没读到，新手引导不会出现在教程中心");
                return;
            }

            if (ITutorialService.RegisteredTutorialGroups.Any(g => string.Equals(g.Id, group.Id, StringComparison.Ordinal)))
            {
                return;
            }

            ITutorialService.RegisteredTutorialGroups.Add(group);
        }
        catch (Exception ex)
        {
            Log("注册新手引导失败: " + ex);
        }
    }

    public static bool Start()
    {
        try
        {
            var service = IAppHost.TryGetService<ITutorialService>();
            if (service == null) { Log("拿不到 ITutorialService"); return false; }

            var tutorial = Load()?.Tutorials?.FirstOrDefault(t => string.Equals(t.Id, TutorialId, StringComparison.Ordinal));
            if (tutorial == null) { Log("教程 JSON 里没有 " + TutorialId); return false; }

            var host = PickHostClassName();
            if (host != null && tutorial.Paragraphs != null)
            {
                foreach (var paragraph in tutorial.Paragraphs)
                {
                    paragraph.TopLevelClassName = host;
                }
            }

            service.BeginTutorial(tutorial);
            return true;
        }
        catch (Exception ex)
        {
            Log("启动新手引导失败: " + ex);
            return false;
        }
    }

    static string? PickHostClassName()
    {
        IReadOnlyList<Window>? windows = null;
        try { windows = AppBase.Current?.DesktopLifetime?.Windows; }
        catch { }

        if (windows == null) return null;

        foreach (var name in PreferredHosts)
        {
            if (windows.Any(w => w != null && string.Equals(w.GetType().FullName, name, StringComparison.Ordinal)))
            {
                return name;
            }
        }

        var fallback = windows.FirstOrDefault(w => w != null && w.IsVisible);
        return fallback?.GetType().FullName;
    }

    static TutorialGroup? Load()
    {
        var assembly = typeof(TutorialGuide).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => string.Equals(n, ResourceName, StringComparison.Ordinal))
            ?? assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("getStarted.json", StringComparison.OrdinalIgnoreCase));

        if (name == null) { Log("内嵌资源里有没 getStarted.json: " + string.Join(",", assembly.GetManifestResourceNames())); return null; }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null) { Log("打不开内嵌资源 " + name); return null; }

        return JsonSerializer.Deserialize<TutorialGroup>(stream);
    }

    static void Log(string message)
    {
        Debug.WriteLine("[ClassSlideSync] " + message);
    }
}
