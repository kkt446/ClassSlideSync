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
            var all = new System.Text.StringBuilder();
            int best = int.MaxValue;

            for (int i = 0; i < starts.Count && i < ends.Count; i++)
            {
                var start = starts[i];
                var end = ends[i];
                index++;
                periods++;
                if (all.Length > 0) all.Append(" / ");
                all.Append("第").Append(index).Append("节 ").Append(Fmt(start)).Append("-").Append(Fmt(end));

                if (end <= start) continue;              // 跨零点的时间点不参与判断

                var from = start - lead;
                var to = end + grace;
                if (now >= from && now <= to)
                {
                    r.InLesson = true;
                    r.InPeriod = now >= start && now <= end;
                    r.Detail = "第" + index + "节 " + Fmt(start) + "-" + Fmt(end);
                    r.All = all.ToString();
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

            r.All = all.ToString();
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
