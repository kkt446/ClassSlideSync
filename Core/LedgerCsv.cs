using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 把整本台账摊成一张 CSV 表。刻意做成纯函数（不碰 UI、不碰文件、只看传进来的对象）：
    /// 导出逻辑最容易错的是日期解析和转义，这两件事必须能脱离窗口单独跑断言。
    ///
    /// 编码是 UTF-8 **带 BOM**、行尾 CRLF：老师拿到 CSV 的第一动作是双击用 Excel 打开，
    /// 没有 BOM 时 Excel 会按系统 ANSI 解码，中文全是乱码；CRLF 则是 Excel/记事本的通行行尾。
    /// </summary>
    public static class LedgerCsv
    {
        /// <summary>表头，列顺序是老师点名要的那十列。</summary>
        public const string Header = "日期,周几,第几节,科目,课件名,起始页,结束页,总页数,时长(分),文件路径";

        static readonly string[] WeekdayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

        /// <summary>
        /// 把台账里所有文件的所有记录收成一条列表，新的在前。
        /// 台账本身是 Dictionary（遍历顺序没有语义），导出的表必须有稳定顺序，
        /// 否则同样的数据每点一次按钮行序都可能不同；老师对账时按时间倒序最顺手。
        /// 时间解析不出来的老记录排在最后，同一时间点保持台账里的原有顺序。
        /// </summary>
        public static List<SessionRecord> Collect(Ledger? ledger)
        {
            var all = new List<SessionRecord>();
            if (ledger?.ByFile == null) return all;

            // 台账是引擎线程写的（Add 加键 / Prune 删键），导出按钮却跑在 UI 线程上。
            // 边导边上课时，枚举撞上结构变化会抛 "Collection was modified"，
            // 老师看到的就是"点了导出没反应"。抄一份快照再排序，后面就不再碰台账。
            lock (ledger.Sync)
            {
                foreach (var kv in ledger.ByFile)
                {
                    var list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null) all.Add(list[i]);
                    }
                }
            }

            // 排序必须**稳定**：同一秒记下的两条、以及 StartedAt 解析不出来的老记录
            // （比较时都算"相等"）要保持在台账里的原有先后。注释以前写的是
            // "OrderBy 是稳定排序"，实现却是 List<T>.Sort（内省排序，不稳定）——
            // 元素一多，相等的记录会被分区过程换位，导出的行序和卡片历史对不上。
            // 这里给每条记录带上它在台账里的下标，相等时按下标收尾，等价于稳定排序。
            var decorated = new List<KeyValuePair<int, SessionRecord>>(all.Count);
            for (int i = 0; i < all.Count; i++) decorated.Add(new KeyValuePair<int, SessionRecord>(i, all[i]));
            decorated.Sort(delegate (KeyValuePair<int, SessionRecord> x, KeyValuePair<int, SessionRecord> y)
            {
                DateTime ta, tb;
                bool oka = TryParseStartedAt(x.Value.StartedAt, out ta);
                bool okb = TryParseStartedAt(y.Value.StartedAt, out tb);
                if (oka && okb)
                {
                    int c = tb.CompareTo(ta);              // 都有时间：新的在前
                    if (c != 0) return c;
                }
                else if (oka != okb) return oka ? -1 : 1;  // 有时间的排前面
                return x.Key.CompareTo(y.Key);              // 同一时间点 / 都没时间：保持台账原序
            });

            var result = new List<SessionRecord>(decorated.Count);
            for (int i = 0; i < decorated.Count; i++) result.Add(decorated[i].Value);
            return result;
        }

        /// <summary>拼出整张表（含表头，每行以 CRLF 结尾）。records 为空时只有表头。</summary>
        public static string ToCsv(IEnumerable<SessionRecord>? records)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append("\r\n");

            if (records != null)
            {
                foreach (var r in records)
                {
                    if (r == null) continue;
                    // 解析失败时 DateText 会原样回退，里面可能带逗号，所以统一过一遍 Escape。
                    sb.Append(Escape(DateText(r.StartedAt))).Append(',');
                    sb.Append(Escape(WeekdayText(r))).Append(',');
                    sb.Append(Escape(PeriodText(r))).Append(',');
                    sb.Append(Escape(r.Subject ?? "")).Append(',');
                    sb.Append(Escape(DeckName(r))).Append(',');
                    sb.Append(Int(NonNegative(r.StartSlide))).Append(',');
                    sb.Append(Int(NonNegative(r.EndSlide))).Append(',');
                    sb.Append(Int(TotalSlides(r))).Append(',');
                    sb.Append(Int(DurationMinutes(r.Seconds))).Append(',');
                    sb.Append(Escape(r.FilePath ?? "")).Append("\r\n");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 落盘用的字节：UTF-8 的 BOM + 正文。BOM 写成 preamble 而不是在字符串前面加 '\uFEFF'，
        /// 免得 ToCsv 的返回值里混进一个不可见字符、测试还得再剥一次。
        /// </summary>
        public static byte[] ToBytes(IEnumerable<SessionRecord>? records)
        {
            var encoding = new UTF8Encoding(true);
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(ToCsv(records));

            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            return bytes;
        }

        /// <summary>导出成功后状态条那一行。路径放在最后，老师一眼看到“几条”和“存哪了”。</summary>
        public static string StatusText(int count, string? path)
        {
            return "已导出 " + Int(count) + " 条到 " + (path ?? "");
        }

        /// <summary>默认文件名带日期：同一天导两次不会互相覆盖（覆盖提示仍然由系统对话框负责）。</summary>
        public static string SuggestedFileName(DateTime now)
        {
            return "课件续页-记录-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
        }

        /// <summary>
        /// CSV 转义：出现分隔符、双引号、CR、LF、制表符，或字段两端有空白时才包引号，
        /// 内部引号翻倍。不无脑给所有字段加引号——老师用记事本看时，满屏引号很难读。
        ///
        /// 分号也算分隔符：Excel 的“列表分隔符”来自系统区域设置（德语/法语等默认是 ';'），
        /// 老师把表发给用这种设置的同事、或自己改过区域设置时，未加引号的 "a;b" 会被拆成两列。
        /// 两端空白同理：不加引号时，Excel 的“分列”/WPS 的导入会把 " 数学 " 两头的空格 trim 掉，
        /// 而科目名/课件名前后带空格往往正是老师手输台账时留下的、有意义的区别。
        /// </summary>
        public static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            bool needQuote = value.IndexOf(',') >= 0
                             || value.IndexOf(';') >= 0
                             || value.IndexOf('"') >= 0
                             || value.IndexOf('\t') >= 0
                             || value.IndexOf('\r') >= 0
                             || value.IndexOf('\n') >= 0
                             || char.IsWhiteSpace(value[0])
                             || char.IsWhiteSpace(value[value.Length - 1]);
            if (!needQuote) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// 日期列只留年月日：列名就叫“日期”，同一节课的起止时间在台账里另有记录。
        /// 解析一律用 InvariantCulture —— 台账是在可能开着 ar-SA 等区域的机器上写的，
        /// 换成 CurrentCulture 解析同一条 "2026-10-24 09:10:00" 会得到不同结果甚至失败。
        /// 解析不了就原样输出，宁可显示得难看，也不能把老师的数据悄悄丢掉。
        /// </summary>
        public static string DateText(string? startedAt)
        {
            DateTime t;
            if (TryParseStartedAt(startedAt, out t))
            {
                return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return (startedAt ?? "").Trim();
        }

        /// <summary>
        /// 周几：优先用记录里存的 Weekday（1=周一…7=周日）。
        /// 老台账没有这个字段（0），就从 StartedAt 反推 —— 否则导出的老记录这一列全是空。
        /// </summary>
        public static string WeekdayText(SessionRecord? record)
        {
            if (record == null) return "";

            int w = record.Weekday;
            if (w >= 1 && w <= 7) return WeekdayNames[w - 1];

            DateTime t;
            if (TryParseStartedAt(record.StartedAt, out t))
            {
                // DayOfWeek：Sunday=0 … Saturday=6；换成“周一=1 … 周日=7”。
                int n = ((int)t.DayOfWeek + 6) % 7 + 1;
                return WeekdayNames[n - 1];
            }
            return "";
        }

        /// <summary>
        /// 第几节：能数出节次就用数字（老师对账时要能排序）；
        /// 数不出来时退回时段名（“午休”“课间”），再没有就空着。
        /// </summary>
        public static string PeriodText(SessionRecord? record)
        {
            if (record == null) return "";
            if (record.Period > 0) return Int(record.Period);
            return (record.PeriodName ?? "").Trim();
        }

        /// <summary>
        /// 课件名：正常文件取文件名（不带目录，表里另有一列完整路径）。
        /// 希沃的课件没有本地文件，台账里存的是 "希沃:标题" 这种伪键；直接 Path.GetFileName
        /// 会把 "希沃:" 一起留下，所以先按 SeewoBridge 的前缀剥掉。
        /// </summary>
        public static string DeckName(SessionRecord? record)
        {
            if (record == null) return "";
            string path = (record.FilePath ?? "").Trim();
            if (path.Length == 0) return "";

            if (path.StartsWith(SeewoBridge.KeyPrefix, StringComparison.Ordinal))
            {
                return path.Substring(SeewoBridge.KeyPrefix.Length).Trim();
            }

            try
            {
                string name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch (ArgumentException)
            {
                // 路径里有非法字符（台账被手改过）时不能因为一行坏数据毁掉整次导出。
                return path;
            }
        }

        /// <summary>
        /// 总页数：SlideCount 是当时读到的页数；老记录没存就退到结束页。
        /// 负数不进表：台账可以被手改/导入，“总页数 = -3”只会在 Excel 里变成一条没法解释的数据。
        /// </summary>
        public static int TotalSlides(SessionRecord? record)
        {
            if (record == null) return 0;
            if (record.SlideCount > 0) return record.SlideCount;
            return NonNegative(record.EndSlide);
        }

        /// <summary>页码列只写 0 或正数；坏数据（手改/导入的负数）按 0 写。</summary>
        static int NonNegative(int value)
        {
            return value > 0 ? value : 0;
        }

        /// <summary>
        /// 时长（分钟）：向下取整。用四舍五入会把 59 秒的误触记成 1 分钟，
        /// 台账里 1 分钟以下的记录本来就该显示 0。
        /// </summary>
        public static int DurationMinutes(int seconds)
        {
            if (seconds <= 0) return 0;
            return seconds / 60;
        }

        /// <summary>时间解析的唯一入口，所有地方都走 InvariantCulture，避免文化差异各解析各的。</summary>
        public static bool TryParseStartedAt(string? text, out DateTime value)
        {
            value = default(DateTime);
            if (string.IsNullOrWhiteSpace(text)) return false;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
