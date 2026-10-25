using System;
using System.Collections.Generic;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 卡片上「还剩 N 页 · 约 M 分钟」背后的全部算法（2026-10-24 功能1）。
    ///
    /// 全是纯函数：只用传进来的台账记录，不碰台账、不碰窗口，测试可以直接喂数组进来断言。
    /// 为什么单独一个文件而不是塞进 SessionEngine：这套数字的边界（脏数据、兜底、夹取）
    /// 比调用它的地方更容易出错，独立出来才好一条条测。
    /// </summary>
    public static class PagePace
    {
        /// <summary>
        /// 每页耗时的合理区间。低于 8 秒基本只有两种情况：那条记录本身是脏的（台账被改过、
        /// 或者是一节课里来回翻同一页），或者老师点了"从第 N 页放映"之后立刻关掉。
        /// 高于 600 秒（10 分钟一页）说明那节课大部分时间根本没在放课件。
        /// 两种情况都别让它们把"还剩几分钟"带偏。
        /// </summary>
        public const int MinSecondsPerPage = 8;
        public const int MaxSecondsPerPage = 600;

        /// <summary>台账里一条可用记录都没有时的兜底：一节 40 分钟的课讲 40 页左右。</summary>
        public const int DefaultSecondsPerPage = 60;

        /// <summary>每份课件在台账里最多留这么多条（Ledger.Add 的截断上限），取平均用不着更多。</summary>
        public const int MaxRecordsPerDeck = 200;

        /// <summary>
        /// 「本次课实测速度」的最少样本（2026-10-25 功能2）：至少翻过 3 页、至少上了 60 秒，
        /// 否则这一场还没资格代表老师在这份课件上的速度。
        ///
        /// 为什么是这两个数：刚开场 20 秒里翻两页，很可能是在找上次讲到哪儿，
        /// 也可能是从第 3 页直接跳到第 30 页（一次翻 27 页），算出来的"每页 1 秒"
        /// 会把「还剩 N 页 · 约 M 分钟」直接压成 1 分钟。宁可用历史均值，也不要这种噪音。
        /// </summary>
        public const int MinSessionPages = 3;
        public const int MinSessionSeconds = 60;

        /// <summary>
        /// 每页平均秒数（老重载，只有历史记录）：优先这份课件自己的记录，它没数据就退回
        /// 全台账，再没有就用 60 秒，最后夹进 [8, 600] 秒。<paramref name="deck"/> 允许为 null 或空。
        /// </summary>
        public static double SecondsPerPage(IList<SessionRecord> deck, IList<SessionRecord> all)
        {
            return SecondsPerPage(deck, all, 0, 0);
        }

        /// <summary>
        /// 每页平均秒数（2026-10-25 功能2 的回落链）：
        ///   本次课实测速度（翻页数 ≥3 且已用时长 ≥60 秒）
        ///   → 这份课件的台账均值
        ///   → 全台账均值
        ///   → 60 秒/页
        /// 最后统一夹进 [8, 600] 秒。会话样本为 0/0 时行为和上一版完全一样。
        /// </summary>
        public static double SecondsPerPage(IList<SessionRecord> deck, IList<SessionRecord> all,
                                           int sessionPages, double sessionSeconds)
        {
            double v = SessionSecondsPerPage(sessionPages, sessionSeconds);
            if (v <= 0) v = Average(deck);
            if (v <= 0) v = Average(all);
            if (v <= 0) v = DefaultSecondsPerPage;
            return ClampSecondsPerPage(v);
        }

        /// <summary>
        /// 本次课已翻页数 ÷ 已用时长。样本不够（不足 3 页或不足 60 秒，或者时长不是
        /// 有效正数）返回 -1，调用方据此回退历史均值 —— 用 -1 而不是 0 当哨兵值，
        /// 是因为 0 秒/页在数学上像是个"有效答案"，容易被误用。
        /// </summary>
        public static double SessionSecondsPerPage(int pages, double seconds)
        {
            if (pages < MinSessionPages) return -1;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return -1;
            if (seconds < MinSessionSeconds) return -1;
            double v = seconds / pages;
            if (v <= 0) return -1;
            return ClampSecondsPerPage(v);
        }

        /// <summary>
        /// 把每页耗时夹进合理区间。非正数（含 NaN）返回 0 表示"没有可用值" —— 由调用方
        /// 决定退回什么兜底值；已经合理的值原样返回。
        /// </summary>
        public static double ClampSecondsPerPage(double v)
        {
            if (double.IsNaN(v) || v <= 0) return 0;
            if (v < MinSecondsPerPage) return MinSecondsPerPage;
            if (v > MaxSecondsPerPage) return MaxSecondsPerPage;
            return v;
        }

        /// <summary>
        /// ΣSeconds / Σ每页页数。只统计真的翻过去了（EndSlide &gt; StartSlide）的会话：
        /// 一页没翻的那条既不是"一页的耗时"、也没有分母，算进来只会把每页耗时拉低。
        /// 一条可用记录都没有时返回 -1（调用方据此决定要不要退回全台账）。
        /// </summary>
        public static double Average(IList<SessionRecord> records)
        {
            if (records == null) return -1;
            double seconds = 0;
            long pages = 0;
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r == null || r.EndSlide <= r.StartSlide) continue;
                int n = r.EndSlide - r.StartSlide + 1;   // 翻了 4 页、停下来时在第 4 页 -> 4 页
                if (n < 1) n = 1;
                if (r.Seconds > 0) seconds += r.Seconds;   // 负数/0 秒是脏数据，只当没花时间
                pages += n;
            }
            return pages <= 0 ? -1 : seconds / pages;
        }

        /// <summary>
        /// 剩余页数与预计分钟数；不满足显示条件时两个都是 0 —— 卡片据此不画这一段。
        ///
        /// 只在"放映中"才算：不在放映时 Current / SlideCount 要么是上一节课的残留，要么
        /// 是窗口标题兜底猜出来的，拿它们算"还剩几页"会给出一个不存在于眼前屏幕的数字。
        /// </summary>
        public static void Remaining(bool showing, int slideCount, int current, double secondsPerPage,
                                     out int pages, out int minutes)
        {
            pages = 0;
            minutes = 0;
            if (!showing || slideCount <= 0 || current <= 0) return;
            int left = slideCount - current;   // 一页正放着，所以从下一页数到最后一页
            if (left <= 0) return;
            if (secondsPerPage < MinSecondsPerPage) secondsPerPage = MinSecondsPerPage;
            if (secondsPerPage > MaxSecondsPerPage) secondsPerPage = MaxSecondsPerPage;
            pages = left;
            int m = (int)Math.Ceiling(left * secondsPerPage / 60.0);
            minutes = m < 1 ? 1 : m;   // "约 0 分钟"没有意义，再快也至少说 1 分钟
        }
    }
}
