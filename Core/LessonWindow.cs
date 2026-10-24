using System;
using System.Collections.Generic;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// "现在在不在上课时段里"的纯计算：只吃时间，不碰 ClassIsland、不碰 COM。
    ///
    /// 单独一个文件（而不是塞进 LessonClock）是为了能被离线测到：课表是早上 8 点的，
    /// 没法等到明天早上再验证"上课前 1 分钟自动开启"到底对不对，所以把判据抽成纯函数，
    /// 用真实课表的时间点 + 各种"现在"去喂它。
    /// </summary>
    public static class LessonWindow
    {
        public sealed class Result
        {
            /// <summary>true = 处在某个上课时段（含提前量与延后量之内）。</summary>
            public bool InLesson;
            /// <summary>
            /// true = 正在这节课的**正课时间里**（start ≤ now ≤ end，不含提前量与延后量）。
            /// 卡片露不露面看这个：下课铃一响就隐藏，而待命（InLesson）还会多留一段延后量，
            /// 好把"最后停在第几页"记完。
            /// </summary>
            public bool InPeriod;
            /// <summary>"第3节 09:10-09:50" / "下一节 08:00（60s 后待命）" / "今天的 8 节课都已结束"。</summary>
            public string Detail = "";
            /// <summary>全表："第1节 08:00-08:40 / 第2节 09:10-09:50 …"，用来核对插件读到的课表。</summary>
            public string All = "";
            /// <summary>今天课表上的节数。0 = 没有课表（节假日/没排课），此时卡片退回"打开课件就显示"。</summary>
            public int Periods;

            /// <summary>
            /// true = 正处在某个上课时间点开始前的 lead 窗口里（已经待命、但还没打铃）。
            /// 2026-10-24 功能7 用它决定卡片要不要转成「课前预备态」。
            /// 必须和 <see cref="InLesson"/> 分开：InLesson 把「课前 lead」和「课后 grace」都算进去，
            /// 只看它分不清现在是"快上课了"还是"刚下课"。
            /// </summary>
            public bool InPreClass;
            /// <summary>即将开始的那一节的起始时刻（InPreClass 时有效）。</summary>
            public TimeSpan PreClassStart;
            /// <summary>即将开始的是当天第几节（1 起）。</summary>
            public int PreClassPeriod;
            /// <summary>距离打铃还有多少秒（InPreClass 时 1..leadSeconds）。</summary>
            public int SecondsToStart;
        }

        /// <summary>
        /// <paramref name="secondsLeft"/> = 距离进入待命还有多少秒（已经在里面就是 0）。
        /// </summary>
        public static Result Compute(TimeSpan now, IList<TimeSpan> starts, IList<TimeSpan> ends,
                                     int leadSeconds, int graceSeconds, out int secondsLeft)
        {
            var r = new Result();
            secondsLeft = 0;
            if (starts == null || ends == null) return r;

            var lead = TimeSpan.FromSeconds(Math.Max(0, leadSeconds));
            var grace = TimeSpan.FromSeconds(Math.Max(0, graceSeconds));

            int index = 0;
            int periods = 0;
            int best = int.MaxValue;

            // 整天的课表先拼好。以前这段在下面的主循环里边走边拼、命中当前时段就 return，
            // 于是 r.All 只到"当前这一节"为止；而 SlideSyncService 的 "clock:" 日志把它当
            // "今天的课表"打出来 —— 排查"卡片为什么没出现/为什么不下课"时看到的是半张课表，
            // 会让人以为下午的课根本没被读到。现在先算完整的一份，命中时直接用它。
            var all = new System.Text.StringBuilder();
            for (int i = 0; i < starts.Count && i < ends.Count; i++)
            {
                if (all.Length > 0) all.Append(" / ");
                all.Append("第").Append(i + 1).Append("节 ").Append(Fmt(starts[i])).Append("-").Append(Fmt(ends[i]));
            }
            string allText = all.ToString();

            // 功能7（2026-10-24）：先单独扫一遍「课前窗口」。主循环碰到第一个命中的时段就 return，
            // 而「课前 lead」和「课后 grace」都落在同一个 InLesson 里，等它返回就分不清是快上课
            // 还是刚下课；预备态只在"还没打铃"时才出现，所以这里单独判定。
            // 先看现在是不是正在某一节的正课里：lead 窗口可能压到上一节的尾巴（连堂课），
            // 那时候老师正在上课，不该弹"课前预备"。
            bool inAnyPeriod = false;
            for (int i = 0; i < starts.Count && i < ends.Count; i++)
            {
                if (ends[i] > starts[i] && now >= starts[i] && now <= ends[i]) { inAnyPeriod = true; break; }
            }
            if (!inAnyPeriod)
            {
                TimeSpan preStart = TimeSpan.MaxValue;
                int preIndex = 0;
                for (int i = 0; i < starts.Count && i < ends.Count; i++)
                {
                    var start = starts[i];
                    if (ends[i] <= start) continue;               // 跨零点的时间点不参与判断
                    var from = start - lead;
                    // 只认"已经进入 lead、还没到打铃"这一段；挑最近的一节。
                    if (now >= from && now < start && start < preStart)
                    {
                        preStart = start;
                        preIndex = i + 1;   // 节号和主循环一样按数组下标算（含被跳过的跨零点项）
                    }
                }
                if (preStart != TimeSpan.MaxValue)
                {
                    r.InPreClass = true;
                    r.PreClassStart = preStart;
                    r.PreClassPeriod = preIndex;
                    r.SecondsToStart = (int)Math.Ceiling((preStart - now).TotalSeconds);
                }
            }

            for (int i = 0; i < starts.Count && i < ends.Count; i++)
            {
                var start = starts[i];
                var end = ends[i];
                index++;
                periods++;

                if (end <= start) continue;              // 跨零点的时间点不参与判断

                var from = start - lead;
                var to = end + grace;
                if (now >= from && now <= to)
                {
                    r.InLesson = true;
                    r.InPeriod = now >= start && now <= end;
                    r.Detail = "第" + index + "节 " + Fmt(start) + "-" + Fmt(end);
                    r.All = allText;
                    r.Periods = periods;
                    return r;
                }
                // 最近的"下一个要开始上课"的时间点：日志里能看出插件在等什么。
                if (now < from)
                {
                    int left = (int)Math.Ceiling((from - now).TotalSeconds);
                    if (left < best) best = left;
                }
            }

            r.All = allText;
            r.Periods = periods;
            if (best != int.MaxValue)
            {
                secondsLeft = best;
                r.Detail = "下一节 " + Fmt(StartOfNext(starts, ends, now)) + "（" + best + "s 后待命）";
            }
            else
            {
                r.Detail = periods > 0 ? "今天的 " + periods + " 节课都已结束" : "今天没有排课";
            }
            return r;
        }

        static TimeSpan StartOfNext(IList<TimeSpan> starts, IList<TimeSpan> ends, TimeSpan now)
        {
            TimeSpan best = TimeSpan.MaxValue;
            for (int i = 0; i < starts.Count && i < ends.Count; i++)
                if (ends[i] > starts[i] && now < starts[i] && starts[i] < best) best = starts[i];
            return best == TimeSpan.MaxValue ? TimeSpan.Zero : best;
        }

        public static string Fmt(TimeSpan t)
        {
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00");
        }
    }
}
