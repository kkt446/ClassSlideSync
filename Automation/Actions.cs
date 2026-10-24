using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 行动一：打开上次没讲完的课件，并等这份课件被识别出来后跳回停下的那一页。
    ///
    /// 注册方式见 Plugin.Initialize 的 services.AddAction&lt;OpenUnfinishedDeckAction&gt;()：
    /// 2.1 的行动必须继承 <see cref="ActionBase"/> 且标注 <see cref="ActionInfoAttribute"/>，
    /// 注册器会按特性里的 Id 把类型注册成 keyed transient。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class OpenUnfinishedDeckAction : ActionBase
    {
        // Id 带插件前缀：ClassIsland 的行动/规则 Id 是全局字典，两个插件都用 "OpenDeck" 会互相顶掉。
        public const string Id = "ClassSlideSync.Plugin.OpenUnfinishedDeck";
        public const string Name = "打开上次未讲完的课件";
        public const string Glyph = "\uE8E5";   // Segoe Fluent Icons: OpenFile

        protected override Task OnInvoke()
        {
            // ActionBase.OnInvoke 的 API 文档明确要求重写时先调 base.OnInvoke()。
            base.OnInvoke();
            // 打开 PowerPoint 要几秒、等它被 COM 识别可能几十秒。行动如果在这里 await，
            // 同一套自动化里排在后面的行动（比如“显示悬浮卡片”）就会被一起卡住，
            // 所以只起一个后台任务，OnInvoke 立刻返回。
            Task.Run(OpenInBackground);
            return Task.CompletedTask;
        }

        static void OpenInBackground()
        {
            var service = TryService();
            var ledger = service?.Ledger;
            if (ledger == null) return;   // 服务还没起来：安静返回，绝不把异常抛回自动化

            // Task.Run 里抛出的异常没有人接（TaskScheduler 只会把它扔掉），
            // 表现就是“自动化点了没反应、日志里一个字都没有”。整段兜住，至少留一条日志。
            try
            {
                var record = UnfinishedDecks.NewestUnfinished(ledger, DateTime.Now, true);
                if (record == null)
                {
                    ledger.Log("automation: 台账里没有 21 天内、文件还在的未讲完课件");
                    return;
                }

                string path = UnfinishedDecks.ResolveExistingFile(record.FilePath);
                if (path.Length == 0)
                {
                    ledger.Log("automation: 最近一条未讲完的记录没有可打开的磁盘文件: " + record.FilePath);
                    return;
                }

                // 连点护栏：老师连点两下会拉起两个演示实例，第二个还会把第一个的放映顶掉。
                // 卡片按钮那条路一直有 8 秒护栏（SlideSyncService.OnOpenDeckRequested），
                // 自动化这条路以前没有 —— 同一份课件可以被两条行动各开一次。
                if (OpenThrottle.IsRepeat(Ledger.Key(path), DateTime.UtcNow))
                {
                    ledger.Log("automation: 8 秒内已经打开过同一份课件，忽略这次重复触发: " + path);
                    return;
                }

                try
                {
                    // UseShellExecute=true：按系统默认程序打开 .pptx（PowerPoint 或 WPS 都行）。
                    // 插件不去猜老师装的是哪套，也不自己指定 exe——那会把默认程序设置绕过去。
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    ledger.Log("automation: 打开失败 " + ex.Message);
                    return;
                }

                int slide = UnfinishedDecks.JumpPage(record);
                if (slide <= 0)
                {
                    // 坏记录（EndSlide<=0，多半是手改/导入的台账）：课件照样打开，但没有页码可跳。
                    // 以前这里固定写“待跳第 0 页”再调 Arm，而 Arm 见到 slide<=0 会立刻返回，
                    // 日志于是在说一件没发生的事，排查时会被带偏。
                    ledger.Log("automation: 打开 " + path + "，记录里没有可用页码（EndSlide="
                               + record.EndSlide + "），只打开不跳页");
                    return;
                }

                ledger.Log("automation: 打开 " + path + "，待跳第 " + slide + " 页");
                // 待跳页先记在内存里：此时套件还没把课件加载完，立刻调 COM 只会失败；
                // 后台轮询到 engine 认出这份文件后再交给它（真正跳页仍由引擎工作线程执行）。
                PendingJump.Arm(service!, path, slide);
            }
            catch (Exception ex)
            {
                try { ledger.Log("automation: 打开未讲完课件时出错 " + ex.Message); } catch { }
            }
        }

        internal static SlideSyncService? TryService()
        {
            try { return IAppHost.TryGetService<SlideSyncService>(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 行动二的“连点护栏”：同一份课件 N 秒内只允许拉起一次。
    ///
    /// 单独抽成一个不依赖宿主类型的静态类，是为了能在不启动 ClassIsland 的反射测试里
    /// 直接断言边界（0 秒、正好 N 秒、系统时间被往回调）。状态是进程级的静态字段，
    /// 和行动实例的生命周期无关 —— 2.1 的行动是 transient，每次触发都是新实例。
    /// </summary>
    internal static class OpenThrottle
    {
        /// <summary>和卡片按钮那条路用的是同一个经验值（见 SlideSyncService.OnOpenDeckRequested）。</summary>
        public const int RepeatGuardSeconds = 8;

        static readonly object gate = new object();
        static string lastKey = "";
        static DateTime lastUtc = DateTime.MinValue;

        /// <summary>
        /// 这次打开是不是 N 秒内的重复触发。调用即记录本次尝试（所以“先问后开”不会漏记）。
        /// </summary>
        /// <param name="key">要打开的课件的台账键（大小写/分隔符已被 <see cref="Ledger.Key"/> 统一）。</param>
        /// <param name="nowUtc">当前 UTC 时间；用 UTC 而不是本地时间，免得夏令时/手动改钟造成负间隔。</param>
        public static bool IsRepeat(string? key, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (gate)
            {
                // nowUtc 倒退（改钟、或调用方传了旧时间戳）不算重复：否则一次时间回拨
                // 会让这份课件在“未来 N 秒”里都打不开。倒退时按新起点重新计时。
                bool repeat = lastKey == key
                              && lastUtc != DateTime.MinValue
                              && nowUtc >= lastUtc
                              && (nowUtc - lastUtc).TotalSeconds < RepeatGuardSeconds;
                if (!repeat)
                {
                    lastKey = key!;
                    lastUtc = nowUtc;
                }
                return repeat;
            }
        }
    }

    /// <summary>
    /// 行动二：显示 / 隐藏悬浮卡片。
    ///
    /// 做成一条“切换”而不是显示、隐藏两条：老师用这个行动的动机只有“现在别挡着”和
    /// “现在要用”，当前是否待命就是唯一需要知道的输入，不需要在设置里再选一次。
    /// </summary>
    [ActionInfo(Id, Name, Glyph, true, AutomationGroups.Plugin)]
    public sealed class ToggleCardAction : ActionBase
    {
        public const string Id = "ClassSlideSync.Plugin.ToggleCard";
        public const string Name = "显示 / 隐藏悬浮卡片";
        public const string Glyph = "\uE890";   // Segoe Fluent Icons: View

        protected override Task OnInvoke()
        {
            base.OnInvoke();
            try
            {
                var service = OpenUnfinishedDeckAction.TryService();
                if (service == null) return Task.CompletedTask;
                // 状态判断放在行动这一层，服务只提供“显示”“隐藏”两个最小入口。
                // 注意不能用 Armed 判断可见性：卡片被 15 秒自动收起或老师手动收回后 armed 仍为真，
                // 那样「显示悬浮卡片」会走到隐藏分支，把引擎也一起停掉（这节课从此不记台账）。
                if (service.CardVisible) service.HideCardForAutomation("自动化：隐藏悬浮卡片");
                else service.ShowCardForAutomation("自动化：显示悬浮卡片");
            }
            catch
            {
                // 自动化里抛异常会打断整套行动，而且老师看不到堆栈；安静地什么都不做。
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 行动一打开课件后的“等识别再跳页”。等待是后台的，且同一时间只保留一个等待：
    /// 老师连点两次行动时，旧的那次应该作废，而不是两次都跳（第二次可能跳到旧页）。
    ///
    /// 生命周期约定（以前三条都没做到，句柄和假日志就是这么来的）：
    /// 1. 每次 Arm 都取消上一次；被取消的那次不再写“等了 120 秒没识别到”的日志；
    /// 2. 每个 CTS 只由它自己那个后台任务在 finally 里 Dispose（不跨线程释放正在 WaitOne 的句柄）；
    /// 3. 任务收尾时把 current 清空，超时/成功/被顶掉三条路都不留悬挂引用。
    /// </summary>
    internal static class PendingJump
    {
        internal const int WaitSeconds = 120;

        static readonly object gate = new object();
        static CancellationTokenSource? current;

        public static void Arm(SlideSyncService service, string path, int slide)
        {
            if (service == null || string.IsNullOrWhiteSpace(path) || slide <= 0) return;

            var cts = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (gate)
            {
                previous = current;
                current = cts;
            }

            // 取消放在锁外：Cancel() 会同步唤醒正在 token.WaitHandle.WaitOne 里的旧任务，
            // 持着 gate 调用等于把两个线程的锁顺序绑在一起（旧任务收尾时也要拿 gate）。
            // 只取消、不释放：旧的 CTS 由它自己的任务在 finally 里释放。
            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
            }

            Task.Run(() => Watch(service, path, slide, cts));
        }

        static void Watch(SlideSyncService service, string path, int slide, CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                string want = Ledger.Key(path);
                string name = Path.GetFileName(path);
                // 2 分钟够 PowerPoint 冷启动 + 大课件加载；再久就说明老师根本没等它，没必要一直轮询。
                DateTime deadline = DateTime.UtcNow.AddSeconds(WaitSeconds);

                while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var engine = service.Engine;
                        var snapshot = engine?.Last;
                        if (snapshot != null && snapshot.AppFound
                            && !string.IsNullOrEmpty(snapshot.FilePath)
                            && SameFile(snapshot.FilePath, want, name))
                        {
                            // 认出课件之后、真正抬跳页标志之前再查一次取消：老师可能正好在这一秒里
                            // 又点了一次行动，旧的那次不该再把放映拽回之前的页。
                            if (token.IsCancellationRequested) return;
                            // 只抬标志，不在这里做 COM：和卡片按钮走同一条 RequestJumpTo 路径，
                            // 真正的跳页仍由引擎工作线程在 Step 里执行。
                            engine!.RequestJumpTo(path, slide);
                            service.Ledger?.Log("automation: 已识别 " + path + "，请求跳到第 " + slide + " 页");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        try { service.Ledger?.Log("automation: 等课件时出错 " + ex.Message); } catch { }
                    }

                    try { token.WaitHandle.WaitOne(1000); } catch { }
                }

                // 只有真的等满 2 分钟才说“放弃”。被下一次行动顶掉时这次是“作废”，
                // 以前这里无条件写一条“等了 120 秒没识别到”，日志于是永远在说谎。
                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        service.Ledger?.Log("automation: 等了 " + WaitSeconds + " 秒没识别到 " + path + "，放弃跳页");
                    }
                    catch { }
                }
            }
            finally
            {
                // 收尾：只有 current 还指向自己时才清空（新的一次可能已经把它换掉了），
                // CTS 由本线程释放 —— 这样不会有谁在 WaitOne 一个已经 Dispose 的句柄。
                lock (gate)
                {
                    if (ReferenceEquals(current, cts)) current = null;
                }
                try { cts.Dispose(); } catch { }
            }
        }

        static bool SameFile(string observed, string want, string name)
        {
            if (want.Length > 0 && Ledger.Key(observed) == want) return true;
            // 个别套件只报窗口标题里的文件名；文件名对得上也算识别到。这里已经要求 engine
            // 真的挂着这份课件（snapshot 来自当前 bridge），同名不同目录的误判概率很低。
            return name.Length > 0
                && string.Equals(Path.GetFileName(observed), name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
