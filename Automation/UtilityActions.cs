using System;
using System.IO;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 行动的「大分支」名字。ClassIsland 2.1 的 <see cref="ActionInfo"/> 第 5 个参数
    /// （defaultGroupToMenu）就是官方支持的分组入口：注册时 <c>ProcessAddToGroup</c> 会在
    /// 「添加行动」菜单里找同名组，找不到就建一个，再把这些行动塞进去。见 Api 文档
    /// ActionInfo 构造函数说明：「在「添加行动」菜单添加默认项的根菜单组。如果找不到该组会添加该组」。
    /// </summary>
    public static class AutomationGroups
    {
        /// <summary>插件在「添加行动」菜单里的大分支名。</summary>
        public const string Plugin = "课件续页";
    }

    /// <summary>
    /// 行动三：发一条 Windows 提示。
    ///
    /// 用 Core\TrayNotifier / Core\ToastNotifier：不需要额外 NuGet，也不依赖系统 Toast 开关。
    /// 标题、正文、停留时长、是否置顶都放进 <see cref="NotifyActionSettings"/>，由 ClassIsland 的
    /// <c>ActionBase&lt;TSettings&gt;</c> 负责 JSON 保存/加载；可编辑界面见
    /// Automation\ActionSettingsControls.cs 的 <see cref="NotifyActionSettingsControl"/>。
    /// 正文支持 {deck}/{page}/{total}/{remaining}，从台账最近一条记录取值。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class NotifyAction : ActionBase<NotifyActionSettings>
    {
        public const string Id = "ClassSlideSync.Plugin.Notify";
        public const string Name = "发一条 Windows 提示";
        public const string Description = "弹出一条 Windows 通知，标题、正文、停留时长、是否置顶都可编辑；正文支持课件名/页码占位符。";
        public const string Glyph = "\uE8BD";   // Segoe Fluent Icons: Message

        /// <summary>最近一次 Invoke 是否真的把通知交给了系统（Tray/Toast 的返回值）。</summary>
        public bool LastShowSucceeded { get; private set; }

        protected override async Task OnInvoke()
        {
            await base.OnInvoke();
            try
            {
                NotifyActionSettings settings = TrySettings() ?? new NotifyActionSettings();

                string title = (settings.Mask ?? "").Trim();
                if (title.Length == 0) title = "课件续页";

                SessionRecord? record = ResolveRecord();
                string body = NotifyTemplate.Render(settings.Content, record).Trim();

                // 置顶时优先走操作中心横幅（Windows 会把它盖在窗口上方）；否则优先托盘气泡，
                // 因为只有气泡的图标停留时长能由我们控制。任何一路失败都回退到另一路。
                // Toast 走的是外部 helper，最坏会等 8 秒；放到线程池里等，避免自动化跑在 UI 线程时卡界面。
                bool shown;
                if (settings.IsTopmostEnabled)
                {
                    shown = await ShowToastAsync(title, body);
                    if (!shown) shown = TrayNotifier.Show(title, body, settings.DurationSeconds);
                }
                else
                {
                    shown = TrayNotifier.Show(title, body, settings.DurationSeconds);
                    if (!shown) shown = await ShowToastAsync(title, body);
                }

                LastShowSucceeded = shown;
                Log(shown
                    ? "automation notify: 已发通知「" + title + "」/「" + body + "」"
                    : "automation notify: 通知没发出去（标题或正文为空，或系统拒绝）");
            }
            catch (Exception ex)
            {
                LastShowSucceeded = false;
                Log("automation notify failed: " + ex.Message);
            }
        }

        static async Task<bool> ShowToastAsync(string title, string body)
        {
            try
            {
                return await Task.Run(() => ToastNotifier.Show(title, body)).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取占位符数据源：当前放映课件优先，没在放映时用台账最新一条。
        /// 直接 new 出来、没经宿主注入设置的实例也能安全调用。
        /// </summary>
        SessionRecord? ResolveRecord()
        {
            try
            {
                var service = OpenUnfinishedDeckAction.TryService();
                string path = service?.Engine?.Last?.FilePath ?? "";
                return NotifyTemplate.Resolve(service?.Ledger, path);
            }
            catch
            {
                return null;
            }
        }

        // 宿主通过 GetInstance 注入设置；反射测试直接 new 时 getter 会抛，兜住后回落到默认文案。
        NotifyActionSettings? TrySettings()
        {
            try
            {
                return Settings;
            }
            catch
            {
                return null;
            }
        }

        static void Log(string line)
        {
            try
            {
                OpenUnfinishedDeckAction.TryService()?.Ledger?.Log(line);
            }
            catch
            {
                // 日志本身失败不能再把行动搞崩，否则自动化会显示「执行失败」。
            }
        }
    }

    /// <summary>
    /// 行动四：把「课件续页」控制面板叫出来。
    ///
    /// 面板的创建链路原本只由托盘图标点击（<c>OnPanelRequested</c>）触发，自动化线程没有公开入口，
    /// 所以给 SlideSyncService 加了一个最小的公开方法 <c>OpenPanelForAutomation</c>：
    /// 它只做「日志 + 转调既有链路」，不复制任何创建逻辑；没 Arm（没开始上课）时安静失败。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class OpenPanelAction : ActionBase
    {
        public const string Id = "ClassSlideSync.Plugin.OpenControlPanel";
        public const string Name = "打开课件续页控制面板";
        public const string Description = "把插件自己的控制面板叫到前台：里面有课件台账、跳页、设置入口。上课没开始（服务未 Arm）时不动作，只记日志。";
        public const string Glyph = "\uE8A7";   // Segoe Fluent Icons: OpenPane

        protected override Task OnInvoke()
        {
            base.OnInvoke();
            try
            {
                var service = OpenUnfinishedDeckAction.TryService();
                if (service == null)
                {
                    Log("automation panel: 服务还没起来，忽略");
                    return Task.CompletedTask;
                }
                service.OpenPanelForAutomation("自动化：打开控制面板");
            }
            catch (Exception ex)
            {
                Log("automation panel failed: " + ex.Message);
            }
            return Task.CompletedTask;
        }

        static void Log(string line)
        {
            try
            {
                OpenUnfinishedDeckAction.TryService()?.Ledger?.Log(line);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 行动五：跳到「上次停的那一页」。
    ///
    /// 只用引擎当前正在放映的那份课件（<c>engine.Last</c>）去台账里取页码：
    /// 如果拿别的台账路径去 <c>RequestJumpTo</c>，引擎会在桥没挂上时 <c>PptBridge.Launch</c>，
    /// 等于在教室里凭空拉起一个 PowerPoint —— 这是上一条跳页行动踩过的坑。
    /// 没有正在放映的课件、或台账里没有有效页码时，安静失败并记一行日志。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class JumpLastPageAction : ActionBase
    {
        public const string Id = "ClassSlideSync.Plugin.JumpLastPage";
        public const string Name = "跳到上次停的那一页";
        public const string Description = "在当前正在放映的课件里，跳回台账记录的最后一页；没有在放映或没有记录时安静跳过，只记日志。";
        public const string Glyph = "\uE7C3";   // Segoe Fluent Icons: Page

        protected override Task OnInvoke()
        {
            base.OnInvoke();
            Ledger? ledger = null;
            try
            {
                var service = OpenUnfinishedDeckAction.TryService();
                if (service == null)
                {
                    Log(null, "automation jump: 服务还没起来，忽略");
                    return Task.CompletedTask;
                }

                ledger = service.Ledger;
                var engine = service.Engine;
                if (engine == null)
                {
                    Log(ledger, "automation jump: 还没开始放映，没有可跳的页");
                    return Task.CompletedTask;
                }

                PptSnapshot? snap = engine.Last;
                string filePath = snap == null ? "" : (snap.FilePath ?? "");
                if (snap == null || !snap.AppFound || filePath.Length == 0)
                {
                    Log(ledger, "automation jump: 当前没有正在放映的课件，跳过");
                    return Task.CompletedTask;
                }

                SessionRecord? record = ledger == null ? null : ledger.Last(filePath);
                int page = UnfinishedDecks.JumpPage(record);
                if (page <= 0)
                {
                    Log(ledger, "automation jump: 台账里没有「" + SafeName(filePath) + "」的有效页码，跳过");
                    return Task.CompletedTask;
                }

                service.RequestJump(page);
                Log(ledger, "automation jump: 已请求跳到第 " + page + " 页（" + SafeName(filePath) + "）");
            }
            catch (Exception ex)
            {
                Log(ledger, "automation jump failed: " + ex.Message);
            }
            return Task.CompletedTask;
        }

        static string SafeName(string path)
        {
            try
            {
                string name = Path.GetFileName(path ?? "");
                return name.Length == 0 ? (path ?? "") : name;
            }
            catch
            {
                return path ?? "";
            }
        }

        static void Log(Ledger? ledger, string line)
        {
            try
            {
                ledger?.Log(line);
            }
            catch
            {
            }
        }
    }
}
