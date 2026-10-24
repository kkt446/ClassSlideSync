using System;
using Avalonia.Controls;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Ui;

namespace ClassSlideSync.Plugin.Services
{
    /// <summary>
    /// Wires the UI-free session engine to the native overlay card.
    ///
    /// The engine owns its own COM thread (single-threaded apartment, blocking calls), and the
    /// card owns its own Win32 message thread. Neither one is the ClassIsland UI thread, on
    /// purpose: a plugin must not be able to stall or crash its host.
    ///
    /// 生命周期分两层（老师的要求：不用课件的时候"啥都不留"）：
    ///   * 调度器（scheduler）一直轻轻跑着，每 2 秒问一次"现在该不该待命"。它不碰 COM、不建窗口。
    ///   * 待命（armed）才创建 COM 引擎和卡片窗口；一旦不该待命，Disarm 把它们全部销毁。
    /// 判断"该不该待命"的规则见 Core/LessonClock.cs（上课前 2 分钟）与 settings.ArmOnDeckOpenOutsideClass
    /// （课表之外老师自己打开了 PowerPoint/WPS 也照样待命 —— 2026-10-15 用户要求"检测到 ppt 软件启动就打
    /// 开"，所以进程枚举现在 2 秒一次，卡片两三秒内就出现）。
    /// </summary>
    public sealed class SlideSyncService : IDisposable
    {
        readonly SyncSettings settings;
        readonly string pluginConfigFolder;
        Ledger ledger;
        SessionEngine engine;
        NativeOverlayCard card;
        // 控制面板是单实例：卡片上的齿轮再点一次只 Activate()，不开第二个窗口。
        // 窗口只在 Avalonia UI 线程上构造/显示（见 OnPanelRequested）。
        ControlPanel panel;
        Func<LessonTag> lessonContext;
        /// <summary>
        /// 课表读数快照：科目、周几、第几节。只由调度器（每 2 秒）刷新，引擎线程只读。
        ///
        /// 为什么要缓存：2026-10-24 之前 lessonContext 这个字段**从来没被赋值**，引擎里那段
        /// "把科目写进台账"的代码等于不存在，台账 39 条记录的 Subject / LessonName 全是空串。
        /// 接上课表服务时又不能现场读 —— SessionEngine.BuildState 每次状态心跳（一秒好几次）
        /// 都会问一遍"现在哪一科"，那是宿主的属性通知链，不能放在引擎线程上。
        /// </summary>
        volatile LessonTag lessonTag = new LessonTag();
        // 上一次写进日志的科目+节次，只在变化时记一行（调度器 2 秒一跳，不然日志被冲爆）。
        string lastLessonTag = "";
        volatile bool started;    // 调度器在跑
        volatile bool armed;      // 引擎与卡片存在
        System.Threading.Timer scheduler;
        System.Threading.Timer watchdog;
        // 主题订阅要留得住句柄：用匿名 lambda 订阅、又不在 Stop() 里退订的话，
        // 插件被停用后 IThemeService 还攥着这个回调（服务对象活着、每次换主题还往
        // 已停止的插件里跑一趟），再 Start 一次就会挂上第二个。
        ClassIsland.Core.Abstractions.Services.IThemeService? themeService;
        // 主题事件用「具名字段」保存订阅：Stop() 里要靠它退订。
        // 事件类型是 EventHandler<ThemeUpdatedEventArgs>（以前用匿名 lambda，退不掉，插件停用后还会被复活）。
        EventHandler<ClassIsland.Core.Models.Theming.ThemeUpdatedEventArgs>? themeHandler;
        int restarts;
        string lastStatus = "";
        string lastClockNote = "";
        // 上一次替它开过课件文件夹的那节课（用户 2026-10-16 第 1 条）：同一节课只开一次，
        // 下课清零，下一节课再开。值就是课表读数（"第3节 09:40-10:20"）。
        string lastLessonKey = "";
        // 2026-10-24 功能4：记住"刚才这一节是哪一科"，下课铃响时主讲科目可能已经变成下一节（或空）。
        bool wasInClass;
        string lastClassSubject = "";
        // 功能7（2026-10-24）：预备态当前展示的是哪份课件/第几页。只在变化时写一行日志，
        // 否则调度器每 2 秒就会往 app.log 里灌一条。
        string lastReadyKey = "";
        // 功能8（2026-10-24）：防止连点。同一份课件 8 秒内只拉起一次——老师点两下会开两个
        // 演示实例，第二个还会把第一个的放映顶掉。
        string lastOpenDeckPath = "";
        DateTime lastOpenDeckAt = DateTime.MinValue;
        bool clockLogged;
        DateTime lastPrune = DateTime.MinValue;
        // 课表说了算、且现在不在正课时间里 → 卡片整体藏起来（下课就消失，用户 2026-10-13 要求）。
        // 引擎照旧盯着课件，只是不往屏幕上画。
        volatile bool suppressCard;

        // 演示软件此刻是不是"真的在桌面上"（进程在 + 有可见窗口，见 ScheduleTick），以及它叫什么。
        // 卡片靠这两个值决定"要不要露出等待中的小窗、标题写谁"（用户 2026-10-15 要求）。
        volatile bool suiteShowing;
        volatile string suiteName = "";
        // 上一次记录的"演示软件状态"，只为了在变化时写一行日志（见 ScheduleTick 的 suite: 行）。
        bool lastSuiteProcess;
        bool lastSuiteShowing;

        public SlideSyncService(SyncSettings settings, string pluginConfigFolder)
        {
            this.settings = settings;
            this.pluginConfigFolder = pluginConfigFolder;
            // 2026-10-24 修 bug：以前这里是 null，引擎里 beginSession 的科目写入永远不执行。
            // 现在固定指向内存快照（只有调度器会替换 lessonTag），引擎怎么调都不会读到半截状态，
            // 也不会被拖进 ClassIsland 的 UI 线程。
            lessonContext = () => lessonTag;
        }

        public SessionEngine Engine { get { return engine; } }
        public Ledger Ledger { get { return ledger; } }
        public NativeOverlayCard Card { get { return card; } }

        /// <summary>true = 引擎与卡片正在运行（上课前 1 分钟到下课后那段时间）。</summary>
        public bool Armed { get { return armed; } }

        /// <summary>最近一次进入待命的原因，设置页/日志用它解释"插件现在为什么开着"。</summary>
        public string ArmReason { get; private set; }

        // 卡片最后一次拿到的状态 = 老师此刻屏幕上看到的东西。
        // 点跳页时用它取“那一页”，而不是让引擎再去查一遍台账（见 OnJumpRequested）。
        volatile CardState lastState;

        /// <summary>ClassIsland 此刻的主题（主色、圆角、字体、字号）；卡片外观的第一来源。</summary>
        public ClassIslandTheme Theme { get; private set; }

        /// <summary>主题 ⊕ 用户自定义之后，卡片最终的样子。</summary>
        public CardAppearance Appearance { get; private set; }

