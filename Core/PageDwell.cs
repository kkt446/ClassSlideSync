using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// 一页的累计停留（2026-10-25 功能1）。Seconds 是按「次」四舍五入后累加的整数秒，
    /// Visits 是这一页被结算过几次 —— 老师一节课里来回翻同一页，两次停留都要算进去。
    ///
    /// 为什么不存原始浮点：台账里一秒级的误差没有意义，而 int 让 dwell.json 小一半、
    /// 老师用记事本打开也读得懂。四舍五入放在写入侧，读取侧不再做二次换算。
    /// </summary>
    public sealed class DwellPage
    {
        /// <summary>累计停留秒数；0 表示这一页没有有效停留（脏数据，落盘前就被丢掉）。</summary>
        public int Seconds { get; set; }

        /// <summary>被结算过的次数（1 = 只停过一次）。</summary>
        public int Visits { get; set; }

        /// <summary>这一页平均每次停多久；Visits 为 0 时返回 0。</summary>
        [JsonIgnore]
        public double AverageSeconds
        {
            get { return Visits > 0 ? (double)Seconds / Visits : 0; }
        }
    }

    /// <summary>
    /// 一份课件的页停留汇总。键就是台账的桶键（<see cref="Ledger.Key"/>），
    /// 所以 dwell.json 和 ledger.json 能按同一把钥匙对齐。
    ///
    /// Subject 是「科目从台账记录里取」的落点：每次结算时用当时那条台账记录的 Subject
    /// 覆盖（空值不覆盖，免得课表没读到时把原来记住的科目抹掉）；课件改名/换目录后
    /// 台账桶键不变，这一份汇总也跟着不变。
    /// </summary>
    public sealed class DeckDwell
    {
        /// <summary>科目（台账记录的 Subject）；空串 = 当时课表给不出科目。</summary>
        public string Subject { get; set; } = "";

        /// <summary>课件显示名（去目录、去「希沃:」前缀），卡片/面板直接拿去画，不解析路径。</summary>
        public string Name { get; set; } = "";

        /// <summary>最后一次写入的时间（本地时间，yyyy-MM-dd HH:mm:ss）。</summary>
        public string UpdatedAt { get; set; } = "";

        /// <summary>这份课件被结算过几场（一场放映算一场，太短的会话不写进来）。</summary>
        public int Sessions { get; set; }

        /// <summary>页号 → 累计停留。页号从 1 起；0/负数在读写两侧都会被丢掉。</summary>
        public Dictionary<int, DwellPage> Pages { get; set; } = new Dictionary<int, DwellPage>();

        /// <summary>所有页累计秒数。</summary>
        [JsonIgnore]
        public int TotalSeconds
        {
            get
            {
                if (Pages == null) return 0;
                long total = 0;
                foreach (var kv in Pages) if (kv.Value != null) total += kv.Value.Seconds;
                return ClampInt(total);
            }
        }

        /// <summary>所有页被结算的总次数。</summary>
        [JsonIgnore]
        public int Visits
        {
            get
            {
                if (Pages == null) return 0;
                long total = 0;
                foreach (var kv in Pages) if (kv.Value != null) total += kv.Value.Visits;
                return ClampInt(total);
            }
        }

        /// <summary>有停留记录的页数（不是总页数）。</summary>
        [JsonIgnore]
        public int PageCount
        {
            get { return Pages == null ? 0 : Pages.Count; }
        }

        /// <summary>这一页累计停了多少秒；没有记录返回 0。</summary>
        public int SecondsAt(int page)
        {
            if (Pages != null && Pages.TryGetValue(page, out DwellPage? p) && p != null) return p.Seconds;
            return 0;
        }

        /// <summary>这一页被结算过几次；没有记录返回 0。</summary>
        public int VisitsAt(int page)
        {
            if (Pages != null && Pages.TryGetValue(page, out DwellPage? p) && p != null) return p.Visits;
            return 0;
        }

        /// <summary>平均每页停留（按结算次数算，不是按页数算）：一页停两次就计两次。</summary>
        [JsonIgnore]
        public double AverageSecondsPerVisit
        {
            get
            {
                int visits = Visits;
                return visits > 0 ? (double)TotalSeconds / visits : 0;
            }
        }

        internal static int ClampInt(long v)
        {
            if (v < 0) return 0;
            if (v > int.MaxValue) return int.MaxValue;
            return (int)v;
        }
    }

    /// <summary>
    /// 按科目聚合出来的一行。科目名沿用卡片的分组约定（空科目 → 「未标注科目」，
    /// 见 <see cref="CardFileGroups.Unmarked"/>），这样以后面板/卡片接上这份数据时，
    /// 标题和课件库的分组对得上，老师不会看到两套叫法。
    /// </summary>
    public sealed class SubjectDwell
    {
        public string Subject { get; set; } = "";

        /// <summary>这个科目所有课件加起来的总停留秒数。</summary>
        public int TotalSeconds { get; set; }

        /// <summary>总结算次数。</summary>
        public int Visits { get; set; }

        /// <summary>各课件有停留记录的页数之和（同一页在不同课件里算两页）。</summary>
        public int PageCount { get; set; }

        /// <summary>参与聚合的课件份数。</summary>
        public int Decks { get; set; }

        /// <summary>参与聚合的会话场数。</summary>
        public int Sessions { get; set; }

        /// <summary>这个科目下的每一份课件（已经按最近使用时间排过）。</summary>
        public List<DeckDwell> Items { get; set; } = new List<DeckDwell>();

        /// <summary>平均每次停留秒数。</summary>
        [JsonIgnore]
        public double AverageSecondsPerVisit
        {
            get { return Visits > 0 ? (double)TotalSeconds / Visits : 0; }
        }
    }

    /// <summary>
    /// 一场放映里的「每页停留」累加器（2026-10-25 功能1）。
    ///
    /// 所有时刻都由调用方传进来，绝不自己读 <c>DateTime.Now</c>：引擎每 250ms 一拍、
    /// 页面切换完全取决于老师的手速，离线测试必须能精确指定「第几秒翻到第几页」。
    /// 时间只有单调向前这一种假设，但时钟被系统校时往回拨也不能产生负数 —— 见 <see cref="Accrue"/>。
    ///
    /// 语义：
    ///   进入某页的那一刻开始计时，页面一换就把上一页结算掉；同一页的重复快照（引擎每个
    ///   节拍都会来一次）不算翻页、不清零。会话结束时调用 <see cref="Settle"/> 把最后
    ///   一页也结算掉 —— 老师在一页上讲了 10 分钟然后关掉课件，这 10 分钟必须算进去。
    /// </summary>
    public sealed class PageDwellTracker
    {
        string key = "";
        string subject = "";
        int page;
        DateTime enteredAt;

        // 同一页可能被反复进入（往前翻一页又翻回来），秒数和次数都要累加。
        readonly Dictionary<int, double> raw = new Dictionary<int, double>();
        readonly Dictionary<int, int> visits = new Dictionary<int, int>();
        int turns;

        /// <summary>这场会话属于哪份课件（台账桶键）。</summary>
        public string Key { get { return key; } }

        /// <summary>这场会话开始时台账记录的科目；可能为空。</summary>
        public string Subject { get { return subject; } }

        /// <summary>当前停在哪一页；0 = 还没进入任何页 / 已经结算完。</summary>
        public int Page { get { return page; } }

        /// <summary>
        /// 这场会话翻了多少页（页面真的变了才算；同一页的重复快照不算，跳页也只算一次）。
        /// 「剩余时间用本次课实测速度」的分子就是它 —— 用「页号差」会被 2 → 30 的跳页
        /// 一下子虚增 28 页，把每页耗时算成几秒钟。
        /// </summary>
        public int Turns { get { return turns; } }

        /// <summary>这场会话有没有攒下至少一页的有效停留。</summary>
        public bool HasData
        {
            get
            {
                foreach (var kv in raw)
                    if (kv.Key > 0 && (int)Math.Round(kv.Value, MidpointRounding.AwayFromZero) > 0) return true;
                return false;
            }
        }

        /// <summary>整场会话累计停留秒数（已按次四舍五入）。</summary>
        public int TotalSeconds
        {
            get
            {
                long total = 0;
                foreach (var kv in raw)
                {
                    if (kv.Key <= 0) continue;
                    total += (int)Math.Round(kv.Value, MidpointRounding.AwayFromZero);
                }
                return DeckDwell.ClampInt(total);
            }
        }

        /// <summary>
        /// 开始一场新会话。page &lt;= 0（页号未知）时先空着，等 <see cref="Enter"/> 给出真实页号；
        /// 这样「从第 0 页开始」的脏快照不会造出一条不存在的第 0 页记录。
        /// </summary>
        public void Begin(string key, string subject, int page, DateTime at)
        {
            // 桶键必须和台账完全一致（Ledger.Key 归一：全路径、去尾斜杠、小写；希沃伪键原样小写），
            // 否则同一份课件在 dwell.json 里会被拆成两条、按课件聚合时对不上台账。
            this.key = Ledger.Key(key);
            this.subject = subject ?? "";
            this.page = page > 0 ? page : 0;
            this.enteredAt = at;
            this.turns = 0;
            raw.Clear();
            visits.Clear();
        }

        /// <summary>
        /// 引擎每拍调一次：当前页变了就把上一页结算掉。页号 &lt;= 0 是脏快照
        /// （COM 一时取不到当前页），既不结算也不换页 —— 不能让一次取数失败把这一页的
        /// 停留时间清掉，也不能凭它凭空造出第 0 页。
        /// </summary>
        public void Enter(int pageNo, DateTime at)
        {
            if (pageNo <= 0) return;
            if (page == pageNo)
            {
                // 同一页的重复快照：只把计时起点往后挪到这次快照？不 —— 那会把两次快照
                // 之间的停留时间丢掉。保持原起点，什么都不做。
                return;
            }
            if (page > 0)
            {
                Accrue(at);
                turns++;
            }
            page = pageNo;
            enteredAt = at;
        }

        /// <summary>
        /// 结算当前页但不换页（会话结束 / 进程即将退出时用）。之后再 Enter 会重新开始计时
        /// 并算一次新的停留（同一页来回翻的语义）。
        /// </summary>
        public void Settle(DateTime at)
        {
            if (page <= 0) return;
            Accrue(at);
            page = 0;
        }

        /// <summary>当前这一页已经停了多久（不结算）；用于日志/诊断。</summary>
        public double CurrentSeconds(DateTime at)
        {
            if (page <= 0) return 0;
            double d = (at - enteredAt).TotalSeconds;
            return d > 0 ? d : 0;
        }

        /// <summary>页号 → 累计秒数（只含有有效停留的页，已四舍五入）。</summary>
        public Dictionary<int, int> Pages
        {
            get
            {
                var result = new Dictionary<int, int>();
                foreach (var kv in raw)
                {
                    if (kv.Key <= 0) continue;
                    int s = (int)Math.Round(kv.Value, MidpointRounding.AwayFromZero);
                    if (s > 0) result[kv.Key] = s;
                }
                return result;
            }
        }

        /// <summary>页号 → 结算次数（只含有有效停留的页，和 <see cref="Pages"/> 一一对应）。</summary>
        public Dictionary<int, int> PageVisits
        {
            get
            {
                var result = new Dictionary<int, int>();
                foreach (var kv in raw)
                {
                    if (kv.Key <= 0) continue;
                    int s = (int)Math.Round(kv.Value, MidpointRounding.AwayFromZero);
                    if (s <= 0) continue;
                    int n;
                    visits.TryGetValue(kv.Key, out n);
                    result[kv.Key] = n > 0 ? n : 1;
                }
                return result;
            }
        }

        /// <summary>某一页累计停了多少秒（未四舍五入前的整数结果；没有就 0）。</summary>
        public int SecondsAt(int pageNo)
        {
            double d;
            if (pageNo <= 0 || !raw.TryGetValue(pageNo, out d)) return 0;
            int s = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            return s > 0 ? s : 0;
        }

        void Accrue(DateTime at)
        {
            if (page <= 0) return;
            double d = (at - enteredAt).TotalSeconds;
            if (d <= 0)
            {
                // 系统校时把时钟往回拨，或者同一毫秒里连着两次结算：不能记负数，
                // 也不能让 enteredAt 停在"未来"，否则接下来的正计时全都会被判成负数。
                enteredAt = at;
                return;
            }
            double cur;
            raw.TryGetValue(page, out cur);
            raw[page] = cur + d;
            int n;
            visits.TryGetValue(page, out n);
            visits[page] = n + 1;
        }
    }

    /// <summary>
    /// 每页停留的落盘层（2026-10-25 功能1）。文件放在插件自己的配置目录里，和 ledger.json
    /// 同级（<c>data\Config\Plugins\ClassSlideSync.Plugin\dwell.json</c>）：
    /// 官方文档明确说插件安装目录不会随配置备份、更新时还会被整个替换，数据只能放
    /// PluginConfigFolder（见 https://docs.classisland.tech/dev/plugins/basics.html）。
    ///
    /// 线程：写方只有引擎线程（会话结束时一次），读方以后可能是面板/卡片线程。
    /// 所以内部所有读写都在同一把锁里，对外返回的是深拷贝 —— 调用方拿到手之后随便改，
    /// 不会改到下一次落盘的内容。
    ///
    /// 落盘沿用台账的 write-then-rename：写到 dwell.tmp 再替换，断电/被杀进程不会留下
    /// 半截 JSON。读坏了不装作没事：文件改名成 dwell.json.corrupt-时间戳 留档，
    /// 再从空白开始，老师至少还能人工拣回。
    /// </summary>
    public sealed class PageDwell
    {
        /// <summary>当前文件格式版本。以后加字段时老文件要能照读，所以只在破坏性改动时才 +1。</summary>
        public const int Version = 1;

        /// <summary>和 ledger.json 同目录的文件名。</summary>
        public const string FileName = "dwell.json";

        /// <summary>最多留多少份课件的汇总。一学期几百份足够，超出时丢最久没用的那份。</summary>
        public const int MaxDecks = 500;

        /// <summary>一份课件最多记多少页。页数异常的脏数据（手改过的文件）不会把内存撑爆。</summary>
        public const int MaxPagesPerDeck = 1000;

        /// <summary>单页停留秒数的上限（6 小时）。超过基本是系统时间被改过，夹住别让平均值失真。</summary>
        public const int MaxPageSeconds = 6 * 60 * 60;

        /// <summary>单页结算次数的上限。</summary>
        public const int MaxPageVisits = 100000;

        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // 中文不转成 \uXXXX：老师用记事本打开 dwell.json 时，课件名和科目要能直接读。
            // 只影响可读性，读回来仍是同一份数据（写入用 UTF-8 无 BOM）。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        readonly string dir;
        readonly string fileName;
        readonly string tempName;
        readonly object sync = new object();
        readonly Action<string>? log;

        Dictionary<string, DeckDwell> decks = new Dictionary<string, DeckDwell>(StringComparer.Ordinal);
        string lastError = "";

        public PageDwell(string dataFolder) : this(dataFolder, null) { }

        /// <param name="logger">落盘/读取失败的记录出口；引擎传 ledger.Log，测试可以传 null。</param>
        public PageDwell(string dataFolder, Action<string>? logger)
        {
            dir = string.IsNullOrWhiteSpace(dataFolder) ? "data" : Path.GetFullPath(dataFolder);
            fileName = Path.Combine(dir, FileName);
            tempName = Path.Combine(dir, "dwell.tmp");
            log = logger;
        }

        /// <summary>和台账同一个目录的构造：dwell.json 永远躺在 ledger.json 旁边。</summary>
        public PageDwell(Ledger ledger) : this(ledger.Root, ledger.Log) { }

        /// <summary>读盘并顺便把脏数据洗干净；文件不存在就是一个空实例（第一次运行）。</summary>
        public static PageDwell Load(string dataFolder, Action<string>? logger = null)
        {
            var d = new PageDwell(dataFolder, logger);
            d.ReadFromDisk();
            return d;
        }

        /// <summary>数据目录（= 插件配置目录）。</summary>
        public string Root { get { return dir; } }

        /// <summary>路径，给日志/面板显示用。</summary>
        public string FilePathOnDisk { get { return fileName; } }

        /// <summary>最近一次读写失败的原因；空串 = 一切正常。</summary>
        public string LastError { get { lock (sync) return lastError; } }

        /// <summary>当前在内存里的课件份数（不含已洗掉的脏数据）。</summary>
        public int DeckCount { get { lock (sync) return decks.Count; } }

        /// <summary>
        /// 把一场会话的页停留并进来。<paramref name="keyOrPath"/> 可以是课件路径，
        /// 也可以是「希沃:课件名」伪键，内部统一走 <see cref="Ledger.Key"/> 归成台账桶键。
        ///
        /// 脏数据在这里拦掉：页号 &lt;= 0、秒数 &lt;= 0 一律丢弃 —— 这种记录要么是
        /// 「从第 0 页开始放映」的坏快照，要么是时钟回拨产生的负数，写进去只会污染
        /// 「这一页平均停多久」。全部被丢掉时不会凭空建一份空课件。
        ///
        /// 只改内存，不落盘：调用方攒够一批（引擎是会话结束）再 <see cref="Save"/>。
        /// </summary>
        public void AddSession(string keyOrPath, string subject, string name,
                               IReadOnlyDictionary<int, int>? pageSeconds, DateTime endedAt)
        {
            AddSession(keyOrPath, subject, name, pageSeconds, null, endedAt);
        }

        /// <summary>
        /// 同上，但把「每页结算了几次」也带进来：同一页来回翻两次，Visits 加 2、
        /// 秒数加两次，以后算「这一页平均停多久」才是对的。pageVisits 为 null 时每页记 1 次。
        /// </summary>
        public void AddSession(string keyOrPath, string subject, string name,
                               IReadOnlyDictionary<int, int>? pageSeconds,
                               IReadOnlyDictionary<int, int>? pageVisits,
                               DateTime endedAt)
        {
            string key = Ledger.Key(keyOrPath);
            if (key.Length == 0 || pageSeconds == null) return;

            var clean = new Dictionary<int, DwellPage>();
            foreach (var kv in pageSeconds)
            {
                int pageNo = kv.Key;
                int seconds = kv.Value;
                if (pageNo <= 0 || seconds <= 0) continue;
                if (seconds > MaxPageSeconds) seconds = MaxPageSeconds;
                int n = 1;
                if (pageVisits != null) pageVisits.TryGetValue(pageNo, out n);
                if (n <= 0) n = 1;
                if (n > MaxPageVisits) n = MaxPageVisits;
                if (clean.Count >= MaxPagesPerDeck && !clean.ContainsKey(pageNo)) break;
                clean[pageNo] = new DwellPage { Seconds = seconds, Visits = n };
            }
            if (clean.Count == 0) return;

            lock (sync)
            {
                DeckDwell deck;
                if (!decks.TryGetValue(key, out deck!) || deck == null)
                {
                    deck = new DeckDwell
                    {
                        // 名字兜底用**原始**路径：key 已经小写，拿它当显示名会把「课件A.pptx」显示成「课件a.pptx」。
                        Name = string.IsNullOrEmpty(name) ? Ledger.DisplayName(keyOrPath) : name.Trim()
                    };
                    decks[key] = deck;
                }
                if (deck.Pages == null) deck.Pages = new Dictionary<int, DwellPage>();

                foreach (var kv in clean)
                {
                    DwellPage existing;
                    if (deck.Pages.TryGetValue(kv.Key, out existing!) && existing != null)
                    {
                        existing.Seconds = DeckDwell.ClampInt((long)existing.Seconds + kv.Value.Seconds);
                        existing.Visits = DeckDwell.ClampInt((long)existing.Visits + kv.Value.Visits);
                    }
                    else
                    {
                        deck.Pages[kv.Key] = kv.Value;
                    }
                }

                deck.Sessions = DeckDwell.ClampInt((long)deck.Sessions + 1);
                // 空科目不覆盖：这节课课表没读到（课间/自习/刚开机）不代表这份课件没有科目。
                if (!string.IsNullOrEmpty(subject)) deck.Subject = subject.Trim();
                if (!string.IsNullOrEmpty(name)) deck.Name = name.Trim();
                deck.UpdatedAt = endedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                TrimDecksLocked();
            }
        }

        /// <summary>
        /// 落盘。任何异常都只记日志、不抛：写 dwell.json 失败绝不能打断引擎收尾，
        /// 更不能让一场课的数据把 ClassIsland 带崩。
        /// </summary>
        public void Save()
        {
            string json;
            lock (sync)
            {
                try
                {
                    var wire = new Wire { Version = Version, Decks = decks };
                    json = JsonSerializer.Serialize(wire, Options);
                }
                catch (Exception ex)
                {
                    lastError = "serialize: " + ex.GetType().Name + " " + ex.Message;
                    WriteLog("dwell save failed (" + lastError + ")");
                    return;
                }
            }
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(tempName, json, new UTF8Encoding(false));
                if (File.Exists(fileName)) File.Replace(tempName, fileName, null);
                else File.Move(tempName, fileName);
                lock (sync) lastError = "";
            }
            catch (Exception ex)
            {
                lock (sync) lastError = "write: " + ex.GetType().Name + " " + ex.Message;
                WriteLog("dwell save failed (" + lastError + ")");
            }
        }

        /// <summary>一组课件的深拷贝快照，最近用过的在前（卡片/面板只读这个）。</summary>
        public List<DeckDwell> Decks()
        {
            lock (sync)
            {
                var list = new List<DeckDwell>(decks.Count);
                foreach (var kv in decks) list.Add(Clone(kv.Value));
                list.Sort(CompareRecent);
                return list;
            }
        }

        /// <summary>单份课件的深拷贝；没有记录返回 null。keyOrPath 可以是路径或桶键。</summary>
        public DeckDwell? Deck(string keyOrPath)
        {
            string key = Ledger.Key(keyOrPath);
            if (key.Length == 0) return null;
            lock (sync)
            {
                DeckDwell d;
                if (!decks.TryGetValue(key, out d!)) return null;
                return Clone(d);
            }
        }

        /// <summary>整份文件的总停留秒数。</summary>
        public int TotalSeconds
        {
            get
            {
                lock (sync)
                {
                    long total = 0;
                    foreach (var kv in decks) if (kv.Value != null) total += kv.Value.TotalSeconds;
                    return DeckDwell.ClampInt(total);
                }
            }
        }

        /// <summary>按科目聚合（科目取每份课件记录里的 Subject，空科目归「未标注科目」）。</summary>
        public List<SubjectDwell> Subjects()
        {
            return AggregateBySubject(Decks());
        }

        /// <summary>
        /// 纯函数版按科目聚合，测试和上面那层拆开：喂一组 DeckDwell 进来就能断言，
        /// 不需要碰文件、也不需要真的跑一场课。
        ///
        /// 排序：总停留时间长的科目在前（老师最该关注的科目先出现），时间相同按科目名
        /// 排序，保证同一份数据每次算出来的顺序一样。
        /// </summary>
        public static List<SubjectDwell> AggregateBySubject(IEnumerable<DeckDwell>? source)
        {
            var groups = new Dictionary<string, SubjectDwell>(StringComparer.Ordinal);
            var order = new List<SubjectDwell>();
            if (source != null)
            {
                foreach (var d in source)
                {
                    if (d == null) continue;
                    string subject = (d.Subject ?? "").Trim();
                    if (subject.Length == 0) subject = CardFileGroups.Unmarked;

                    SubjectDwell g;
                    if (!groups.TryGetValue(subject, out g!) || g == null)
                    {
                        g = new SubjectDwell { Subject = subject };
                        groups[subject] = g;
                        order.Add(g);
                    }
                    g.TotalSeconds = DeckDwell.ClampInt((long)g.TotalSeconds + d.TotalSeconds);
                    g.Visits = DeckDwell.ClampInt((long)g.Visits + d.Visits);
                    g.PageCount = DeckDwell.ClampInt((long)g.PageCount + d.PageCount);
                    g.Sessions = DeckDwell.ClampInt((long)g.Sessions + d.Sessions);
                    g.Decks++;
                    g.Items.Add(d);
                }
            }

            order.Sort((a, b) =>
            {
                int c = b.TotalSeconds.CompareTo(a.TotalSeconds);
                return c != 0 ? c : string.CompareOrdinal(a.Subject, b.Subject);
            });
            // 组内也按最近使用时间排，和课件库的规矩一致。
            foreach (var g in order) g.Items.Sort(CompareRecent);
            return order;
        }

        // ------------------------------------------------------------------ disk

        sealed class Wire
        {
            public int Version { get; set; }
            public Dictionary<string, DeckDwell> Decks { get; set; } = new Dictionary<string, DeckDwell>();
        }

        void ReadFromDisk()
        {
            if (!File.Exists(fileName)) return;
            try
            {
                string json = File.ReadAllText(fileName, Encoding.UTF8);
                var wire = JsonSerializer.Deserialize<Wire>(json, Options);
                if (wire == null || wire.Decks == null) return;

                var loaded = new Dictionary<string, DeckDwell>(StringComparer.Ordinal);
                foreach (var kv in wire.Decks)
                {
                    string key = (kv.Key ?? "").Trim();
                    if (key.Length == 0) continue;
                    var deck = SanitizeDeck(key, kv.Value);
                    if (deck != null) loaded[key] = deck;
                }
                lock (sync) decks = loaded;
            }
            catch (Exception ex)
            {
                // 坏文件不能一上来就覆盖：先改名留档，再空着开始（和 Ledger.Load 一个思路）。
                string why = ex.GetType().Name + " " + ex.Message;
                try
                {
                    File.Move(fileName, fileName + ".corrupt-"
                        + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                }
                catch { }
                lock (sync) lastError = "read: " + why;
                WriteLog("dwell load failed (" + why + "), starting empty");
            }
        }

        /// <summary>读到的每一份课件都洗一遍：页号/秒数/次数为脏的直接丢，空壳课件不留。</summary>
        static DeckDwell? SanitizeDeck(string key, DeckDwell? deck)
        {
            if (deck == null) return null;
            var pages = new Dictionary<int, DwellPage>();
            if (deck.Pages != null)
            {
                foreach (var kv in deck.Pages)
                {
                    int pageNo = kv.Key;
                    var p = kv.Value;
                    if (pageNo <= 0 || p == null) continue;
                    int seconds = p.Seconds;
                    if (seconds <= 0) continue;                       // 负秒/0 秒 = 脏数据
                    if (seconds > MaxPageSeconds) seconds = MaxPageSeconds;
                    int visits = p.Visits;
                    if (visits <= 0) visits = 1;                      // 有秒数没次数：当成停过 1 次
                    if (visits > MaxPageVisits) visits = MaxPageVisits;
                    if (pages.Count >= MaxPagesPerDeck && !pages.ContainsKey(pageNo)) break;
                    pages[pageNo] = new DwellPage { Seconds = seconds, Visits = visits };
                }
            }
            if (pages.Count == 0) return null;                        // 一页有效停留都没有，不留空壳

            deck.Pages = pages;
            if (deck.Sessions < 0) deck.Sessions = 0;
            deck.Subject = (deck.Subject ?? "").Trim();
            deck.Name = string.IsNullOrEmpty(deck.Name) ? Ledger.DisplayName(key) : deck.Name.Trim();
            deck.UpdatedAt = deck.UpdatedAt ?? "";
            return deck;
        }

        /// <summary>最多留 MaxDecks 份：丢最久没用的那份（UpdatedAt 是 yyyy-MM-dd HH:mm:ss，直接比字符串）。</summary>
        void TrimDecksLocked()
        {
            if (decks.Count <= MaxDecks) return;
            while (decks.Count > MaxDecks)
            {
                string? oldestKey = null;
                string oldestAt = "";
                string oldestName = "";
                foreach (var kv in decks)
                {
                    string at = kv.Value == null ? "" : (kv.Value.UpdatedAt ?? "");
                    string nm = kv.Value == null ? "" : (kv.Value.Name ?? "");
                    if (oldestKey == null
                        || string.CompareOrdinal(at, oldestAt) < 0
                        || (at == oldestAt && string.CompareOrdinal(nm, oldestName) < 0))
                    {
                        oldestKey = kv.Key;
                        oldestAt = at;
                        oldestName = nm;
                    }
                }
                if (oldestKey == null) break;
                decks.Remove(oldestKey);
            }
        }

        static int CompareRecent(DeckDwell a, DeckDwell b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            int c = string.CompareOrdinal(b.UpdatedAt ?? "", a.UpdatedAt ?? "");
            if (c != 0) return c;
            return string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
        }

        static DeckDwell Clone(DeckDwell src)
        {
            var copy = new DeckDwell
            {
                Subject = src.Subject ?? "",
                Name = src.Name ?? "",
                UpdatedAt = src.UpdatedAt ?? "",
                Sessions = src.Sessions
            };
            if (src.Pages != null)
                foreach (var kv in src.Pages)
                {
                    if (kv.Value == null) continue;
                    copy.Pages[kv.Key] = new DwellPage { Seconds = kv.Value.Seconds, Visits = kv.Value.Visits };
                }
            return copy;
        }

        void WriteLog(string message)
        {
            try { if (log != null) log(message); } catch { }
        }
    }
}
