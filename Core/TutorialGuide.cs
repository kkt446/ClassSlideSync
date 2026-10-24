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

/// <summary>
/// 插件的新手引导，走 ClassIsland 2.1 自带的教程（Tutorial）机制，不自己画窗口。
///
/// 教程内容写在 Assets/Tutorials/getStarted.json 里（ClassIsland 自己的教程也是这个 JSON 格式），
/// 这里只做两件事：
///
///   1) <see cref="Register"/>：把教程组挂到 <see cref="ITutorialService.RegisteredTutorialGroups"/>，
///      于是它会出现在 ClassIsland 的「教程中心」列表里 —— 这是原生入口。
///      （SDK 里有个 AddTutorialGroupByUri 的便利方法，但那个要从 avares:// 资源里读；
///      我们对插件程序集用内嵌资源读，同样是 System.Text.Json 反序列化同一个模型，
///      只是不依赖宿主的 Avalonia 资源查找，跨版本更稳。）
///
///   2) <see cref="Start"/>：从插件设置页的按钮启动引导。
///
/// 启动时有个坑必须处理：TutorialService 按 paragraph.TopLevelClassName 去
/// DesktopLifetime.Windows 里找一个窗口当宿主，**找不到就直接静默返回**，教程什么也不显示。
/// 所以 Start() 会把段落的宿主换成本次真正打开着的那个窗口（设置窗口 / 教程中心 / 主界面）。
/// </summary>
internal static class TutorialGuide
{
    /// <summary>教程组 Id，同时也是 JSON 里的 Id。</summary>
    public const string GroupId = "ClassSlideSync.Plugin.Tutorials";

    /// <summary>新手引导这一条教程的 Id（JSON 里的 Tutorials[].Id）。</summary>
    public const string TutorialId = "slidesync.getStarted";

    /// <summary>内嵌资源的默认逻辑名：&lt;RootNamespace&gt;.&lt;路径点分&gt;。</summary>
    const string ResourceName = "ClassSlideSync.Plugin.Assets.Tutorials.getStarted.json";

    /// <summary>
    /// 宿主窗口的优先顺序：设置窗口（点设置页按钮时就在它上面）→ 教程中心（从教程中心播放时）
    /// → 主界面。名字必须和 ClassIsland 的类型全名逐字一致。
    /// </summary>
    static readonly string[] PreferredHosts =
    {
        "ClassIsland.Views.SettingsWindowNew",
        "ClassIsland.Views.TutorialCenterWindow",
        "ClassIsland.MainWindow",
    };

    /// <summary>把教程组挂进 ClassIsland 的教程列表（在 Plugin.Initialize 里调一次）。</summary>
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
            // 引导挂了不能影响插件本体，记一笔就算了。
            Log("注册新手引导失败: " + ex);
        }
    }

    /// <summary>
    /// 启动新手引导（设置页那个按钮走这里）。返回 false 表示没能启动，调用方可以提示一下。
    /// </summary>
    public static bool Start()
    {
        try
        {
            var service = IAppHost.TryGetService<ITutorialService>();
            if (service == null) { Log("拿不到 ITutorialService"); return false; }

            // 每次重新读一份：这样临时改宿主窗口不会污染已经注册的那一条（教程中心那一条还得能用）。
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

    /// <summary>本次真正打开着的宿主窗口类型全名；一个都没有时返回 null（保持 JSON 里的默认值）。</summary>
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

        // 兜底：任意一个可见窗口。宿主只要不是 null，TeachingTip 就会显示出来。
        var fallback = windows.FirstOrDefault(w => w != null && w.IsVisible);
        return fallback?.GetType().FullName;
    }

    /// <summary>从程序集内嵌资源读教程 JSON。读不到返回 null。</summary>
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
