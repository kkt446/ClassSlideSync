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

        // 2026-10-24 用户要求：把「横幅」这个依赖做成「插件启用过一次就装好」。
        // 这里只做一次登记检查（后台线程）：helper 在就让它写好 HKCU 的 AppUserModelId 与开始菜单
        // 快捷方式，并把 helper 路径 + 版本写进 PluginConfigFolder\toast-installed.txt；
        // 已经登记过（标记文件对得上）就直接跳过。helper 缺失或登记失败一律安静忽略，
        // 下课提醒会自己退回托盘气泡（Core\TrayNotifier.cs），绝不因为通知把插件搞挂。
        ToastNotifier.InstallOnce(PluginConfigFolder);

        services.AddSingleton(Settings);
        services.AddSingleton(Service);
        services.AddSettingsPage<SlideSyncSettingsPage>();

        // 自动化（2.1）：行动要继承 ActionBase + 标 [ActionInfo]，由 AddAction<T>() 按特性里的 Id
        // 注册成 keyed transient；规则没有基类/特性，用 AddRule(id, name, glyph, bool Invoke(object))
        // 直接注册处理器。两边的 Id 都带 ClassSlideSync.Plugin. 前缀（用户的第 11 条需求）。
        // 文档与反射依据见 stability-report-automation.txt。
        services.AddAction<OpenUnfinishedDeckAction>();
        services.AddAction<ToggleCardAction>();
        // 2026-10-24 用户要求「自动化里加点这个插件的实用东西」：行动分别在
        // Automation\UtilityActions.cs 与 Automation\DeckActions.cs。
        // Notify / JumpToPage 需要可编辑设置：AddAction<TAction, TSettingsControl>() 会把设置控件
        // 以同一个 Id 注册成 keyed transient，ClassIsland 打开行动设置抽屉时按 Id 取它；
        // 只注册 AddAction<TAction>() 的话行动没有设置界面（这正是旧版「没法自定义发啥」的原因）。
        services.AddAction<NotifyAction, NotifyActionSettingsControl>();
        services.AddAction<OpenPanelAction>();
        services.AddAction<JumpLastPageAction>();
        services.AddAction<JumpToPageAction, JumpToPageSettingsControl>();
        services.AddAction<OpenDeckFolderAction>();
        services.AddAction<ClearDeckHistoryAction>();
        services.AddRule(
            UnfinishedDeckRule.Id,
            UnfinishedDeckRule.Name,
            UnfinishedDeckRule.Glyph,
            UnfinishedDeckRule.Handle);

        // 2026-10-25 用户第 4 条需求：「自动化，当讲到 x 面时」——不是行动，是**触发条件**，
        // 所以要注册成触发器而不是 AddAction。触发器与行动同构：AddTrigger<TTrigger, TSettingsControl>()
        // 按 [TriggerInfo] 里的 Id 把触发器和它的设置控件各注册成一条 keyed transient；
        // 只写 AddTrigger<TTrigger>() 就没有设置界面（页码没法填）。取证见 stability-report-trigger.txt。
        services.AddTrigger<SlidePageReachedTrigger, SlidePageReachedSettingsControl>();

        // 主界面组件「课件进度」：设置 → 主界面 → 编辑组件 里能把它拖进任何一行。
        // 组件自己订阅 SlideSyncService.SlideProgressChanged；没课件放映时把自身 IsVisible 关掉，
        // 不留占位（宿主对组件 IsVisible 的 setter 是 internal，插件不能代设）。
        services.AddComponent<DeckProgressComponent>();

        // 新手引导：挂进 ClassIsland 自带的教程列表（教程中心里能看到「课件续页」这一组）。
        // 设置页上的「新手引导」按钮和它用的是同一份教程内容，见 Core/TutorialGuide.cs。
        TutorialGuide.Register();

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
