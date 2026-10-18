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
        Func<string[]> lessonContext;
        volatile bool started;    // 调度器在跑
        volatile bool armed;      // 引擎与卡片存在
        System.Threading.Timer scheduler;
        System.Threading.Timer watchdog;
        int restarts;
        string lastStatus = "";
        string lastClockNote = "";
        // 上一次替它开过课件文件夹的那节课（用户 2026-10-16 第 1 条）：同一节课只开一次，
        // 下课清零，下一节课再开。值就是课表读数（"第3节 09:40-10:20"）。
        string lastLessonKey = "";
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
        public Func<string[]> LessonContext
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

        void ScheduleTick(object state)
        {
            try
            {
                if (!started) return;

                bool want = false;
                string reason = "";
                bool timetableGoverns = false;
                bool inClass = false;
                string lessonKey = "";

                if (settings.AutoStartBeforeClass)
                {
                    var v = LessonClock.Check(settings.PreClassLeadSeconds, settings.PostClassGraceSeconds);
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

                // 用户 2026-10-15 的要求："插件在后台一直常驻，只看 WPS 演示 / PowerPoint 的进程 ——
                // 开了就把卡片从隐藏改成打开，关了就继续隐藏。" 所以这里不再要求"必须先打开课件"。
                // 进程那一侧已经排掉了 WPS 文字/表格/云服务/传输助手（见 PptBridge.SuiteIsRunning）。
                // 但**不能只看进程名**：这台机器上 WPS 12.1 把演示窗口挂在 wps.exe 上（wpp.exe 只是
                // 后台引擎），只认进程名时老师开着 PPT 插件也认为"演示软件不在" —— 所以进程在、
                // 或者桌面上有演示窗口（窗口类名 PPTFrameClass / PP12FrameClass / screenClass），都算。
                bool seewoProcess = SeewoBridge.LooksRunning();
                bool pptProcess = PptBridge.SuiteActive();
                bool suiteProcess = pptProcess || seewoProcess;
                bool suiteOnScreen = PptBridge.SuiteOnScreen();
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
                suppressCard = timetableGoverns && !inClass && !settings.ShowCardOutsideClass;

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
            armed = true;
            ArmReason = reason;
            ledger.Log("arm: " + reason);

            var c = new NativeOverlayCard();
            c.Configure(Appearance, settings.AutoShowOverlay, settings.AutoHide, settings.AutoHideSeconds);
            c.JumpRequested += OnJumpRequested;
            c.HistoryJumpRequested += OnHistoryJumpRequested;   // 历史面板里点某一条 → 跳到那份文件的这一页
            c.PositionChanged += OnPositionChanged;   // 用户第 3 条：拖完记住位置
            // 卡片的显示/隐藏变化写进 app.log：老师报"卡片该出来却没出来"时，日志要能直接看出来。
            c.Log = m => { try { ledger.Log("card: " + m); } catch { } };
            c.Start();
            card = c;

            // The card builds its window on its own thread, so a creation failure is invisible here
            // unless it is written down. Waiting for the thread (off the host's UI thread) is the
            // only way to tell "the teacher sees a card" from "the thread died quietly".
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1500);
                try { ledger.Log("overlay: " + c.Diag()); } catch { }
            });

            var e = new SessionEngine(ledger, settings)
            {
                LessonContext = lessonContext,
                Diag = settings.DiagnosticLog,
                SuppressCard = suppressCard,
                SuiteRunning = suiteShowing,
                SuiteName = suiteName
            };
            e.StateChanged += OnStateChanged;
            e.Start();
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
                try { c.Dispose(); } catch { }
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
                themes.ThemeUpdated += (s, e) =>
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

        void RequestJump(int slide)
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
                if (c != null) c.Configure(Appearance, settings.AutoShowOverlay, settings.AutoHide, settings.AutoHideSeconds);
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
            Disarm("插件停止 / ClassIsland 退出");
        }

        public void Dispose() { Stop(); }
    }
}
