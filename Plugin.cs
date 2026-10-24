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

        TutorialGuide.Register();

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
