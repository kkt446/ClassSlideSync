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
using ClassSlideSync.Plugin.Automation;
using ClassSlideSync.Plugin.Views.SettingsPages;
using ClassSlideSync.Plugin.Components;

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
        // ===== 平台闸门（必须留在 Initialize 的第一行）=====
        // 主工程 TFM 是 net8.0，同一个 dll 在 Windows 和 Linux 上都会被 ClassIsland 原样加载
        // （宿主不按 TFM/OS 过滤插件）。Windows 专属的部分仍然全部留在这个分支后面：分层窗口
        // （user32/gdi32）、COM（PowerPoint/WPS 的 ROT 与 IMessageFilter）、托盘气泡
        // （Shell_NotifyIconW）、操作中心横幅（Tools\ToastHelper 是 net8.0-windows10.0.19041.0
        // 的 WinRT exe）。
        //
        // 非 Windows 做三件事：记一行日志、注册一个只读说明页、启动 Linux 页码源
        // （Core/LinuxSlideSource.cs，读适配器写的 ~/.local/share/classslidesync/current.json）。
        // 仍然绝不注册卡片/组件/行动/规则/触发器/教程；SlideSyncService.Start() 在非 Windows 上
        // 只起一个 500ms 的读文件定时器就 return，不建 NativeOverlayCard、不碰 COM/主题/调度器
        // （见 StartLinux）。于是 PptBridge / TrayNotifier 在 Linux 上连构造都不会发生
        //（这几类各自另有运行时护栏，见 stability-report-linux.txt / stability-report-linux-source.txt）。
        // Windows 上这个分支恒不成立，下面每一行都和改动前完全一样。
        if (!OperatingSystem.IsWindows())
        {
            // Ledger.Log 内部 CreateDirectory + try/catch，写不进去也不会抛。
            // 注意这里不调 PlatformGuard.Announce：闸门不是在「跳过」，而是 Linux 上唯一会走的路，
            // 所以文案要说清楚「注册了只读设置页 + Linux 页码源」。
            new Ledger(PluginConfigFolder).Log(
                "platform: non-windows — read-only settings page + Linux page source (file bridge) " +
                "are registered; cards / components / automations / triggers / tutorial are skipped");
            services.AddSettingsPage<PlatformUnavailablePage>();

            // Linux 页码源：服务只读 current.json，不产生窗口。包在 try/catch 里 ——
            // 配置读不出来不能让插件加载失败（否则连只读设置页都没了）。
            try
            {
                DataFolder = PluginConfigFolder;
                Settings = ConfigureFileHelper.LoadConfig<SyncSettings>(
                    Path.Combine(PluginConfigFolder, "SyncSettings.json"));
                Settings.DataFolder = DataFolder;
                Service = new SlideSyncService(Settings, PluginConfigFolder);
                services.AddSingleton(Settings);
                services.AddSingleton(Service);

                // 目录 + 中文协议说明：幂等，适配器安装时也会调，插件启动再兜一次。
                LinuxSlideSource.Maintain();

                // 与 Windows 分支同一个启动时机（宿主 AppStarted），而不是 Initialize 里 ——
                // 那时 ClassIsland 的 IO/日志目录还没完全就绪。
                AppBase.Current.AppStarted += (_, _) =>
                {
                    try { Service.Start(); }
                    catch (Exception ex)
                    {
                        try { Service?.Ledger?.Log("linux source start failed: " + ex); } catch { }
                    }
                };
            }
            catch (Exception ex)
            {
                new Ledger(PluginConfigFolder).Log(
                    "platform: linux page source wiring failed: " + ex.GetType().Name + " " + ex.Message);
            }
            return;
        }

        DataFolder = PluginConfigFolder;

        // ===== 宿主能力探测（必须在注册之前）=====
        // 插件是按 2.1.0.1 的 SDK 编译的，IL 里写死了 2.1 才有的类型；旧宿主缺这些类型时，
        // JIT 会在「方法开始执行之前」抛 TypeLoadException —— 方法自己的 try/catch 接不住。
        // 所以先反射问清楚宿主有什么，再决定注册什么，缺失的一律跳过 + 记一行 app.log。
        // 依据见 stability-report-hostcompat.txt 里的 2.0.4 事实表。
        var ledger = new Ledger(PluginConfigFolder);
        var caps = HostCompat.Initialize(line => ledger.Log(line));

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

        // 2026-10-24 用户要求：把「横幅」这个依赖做成「插件启用过一次就装好」。
        // 这里只做一次登记检查（后台线程）：helper 在就让它写好 HKCU 的 AppUserModelId 与开始菜单
        // 快捷方式，并把 helper 路径 + 版本写进 PluginConfigFolder\toast-installed.txt；
        // 已经登记过（标记文件对得上）就直接跳过。helper 缺失或登记失败一律安静忽略，
        // 下课提醒会自己退回托盘气泡（Core\TrayNotifier.cs），绝不因为通知把插件搞挂。
        ToastNotifier.InstallOnce(PluginConfigFolder);

        services.AddSingleton(Settings);
        services.AddSingleton(Service);

        // 设置页：2.0.0.1 起就有 SettingsPageBase + AddSettingsPage<T>；仍然走探测，
        // 万一将来某个宿主改了名，这里退化成「没有设置页但插件照常跑」，而不是白屏。
        HostCompat.TryRun("settings-page", "SettingsPageBase + AddSettingsPage<T>", caps.SettingsPages,
            () => services.AddSettingsPage<SlideSyncSettingsPage>());

        // 自动化 / 组件 / 教程：全部分区注册，每区一个探测 + 一行日志。见下方 RegisterAutomation。
        RegisterAutomation(services, caps);

        // 只启动"调度器"：它每 5 秒问一次课表，决定要不要进入待命（上课前 1 分钟，见 Core/LessonClock.cs）。
        // 真正占资源的东西（COM 轮询、卡片窗口）只在待命期间存在，下课后会被全部销毁。
        AppBase.Current.AppStarted += (_, _) =>
        {
            // 2026-10-24 用户反馈「自动化里看不到我们」：ClassIsland 没有提供注册结果回调，
            // 所以启动后自己反射一遍 IAutomationService 的注册表，把和本插件有关的条目写进 app.log。
            // 只读、不抛：拿不到就跳过，绝不影响插件启动。
            LogAutomationRegistrations(Service);
            try { Service.Start(); }
            catch (Exception ex) { Service?.Ledger?.Log("启动失败: " + ex); }
        };

        AppBase.Current.AppStopping += (_, _) =>
        {
            try { Service.Stop(); }
            catch { }
        };
    }

    /// <summary>
    /// 自动化 / 主界面组件 / 新手引导的分区注册。
    ///
    /// 每个分区都先问 <see cref="HostCompat"/> 宿主有没有这个能力，再注册；缺失时跳过并记
    /// <c>compat: skipped &lt;feature&gt; (host &lt;version&gt; lacks &lt;api&gt;)</c>（同一 feature 只记一次）。
    /// 带设置抽屉的行动在旧宿主上退回单泛型重载：**行动照样能执行**，只是没有设置界面。
    ///
    /// 实测结论（2.0.0.1–2.1.0.1 全版本反射）：ActionBase / TriggerBase / ComponentBase /
    /// AddRule / AddSettingsPage / 双泛型 AddAction&lt;,&gt; / AddTrigger&lt;,&gt; 从 2.0.0.1 就有；
    /// 真正缺的只有 2.0.3 才加的教程系统（ITutorialService）。
    /// </summary>
    internal static void RegisterAutomation(IServiceCollection services, HostCapabilities caps)
    {
        // —— 行动：不需要设置抽屉的直接注册 ——
        RegisterAction(services, caps, "action:open-unfinished-deck", () => services.AddAction<OpenUnfinishedDeckAction>());
        RegisterAction(services, caps, "action:toggle-card", () => services.AddAction<ToggleCardAction>());
        RegisterAction(services, caps, "action:open-panel", () => services.AddAction<OpenPanelAction>());
        RegisterAction(services, caps, "action:jump-last-page", () => services.AddAction<JumpLastPageAction>());
        RegisterAction(services, caps, "action:open-deck-folder", () => services.AddAction<OpenDeckFolderAction>());
        RegisterAction(services, caps, "action:clear-deck-history", () => services.AddAction<ClearDeckHistoryAction>());

        // —— 行动：带设置抽屉（旧宿主缺双泛型时退回单泛型，行动仍可执行）——
        // Notify / JumpToPage 需要可编辑设置：AddAction<TAction, TSettingsControl>() 会把设置控件
        // 以同一个 Id 注册成 keyed transient，ClassIsland 打开行动设置抽屉时按 Id 取它；
        // 只注册 AddAction<TAction>() 的话行动没有设置界面（这正是旧版「没法自定义发啥」的原因）。
        RegisterActionWithSettings(services, caps, "action:notify",
            () => services.AddAction<NotifyAction, NotifyActionSettingsControl>(),
            () => services.AddAction<NotifyAction>());
        RegisterActionWithSettings(services, caps, "action:jump-to-page",
            () => services.AddAction<JumpToPageAction, JumpToPageSettingsControl>(),
            () => services.AddAction<JumpToPageAction>());

        // —— 规则：没有基类/特性，用 AddRule(id, name, glyph, bool Invoke(object)) 直接注册处理器 ——
        HostCompat.TryRun("rule:unfinished-deck", "AddRule(id,name,glyph,handle)", caps.Rules,
            () => services.AddRule(
                UnfinishedDeckRule.Id,
                UnfinishedDeckRule.Name,
                UnfinishedDeckRule.Glyph,
                UnfinishedDeckRule.Handle));

        // —— 触发器：2026-10-25 用户第 4 条需求「自动化，当讲到 x 面时」 ——
        // 触发器与行动同构：AddTrigger<TTrigger, TSettingsControl>() 按 [TriggerInfo] 里的 Id
        // 把触发器和它的设置控件各注册成一条 keyed transient；只写 AddTrigger<TTrigger>()
        // 就没有设置界面（页码没法填）。取证见 stability-report-trigger.txt。
        if (caps.TriggersWithSettings)
        {
            HostCompat.TryRun("trigger:slide-page-reached", "AddTrigger<T,TTriggerSettings>", true,
                () => services.AddTrigger<SlidePageReachedTrigger, SlidePageReachedSettingsControl>());
        }
        else if (caps.Triggers)
        {
            HostCompat.Skip("trigger:slide-page-reached.settings-control", "AddTrigger<T,TSettingsControl>");
            HostCompat.TryRun("trigger:slide-page-reached", "AddTrigger<T> (degraded, no settings UI)", true,
                () => services.AddTrigger<SlidePageReachedTrigger>());
        }
        else
        {
            HostCompat.Skip("trigger:slide-page-reached", "TriggerBase + AddTrigger<T>");
        }

        // —— 主界面组件「课件进度」：设置 → 主界面 → 编辑组件 里能把它拖进任何一行 ——
        // 组件自己订阅 SlideSyncService.SlideProgressChanged；没课件放映时把自身 IsVisible 关掉，
        // 不留占位（宿主对组件 IsVisible 的 setter 是 internal，插件不能代设）。
        HostCompat.TryRun("component:deck-progress", "ComponentBase + AddComponent<T>", caps.Components,
            () => services.AddComponent<DeckProgressComponent>());

        // —— 新手引导：挂进 ClassIsland 自带的教程列表（教程中心里能看到「课件续页」这一组）——
        // 教程系统是 2.0.3 才加的；2.0.0.1–2.0.2 没有 ITutorialService，这里整段跳过。
        // 注意：TutorialGuide.Register 的方法体里直接引用了 ITutorialService，在缺该类型的宿主上
        // 一旦 JIT 就会抛 TypeLoadException，所以**必须在调用点之前**判断，不能靠它内部的 try/catch。
        HostCompat.TryRun("tutorial", "ITutorialService.RegisteredTutorialGroups", caps.Tutorials,
            () => TutorialGuide.Register());
    }

    static void RegisterAction(IServiceCollection services, HostCapabilities caps, string feature, Action register) =>
        HostCompat.TryRun(feature, "ActionBase + AddAction<T>", caps.Actions, register);

    /// <summary>
    /// 带设置抽屉的行动。旧宿主没有 AddAction&lt;T, TSettingsControl&gt; 时退回 AddAction&lt;T&gt;：
    /// 行动本身仍能执行（用户点了有反应），只是没有设置界面 —— 这是有意义的降级，不是静默丢弃。
    /// </summary>
    static void RegisterActionWithSettings(IServiceCollection services, HostCapabilities caps, string feature,
        Action withSettings, Action plain)
    {
        if (caps.ActionsWithSettings)
        {
            HostCompat.TryRun(feature, "AddAction<T,TSettingsControl>", true, withSettings);
            return;
        }

        if (caps.Actions)
        {
            HostCompat.Skip(feature + ".settings-control", "AddAction<T,TSettingsControl>");
            HostCompat.TryRun(feature, "AddAction<T> (degraded, no settings drawer)", true, plain);
            return;
        }

        HostCompat.Skip(feature, "ActionBase + AddAction<T>");
    }

    /// <summary>
    /// 把 IAutomationService 里注册到的、属于本插件的行动/规则/触发 id 写进 app.log。
    /// 用途只有一个：用户说「自动化里看不到」时，能立刻分清是「没注册上」还是「菜单里不容易找到」。
    /// </summary>
    static void LogAutomationRegistrations(SlideSyncService service)
    {
        try
        {
            var auto = ClassIsland.Shared.IAppHost.TryGetService<ClassIsland.Core.Abstractions.Services.IAutomationService>();
            if (auto == null)
            {
                service?.Ledger?.Log("automation: IAutomationService 不可用（无法确认行动是否注册）");
                return;
            }

            var found = new List<string>();
            foreach (var prop in auto.GetType().GetProperties())
            {
                object? value;
                try { value = prop.GetValue(auto); } catch { continue; }
                if (value is System.Collections.IEnumerable list && value is not string)
                {
                    foreach (var item in list)
                    {
                        if (item == null) continue;
                        string text = item.ToString() ?? "";
                        if (text.Contains("ClassSlideSync", StringComparison.OrdinalIgnoreCase)) found.Add(prop.Name + ":" + text);
                    }
                }
                else if (value != null && (value.ToString() ?? "").Contains("ClassSlideSync", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(prop.Name + ":" + value);
                }
            }

            service?.Ledger?.Log(found.Count > 0
                ? "automation: 已注册 " + found.Count + " 项 -> " + string.Join(" | ", found)
                : "automation: 注册表里没有本插件的条目（AddAction 可能没生效）");
        }
        catch (Exception ex)
        {
            service?.Ledger?.Log("automation: 自检失败 " + ex.Message);
        }
    }
}
