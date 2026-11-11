using System;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 自动化触发器：打开课件时触发一次（2026-11-11 用户第 3 条需求）。
    ///
    /// 没有设置项，所以用非泛型 <c>TriggerBase</c>，注册走 <c>AddTrigger&lt;T&gt;()</c>。
    /// 数据源同样是 <see cref="SlideSyncService.SlideProgressChanged"/>：只读快照里的
    /// <see cref="SlideProgress.DeckOpen"/>，在「没课件 → 有课件」的跳变沿触发一次。
    ///
    /// 为什么按跳变沿而不是「只要开着就触发」：后者会在每次心跳都点着自动化。
    /// 为什么加载时先播种不触发：工作流加载那一刻课件可能已经开着（那不是「刚打开」），
    /// 先记下当前状态，等真正发生 开→关 / 关→开 跳变时再触发，避免 ClassIsland 一启动就误触发。
    /// </summary>
    [TriggerInfo(Id, Name, Glyph)]
    public sealed class DeckOpenedTrigger : TriggerBase
    {
        public const string Id = "ClassSlideSync.Plugin.DeckOpened";
        public const string Name = "打开课件时";
        /// <summary>Segoe Fluent Icons: OpenFile。</summary>
        public const string Glyph = "\uE8E5";

        SlideSyncService? service;

        /// <summary>上一拍的「有没有课件」。跳变沿判定的另一半。</summary>
        bool wasOpen;

        /// <summary>是否已经用第一份快照播过种。播种那次不触发（见类注释）。</summary>
        bool seeded;

        /// <summary>真的调过几次 Trigger()。自测用。</summary>
        public int FireCount { get; private set; }

        /// <summary>最近一次触发时看到的快照。自测/日记用。</summary>
        public SlideProgress? LastFired { get; private set; }

        public override void Loaded()
        {
            Hook();
            var p = service?.Progress;
            if (p != null) Consider(p);
        }

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
                Log("deck-opened trigger hook failed: " + ex.Message);
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

        /// <summary>判一次：是不是「刚打开」。public 是为了自测能直接喂快照。</summary>
        public bool Consider(SlideProgress progress)
        {
            try
            {
                if (progress == null) return false;
                bool open = progress.DeckOpen;

                if (!seeded)
                {
                    seeded = true;
                    wasOpen = open;
                    return false;
                }

                bool fire = open && !wasOpen;
                wasOpen = open;
                if (!fire) return false;

                Fire(progress);
                return true;
            }
            catch (Exception ex)
            {
                Log("deck-opened trigger evaluate failed: " + ex.Message);
                return false;
            }
        }

        void Fire(SlideProgress progress)
        {
            FireCount++;
            LastFired = progress;
            Log("trigger fire: " + Name + "（" + progress.Describe() + "）");
            Trigger();
        }

        void Log(string line)
        {
            try { service?.Ledger?.Log(line); } catch { }
        }
    }

    /// <summary>
    /// 自动化触发器：关闭课件时触发一次（2026-11-11 用户第 3 条需求）。
    /// 与 <see cref="DeckOpenedTrigger"/> 镜像：在「有课件 → 没课件」的跳变沿触发一次。
    /// 同样加载时先播种不触发，避免工作流加载时课件恰好关着就误触发。
    /// </summary>
    [TriggerInfo(Id, Name, Glyph)]
    public sealed class DeckClosedTrigger : TriggerBase
    {
        public const string Id = "ClassSlideSync.Plugin.DeckClosed";
        public const string Name = "关闭课件时";
        /// <summary>Segoe Fluent Icons: ChromeClose。</summary>
        public const string Glyph = "\uE8BB";

        SlideSyncService? service;
        bool wasOpen;
        bool seeded;

        public int FireCount { get; private set; }
        public SlideProgress? LastFired { get; private set; }

        public override void Loaded()
        {
            Hook();
            var p = service?.Progress;
            if (p != null) Consider(p);
        }

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
                Log("deck-closed trigger hook failed: " + ex.Message);
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

        /// <summary>判一次：是不是「刚关闭」。public 是为了自测能直接喂快照。</summary>
        public bool Consider(SlideProgress progress)
        {
            try
            {
                if (progress == null) return false;
                bool open = progress.DeckOpen;

                if (!seeded)
                {
                    seeded = true;
                    wasOpen = open;
                    return false;
                }

                bool fire = !open && wasOpen;
                wasOpen = open;
                if (!fire) return false;

                Fire(progress);
                return true;
            }
            catch (Exception ex)
            {
                Log("deck-closed trigger evaluate failed: " + ex.Message);
                return false;
            }
        }

        void Fire(SlideProgress progress)
        {
            FireCount++;
            LastFired = progress;
            Log("trigger fire: " + Name + "（" + progress.Describe() + "）");
            Trigger();
        }

        void Log(string line)
        {
            try { service?.Ledger?.Log(line); } catch { }
        }
    }
}