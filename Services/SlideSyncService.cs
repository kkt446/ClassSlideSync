using System;
using Avalonia.Controls;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Ui;

namespace ClassSlideSync.Plugin.Services
{
    public sealed class SlideSyncService : IDisposable
    {
        readonly SyncSettings settings;
        readonly string pluginConfigFolder;
        Ledger ledger;
        SessionEngine engine;
        NativeOverlayCard card;
        Func<string[]> lessonContext;
        volatile bool started;
        volatile bool armed;
        System.Threading.Timer scheduler;
        System.Threading.Timer watchdog;
        int restarts;
        string lastStatus = "";
        string lastClockNote = "";
        string lastLessonKey = "";
        bool clockLogged;
        DateTime lastPrune = DateTime.MinValue;
        volatile bool suppressCard;

        volatile bool suiteShowing;
        volatile string suiteName = "";
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

        public bool Armed { get { return armed; } }

        public string ArmReason { get; private set; }

        volatile CardState lastState;

        public ClassIslandTheme Theme { get; private set; }

        public CardAppearance Appearance { get; private set; }

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

        public void Start()
        {
            if (started) return;
            started = true;

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
                    if (!clockLogged)
                    {
                        clockLogged = true;
                        ledger.Log("clock: " + (v.Known ? "课表 " + v.All : "没有课表"));
                    }
                    inClass = v.InPeriod;
                    lessonKey = v.InPeriod ? v.Detail : "";
                    timetableGoverns = v.Known && v.Periods > 0;
                    if (v.InLesson) { want = true; reason = "上课时段 " + v.Detail; }
                    string note = v.Known ? (v.InLesson ? "在上课时段 " + v.Detail : RoundCountdown(v.Detail)) : "没有课表";
                    if (note != lastClockNote)
                    {
                        lastClockNote = note;
                        ledger.Log("clock: " + note);
                    }
                }

                if (inClass && lessonKey.Length > 0 && lessonKey != lastLessonKey)
                {
                    lastLessonKey = lessonKey;
                    TryOpenSubjectFolder();
                }

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

                suppressCard = timetableGoverns && !inClass && !settings.ShowCardOutsideClass;

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

        string CurrentSubjectName()
        {
            try
            {
                var lessons = IAppHost.TryGetService<ILessonsService>();
                var subject = lessons == null ? null : lessons.CurrentSubject;
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
            c.HistoryJumpRequested += OnHistoryJumpRequested;
            c.PositionChanged += OnPositionChanged;
            c.Log = m => { try { ledger.Log("card: " + m); } catch { } };
            c.Start();
            card = c;

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
                    ledger.Log("card: " + card.Diag());
                }
                var age = (DateTime.UtcNow - e.LastStepUtc).TotalSeconds;
                if (age < 30) return;
                if (restarts >= 5)
                {
                    return;
                }
                restarts++;
                ledger.Log("watchdog: no progress for " + (int)age + "s -- the COM thread is stuck; "
                    + "starting a replacement engine (restart #" + restarts + ")");
                var ctx = e.LessonContext;
                try { e.StateChanged -= OnStateChanged; } catch { }
                try { e.Dispose(); } catch { }
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

        void OnJumpRequested()
        {
            var st = lastState;
            int page = st == null ? 0 : st.Previous;
            RequestJump(page > 0 ? page : 0);
        }

        void OnHistoryJumpRequested(string filePath, int slide)
        {
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
                if (slide > 0) e.RequestJumpTo(filePath, slide);
                else e.RequestJump();
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
                RecomputeAppearance();
                var c = card;
                if (c != null) c.Configure(Appearance, settings.AutoShowOverlay, settings.AutoHide, settings.AutoHideSeconds);
                var e = engine;
                if (e != null) e.Diag = settings.DiagnosticLog;
                PruneHistory();
            }
            catch { }
        }

        public CardAppearance RecomputeAppearance()
        {
            if (Theme == null) Theme = ClassIslandTheme.Load(pluginConfigFolder, null);
            ProbeUiTheme();
            Appearance = CardAppearance.Resolve(settings, Theme);
            ledger?.Log("card look: " + Appearance.Describe());
            card?.SetAppearance(Appearance);
            return Appearance;
        }

        void ProbeUiTheme()
        {
            if (Theme == null) return;
            try
            {
                Avalonia.Media.Color c;
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
            }
            return false;
        }

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
