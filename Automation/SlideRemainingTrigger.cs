using System;
using System.Collections.Generic;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 触发器「课件剩余约 N 分钟」的设置。只有一个字段：剩余几分钟时触发。
    /// POCO 必须能公共无参构造（宿主设置控件在 GetInstance 里 null 时 new 一个它）。
    /// </summary>
    public sealed class SlideRemainingSettings
    {
        /// <summary>剩余约几分钟时触发（1-999，默认 5）。超出范围的值在使用时被夹回去，不改写用户填的数字。</summary>
        public int Minutes { get; set; } = SlideRemainingTrigger.DefaultMinutes;
    }

    /// <summary>
    /// 自动化触发器：课件剩余约 N 分钟时触发一次（2026-11-11 用户第 3 条需求）。
    ///
    /// 与「课件讲到第 N 页」同构：数据源是 <see cref="SlideSyncService.SlideProgressChanged"/> 推来的
    /// <see cref="SlideProgress"/> 快照，这里只读快照里的 <see cref="SlideProgress.RemainingMinutes"/>
    /// （引擎按实测翻页速度估算，见 Core/PagePace.cs），不碰 COM、不新开线程、不轮询。
    ///
    /// 剩余时间只在「真的在放映」时才算得出（编辑视图没有翻页速度可言），所以本触发天然只认放映中的课件，
    /// 不需要「任何课件都算」开关。同一节课同一份课件只触发一次：剩余时间第一次降到 N 分钟以内时点着，
    /// 之后继续往下走不会重复触发；换课件 / 换下一节课后重新允许触发。
    /// </summary>
    [TriggerInfo(Id, Name, Glyph)]
    public sealed class SlideRemainingTrigger : TriggerBase<SlideRemainingSettings>
    {
        /// <summary>宿主注册表里的键。带插件前缀，避免和别的插件撞 Id。</summary>
        public const string Id = "ClassSlideSync.Plugin.SlideRemaining";

        /// <summary>触发器列表里显示的名字。</summary>
        public const string Name = "课件剩余约 N 分钟";

        /// <summary>触发器列表里的图标（Segoe Fluent Icons: Clock，表示「时间」）。</summary>
        public const string Glyph = "\uE823";

        public const int MinMinutes = 1;
        public const int MaxMinutes = 999;
        public const int DefaultMinutes = 5;

        /// <summary>记住多少条「已触发」记录。和页码触发器同一个经验值。</summary>
        const int MaxRemembered = 64;

        /// <summary>
        /// 已触发过的「课次 + 课件」。同一节课同一份课件只触发一次（剩余时间第一次降到阈值以内时）。
        /// 实例字段而非静态：每个工作流有自己的触发器实例，互不吃掉对方的记录。
        /// </summary>
        readonly HashSet<string> fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        readonly object gate = new object();

        SlideSyncService? service;

        /// <summary>真的调过几次 <c>Trigger()</c>。自测读它，宿主不用。</summary>
        public int FireCount { get; private set; }

        /// <summary>最近一次触发时看到的快照（日记/自测用）。</summary>
        public SlideProgress? LastFired { get; private set; }

        /// <summary>
        /// 宿主把触发器挂进工作流后调用。挂事件并立刻用当前快照判一次：
        /// 触发器可能是在剩余时间已经降到 N 分钟以内之后才被加上/重新加载的。
        /// </summary>
        public override void Loaded()
        {
            Hook();
            var p = service?.Progress;
            if (p != null) Consider(p);
        }

        /// <summary>宿主卸载工作流时调用。必须退订，实例复用时不能挂两份回调。</summary>
        public override void UnLoaded()
        {
            Unhook();
        }

        void Hook()
        {
            try
            {
                var svc = OpenUnfinishedDeckAction.TryService();
                if (svc == null) return;
                if (ReferenceEquals(svc, service)) return;
                Unhook();
                service = svc;
                svc.SlideProgressChanged += OnProgress;
            }
            catch (Exception ex)
            {
                Log("remaining trigger hook failed: " + ex.Message);
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
        /// 判一次：剩余时间该不该触发。真触发时返回 true。public 是为了自测能直接喂快照。
        /// </summary>
        public bool Consider(SlideProgress progress)
        {
            try
            {
                if (progress == null) return false;
                // 没课件 / 没在放映：剩余时间无从谈起。
                if (!progress.DeckOpen || !progress.Showing) return false;

                int remaining = progress.RemainingMinutes;
                // 0 = 还没估出速度 / 已经讲完，不触发。
                if (remaining <= 0) return false;
                if (remaining > TargetMinutes()) return false;

                string key = DedupeKey(progress);
                lock (gate)
                {
                    if (fired.Contains(key)) return false;
                    fired.Add(key);
                    if (fired.Count > MaxRemembered) fired.Clear();
                }

                Fire(progress, remaining);
                return true;
            }
            catch (Exception ex)
            {
                Log("remaining trigger evaluate failed: " + ex.Message);
                return false;
            }
        }

        void Fire(SlideProgress progress, int remaining)
        {
            FireCount++;
            LastFired = progress;
            Log("trigger fire: " + Name + " → 剩余约 " + remaining + " 分钟（" + progress.Describe() + "）");
            // 一次性事件（「快讲完了」），没有对应的「不再快讲完」状态，不调 TriggerRevert()。
            Trigger();
        }

        /// <summary>设置里的 N，夹在 1-999。宿主还没注入设置时 Settings 返回 null，回落到默认 5。</summary>
        int TargetMinutes()
        {
            var settings = TrySettings();
            int minutes = settings?.Minutes ?? DefaultMinutes;
            return Math.Clamp(minutes, MinMinutes, MaxMinutes);
        }

        SlideRemainingSettings? TrySettings()
        {
            try { return Settings; }
            catch { return null; }
        }

        /// <summary>去重键 = 课次 + 课件（与页码触发器同一套退让规则）。</summary>
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