using System;
using System.Collections.Generic;
using System.Globalization;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Models.Profile;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// "这一节课是哪一科、周几、第几节"。老师 2026-10-24 说历史里每条只有
    /// "第 3 页 · 3 小时前"，看不出是哪一节课记的，所以开始一次记录时把这几样
    /// 一起抄进台账（<see cref="SessionRecord"/> 里的同名字段）。
    ///
    /// 这个类只装数据：读课表在 <see cref="LessonTimetable"/>，拼字在
    /// <see cref="HistoryLine"/>。两边都是纯计算，能离线测——课表是早上八点的，
    /// 不能等到明天早上再验证"第几节算得对不对"。
    /// </summary>
    public sealed class LessonTag
    {
        /// <summary>科目名（ClassIsland 的 Subject.Name，例如"数学"）。拿不到就是空串。</summary>
        public string Subject = "";

        /// <summary>
        /// 这一节的名字：时段自己有名字（课表里的时段名，例如"午休"）就用它，否则用
        /// "第 3 节"。卡片副标题显示成"数学 · 第 3 节"。
        /// </summary>
        public string LessonName = "";

        /// <summary>时段自己的名字（TimeLayoutItem.BreakName），没有名字就是空串。</summary>
        public string PeriodName = "";

        /// <summary>当天第几节（1 起）。0 = 数不出来：课间、没排课、拿不到课表。</summary>
        public int Period;

        /// <summary>周几：1 = 周一 … 7 = 周日。0 = 不知道。</summary>
        public int Weekday;

        /// <summary>卡片副标题："数学 · 第 3 节" / "数学" / ""（课间或没有课表时整行不显示）。</summary>
        public string Context
        {
            get
            {
                if (Subject.Length == 0) return "";
                return LessonName.Length > 0 ? Subject + " · " + LessonName : Subject;
            }
        }
    }

    /// <summary>
    /// 把 ClassIsland 的课表读成 <see cref="LessonTag"/>。
    ///
    /// 只在调度线程里调（Services\SlideSyncService.cs 每 2 秒一跳，那时本来就在读课表），
    /// 引擎线程只读那份缓存快照 —— 引擎每次状态心跳都会问一遍"现在哪一科"
    /// （见 SessionEngine.BuildState），现场读宿主的服务要经过属性通知链，太贵。
    /// </summary>
    public static class LessonTimetable
    {
        /// <summary>
        /// 课表里的一个时段。只留判断要用的字段，不引用 ClassIsland 的类型，
        /// 于是"第几节"的算法可以拿真课表的时间点离线喂。
        /// </summary>
        public sealed class Slot
        {
            public TimeSpan Start;
            public TimeSpan End;
            /// <summary>0 = 上课, 1 = 课间, 2 = 分割线, 3 = 行动（TimeLayoutItem.TimeType）。</summary>
            public int TimeType;
            public string Name = "";
        }

        /// <summary>1 = 周一 … 7 = 周日（DateTime.DayOfWeek 的 Sunday = 0 挪到末尾）。</summary>
        public static int WeekdayOf(DateTime t)
        {
            int d = (int)t.DayOfWeek;
            return d == 0 ? 7 : d;
        }

        /// <summary>
        /// <paramref name="at"/> 落在当天第几节（1 起）：按顺序数 TimeType == 0 的时段，
        /// 命中那个就是它。课间、放学前后的空当、没排课都返回 0（调用方退回日期格式）。
        /// </summary>
        public static int PeriodAt(IList<Slot> slots, TimeSpan at)
        {
            if (slots == null) return 0;
            int n = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || s.TimeType != 0) continue;
                n++;
                // 起止写反的时段（跨零点、或老师手工填错）不参与判断，但节号照样占一位，
                // 免得它后面所有节的号码整体往前挪一格。
                if (s.End <= s.Start) continue;
                if (at >= s.Start && at < s.End) return n;
            }
            return 0;
        }

        /// <summary>
        /// 把当天课表的时段抄成 <see cref="Slot"/>。拿不到课表就返回空表（不抛）。
        /// 注意 ClassIsland 2.1 的 TimeLayout.Layouts 就是时段列表本身，没有嵌套一层
        /// "layout 数组"，所以直接遍历它。
        /// </summary>
        public static List<Slot> SlotsOf(ClassPlan plan)
        {
            var slots = new List<Slot>();
            try
            {
                var layout = plan == null ? null : plan.TimeLayout;
                if (layout == null || layout.Layouts == null) return slots;
                foreach (var item in layout.Layouts)
                {
                    if (item == null) continue;
                    slots.Add(new Slot
                    {
                        Start = item.StartTime,
                        End = item.EndTime,
                        TimeType = item.TimeType,
                        Name = Clean(item.BreakName),
                    });
                }
            }
            catch
            {
                // 课表对象半死不活的时候宁可当"没有课表"，也不能让调度器炸掉。
            }
            return slots;
        }

        /// <summary>
        /// 读一次课表。任何一步拿不到都退回一个只有周几的空标签：课间、没排课、
        /// 服务还没起来都是正常情形，不许抛。
        /// </summary>
        public static LessonTag Read(DateTime now)
        {
            var tag = new LessonTag();
            tag.Weekday = WeekdayOf(now);
            try
            {
                var lessons = IAppHost.TryGetService<ILessonsService>();
                if (lessons == null) return tag;

                var subject = lessons.CurrentSubject;
                if (subject != null && !string.IsNullOrWhiteSpace(subject.Name)) tag.Subject = subject.Name.Trim();

                var item = lessons.CurrentTimeLayoutItem;
                if (item != null) tag.PeriodName = Clean(item.BreakName);

                tag.Period = PeriodAt(SlotsOf(PlanOf(lessons, now)), now.TimeOfDay);
                tag.LessonName = tag.PeriodName.Length > 0
                    ? tag.PeriodName
                    : (tag.Period > 0 ? "第 " + tag.Period.ToString(CultureInfo.InvariantCulture) + " 节" : "");
            }
            catch
            {
                // 读课表失败 = 当作"不知道是哪节课"，历史行退回日期格式，插件照常记页码。
            }
            return tag;
        }

        /// <summary>
        /// 当天的课表：先按日期取（节假日/轮换课表都靠它），取不到再退回"当前生效的那张"。
        /// </summary>
        static ClassPlan PlanOf(ILessonsService lessons, DateTime now)
        {
            try
            {
                Guid? guid;
                var plan = lessons.GetClassPlanByDate(now, out guid);
                if (plan != null) return plan;
            }
            catch { }
            try { return lessons.CurrentClassPlan; } catch { return null; }
        }

        static string Clean(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }
    }
}
