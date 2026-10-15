using System;
using System.Collections.Generic;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 用 ClassIsland 的课表回答一个问题："现在该不该把插件开着？"
    ///
    /// 规则（都能在设置页里调）：某个"上课"时间点开始前 leadSeconds 秒进入待命，这个时间点
    /// 结束后再留 graceSeconds 秒。其余时间插件不占任何东西——不轮询 COM、不建窗口。
    ///
    /// 判断本身在 <see cref="LessonWindow"/> 里（纯计算，可离线测）；这里只负责把课表读出来。
    /// </summary>
    public static class LessonClock
    {
        public sealed class Verdict
        {
            /// <summary>false = 没拿到课表（没排课/服务拿不到），调用方要自己兜底。</summary>
            public bool Known;
            /// <summary>true = 现在处在某个上课时段（含提前量与延后量）。</summary>
            public bool InLesson;
            /// <summary>true = 正在正课时间里（不含提前量与延后量）。卡片显示与否看这个。</summary>
            public bool InPeriod;
            /// <summary>日志用："第3节 09:40-10:20"；不在时段里时是最近的下一个时段。</summary>
            public string Detail = "";
            /// <summary>今天的上课时间点全表，只在第一轮日志里打一次，方便核对插件读到的课表对不对。</summary>
            public string All = "";
            /// <summary>距离进入待命还有多少秒（已经待命就是 0）。</summary>
            public int SecondsUntilArmed;
            /// <summary>今天课表上的节数。0 = 没有课表，此时卡片退回"打开课件就显示"的老规矩。</summary>
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
                    // TimeLayoutItem.TimeType: 0 = 上课, 1 = 课间, 2 = 分割线, 3 = 行动
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
                // 课表服务出问题不能连累插件：当作"不知道"，让调用方用兜底逻辑。
            }
            return v;
        }
    }
}