        /// <summary>Supplies (subject, lesson name) so ledger rows carry the lesson context.</summary>
        public Func<LessonTag> LessonContext
        {
            get { return lessonContext; }
            set
            {
                lessonContext = value;
                var e = engine;
                if (e != null) e.LessonContext = value;
            }
        }

        // ---- 调度器 -------------------------------------------------------------

        public void Start()
        {
            if (started) return;
            started = true;

            // 台账与主题是常驻的：即使不待命，设置页也要能读到历史、看到主题。
            ledger = Ledger.Load(settings.DataFolder);
            ledger.Log("=== plugin start, data folder: " + ledger.Root);
            // 科目目录命中有歧义时由 FindSubjectFolder 记一条日志（F2）；设置类不认识 Ledger，
            // 这里把出口接上，日志仍然落在同一个 app.log。
            settings.LogSink = m => { try { ledger.Log(m); } catch { } };

            Theme = ClassIslandTheme.Load(pluginConfigFolder, m => ledger.Log("theme: " + m));
            ProbeUiTheme();
            Appearance = CardAppearance.Resolve(settings, Theme);
            ledger.Log("theme: " + Theme.Describe());
            ledger.Log("card look: " + Appearance.Describe());

            WatchThemeChanges();
            PruneHistory();

            ledger.Log("scheduler started (pre-class " + settings.PreClassLeadSeconds + "s, grace "
                + settings.PostClassGraceSeconds + "s, arm-on-deck=" + settings.ArmOnDeckOpenOutsideClass + ")");
            // 2 秒一次：老师刚打开 PPT/WPS，卡片要在两三秒内就冒出来（原来是 5 秒，显得像坏了）。
            // 这一跳只读课表 + 枚举进程名，不碰 COM、不建窗口。
            scheduler = new System.Threading.Timer(ScheduleTick, null, 300, 2000);
        }

        // 调度器重入闸：Timer 是周期性的（2 s 一次），某一跳超过 2 s 时下一个回调会
        // 在另一条线程池线程上叠进来。见 ScheduleTick 里的注释。
        int tickBusy;

