using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;
using ClassSlideSync.Plugin.Views.SettingsPages;

namespace ClassSlideSync.Plugin;

/// <summary>
/// ClassIsland plugin entry point.
///
/// The plugin watches a running PowerPoint / WPS slideshow, keeps a ledger of where each
/// lesson stopped, and shows a floating card on the right edge of the screen with the page
/// number to jump back to. The actual work lives in <see cref="SlideSyncService"/> and the
/// UI-free engine under Core/, so that all of it can be exercised without ClassIsland running.
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    public SyncSettings Settings { get; private set; } = new SyncSettings();

    public SlideSyncService Service { get; private set; }

    internal string DataFolder { get; private set; }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        DataFolder = PluginConfigFolder;

        Settings = ConfigureFileHelper.LoadConfig<SyncSettings>(
            Path.Combine(PluginConfigFolder, "SyncSettings.json"));
        Settings.DataFolder = DataFolder;
        Settings.PropertyChanged += (_, _) =>
        {
            ConfigureFileHelper.SaveConfig<SyncSettings>(
                Path.Combine(PluginConfigFolder, "SyncSettings.json"), Settings);
            Service?.OnSettingsChanged();
        };

        Service = new SlideSyncService(Settings, PluginConfigFolder);
        services.AddSingleton(Settings);
        services.AddSingleton(Service);
        services.AddSettingsPage<SlideSyncSettingsPage>();

        // 只启动"调度器"：它每 5 秒问一次课表，决定要不要进入待命（上课前 1 分钟，见 Core/LessonClock.cs）。
        // 真正占资源的东西（COM 轮询、卡片窗口）只在待命期间存在，下课后会被全部销毁。
        AppBase.Current.AppStarted += (_, _) =>
        {
            try { Service.Start(); }
            catch (Exception ex) { Service?.Ledger?.Log("启动失败: " + ex); }
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            try { Service.Stop(); }
            catch { }
        };
    }
}
