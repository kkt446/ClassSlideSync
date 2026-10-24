using System;
using System.Collections.Generic;

namespace ClassSlideSync.Plugin.Core
{
    public static class LessonWindow
    {
        public sealed class Result
        {
            public bool InLesson;
            public bool InPeriod;
            public string Detail = "";
            public string All = "";
            public int Periods;
        }

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

                if (end <= start) continue;

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