        void ScheduleTick(object state)
        {
            // 一跳只要超过 2 s（COM 重连 / 进程枚举偶发卡顿 / 磁盘慢），Timer 的下一拍就会
            // 在另一条线程池线程上叠进来，两跳同时改 armed/engine/卡片，典型症状是卡片闪一下
            // 消失、或者 engine 被指到一个已经 Dispose 的实例上。这里直接丢掉重叠的那一拍：
            // 2 s 后还有下一拍，不需要补跑。
            if (Interlocked.Exchange(ref tickBusy, 1) == 1) return;
            try
            {
                if (!started) return;

                // 2026-10-24 功能3：刷新课表快照（科目 / 周几 / 第几节）。引擎在另一条线程上只读它，
                // 开始一次记录时抄进台账，历史行就能写成"周三 第 3 节 · 15:20 · 第 5 → 8 页"。
                RefreshLessonTag();

                bool want = false;
                string reason = "";
                bool timetableGoverns = false;
                bool inClass = false;
                string lessonKey = "";
                // 功能7（2026-10-24）：课前预备态要用课表读数里的 InPreClass / PreClassSubject，
                // 而 suppressCard 也要知道"现在是课前窗口"（那不算课表外），所以把读数提到外面。
                LessonClock.Verdict clock = null;

                if (settings.AutoStartBeforeClass)
                {
                    var v = LessonClock.Check(settings.PreClassLeadSeconds, settings.PostClassGraceSeconds);
                    clock = v;
                    // 第一次读课表记一行：老师（和我）能确认插件读到的是哪张课表。
                    if (!clockLogged)
                    {
                        clockLogged = true;
                        ledger.Log("clock: " + (v.Known ? "课表 " + v.All : "没有课表"));
                    }
                    inClass = v.InPeriod;
                    lessonKey = v.InPeriod ? v.Detail : "";
                    // 今天课表上有课 → 显示与否由课表说了算（下课铃一响就隐藏，用户 2026-10-13 要求）。
                    timetableGoverns = v.Known && v.Periods > 0;
                    if (v.InLesson) { want = true; reason = "上课时段 " + v.Detail; }
                    // 课表读数变了就记一行：老师（和我）能确认插件真的在读课表、在等哪一节课。
                    // 倒计时的秒数只在这个日志里按分钟粗写，否则调度器每 2 秒就写一行。
                    string note = v.Known ? (v.InLesson ? "在上课时段 " + v.Detail : RoundCountdown(v.Detail)) : "没有课表";
                    if (note != lastClockNote)
                    {
                        lastClockNote = note;
                        ledger.Log("clock: " + note);
                    }
                }

                // 用户 2026-10-16 第 1 条：正课一开始就把这一科的课件文件夹打开（同一节课只开一次）。
                // 值取课表读数（含节次与时间），下课铃一响 lessonKey 变空，下一节课重新开。
                if (inClass && lessonKey.Length > 0 && lessonKey != lastLessonKey)
                {
                    lastLessonKey = lessonKey;
                    TryOpenSubjectFolder();
                }

                // 2026-10-24 功能4：上课期间一直记住这一科，下课铃响的那一刻用它去台账里找这一节的记录。
                if (inClass)
                {
                    string nowSubject = CurrentSubjectName();
                    if (nowSubject.Length > 0) lastClassSubject = nowSubject;
                }
                else if (wasInClass)
                {
                    MaybeNotifyClassEnd();
                }
                wasInClass = inClass;

                // 用户 2026-10-15 的要求："插件在后台一直常驻，只看 WPS 演示 / PowerPoint 的进程 ——
                // 开了就把卡片从隐藏改成打开，关了就继续隐藏。" 所以这里不再要求"必须先打开课件"。
                // 进程那一侧已经排掉了 WPS 文字/表格/云服务/传输助手（见 PptBridge.SuiteIsRunning）。
                // 但**不能只看进程名**：这台机器上 WPS 12.1 把演示窗口挂在 wps.exe 上（wpp.exe 只是
                // 后台引擎），只认进程名时老师开着 PPT 插件也认为"演示软件不在" —— 所以进程在、
                // 或者桌面上有演示窗口（窗口类名 PPTFrameClass / PP12FrameClass / screenClass），都算。
                bool seewoProcess = SeewoBridge.LooksRunning();
                // 注意顺序：SuiteActive() 内部就是 SuiteIsRunning() || SuiteOnScreen()。
                // 以前这里同时调 SuiteActive() 和 SuiteOnScreen()，同一拍里 EnumWindows 跑了两遍
                // （每遍还要为每个可见窗口开一次进程句柄）。窗口只数一遍，进程判定复用同一结果。
                bool suiteOnScreen = PptBridge.SuiteOnScreen();
                bool pptProcess = PptBridge.SuiteIsRunning() || suiteOnScreen;
                bool suiteProcess = pptProcess || seewoProcess;
                if (pptProcess) suiteName = PptBridge.SuiteName();
                else if (seewoProcess) suiteName = "希沃白板";
                if (suiteProcess && suiteName.Length == 0) suiteName = "演示软件";
                bool bridgeHoldsDeck = engine != null && engine.Last != null
                    && (PptBridge.LooksLikePath(engine.Last.FilePath) || SeewoBridge.IsSeewoKey(engine.Last.FilePath));
                suiteShowing = suiteProcess && (bridgeHoldsDeck || suiteOnScreen || seewoProcess);

                // 只在变化时记一行：老师报"我开 PPT 卡片没出来"时，这一行直接告诉我们卡在哪一步
                // （进程没认出来 / 桌面上没看到演示窗口 / 桥接那个实例里没有课件）。
                if (suiteProcess != lastSuiteProcess || suiteShowing != lastSuiteShowing)
                {
                    lastSuiteProcess = suiteProcess;
                    lastSuiteShowing = suiteShowing;
                    ledger.Log("suite: 进程=" + (suiteProcess ? "在" : "不在")
                        + " 希沃=" + (seewoProcess ? "在" : "不在")
                        + " 桌面窗口=" + (suiteOnScreen ? "有" : "无")
                        + " 桥上有课件=" + (bridgeHoldsDeck ? "是" : "否")
                        + " → 卡片" + (suiteShowing ? "显示" : "隐藏"));
                }

                if (!want && settings.ArmOnDeckOpenOutsideClass && suiteProcess)
                {
                    want = true;
                    reason = "检测到 " + (suiteName.Length > 0 ? suiteName : "演示软件");
                }

                // 卡片露不露面：课表说了算的时候只在上课时段显示；想在上课之外（自习课）也看到，
                // 就把设置页里的「课表外也显示卡片」打开。
                // 功能7（2026-10-24）：课前预备窗口不算"课表外"——那正是要提示老师打开课件的时候，
                // 否则 ShowCardOutsideClass 关着（默认）时预备态会被 suppressCard 直接抹掉。
                bool inPreClass = clock != null && clock.InPreClass;
                suppressCard = timetableGoverns && !inClass && !inPreClass && !settings.ShowCardOutsideClass;

                // 2026-10-15 加的护栏：桥上明明还开着课件（老师正看着呢），就不许因为"进程名没认出来"
                // 而退出 —— 一退出卡片就没了，老师看到的就是"啥都没弹"。进程枚举有时候认不出 WPS
                // 的某些版本/时机（SuiteIsRunning 的名字表是猜的），手上这份课件的证据更可靠。
                var lastSnap = engine == null ? null : engine.Last;
                bool bridgeHasDeck = lastSnap != null
                    && (PptBridge.LooksLikePath(lastSnap.FilePath) || SeewoBridge.IsSeewoKey(lastSnap.FilePath));

                if (want && !armed) Arm(reason);
                else if (!want && armed)
                {
                    if (bridgeHasDeck)
                    {
                        if (settings.DiagnosticLog) ledger.Log("keep: 桥上还有课件（" + lastSnap.FilePath + "），不退出");
                    }
                    else
                    {
                        Disarm("没有课表时段，也没检测到演示软件");
                    }
                }
                var live = engine;
                if (live != null)
                {
                    live.SuppressCard = suppressCard;
                    live.SuiteRunning = suiteShowing;
                    live.SuiteName = suiteName;

                    // 功能7（2026-10-24）：课前预备窗口里，如果这一科上次那份没讲完，就给引擎一份
                    // 「打开并接着讲」的快照（引擎只收快照，台账还是在这里读）。
                    // 判据全部来自课表：InPreClass 已经排除了正课和下课后的 grace；科目取下一节
                    // （NextClassSubject），不能拿 CurrentSubject —— 课前通常还在上一科的课间。
                    if (inPreClass && clock.PreClassSubject.Length > 0)
                    {
                        var rec = ledger.LastUnfinishedForSubject(clock.PreClassSubject, settings.HistoryRetentionDays);
                        // 文件被挪走/删掉时不进预备态：点了也打不开，不如继续显示"等待放映"。
                        if (rec != null && rec.EndSlide > 0 && !string.IsNullOrEmpty(rec.FilePath)
                            && System.IO.File.Exists(rec.FilePath))
                        {
                            int remaining = rec.SlideCount > rec.EndSlide ? rec.SlideCount - rec.EndSlide : 0;
                            live.SetReady(rec.FilePath, rec.EndSlide, remaining);
                            string readyKey = rec.FilePath + "|" + rec.EndSlide + "|" + remaining;
                            if (readyKey != lastReadyKey)
                            {
                                lastReadyKey = readyKey;
                                ledger.Log("ready: 科目=" + clock.PreClassSubject + " 文件=" + Path.GetFileName(rec.FilePath)
                                    + " 停在第 " + rec.EndSlide + " 页"
                                    + (remaining > 0 ? "（还有 " + remaining + " 页）" : "")
                                    + " · " + clock.PreClassDetail);
                            }
                        }
                        else
                        {
                            if (lastReadyKey.Length > 0) { lastReadyKey = ""; ledger.Log("ready: off（这一科没有没讲完的记录）"); }
                            live.ClearReady();
                        }
                    }
                    else
                    {
                        if (lastReadyKey.Length > 0) { lastReadyKey = ""; ledger.Log("ready: off（离开课前窗口）"); }
                        live.ClearReady();
                    }

                    // 2026-10-17 用户实测「关掉 PPT 再打开，插件要好几十秒才响应」：那几十秒里引擎
                    // 线程正卡在 WPS 的 COM 调用上（它自己没辙，只能等），但卡片不该跟着一起僵住。
                    // 心跳超过 2.5 秒没动，就用内存里的状态兜一次，让老师至少看到「正在识别课件…」。
                    if (suiteShowing && (DateTime.UtcNow - live.LastStepUtc).TotalSeconds > 2.5)
                    {
                        live.PushCheapState();
                    }
                }

                if ((DateTime.Now - lastPrune).TotalHours > 24) PruneHistory();
            }
            catch (Exception ex)
            {
                try { ledger?.Log("scheduler failed: " + ex.Message); } catch { }
            }
            finally
            {
                Interlocked.Exchange(ref tickBusy, 0);
            }
        }

