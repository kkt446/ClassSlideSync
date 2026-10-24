using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>「跳到指定页」的设置：一个 1-999 的页码。</summary>
    public sealed class JumpToPageSettings
    {
        /// <summary>目标页码，从 1 开始。默认 1。</summary>
        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// 行动六：跳到指定页。
    ///
    /// 和「跳到上次停的那一页」不同：页码由老师在行动设置里填，不读台账。
    /// 跳页本身复用 SlideSyncService 已有的公开入口 <c>RequestJump(int)</c>：
    /// 桥没挂上时它自己会安静失败，自动化线程不需要知道 PowerPoint/WPS 的细节。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class JumpToPageAction : ActionBase<JumpToPageSettings>
    {
        public const string Id = "ClassSlideSync.Plugin.JumpToPage";
        public const string Name = "跳到指定页";
        public const string Description = "把当前正在放映的课件跳到设置里填的那一页；没有在放映时安静跳过，只记日志。";
        public const string Glyph = "\uE7C3";   // Segoe Fluent Icons: Page

        protected override Task OnInvoke()
        {
            base.OnInvoke();
            Ledger? ledger = null;
            try
            {
                var settings = TrySettings();
                int page = Math.Clamp(settings?.Page ?? 1, 1, 999);

                var service = OpenUnfinishedDeckAction.TryService();
                if (service == null)
                {
                    Log(null, "automation jump-to: 服务还没起来，忽略（第 " + page + " 页）");
                    return Task.CompletedTask;
                }

                ledger = service.Ledger;
                service.RequestJump(page);
                Log(ledger, "automation jump-to: 已请求跳到第 " + page + " 页");
            }
            catch (Exception ex)
            {
                Log(ledger, "automation jump-to failed: " + ex.Message);
            }
            return Task.CompletedTask;
        }

        // 宿主通过 GetInstance 注入设置；直接 new 出来的实例（比如反射测试）没有注入，
        // getter 会抛 ArgumentNullException，所以这里统一兜住并回落到默认页码。
        JumpToPageSettings? TrySettings()
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

    /// <summary>
    /// 行动七：打开课件所在文件夹。
    ///
    /// SlideSyncService 里打开文件夹的链路（<c>OpenPanelFolder</c>/<c>TryOpenSubjectFolder</c>）
    /// 都是 private，本任务不允许改服务文件，所以这里按同样的两步做：
    /// 先走 ClassIsland 的 IUriNavigationService（和宿主自己的「打开文件夹」行为一致），
    /// 失败再回退 Process.Start(UseShellExecute=true)。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class OpenDeckFolderAction : ActionBase
    {
        public const string Id = "ClassSlideSync.Plugin.OpenDeckFolder";
        public const string Name = "打开课件所在文件夹";
        public const string Description = "按当前放映/台账里最新的课件路径，在资源管理器里打开它所在的文件夹；路径失效时安静跳过。";
        public const string Glyph = "\uE8B7";   // Segoe Fluent Icons: FolderOpen

        protected override Task OnInvoke()
        {
            base.OnInvoke();
            Ledger? ledger = null;
            try
            {
                var service = OpenUnfinishedDeckAction.TryService();
                if (service == null)
                {
                    Log(null, "automation folder: 服务还没起来，忽略");
                    return Task.CompletedTask;
                }

                ledger = service.Ledger;
                string path = DeckActions.ResolveDeckPath(service, ledger);
                string existing = UnfinishedDecks.ResolveExistingFile(path);
                if (existing.Length == 0)
                {
                    Log(ledger, "automation folder: 没有可打开的课件路径（当前或台账最新记录都为空/不存在）");
                    return Task.CompletedTask;
                }

                string folder = Path.GetDirectoryName(existing) ?? "";
                if (folder.Length == 0 || !Directory.Exists(folder))
                {
                    Log(ledger, "automation folder: 课件所在文件夹不存在，跳过：" + existing);
                    return Task.CompletedTask;
                }

                if (DeckActions.OpenFolder(folder))
                {
                    Log(ledger, "automation folder: 已打开「" + folder + "」");
                }
                else
                {
                    Log(ledger, "automation folder: 打开文件夹失败：「" + folder + "」");
                }
            }
            catch (Exception ex)
            {
                Log(ledger, "automation folder failed: " + ex.Message);
            }
            return Task.CompletedTask;
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

    /// <summary>
    /// 行动八：清除当前课件的记录。
    ///
    /// 只删台账里的历史（<c>Ledger.RemoveMany</c>），绝不碰课件文件本身。
    /// RemoveMany 接受台账键或路径，内部已经做了「两种都试 + 自己 Save + 写日志 + 不抛」，
    /// 这里只负责挑出「当前课件」的路径并把返回条数记进日志。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class ClearDeckHistoryAction : ActionBase
    {
        public const string Id = "ClassSlideSync.Plugin.ClearDeckHistory";
        public const string Name = "清除当前课件的记录";
        public const string Description = "把当前放映（或台账里最新）这份课件的讲课记录从台账里删掉；只删记录，不删课件文件。";
        public const string Glyph = "\uE74D";   // Segoe Fluent Icons: Delete

        protected override Task OnInvoke()
        {
            base.OnInvoke();
            Ledger? ledger = null;
            try
            {
                var service = OpenUnfinishedDeckAction.TryService();
                if (service == null)
                {
                    Log(null, "automation clear: 服务还没起来，忽略");
                    return Task.CompletedTask;
                }

                ledger = service.Ledger;
                if (ledger == null)
                {
                    Log(null, "automation clear: 台账还没加载，忽略");
                    return Task.CompletedTask;
                }

                string path = DeckActions.ResolveDeckPath(service, ledger);
                if (path.Length == 0)
                {
                    Log(ledger, "automation clear: 没有当前/上次课件，忽略");
                    return Task.CompletedTask;
                }

                int removed = ledger.RemoveMany(new[] { path });
                if (removed > 0)
                {
                    Log(ledger, "automation clear: 已清除「" + DeckActions.SafeName(path) + "」的 " + removed + " 条记录");
                }
                else
                {
                    Log(ledger, "automation clear: 台账里没有「" + DeckActions.SafeName(path) + "」的记录，忽略");
                }
            }
            catch (Exception ex)
            {
                Log(ledger, "automation clear failed: " + ex.Message);
            }
            return Task.CompletedTask;
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

    /// <summary>上面几个行动共用的小工具；不依赖实例，失败一律安静返回。</summary>
    internal static class DeckActions
    {
        /// <summary>
        /// 当前放映课件的路径优先；没有在放映时退回台账最新一条。
        /// 注意不要对路径做「文件必须存在」的过滤：清除记录时文件可能已经被老师移走了。
        /// </summary>
        internal static string ResolveDeckPath(SlideSyncService? service, Ledger? ledger)
        {
            try
            {
                PptSnapshot? snap = service?.Engine?.Last;
                string path = snap == null ? "" : (snap.FilePath ?? "");
                if (path.Length > 0) return path;
                return ledger?.Newest()?.FilePath ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>先走宿主的 URI 导航，失败再回退 ShellExecute。返回是否已发起。</summary>
        internal static bool OpenFolder(string folder)
        {
            try
            {
                var nav = IAppHost.TryGetService<IUriNavigationService>();
                if (nav != null)
                {
                    nav.Navigate(new Uri(folder));
                    return true;
                }
            }
            catch
            {
                // 宿主导航服务不可用时继续走下面的 ShellExecute。
            }

            try
            {
                Process.Start(new ProcessStartInfo(folder)
                {
                    UseShellExecute = true,
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static string SafeName(string path)
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
    }
}
