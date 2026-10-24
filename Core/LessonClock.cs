using System;
using System.Collections.Generic;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassSlideSync.Plugin.Core
{
    public static class LessonClock
    {
        public sealed class Verdict
        {
            public bool Known;
            public bool InLesson;
            public bool InPeriod;
            public string Detail = "";
            public string All = "";
            public int SecondsUntilArmed;
            public int Periods;
        }

        public static Verdict Check(int leadSeconds, int graceSeconds)
        {
            var v = new Verdict();
            try
            {
                var lessons = IAppHost.TryGetService<ILessonsService>();
                if (lessons == null) { v.Detail = "拿不到课表服务"; return v; }

                Guid? guid;
                var plan = lessons.GetClassPlanByDate(DateTime.Now, out guid);
                if (plan == null) { v.Detail = "今天没有课表"; return v; }
                var items = plan.ValidTimeLayoutItems;
                if (items == null) { v.Detail = "今天没有排课"; return v; }

                v.Known = true;
                var starts = new List<TimeSpan>();
                var ends = new List<TimeSpan>();
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (item.TimeType != 0) continue;
                    starts.Add(item.StartTime);
                    ends.Add(item.EndTime);
                }

                int left;
                var r = LessonWindow.Compute(DateTime.Now.TimeOfDay, starts, ends, leadSeconds, graceSeconds, out left);
                v.InLesson = r.InLesson;
                v.InPeriod = r.InPeriod;
                v.Detail = r.Detail;
                v.All = r.All;
                v.SecondsUntilArmed = left;
                v.Periods = r.Periods;
            }
            catch
            {
            }
            return v;
        }
    }
}