        /// <summary>
        /// 读一次课表，把结果换成内存快照（<see cref="lessonTag"/>）。调度器每 2 秒调一次，
        /// 引擎线程只读这个快照 —— 见字段 <see cref="lessonTag"/> 上的注释。
        ///
        /// <see cref="LessonTimetable.Read"/> 自己不会抛，这里再包一层：Timer 的回调在
        /// 线程池线程上跑，漏一个异常就可能把整个调度器带走（插件就"再也不动"了）。
        /// 科目/节次变化时只写一行日志：每 2 秒一行会把日志冲爆（老师的机器不是开发机）。
        /// </summary>
        void RefreshLessonTag()
        {
            try
            {
                var tag = LessonTimetable.Read(DateTime.Now);
                lessonTag = tag;
                string note = tag.Subject + "|" + tag.Weekday + "|" + tag.Period + "|" + tag.PeriodName + "|" + tag.LessonName;
                if (note == lastLessonTag) return;
                lastLessonTag = note;
                ledger?.Log("lesson: 科目=" + (tag.Subject.Length > 0 ? tag.Subject : "（空，未标注科目）")
                    + " 周" + tag.Weekday + " 第" + tag.Period + "节"
                    + (tag.PeriodName.Length > 0 ? " 时段=" + tag.PeriodName : "")
                    + " → 历史行" + (tag.Period > 0 ? "写节次" : "退回日期"));
            }
            catch (Exception ex)
            {
                try { ledger?.Log("lesson: 读取课表失败 " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 把「下一节 14:00（5680s 后待命）」里的秒数写粗一点（分钟/小时）。只是为了日志别刷屏：
        /// 调度器现在 2 秒一跳，照原样写会每 2 秒多一行。
        /// </summary>
        static string RoundCountdown(string detail)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(detail, @"^(.*)（(\d+)s 后待命）$");
                if (!m.Success) return detail;
                int sec = int.Parse(m.Groups[2].Value);
                if (sec >= 3600) return m.Groups[1].Value + "（约 " + Math.Round(sec / 3600.0) + " 小时后待命）";
                if (sec >= 120) return m.Groups[1].Value + "（约 " + Math.Round(sec / 60.0) + " 分钟后待命）";
                return m.Groups[1].Value + "（约 " + (sec / 10 * 10) + "s 后待命）";
            }
            catch { return detail; }
        }

        /// <summary>
        /// 老师把卡片拖到别处了（2026-10-16 第 3 条）：把新位置写回设置，下次启动就从这儿出现。
        /// 卡片自己已经先摆好了，这里只负责记住；Plugin 订阅了 Settings.PropertyChanged，
        /// 赋值会顺带落盘。
        /// </summary>
        void OnPositionChanged(bool onRight, int verticalPercent)
        {
            try
            {
                settings.CardOnLeft = !onRight;
                settings.CardVerticalPercent = verticalPercent;
                ledger.Log("card moved: side=" + (onRight ? "right" : "left") + " v=" + verticalPercent + "%");
            }
            catch (Exception ex) { try { ledger?.Log("card move failed: " + ex.Message); } catch { } }
        }

        /// <summary>
        /// 上课了：把当前科目对应的课件文件夹打开（2026-10-16 第 1 条）。
        /// 按 docs.classisland.tech 的标准，外部导航交给宿主的 IUriNavigationService.Navigate
        /// （ClassIsland 自己也是 shell-execute 打开文件/文件夹的，见 ClassIsland.dll 里的
        /// ProcessStartInfo.UseShellExecute）；它要是抛异常，再退回落回 .NET 的 ShellExecute。
        /// </summary>
        /// <summary>
        /// 2026-10-24 功能4：下课铃响的那一刻弹一条 Windows 通知。
        /// 用户的要求是"下课且当前课件已退出"——课件还开着说明老师在拖堂讲最后几页，这时候弹通知就是打扰，
        /// 所以只要演示软件还在前台或者桥上还挂着课件，就直接不弹。
        /// </summary>
        void MaybeNotifyClassEnd()
        {
            try
            {
                if (!settings.ClassEndNotification) return;

                bool deckStillOpen = suiteShowing
                    || (engine != null && engine.Last != null && PptBridge.SuiteActive());
                if (deckStillOpen)
                {
                    ledger.Log("notify: 下课了但课件还开着，不弹");
                    return;
                }

                var rec = FindTodayLessonRecord(lastClassSubject);
                if (rec == null)
                {
                    ledger.Log("notify: 这一节没有可通知的记录，跳过");
                    return;
                }

                string name = System.IO.Path.GetFileName(rec.FilePath ?? "");
                if (name.Length == 0) name = rec.FilePath ?? "课件";

                int total = rec.SlideCount;
                int page = rec.EndSlide;
                string text;
                if (total > 0 && page >= total)
                    text = "「" + name + "」全讲完了，共 " + total + " 页";
                else if (total > 0 && page > 0)
                    text = "「" + name + "」记到第 " + page + " 页，还剩 " + (total - page) + " 页";
                else
                    text = "「" + name + "」记到第 " + Math.Max(1, page) + " 页";

                string title = "课件续页" + (lastClassSubject.Length > 0 ? " · " + lastClassSubject : " · 下课");

                // 2026-10-24 用户要求「要横幅」：优先走操作中心横幅（Tools\ToastHelper 这个独立小工具，
                // 插件包自带）。helper 不在、系统通知被关掉、helper 报错 —— 全都静默返回 false，
                // 这时再退回原来的托盘气泡，保证「不管在哪台机器上，至少有一条提醒」。
                if (ToastNotifier.Show(title, text)) ledger.Log("notify: toast " + text);
                else if (TrayNotifier.Show(title, text)) ledger.Log("notify: balloon " + text);
                else ledger.Log("notify: 系统不让弹（横幅与托盘气泡都失败）");
            }
            catch (Exception ex)
            {
                ledger.Log("notify failed: " + ex.Message);
            }
        }

        /// <summary>今天最近的一条记录；给了科目就优先同一科，找不到再退回今天任意一条。</summary>
        SessionRecord? FindTodayLessonRecord(string subject)
        {
            if (ledger == null) return null;
            DateTime today = DateTime.Today;
            SessionRecord? best = null;
            SessionRecord? bestAny = null;
            DateTime bestAt = DateTime.MinValue;
            DateTime bestAnyAt = DateTime.MinValue;

            foreach (var kv in ledger.ByFile)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r == null) continue;
                    DateTime at;
                    if (!DateTime.TryParse(r.StartedAt, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out at)) continue;
                    if (at.Date != today) continue;

                    if (at > bestAnyAt) { bestAnyAt = at; bestAny = r; }

                    string rs = (r.Subject ?? "").Trim();
                    bool sameSubject = subject.Length == 0
                        || string.Equals(rs, subject, StringComparison.Ordinal);
                    if (sameSubject && at > bestAt) { bestAt = at; best = r; }
                }
            }

            return best ?? bestAny;
        }

