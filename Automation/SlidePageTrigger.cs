using System;
using System.Collections.Generic;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 触发器「课件讲到第 N 页」的设置。
    ///
    /// 只需要两个字段：讲到第几页、算不算别的课件。POCO 必须能公共无参构造——
    /// 宿主的设置控件在 <c>GetInstance</c> 里 null 时就是 new 一个它（见
    /// <see cref="SlidePageReachedSettingsControl"/>）。
    /// </summary>
    public sealed class SlidePageReachedSettings
    {
        /// <summary>讲到第几页（1-999，默认 1）。超出范围的值在使用时被夹回去，不改写用户填的数字。</summary>
        public int Page { get; set; } = SlidePageReachedTrigger.MinPage;

        /// <summary>
        /// false（默认）= 只认「正在放映的那份课件」；true = 演示软件里打开着的课件翻到这一页也算。
        ///
        /// 为什么要有这个开关：老师课中经常把课件停在某个文件上翻来翻去（编辑视图、放映窗口被
        /// 别的窗口挡住、WPS 的放映窗口识别不到），这时候 Page.Current 一样会跳，但它不是
        /// "讲到"。默认只认放映中的，避免误触发；需要宽松点的老师可以把它勾上。
        /// </summary>
        public bool AnyDeck { get; set; }
    }

    /// <summary>
    /// 自动化触发器：课件讲到第 N 页时触发一次。
    ///
    /// 取证（ClassIsland 2.1.0.1 程序集 + api.docs，详见 stability-report-trigger.txt）：
    ///   * 基类 <c>ClassIsland.Core.Abstractions.Automation.TriggerBase</c>，泛型版
    ///     <c>TriggerBase&lt;TSettings&gt;</c> 提供 <c>protected T Settings</c>（读 SettingsInternal）；
    ///   * 注册靠 <c>TriggerRegistryExtensions.AddTrigger&lt;TTrigger, TSettingsControl&gt;()</c>，
    ///     与行动的双泛型同构：触发器与设置控件各注册成一条 keyed transient（key = TriggerInfo.Id）；
    ///   * <c>[TriggerInfo(id, name, iconGlyph)]</c> 是必需的，缺了 AddTrigger 会抛异常；
    ///   * 声明触发用 <c>protected Trigger()</c>（还有 TriggerRevert()，本触发是一次性事件，不用它）。
    ///
    /// 数据来源是 <see cref="SlideSyncService.SlideProgressChanged"/>：引擎每次状态心跳把
    /// 「现在讲到哪一页」抄成 <see cref="SlideProgress"/> 快照，这里只读快照，不碰 COM、
    /// 不新开线程、不轮询——插件里已经有引擎在盯 PowerPoint/WPS 了，触发器再盯一遍就是自找双份状态。
    /// </summary>
    [TriggerInfo(Id, Name, Glyph)]
    public sealed class SlidePageReachedTrigger : TriggerBase<SlidePageReachedSettings>
    {
        /// <summary>宿主注册表里的键。带插件前缀，避免和别的插件撞 Id。</summary>
        public const string Id = "ClassSlideSync.Plugin.SlidePageReached";

        /// <summary>触发器列表里显示的名字。</summary>
        public const string Name = "课件讲到第 N 页";

        /// <summary>触发器列表里的图标（Segoe Fluent Icons: LightningBolt，表示"触发"）。</summary>
        public const string Glyph = "\uE945";

        public const int MinPage = 1;
        public const int MaxPage = 999;

        /// <summary>记住多少条「已触发」记录。老师一个学期的课也就几百份课件，64 条够用。</summary>
        const int MaxRemembered = 64;

        /// <summary>
        /// 已触发过的「课次 + 课件」。同一节课同一份课件只触发一次：
        /// 老师翻回第 3 页再看一遍、或者跳页时在 N 页上停两下，都不应该再触发。
        /// 换一节课 / 换一份课件就会得到新键，于是重新允许触发。
        ///
        /// 放在实例字段里而不是静态字段：每个工作流有自己的触发器实例，
        /// 两个工作流都配了「第 5 页」时应该各触发一次，不能被彼此吃掉。
        /// </summary>
        readonly HashSet<string> fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly object gate = new object();

        SlideSyncService? service;

        /// <summary>真的调过几次 <c>Trigger()</c>。自测读它判断"触发了几次"，宿主不用它。</summary>
        public int FireCount { get; private set; }

        /// <summary>最近一次触发时看到的快照（日记/自测用）。</summary>
        public SlideProgress? LastFired { get; private set; }

        /// <summary>
        /// 宿主在把触发器挂进工作流之后调用。这里挂事件，并且立刻用当前快照判一次：
        /// 触发器可能是在课件已经停在 / 已经讲过第 N 页之后才被加上或重新加载的，
        /// 那种情况下不该等老师再翻一页。
        /// </summary>
        public override void Loaded()
        {
            Hook();
            var p = service?.Progress;
            if (p != null) Consider(p);
        }

        /// <summary>宿主卸载工作流时调用。必须退订：实例被复用（工作流重新加载）时不能挂两份回调。</summary>
        public override void UnLoaded()
        {
            Unhook();
        }

        void Hook()
        {
            try
            {
                // 服务由 Plugin.Initialize 注册成单例；ActionBase 那边已有同样的取法（TryService），
                // 这里复用，免得插件里出现第二种"怎么拿服务"的写法。
                var svc = OpenUnfinishedDeckAction.TryService();
                if (svc == null) return;
                if (ReferenceEquals(svc, service)) return;
                Unhook();
                service = svc;
                svc.SlideProgressChanged += OnProgress;
            }
            catch (Exception ex)
            {
                Log("trigger hook failed: " + ex.Message);
            }
        }

        void Unhook()
        {
            var svc = service;
            service = null;
            if (svc == null) return;
            try { svc.SlideProgressChanged -= OnProgress; } catch { }
        }

        void OnProgress(SlideProgress progress)
        {
            Consider(progress);
        }

        /// <summary>
        /// 判一次：这一页该不该触发。真触发时返回 true。
        ///
        /// public 是为了自测能直接喂快照（不需要真去开 PowerPoint）；宿主走事件那条路，
        /// 不会调它。
        /// </summary>
        public bool Consider(SlideProgress progress)
        {
            try
            {
                if (progress == null) return false;

                int page = TargetPage();
                // 没有课件 / 页码未知：什么都不做，也绝不抛（自动化里抛异常会打断整条工作流）。
                if (!progress.DeckOpen || progress.Current <= 0) return false;
                if (!progress.Showing && !AnyDeck()) return false;
                if (progress.Current != page) return false;

                string key = DedupeKey(progress);
                lock (gate)
                {
                    if (fired.Contains(key)) return false;
                    fired.Add(key);
                    // 只防内存无限长：清空等于"忘了以前触发过"，最多让同一份课件多触发一次，
                    // 比让集合无限长好。
                    if (fired.Count > MaxRemembered) fired.Clear();
                }

                Fire(progress, page);
                return true;
            }
            catch (Exception ex)
            {
                Log("trigger evaluate failed: " + ex.Message);
                return false;
            }
        }

        void Fire(SlideProgress progress, int page)
        {
            FireCount++;
            LastFired = progress;
            Log("trigger fire: " + Name + " → 第 " + page + " 页（" + progress.Describe() + "）");
            // 基类的 protected Trigger()：它内部去触发 Triggered 事件，工作流接到之后跑行动。
            // 这个触发是一次性事件（"讲到了"），没有"不再讲到"的状态，所以不调 TriggerRevert()。
            Trigger();
        }

        /// <summary>设置里的 N，夹在 1-999。宿主还没注入设置时 <c>Settings</c> 返回 null，回落到 1。</summary>
        int TargetPage()
        {
            var settings = TrySettings();
            int page = settings?.Page ?? MinPage;
            return Math.Clamp(page, MinPage, MaxPage);
        }

        bool AnyDeck()
        {
            var settings = TrySettings();
            return settings != null && settings.AnyDeck;
        }

        // 宿主是先 new 实例、再塞 SettingsInternal 的，构造/加载期间读到 null 是正常情形。
        SlidePageReachedSettings? TrySettings()
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

        /// <summary>
        /// 去重键 = 课次 + 课件。
        ///
        /// 课次优先用课表读数（"数学|第 3 节|3"）；没有课表时退到当天日期（<see cref="SlideProgress.DayKey"/>），
        /// 这样"今天上这节课时触发过一次"仍然是当天有效——第二天再讲同一份课件会重新触发。
        /// 课件用完整路径；路径拿不到时退到文件名，再拿不到就用页码兜底（宁可多触发，也不要因为
        /// 拿不到路径就永不触发）。
        /// </summary>
        static string DedupeKey(SlideProgress progress)
        {
            string lesson = progress.LessonKey.Length > 0 ? progress.LessonKey : progress.DayKey;
            string deck = progress.DeckPath.Length > 0
                ? progress.DeckPath
                : (progress.DeckName.Length > 0 ? progress.DeckName : "#" + progress.Current);
            return lesson + "|" + deck;
        }

        void Log(string line)
        {
            try { service?.Ledger?.Log(line); } catch { }
        }
    }
}
