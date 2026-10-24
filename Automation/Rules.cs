using System;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 规则「上次课件没讲完」：给自动化的「如果…就…」当条件用。
    ///
    /// 2.1 的规则没有基类、也没有特性：它就是一个 Id + 一个 <c>bool Invoke(object settings)</c>
    /// 处理器，注册在 Plugin.Initialize 的 services.AddRule(...)（文档与反射结论见
    /// stability-report-automation.txt）。处理器不抛异常——规则引擎在一次判定里会调多条规则，
    /// 一条抛了会让整套规则集的结果都不确定。
    /// </summary>
    public static class UnfinishedDeckRule
    {
        public const string Id = "ClassSlideSync.Plugin.UnfinishedDeck";
        public const string Name = "上次课件没讲完";
        public const string Glyph = "\uE7BA";   // Segoe Fluent Icons: Warning

        /// <summary>
        /// 判定条件（用户原话）：最近一条记录 <c>SlideCount &gt; 0 &amp;&amp; EndSlide &lt; SlideCount</c>
        /// 且这条记录不超过 21 天。
        ///
        /// 取“全库最近一条记录”再判断它讲没讲完，而不是“最近一条未讲完的记录是否存在”。
        /// 后者在“上节课讲完了、再上一节没讲完”时会错误地成立，老师看到的就是
        /// “明明讲完了，自动化还说没讲完”。
        /// </summary>
        public static bool Evaluate(Ledger? ledger, DateTime now)
        {
            if (ledger == null) return false;
            // 不用 ledger.Newest()：它按 StartedAt 的**字符串**比大小，而台账里的时间是自由文本
            // （"2026/9/8" vs "2026-09-30" 会排反），于是“最近一条”可能取到旧记录，规则与行动
            // 指向两份不同的课件。UnfinishedDecks.NewestRecord 用解析后的时间，并且和行动共用
            // 同一个比较器 —— 这就是“两边用同一把尺子”。（Ledger.cs 不在本次改动范围。）
            var newest = UnfinishedDecks.NewestRecord(ledger);
            if (newest == null) return false;
            return UnfinishedDecks.IsUnfinished(newest) && UnfinishedDecks.IsFresh(newest, now);
        }

        /// <summary>
        /// ClassIsland 规则引擎调用入口。拿不到服务（插件尚未启动完 / 已停止）时安静返回 false：
        /// 规则是查询，不是命令，缺数据时“不成立”比抛异常安全。
        /// </summary>
        public static bool Handle(object settings)
        {
            try
            {
                var service = IAppHost.TryGetService<SlideSyncService>();
                var ledger = service?.Ledger;
                return Evaluate(ledger, DateTime.Now);
            }
            catch
            {
                return false;
            }
        }
    }
}