        void TryOpenSubjectFolder()
        {
            string subject = "";
            string folder = "";
            try
            {
                if (!settings.AutoOpenSubjectFolder) return;
                subject = CurrentSubjectName();
                if (subject.Length == 0) { ledger.Log("subject folder: 课表没给出科目名，跳过"); return; }
                folder = settings.FindSubjectFolder(subject);
                if (folder.Length == 0) { ledger.Log("subject folder: " + subject + " 没有配文件夹"); return; }
                if (!System.IO.Directory.Exists(folder)) { ledger.Log("subject folder: " + folder + " 不存在"); return; }

                bool handed = false;
                try
                {
                    var nav = IAppHost.TryGetService<IUriNavigationService>();
                    if (nav != null)
                    {
                        // 实测（.NET 8）：new Uri(@"D:\课件\第3节 #1") 会把 # 转义成 %23、不会截断，
                        // 含 % 的目录同理，所以这里直接交 URI 是安全的；真正不可检测的是宿主
                        // “收下 URI 但什么都没打开”，那种情况只能靠用户反馈（见报告“未改”一节）。
                        nav.Navigate(new Uri(folder));
                        handed = true;
                    }
                }
                catch (Exception ex)
                {
                    ledger.Log("subject folder: Navigate 失败（" + ex.GetType().Name + "），改用 ShellExecute");
                }

                if (!handed)
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                }

                ledger.Log("subject folder: opened " + folder + " for " + subject);
            }
            catch (Exception ex)
            {
                try { ledger?.Log("subject folder failed: " + subject + " -> " + folder + " : " + ex.Message); } catch { }
            }
        }

        /// <summary>当前科目名（ClassIsland 课表里的"科目"，课间/没有课表时是空串）。</summary>
        string CurrentSubjectName()
        {
            try
            {
                var lessons = IAppHost.TryGetService<ILessonsService>();
                var subject = lessons == null ? null : lessons.CurrentSubject;   // 属性在父接口 IPublicLessonsService 上
                if (subject == null) return "";
                string name = subject.Name;
                return string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
            }
            catch (Exception ex)
            {
                try { ledger?.Log("subject read failed: " + ex.Message); } catch { }
                return "";
            }
        }

        void Arm(string reason)
        {
            if (armed) return;
            // Stop() 之后任何一拍都不许再建窗口：调度器可能正跑到一半（Stop 不会等它），
            // 建出来的卡片没人收，宿主退出时就会留一个孤儿窗口。
            if (!started) return;
            armed = true;
            ArmReason = reason;
            ledger.Log("arm: " + reason);

            // 卡片窗口是插件自建的 Win32 窗口（Ui\NativeOverlayCard.cs 里从 266 行起在自己的
            // "ClassSlideSync-overlay" 线程上跑消息循环），不是 Avalonia Window，任何线程都能 new。
            // 之前这里写着“必须切到宿主 UI 线程”，那是错的：真去 Post + 等 UI 线程，宿主 UI 卡住时
            // 反而把调度器线程钉住 3 秒，还会留下半建好的卡片。直接在建，失败就把 armed 收回去。
            NativeOverlayCard? c = null;
            try
            {
                c = BuildCard();
            }
            catch (Exception ex)
            {
                // 建窗口失败绝不能留下 armed=true：否则这一轮永远不会再 Arm，也不会走 Disarm，
                // 插件就这么静默死掉（老师看到的现象是“卡片再也不出来了”）。
                armed = false;
                ledger.Log("arm failed: 建卡片失败：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            if (c == null)
            {
                // 建窗口失败绝不能留下 armed=true：否则这一轮永远不会再 Arm，
                // 也不会走 Disarm，插件就这么静默死掉了。
                armed = false;
                ledger.Log("arm failed: 建卡片返回 null");
                return;
            }
            card = c;

            // 卡片的失败（窗口建好但没显示、被别的窗口挡住）在这里是看不见的，只能靠事后一行 Diag。
            // 延迟 1.5 s 写日志，用来区分“老师真的看到卡片”和“窗口根本没开出来”。
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1500);
                try { ledger.Log("overlay: " + c.Diag()); } catch { }
            });

            SessionEngine e;
            try
            {
                e = new SessionEngine(ledger, settings)
                {
                    LessonContext = lessonContext,
                    Diag = settings.DiagnosticLog,
                    SuppressCard = suppressCard,
                    SuiteRunning = suiteShowing,
                    SuiteName = suiteName
                };
                e.StateChanged += OnStateChanged;
                e.Start();
            }
            catch (Exception ex)
            {
                // 引擎起不来就把卡片和 armed 一起收回去，别留下“只有卡片、没有引擎”的半成品。
                card = null!;   // 字段声明成非空类型，这里明确表达“摘掉”（null! 不产生新告警）
                try { c.Dispose(); } catch { }
                armed = false;
                ledger.Log("arm failed: 引擎启动失败：" + ex.GetType().Name + " " + ex.Message);
                return;
            }
            engine = e;

            ledger.Log("engine started");

            // A COM call can block forever (PowerPoint waiting on a modal dialog does exactly that),
            // and a blocked worker means the plugin quietly stops noticing lessons -- the failure the
            // user hit in the classroom. Watch the heartbeat and start a replacement engine; the stuck
            // thread is leaked on purpose (nothing can abort a blocked COM call), which is why the
            // restart count is capped.
            restarts = 0;
            watchdog = new System.Threading.Timer(WatchdogTick, null, 20000, 10000);
        }

        /// <summary>在**调用方自己的线程**上把卡片建好（调用方保证是 UI 线程）。</summary>
        NativeOverlayCard BuildCard()
        {
            var c = new NativeOverlayCard();
            c.Configure(Appearance, settings.AutoShowOverlay, settings.AutoHide, settings.AutoHideSeconds, settings.EnableControlPanel);
            c.JumpRequested += OnJumpRequested;
            c.HistoryJumpRequested += OnHistoryJumpRequested;   // 历史面板里点某一条 → 跳到那份文件的这一页
            c.PositionChanged += OnPositionChanged;   // 用户第 3 条：拖完记住位置
            c.PanelRequested += OnPanelRequested;     // 右上角齿轮 → 独立 Avalonia 控制面板
            c.OpenDeckRequested += OnOpenDeckRequested;   // 功能8：预备态点「打开并接着讲」
            // 卡片的显示/隐藏变化写进 app.log：老师报"卡片该出来却没出来"时，日志要能直接看出来。
            c.Log = m => { try { ledger.Log("card: " + m); } catch { } };
            c.Start();
            return c;
        }

        void Disarm(string reason)
        {
            if (!armed) return;
            armed = false;
            ledger.Log("disarm: " + reason);

            try { watchdog?.Dispose(); } catch { }
            watchdog = null;

            var e = engine;
            engine = null;
            if (e != null)
            {
                try { e.StateChanged -= OnStateChanged; } catch { }
                try { e.Dispose(); } catch (Exception ex) { ledger.Log("engine stop failed: " + ex.Message); }
            }

            var c = card;
            card = null;
            if (c != null)
            {
                try { c.JumpRequested -= OnJumpRequested; } catch { }
                try { c.HistoryJumpRequested -= OnHistoryJumpRequested; } catch { }
                try { c.PositionChanged -= OnPositionChanged; } catch { }
                try { c.PanelRequested -= OnPanelRequested; } catch { }
                try { c.OpenDeckRequested -= OnOpenDeckRequested; } catch { }
                try { c.Dispose(); } catch { }
            }

            // 面板挂在卡片上，卡片没了就一起收掉；Close 必须回 UI 线程。
            var p = panel;
            panel = null;
            if (p != null)
            {
                try { p.JumpRequested -= OnPanelJumpRequested; } catch { }
                try { Avalonia.Threading.Dispatcher.UIThread.Post(() => { try { p.Close(); } catch { } }); } catch { }
            }

            lastStatus = "";
            ledger.Log("disarmed -- 屏幕上不留任何东西");
        }

        void WatchdogTick(object state)
        {
            try
            {
                var e = engine;
                if (!armed || e == null) return;
                if (e.Diag && card != null)
                {
                    // 卡片是另一个线程画的，出问题时不能只靠肉眼看屏幕：把它的自述定期写进日志
                    ledger.Log("card: " + card.Diag());
                }
                var age = (DateTime.UtcNow - e.LastStepUtc).TotalSeconds;
                if (age < 30) return;
                if (restarts >= 5)
                {
                    return;   // 已经换过 5 个引擎，再换也是白换，别把机器拖垮
                }
                restarts++;
                ledger.Log("watchdog: no progress for " + (int)age + "s -- the COM thread is stuck; "
                    + "starting a replacement engine (restart #" + restarts + ")");
                var ctx = e.LessonContext;
                try { e.StateChanged -= OnStateChanged; } catch { }
                try { e.Dispose(); } catch { }   // Stop() 里 Join(1500)，卡死的线程最多拖 1.5 秒
                var fresh = new SessionEngine(ledger, settings)
                {
                    LessonContext = ctx,
                    Diag = settings.DiagnosticLog,
                    SuppressCard = suppressCard,
                    SuiteRunning = suiteShowing,
                    SuiteName = suiteName
                };
                if (!armed)
                {
                    // 这一拍还在重建引擎时，另一条线程（下课 / 插件停止 / 自动化）已经把
                    // 待命状态收掉了。此时再把引擎装回去，就是"卡片没了但引擎还在跑"。
                    try { fresh.Dispose(); } catch { }
                    return;
                }
                fresh.StateChanged += OnStateChanged;
                engine = fresh;
                fresh.Start();
            }
            catch (Exception ex)
            {
                try { ledger?.Log("watchdog failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// ClassIsland 换主题时跟着变。主题更新事件在 UI 线程上发，而重算外观要读 Avalonia 资源，
        /// 所以这里必须回到 UI 线程去做。
        /// </summary>
        void WatchThemeChanges()
        {
            try
            {
                var themes = ClassIsland.Shared.IAppHost.TryGetService<ClassIsland.Core.Abstractions.Services.IThemeService>();
                if (themes == null) { ledger.Log("theme watcher: IThemeService 不可用"); return; }
                if (themeService == themes && themeHandler != null) return;   // 重复 Start 不要挂第二个
                themeService = themes;
                themeHandler = (s, e) =>
                {
                    try
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try { RecomputeAppearance(); } catch { }
                        });
                    }
                    catch { }
                };
                themes.ThemeUpdated += themeHandler;
                ledger.Log("theme watcher attached");
            }
            catch (Exception ex)
            {
                ledger.Log("theme watcher failed: " + ex.Message);
            }
        }

        /// <summary>清掉太久没用的课件历史（默认 3 周），启动时一次，之后每天一次。</summary>
        void PruneHistory()
        {
            try
            {
                lastPrune = DateTime.Now;
                int days = settings.HistoryRetentionDays;
                int removed = ledger.Prune(days);
                if (removed > 0) ledger.Log("history: pruned " + removed + " record(s) older than " + days + " day(s)");
            }
            catch (Exception ex)
            {
                try { ledger?.Log("prune failed: " + ex.Message); } catch { }
            }
        }

        // ---- 事件 ---------------------------------------------------------------

        void OnJumpRequested()
        {
            // 点卡片正文/跳页行 → 跳到卡片上写着的那一页。
            // 必须用“老师看到的那个状态”里的页码：工作线程执行跳页要等下一次 Step（几十~几百毫秒），
            // 这段时间里希沃退出授课（课件被关）、另一个套件的旧课件被认出来，键就变了
            // （2026-10-18 日志：14:26:26 卡片上是希沃的 1->8，14:26:32 点跳转跳到了 WPS 第 6 页）。
            var st = lastState;
            int page = st == null ? 0 : st.Previous;   // 跳页行/正文写的都是 Previous（见 Paint）
            RequestJump(page > 0 ? page : 0);
        }

        void OnHistoryJumpRequested(string filePath, int slide)
        {
            // 文件夹面板里的历史行可能是另一份课件的，必须把文件一起交给引擎；
            // 单文件历史面板传的是同一条记录的文件（旧行为不变）。
            RequestJump(filePath, slide);
        }

        /// <summary>
        /// 功能8（2026-10-24）：课前预备态里点了「打开并接着讲」。用系统默认程序打开老师自己的
        /// .pptx 是**外部文件关联**（不是插件宿主导航），所以走 Process.Start + UseShellExecute，
        /// 不经 IUriNavigationService —— 后者是给"在 ClassIsland 里跳页面"用的。
        /// 打开之后不在这里等：引擎在 Bridge 第一次认出这份课件时（见 SessionEngine.
        /// RequestJumpOnOpen / TryJumpPendingOpen）才跳到上次那页。
        /// </summary>
        void OnOpenDeckRequested(string filePath, int slide)
        {
            string path = filePath ?? "";
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!System.IO.File.Exists(path))
                {
                    ledger?.Log("open deck: 文件不在了 " + path);
                    var miss = card;
                    if (miss != null) miss.ShowStatus("课件不在了");
                    return;
                }
                // 连点保护：同一份课件 8 秒内只拉起一次。老师点两下会开两个演示实例，
                // 第二个还会把第一个的放映顶掉。
                var now = DateTime.UtcNow;
                if (string.Equals(path, lastOpenDeckPath, StringComparison.OrdinalIgnoreCase)
                    && (now - lastOpenDeckAt).TotalSeconds < 8) return;
                lastOpenDeckPath = path;
                lastOpenDeckAt = now;

                ledger?.Log("open deck: " + path + "（接着讲第 " + slide + " 页）");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                var e = engine;
                if (e != null) e.RequestJumpOnOpen(path, slide);
                var c = card;
                if (c != null) c.ShowStatus("正在打开课件…");
            }
            catch (Exception ex)
            {
                try { ledger?.Log("open deck failed: " + path + " : " + ex.Message); } catch { }
                var c = card;
                if (c != null) c.ShowStatus("打不开课件");
            }
        }

        /// <summary>
        /// 卡片右上角齿轮（浮层线程）→ 打开控制面板。Window 只能在 Avalonia UI 线程构造，
        /// 所以整段都 Post 回 UI 线程；已经开着就只 Activate，绝不开第二个。
        /// </summary>
        void OnPanelRequested()
        {
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (panel != null)
                        {
                            if (!panel.IsVisible) panel.Show();
                            // Win32 里最小化的窗口只 Activate() 不会还原，老师点齿轮会“什么都没发生”。
                            if (panel.WindowState == Avalonia.Controls.WindowState.Minimized)
                                panel.WindowState = Avalonia.Controls.WindowState.Normal;
                            panel.Activate();
                            return;
                        }
                        // 以前这里 !armed 就直接 return：课后点设置页的「打开」、或卡片刚被 Disarm 时
                        // 点齿轮，表现都是「点了没反应」。面板只读台账，未 Arm 时也允许打开。
                        var p = new ControlPanel(ledger, settings, () => lastState, OpenPanelFolder, this);
                        p.JumpRequested += OnPanelJumpRequested;
                        // 老师点 X 关掉后把单实例引用清掉，下次齿轮才能重新建窗口。
                        p.Closed += (_, __) => { if (ReferenceEquals(panel, p)) panel = null; };
                        try
                        {
                            p.Show();
                        }
                        catch
                        {
                            // Show 抛异常时 Closed 不会触发，引用会永久挂在半成品上，之后点齿轮全部失败。
                            if (ReferenceEquals(panel, p)) panel = null;
                            p.JumpRequested -= OnPanelJumpRequested;
                            try { (p as IDisposable)?.Dispose(); } catch { }
                            throw;
                        }
                        panel = p;
                        p.Activate();
                        ledger?.Log("panel: opened");
                    }
                    catch (Exception ex)
                    {
                        try { ledger?.Log("panel open failed: " + ex); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                try { ledger?.Log("panel dispatch failed: " + ex.Message); } catch { }
            }
        }

        void OnPanelJumpRequested(string filePath, int slide)
        {
            // 面板的“立即跳转”和卡片走同一条链：engine.RequestJumpTo(文件, 页码)。
            RequestJump(filePath, slide);
        }

        /// <summary>
        /// 面板的“打开文件夹”。和 TryOpenSubjectFolder 一样优先交给宿主的 IUriNavigationService，
        /// 拿不到服务或抛异常时回退 ShellExecute —— 老师机器上 explorer 总是有的。
        /// </summary>
        void OpenPanelFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                bool handed = false;
                try
                {
                    var nav = IAppHost.TryGetService<IUriNavigationService>();
                    if (nav != null)
                    {
                        // 见 TryOpenSubjectFolder：实测 URI 不会截断 #/% 路径，直接交宿主。
                        nav.Navigate(new Uri(folder));
                        handed = true;
                    }
                }
                catch (Exception ex)
                {
                    ledger?.Log("panel folder: Navigate 失败（" + ex.GetType().Name + "），改用 ShellExecute");
                }

                if (!handed)
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                }
                ledger?.Log("panel folder: opened " + folder);
            }
            catch (Exception ex)
            {
                try { ledger?.Log("panel folder failed: " + folder + " : " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 跳到当前放映课件的某一页（自动化「跳到上次停的那一页」用；面板选页也走这里）。
        /// 只转发给引擎，引擎自己判断跳不过去时该记什么日志。
        /// </summary>
        public void RequestJump(int slide)
        {
            RequestJump(null, slide);
        }

        void RequestJump(string filePath, int slide)
        {
            try
            {
                var e = engine;
                if (e == null) return;
                // slide > 0 = 历史面板选中的具体页；0 = 沿用原来的“上次停的那一页”。
                if (slide > 0) e.RequestJumpTo(filePath, slide);
                else e.RequestJump();
                // 这里不能报“已跳到第 N 页”：RequestJump 只是把请求丢给工作线程（见 SessionEngine），
                // 此时 engine.Last 还是跳之前的页码，会印出“已跳转到第 1 页”这种假消息。真正的结果由
                // 工作线程 SetStatus 推回来。
                var c = card;
                if (c != null) c.ShowStatus("正在跳转…");
            }
            catch (Exception ex)
            {
                ledger?.Log("jump failed: " + ex.Message);
                var c = card;
                if (c != null) c.ShowStatus("跳转失败：" + ex.Message);
            }
        }

        void OnStateChanged(CardState st)
        {
            try
            {
                var c = card;
                if (c == null) return;
                // 先记下“屏幕上现在是什么”，跳页时 (可能就在几毫秒后) 会用这一份。
                lastState = st;
                if (st.Status != lastStatus)
                {
                    lastStatus = st.Status;
                    c.ShowStatus(st.Status);
                }
                c.Apply(st);
            }
            catch (Exception ex)
            {
                ledger?.Log("state push failed: " + ex.Message);
            }
        }

        public void OnSettingsChanged()
        {
            try
            {
                // The engine keeps the same SyncSettings instance, so its properties are already
                // live; the card needs both its timers re-armed and its appearance recomputed --
                // 老师在设置页里动一下滑块，屏幕上的卡片就该当场变。
                RecomputeAppearance();
                var c = card;
                if (c != null) c.Configure(Appearance, settings.AutoShowOverlay, settings.AutoHide, settings.AutoHideSeconds, settings.EnableControlPanel);
                var e = engine;
                if (e != null) e.Diag = settings.DiagnosticLog;
                // 保留期可能刚被改短：立刻按新值清一次，老师能看到历史真的被清掉了。
                PruneHistory();
            }
            catch { }
        }

        /// <summary>重新算一遍"主题 ⊕ 自定义"。ClassIsland 主题本身变了（或首次启动）也要走这里。</summary>
        public CardAppearance RecomputeAppearance()
        {
            if (Theme == null) Theme = ClassIslandTheme.Load(pluginConfigFolder, null);
            ProbeUiTheme();
            Appearance = CardAppearance.Resolve(settings, Theme);
            ledger?.Log("card look: " + Appearance.Describe());
            card?.SetAppearance(Appearance);
            return Appearance;
        }

        /// <summary>
        /// 强调色只认运行时的 SystemAccentColor。ClassIsland 的主题色来源有三档
        /// （AppColorSource: 自定义 / 系统壁纸 / 系统主题色），现在是"系统主题色"时，
        /// Settings.json 里的 PrimaryColor 是死值，只有 Avalonia 里的这个资源是真的。
        /// 必须有 UI 线程；这个方法只从 AppStarted、设置变更这些 UI 线程调用点进来。
        /// </summary>
        void ProbeUiTheme()
        {
            if (Theme == null) return;
            try
            {
                Avalonia.Media.Color c;
                // 顺序有讲究：本体画"强调色面"（岛上的高亮块、强调按钮）用的是
                // AccentFillColorDefaultBrush，它在浅色主题下比系统强调色本身更深一档
                // （屏幕上实测是 #0067C0，而 SystemAccentColor 是 #0078D4）。
                // 要跟屏幕上的颜色一模一样，就得优先拿这个画刷。
                if (TryBrush("AccentFillColorDefaultBrush", out c))
                {
                    Theme.AccentR = c.R; Theme.AccentG = c.G; Theme.AccentB = c.B;
                    Theme.AccentFromUi = true;
                    Theme.AccentNote = "avalonia:AccentFillColorDefaultBrush";
                }
                else if (TryBrush("SystemAccentColor", out c))
                {
                    Theme.AccentR = c.R; Theme.AccentG = c.G; Theme.AccentB = c.B;
                    Theme.AccentFromUi = true;
                    Theme.AccentNote = "avalonia:SystemAccentColor";
                }
                else if (TryBrush("SystemAccentColorDark1", out c))
                {
                    Theme.AccentR = c.R; Theme.AccentG = c.G; Theme.AccentB = c.B;
                    Theme.AccentFromUi = true;
                    Theme.AccentNote = "avalonia:SystemAccentColorDark1";
                }
                else
                {
                    Theme.AccentFromUi = false;
                    Theme.AccentNote = "settings.json:PrimaryColor";
                }
            }
            catch (Exception ex)
            {
                Theme.AccentFromUi = false;
                Theme.AccentNote = "probe failed: " + ex.GetType().Name;
            }
        }

        /// <summary>从 Avalonia 资源里取一个颜色（画刷或裸 Color 都认）。必须在 UI 线程调用。</summary>
        static bool TryBrush(string key, out Avalonia.Media.Color color)
        {
            color = default;
            try
            {
                if (AppBase.Current == null) return false;
                object value = null;
                if (!AppBase.Current.TryFindResource(key, out value) || value == null) return false;
                if (value is Avalonia.Media.SolidColorBrush brush)
                {
                    color = brush.Color;
                    return true;
                }
                if (value is Avalonia.Media.Color direct)
                {
                    color = direct;
                    return true;
                }
            }
            catch
            {
                // 取不到就算没取到，回落到 Settings.json
            }
            return false;
        }

        /// <summary>Show the card on demand (settings page / debug). 没待命时临时开一次。</summary>
        public void ShowCardNow()
        {
            try
            {
                if (!armed) Arm("设置页预览");
                var c = card;
                if (c == null) return;
                c.Expand();
            }
            catch (Exception ex)
            {
                try { ledger?.Log("preview failed: " + ex.Message); } catch { }
            }
        }

        public void Stop()
        {
            started = false;
            try { scheduler?.Dispose(); } catch { }
            scheduler = null;

            // 主题订阅必须退掉：宿主不回收插件实例，不退订就有第二个 Start 挂两份回调、
            // 停用后换主题还会往已停止的插件里跑。先摘句柄再 -=，避免重入时双重退订。
            var th = themeService;
            var h = themeHandler;
            themeService = null;
            themeHandler = null;
            if (th != null && h != null)
            {
                try { th.ThemeUpdated -= h; } catch (Exception ex) { try { ledger?.Log("theme unwatch failed: " + ex.Message); } catch { } }
            }

            // 停掉之后不再往已停的账本里写（LogSink 语义上可空，只是声明成非空类型，用 null! 明确表达“摘掉”）
            try { settings.LogSink = null!; } catch { }

            Disarm("插件停止 / ClassIsland 退出");
        }

        public void Dispose() { Stop(); }

        // ───────────────────────── 自动化接口（ClassIsland 行动调用） ─────────────────────────
        // 行动是插件之外的调用方。让它们直接摸 card / engine 字段，等于把窗口和引擎的生命周期
        // 复制到第二个地方；这里只做两件事：把调用切回 UI 线程，然后走既有的 Arm/Disarm。
        // 待命/收起的唯一真相仍然是 armed，不复制任何引擎逻辑（双引擎、双卡片是这类插件
        // 最典型的翻车方式）。

        /// <summary>
        /// 自动化「显示悬浮卡片」：没待命就临时 Arm 一次；已待命（可能只是被收成小条）就展开。
        /// </summary>
        public void ShowCardForAutomation(string reason)
        {
            AutomationOnUi(() =>
            {
                try
                {
                    if (!armed) Arm(AutomationReason(reason, "自动化：显示悬浮卡片"));
                    var c = card;
                    if (c != null) c.Expand();
                }
                catch (Exception ex)
                {
                    try { ledger?.Log("automation show failed: " + ex.Message); } catch { }
                }
            });
        }

        /// <summary>
        /// 卡片此刻是否真的显示着。自动化「切换悬浮卡片」必须靠它判断该显示还是该隐藏——
        /// 用 armed 代在这里是错的：卡片被 15 秒自动收起或老师手动收回后 armed 仍为真。
        /// </summary>
        public bool CardVisible
        {
            get
            {
                var c = card;
                return armed && c != null && c.IsCreated && !c.IsHidden;
            }
        }

        /// <summary>自动化「隐藏悬浮卡片」：只收卡片，不拆引擎（拆了这节课就不记台账了）。</summary>
        public void HideCardForAutomation(string reason)
        {
            AutomationOnUi(() =>
            {
                try
                {
                    if (!armed) return;
                    var c = card;
                    if (c == null || !c.IsCreated) return;
                    c.HideForAutomation();
                    try { ledger?.Log("automation hide card: " + AutomationReason(reason, "自动化：隐藏悬浮卡片")); } catch { }
                }
                catch (Exception ex)
                {
                    try { ledger?.Log("automation hide failed: " + ex.Message); } catch { }
                }
            });
        }

        /// <summary>
        /// 自动化「打开控制面板」：复用托盘图标那条 OnPanelRequested 链路，不复制面板创建逻辑。
        /// 没 Arm 时面板没有可展示的台账（OnPanelRequested 自己也会 return），这里主动记一行，
        /// 让自动化日志能看出是「跳过」而不是「失败」。
        /// </summary>
        public void OpenPanelForAutomation(string reason)
        {
            string why = AutomationReason(reason, "自动化：打开控制面板");
            try
            {
                // 2026-10-25 用户报「控制面板打不开」：面板只读台账，课后点设置页的按钮也该能开，
                // 所以不再把 armed 当门槛，只在日志里记状态。
                ledger?.Log("automation panel: " + why + (armed ? "（上课中）" : "（未上课，只读台账）"));
                OnPanelRequested();
            }
            catch (Exception ex)
            {
                try { ledger?.Log("automation panel failed: " + ex.Message); } catch { }
            }
        }

        static string AutomationReason(string reason, string fallback)
        {
            return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
        }

        /// <summary>
        /// 把行动线程切到 UI 线程：Avalonia 的 Window 只能在 UI 线程构造（见 Arm 里的注释），
        /// 而自动化行动不保证在 UI 线程执行。拿不到 Dispatcher 时安静放弃——行动失败不该
        /// 让 ClassIsland 弹异常。
        /// </summary>
        void AutomationOnUi(Action action)
        {
            try
            {
                var ui = Avalonia.Threading.Dispatcher.UIThread;
                if (ui.CheckAccess()) action();
                else ui.Post(action);
            }
            catch (Exception ex)
            {
                try { ledger?.Log("automation dispatch failed: " + ex.Message); } catch { }
            }
        }
    }
}
