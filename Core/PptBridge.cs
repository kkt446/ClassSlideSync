using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>One observation of the presentation application.</summary>
    public sealed class PptSnapshot
    {
        public bool AppFound;          // a COM instance (Office or WPS) answered
        public string AppKind = "";    // "PowerPoint" / "WPS" / ""
        public string ProgId = "";     // the moniker that answered
        public bool Showing;           // a full-screen slideshow is on screen right now
        public string FilePath = "";   // the file being shown ("" when unknown)
        public int Current;            // current slide, 1-based (0 when unknown)
        public int SlideCount;         // 0 when unknown
        public string Diag = "";       // what worked / what failed

        public override string ToString()
        {
            return "app=" + (AppFound ? AppKind : "none") + " showing=" + Showing
                + " file=" + FilePath + " slide=" + Current + "/" + SlideCount;
        }
    }

    /// <summary>
    /// Talks to a running PowerPoint / WPS presentation over COM, by late binding only.
    /// Late binding matters: the plugin must not require Office PIAs, and WPS does not ship
    /// equivalents, so every call goes through IDispatch by name.
    ///
    /// The instance is located through the Running Object Table. Marshal.GetActiveObject
    /// would be the one-liner, but it was removed in .NET Core / .NET 5+, so the ROT walk is
    /// the supported route -- with two fallbacks for machines where the ROT walk comes back
    /// empty even though the suite is plainly running (observed on the development machine).
    /// </summary>
    public sealed class PptBridge : IDisposable
    {
        [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
        [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable pprot);
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object ppunk);
        [DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

        // 放映窗口是普通窗口，不像我们的卡片那样带 TOPMOST：实测点完跳转后它老老实实全屏铺在
        // (0,0) 2560x1600，前台却还是浏览器 —— 老师会以为"点了没反应"。
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        // 判断"演示软件是不是真的在桌面上"用的（见 SuiteOnScreen）。
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [ComImport, Guid("00000010-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IRunningObjectTable
        {
            void Register(int grfFlags, [MarshalAs(UnmanagedType.Interface)] object punkObject, [MarshalAs(UnmanagedType.Interface)] object pmkObjectName);
            void Revoke(int dwRegister);
            void NoteChangeTime(int dwRegister, ref System.Runtime.InteropServices.ComTypes.FILETIME pfiletime);
            void GetTimeOfLastChange([MarshalAs(UnmanagedType.Interface)] object pmkObjectName, out System.Runtime.InteropServices.ComTypes.FILETIME pfiletime);
            void IsRunning([MarshalAs(UnmanagedType.Interface)] object pmkObjectName);
            void GetObject([MarshalAs(UnmanagedType.Interface)] object pmkObjectName, [MarshalAs(UnmanagedType.Interface)] out object ppunkObject);
            void EnumRunning([MarshalAs(UnmanagedType.Interface)] out IEnumMoniker ppenumMoniker);
        }

        [ComImport, Guid("00000102-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEnumMoniker
        {
            [PreserveSig] int Next(int celt, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)][Out] IMoniker[] rgelt, IntPtr pceltFetched);
            void Skip(int celt);
            void Reset();
            void Clone(out IEnumMoniker ppenum);
        }

        [ComImport, Guid("0000000f-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMoniker
        {
            void GetClassID(out Guid pClassID);
            void GetSizeMax(out long pcbSize);
            void BindToObject(object pbc, object pmkToLeft, ref Guid riidResult, [MarshalAs(UnmanagedType.Interface)] out object ppvResult);
            void BindToStorage(object pbc, object pmkToLeft, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObj);
            void Reduce(object pmkToLeft, object pmkToRight, object pbc, int dwReduceHowFar, ref object ppmkToLeft, out IMoniker ppmkReduced);
            void ComposeWith(IMoniker pmkRight, bool fOnlyIfNotGeneric, out IMoniker ppmkComposite);
            void Enum(bool fForward, out IEnumMoniker ppenumMoniker);
            void IsEqual(IMoniker pmkOtherMoniker);
            void Hash(out int pdwHash);
            void IsRunning(object pbc, object pmkToLeft, object pmkNewlyRunning);
            void GetTimeOfLastChange(object pbc, object pmkToLeft, out System.Runtime.InteropServices.ComTypes.FILETIME pFileTime);
            void Inverse(out IMoniker ppmk);
            void CommonPrefixWith(IMoniker pmkOther, out IMoniker ppmkPrefix);
            void RelativePathTo(IMoniker pmkOther, out IMoniker ppmkRelPath);
            void GetDisplayName(object pbc, object pmkToLeft, [MarshalAs(UnmanagedType.LPWStr)] out string ppszDisplayName);
            void ParseDisplayName(object pbc, object pmkToLeft, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out int pchEaten, out IMoniker ppmkOut);
            void IsSystemMoniker(out int pdwMksys);
        }

        [ComImport, Guid("0000000e-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IBindCtx
        {
        }

        // Both Office and WPS expose the presentation application under one of these.
        static readonly string[] MonikerProgIds = { "PowerPoint.Application", "Kwpp.Application", "WPP.Application" };
        static readonly string[] LiveProgIds = { "PowerPoint.Application", "Kwpp.Application", "WPP.Application", "wpp.Application" };

        /// <summary>
        /// 只认这两个进程是"演示软件"（见 SuiteIsRunning 的注释：WPS 的常驻进程一律不认，
        /// 否则卡片永远关不掉）。
        /// </summary>
        static readonly string[] SuiteProcessNames = { "POWERPNT", "wpp" };

        /// <summary>
        /// 演示窗口的窗口类名（2026-10-15 实测：PowerPoint 365 是 `PPTFrameClass`，放映窗口是
        /// `screenClass`；WPS 演示 12.1 是 `PP12FrameClass`）。见 SuiteOnScreen。
        /// </summary>
        static readonly string[] SuiteWindowClasses = { "PPTFrameClass", "PP12FrameClass", "screenClass" };

        /// <summary>
        /// 2026-10-17 实测（用户报「关掉 PPT 再打开，插件要好几十秒才响应」时顺手枚举了本机窗口）：
        /// 现在这版 WPS 的文档/放映窗口是 Qt 做的，类名 `Qt5QWindowIcon`，而且**主进程是 wps.exe**
        /// （枚举结果：wps.exe / wpscloudsvr.exe，一个 wpp.exe 都没有）。这个类名太通用，所以只在
        /// "窗口属于演示软件进程"时才认（见 IsSuiteFrame），免得别的 Qt 程序把卡片勾出来。
        /// </summary>
        static readonly string[] SuiteQtFrameClasses = { "Qt5QWindowIcon" };

        /// <summary>
        /// 这些进程的窗口才可能是演示窗口。wps.exe 只出现在这里、**不**进 SuiteProcessNames：
        /// 它是开机常驻的（WPS 文字/表格也用），进了进程判据卡片就永远关不掉。
        /// </summary>
        static readonly string[] SuiteWindowOwnerNames = { "POWERPNT", "wpp", "wps" };

        public object Com { get; private set; }
        public string ProgId { get; private set; }
        readonly string label;

        // 由自动化打开的演示文稿必须一直留着引用，否则 Office 会在垃圾回收时把它关掉。
        object keepAlive;
        // 同理：Run() 返回的放映窗口引用一被释放，放映就可能自己结束（现象是“刚开映两三秒就没了”）。
        object keepAliveShow;

        PptBridge(object com, string progId, string label)
        {
            Com = com; ProgId = progId; this.label = label;
        }

        /// <summary>
        /// 测试用注入点：测试进程自己创建了 PowerPoint 并持有 COM 引用时，可以不经过 ROT 直接使用它。
        /// 生产环境永远为 null —— 插件必须去附着老师正在用的那个实例。
        /// </summary>
        public static PptBridge TestOverride;

        /// <summary>测试用：把已有的 COM 对象包成 PptBridge。</summary>
        public static PptBridge Wrap(object com, string progId, string label)
        {
            return new PptBridge(com, progId, label) { AttachMode = "test" };
        }

        public string Label { get { return label; } }

        /// <summary>最近一次拿到放映视图用的路线（诊断用，会写进日志）。</summary>
        public string LastViewRoute { get; private set; } = "";

        /// <summary>
        /// How the connection was made. "rot" = found through the Running Object Table;
        /// "live" = COM handed us a running instance; "created" = COM started/bound the
        /// server. Worth keeping apart, because on some machines the ROT walk returns
        /// nothing at all while COM itself works perfectly.
        /// </summary>
        public string AttachMode { get; private set; }

        public static PptBridge Connect()
        {
            if (TestOverride != null) return TestOverride;
            // 1) Preferred: walk the ROT and, if that fails, ask GetActiveObject for every ProgID.
            //    It finds Office and WPS without being told which is installed, and it never starts
            //    a second copy of the suite. Crucially it picks the instance that actually has a
            //    deck open (see ScoreInstance): on 2026-10-15 the teacher double-clicked a file,
            //    WPS opened a *new* instance, and "the first instance that answered" was an empty
            //    shell -- so the card never appeared and the teacher saw nothing at all.
            object com;
            string moniker;
            string progId;
            bool fromRot;
            if (TryFindBestInstance(out com, out moniker, out progId, out fromRot))
            {
                string label = fromRot ? ClassifyLabel(moniker) : ClassifyLabel(progId) + " (GetActiveObject)";
                return new PptBridge(com, fromRot ? moniker : progId, label) { AttachMode = fromRot ? "rot" : "live" };
            }

            // 2) Nothing. Deliberately *not* Activator.CreateInstance: that call succeeds by
            //    launching a headless POWERPNT.EXE that the teacher never asked for. It also lies
            //    about reality -- it reports "a presentation suite is here" on a machine where
            //    nobody is presenting. Both Office and WPS are single-instance servers, so if the
            //    teacher really is presenting, step 1 or 2 already found them.
            return null;
        }

        /// <summary>
        /// 桌面上有没有"演示"软件在跑。故意不碰 COM：课表之外的时段，插件要靠这个便宜的检查
        /// 判断"老师是不是自己打开了演示软件"，两秒问一次也不会有负担。
        ///
        /// 名字表**故意**只有两个（用户 2026-10-15 要求把 WPS 那些常驻的家伙排除掉）：
        ///   认：POWERPNT.EXE（Office 365 的 PowerPoint）、wpp.exe（WPS 演示）
        ///   不认：wps.exe（WPS 文字）、et.exe（WPS 表格）、wpspdf.exe、wpsoffice.exe / ksolaunch.exe
        ///         （WPS 首页）、wpscloudsvr.exe（云服务）、transerr.exe（传输助手）、wpscenter.exe ——
        ///         这些是开机就常驻的，认了它们卡片就永远关不掉，老师会以为插件坏了。
        /// </summary>
        public static bool SuiteIsRunning()
        {
            for (int i = 0; i < SuiteProcessNames.Length; i++)
                if (ProcessNamed(SuiteProcessNames[i])) return true;
            return false;
        }

        /// <summary>正在跑的演示软件叫什么（都跑着就优先 WPS 演示，老师这台机器实际用的是它），给卡片当标题用。</summary>
        public static string SuiteName()
        {
            if (ProcessNamed("wpp")) return "WPS 演示";
            if (ProcessNamed("POWERPNT")) return "PowerPoint";
            return "";
        }

        /// <summary>
        /// 老师现在是不是开着演示软件：**进程在**（POWERPNT.EXE / wpp.exe）**或者桌面上有演示窗口**。
        /// 后半个条件不能少：这台机器上 WPS 12.1 把"演示文稿1 - WPS Office"那个窗口挂在 wps.exe 上，
        /// 只按进程名判断时，老师明明开着 PPT，插件却认为演示软件不在（用户 2026-10-15 报的
        /// 「我开 ppt，悬浮窗一次都没打开」就是这条）。窗口类名判据见 SuiteOnScreen。
        /// </summary>
        public static bool SuiteActive()
        {
            return SuiteIsRunning() || SuiteOnScreen();
        }

        /// <summary>
        /// 桌面上有没有**演示窗口**（PowerPoint / WPS 演示）。这里绝对不能只按进程名找 ——
        /// 2026-10-15 在这台机器上实测（WPS 12.1.0.28505）：
        ///   * 真正那个"演示文稿1 - WPS Office"窗口（类名 `PP12FrameClass`）挂在 **wps.exe** 上；
        ///   * `wpp.exe` 只是后台引擎/OLE 服务，它的几十个顶层窗口一个都不是可见的。
        /// 所以判据是**窗口类名**（PowerPoint 用 `PPTFrameClass`，WPS 演示用 `PP12FrameClass`，
        /// 放映窗口是 `screenClass`），再拿"演示软件自己的进程 + 可见窗口"兜底。
        /// 这样 WPS 那堆常驻的家伙（没有窗口的 wps.exe / 云服务 / 传输助手）不会让卡片永远露着。
        /// </summary>
        public static bool SuiteOnScreen()
        {
            bool found = false;
            EnumWindowsProc cb = delegate (IntPtr h, IntPtr lparam)
            {
                if (!IsWindowVisible(h)) return true;
                if (IsSuiteFrame(h)) { found = true; return false; }
                return true;
            };
            try { EnumWindows(cb, IntPtr.Zero); } catch { }
            GC.KeepAlive(cb);
            return found;
        }

        /// <summary>这个可见窗口是不是演示软件的窗口（先看类名，再按"演示软件自己的进程"兜底）。</summary>
        static bool IsSuiteFrame(IntPtr h)
        {
            var sb = new StringBuilder(64);
            try { GetClassNameW(h, sb, sb.Capacity); } catch { return false; }
            string cls = sb.ToString();
            for (int i = 0; i < SuiteWindowClasses.Length; i++)
                if (string.Equals(cls, SuiteWindowClasses[i], StringComparison.Ordinal)) return true;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try
            {
                // 2026-10-16：这个 Process 以前不 Dispose —— 调度器每 2 秒问一次"桌面上有没有演示窗口"，
                // 一上午就能漏掉几千个句柄。用户反馈用久了 WPS 关不干净，顺手一起收掉。
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    string n = p.ProcessName;
                    if (n.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase)
                        || n.Equals("wpp", StringComparison.OrdinalIgnoreCase)) return true;
                    // 2026-10-17：wps.exe 是新版 WPS 的真正窗口宿主（WPS 文字/表格也在里面）。
                    // 只有类名是我们认得的 WPS Qt 主框架时才算它是"演示窗口"——判错的代价只是卡片
                    // 多显示一句"正在识别课件…"，判漏的代价是老师开着课件插件却毫不知情。
                    if (n.Equals("wps", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < SuiteQtFrameClasses.Length; i++)
                            if (string.Equals(cls, SuiteQtFrameClasses[i], StringComparison.Ordinal)) return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 桌面上可见的演示窗口各自“打开的文档名”（从窗口标题里取，不需要 COM）。
        ///
        /// 2026-10-17 用户实测：WPS 打开一个文件的头十几秒里，它的对象模型**接受调用但不回话**，
        /// 于是“这到底是什么课件”全压在 COM 上，卡片只能干等，用户看到的就是
        /// 「一直在识别课件，过十秒才识别出来」。窗口标题是免费的（GetWindowTextW，微秒级），
        /// 拿到文档名就能去台账里翻出“上次讲到第几页”，卡片立刻有内容；COM 回话后再补实时页码。
        /// </summary>
        public static string[] SuiteFrameDocuments()
        {
            var list = new List<string>();
            EnumWindowsProc cb = delegate (IntPtr h, IntPtr lparam)
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    if (!IsSuiteFrame(h)) return true;
                    var sb = new StringBuilder(512);
                    GetWindowTextW(h, sb, sb.Capacity);
                    string doc = DocumentFromTitle(sb.ToString());
                    if (doc.Length > 0 && !list.Contains(doc)) list.Add(doc);
                }
                catch { }
                return true;
            };
            try { EnumWindows(cb, IntPtr.Zero); } catch { }
            GC.KeepAlive(cb);
            return list.ToArray();
        }

        /// <summary>从窗口标题里抠出文档名：“测试课件.pptx - WPS Office” → “测试课件.pptx”。</summary>
        static string DocumentFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            int i = title.IndexOf(" - ", StringComparison.Ordinal);
            string head = (i > 0 ? title.Substring(0, i) : title).Trim();
            string[] exts = { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".dps", ".dpt" };
            for (int k = 0; k < exts.Length; k++)
                if (head.EndsWith(exts[k], StringComparison.OrdinalIgnoreCase)) return head;
            return "";
        }

        /// <summary>只看进程名（大小写不敏感），给上面几个判断用。</summary>
        static bool ProcessNamed(string name)
        {
            try
            {
                var ps = System.Diagnostics.Process.GetProcessesByName(name);
                if (ps == null || ps.Length == 0) return false;
                // 同上：这批 Process 句柄要还回去（老的写法只看了长度就丢掉，句柄一直挂着）。
                for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 老师主动点了卡片上的“从第 N 页开始放映”：这时候可以真的把 PowerPoint 拉起来。
        /// 与 <see cref="Connect"/> 的克制是两回事——Connect 是后台轮询，不能凭空启动一个老师
        /// 根本没要的程序；这里是用户自己按下去的按钮，拉不起来才是 bug。
        /// </summary>
        public static PptBridge Launch(string filePath)
        {
            if (TestOverride != null) return TestOverride;

            // 也许只是刚启动、还没来得及进 ROT，先按常规路子找一遍。
            var existing = Connect();
            if (existing != null) return existing;

            foreach (var pid in LiveProgIds)
            {
                try
                {
                    var t = Type.GetTypeFromProgID(pid, false);
                    if (t == null) continue;
                    var com = Activator.CreateInstance(t);
                    if (com == null) continue;
                    var b = new PptBridge(com, pid, ClassifyLabel(pid) + " (启动)") { AttachMode = "launch" };
                    // 起在后台也能放映，但老师看不见窗口会以为程序没打开。
                    b.TrySet(com, "Visible", true);
                    // 刚拉起来的 PowerPoint 头十几秒是“半成品”：COM 对象已经在了，但对象模型还没
                    // 准备好，这时候 Open/Run 都会被它糊弄过去（参见 2026-10-13 13:24 那次：窗口
                    // 开了、课件也开了，就是没进放映）。
                    // 判据必须是“Presentations 取得到、而且 Count 读得出来”。写成 `Count >= 0`
                    // 是个恒真条件：对象模型没就绪时 P(...) 返回 null、AsInt(null) 返回 0，
                    // 于是“等就绪”变成“根本不等”，转头 Open 就报“拿不到 Presentations 集合”
                    // （2026-10-13 22:00 实测 0 ms 就失败）。
                    var ready = WaitFor(() =>
                    {
                        try
                        {
                            var presos = P(com, "Presentations");
                            if (presos == null) return false;
                            return P(presos, "Count") != null;
                        }
                        catch { return false; }
                    }, 60000, 500);
                    if (!ready) continue;

                    // 顺手把课件打开：老师点的是“从第 N 页开始放映”，早一点打开就少一次往返。
                    // Office 刚起来时会拒绝调用（0x80010001 之类），所以重试几轮。
                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            var oerr = b.OpenDeck(filePath);
                            if (oerr == null) break;
                            System.Threading.Thread.Sleep(1500);
                        }
                    }
                    return b;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 连不上时的定位工具：ROT 里到底有什么、每个 ProgID 的 GetActiveObject 返回什么 HRESULT。
        /// 单独放在这里（而不是测试工程里），因为 P/Invoke 声明必须和真正连接时用的那一份完全一致。
        /// </summary>
        /// <summary>
        /// 等一个条件成立，最多等 timeoutMs 毫秒。COM 刚启动时很多对象是“半成品”，
        /// 只靠 TryCall 是否返回 null 判断不了“好了没有”。
        /// </summary>
        internal static bool WaitFor(Func<bool> ready, int timeoutMs, int stepMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { if (ready()) return true; } catch { }
                System.Threading.Thread.Sleep(stepMs);
            }
            try { return ready(); } catch { return false; }
        }

        public static string ConnectDiag()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ROT 行走:");
            try { sb.Append(DumpRot()); }
            catch (Exception ex) { sb.AppendLine("  DumpRot 抛异常: " + ex.GetType().Name + " " + ex.Message); }

            sb.AppendLine("GetActiveObject:");
            foreach (var pid in LiveProgIds)
            {
                try
                {
                    Guid clsid;
                    int hr = CLSIDFromProgID(pid, out clsid);
                    if (hr != 0) { sb.AppendLine("  " + pid + " -> CLSIDFromProgID 失败 0x" + hr.ToString("X8")); continue; }
                    object o;
                    GetActiveObject(ref clsid, IntPtr.Zero, out o);
                    sb.AppendLine("  " + pid + " -> 成功: " + (o == null ? "null" : o.GetType().FullName));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  " + pid + " -> 失败: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Pick the deck to watch. Prefers whatever is on screen right now, so the plugin keeps
        /// working when a teacher has two decks open or opens one before the other.
        /// </summary>
        public object FindActivePresentation()
        {
            object best = null;
            try
            {
                var pres = P(Com, "Presentations");
                int n = AsInt(P(pres, "Count")) ?? 0;
                for (int i = 1; i <= n; i++)
                {
                    var p = PIndex(pres, "Item", i);
                    if (p == null) continue;
                    if (P(p, "SlideShowWindow") != null) return p;   // this one is on screen
                    if (best == null) best = p;
                }
            }
            catch { }
            if (best != null) return best;
            try { return P(Com, "ActivePresentation"); } catch { return null; }
        }

        static string ClassifyLabel(string monikerOrProgId)
        {
            if (monikerOrProgId == null) return "";
            if (monikerOrProgId.IndexOf("Kwpp", StringComparison.OrdinalIgnoreCase) >= 0) return "WPS";
            if (monikerOrProgId.IndexOf("WPP", StringComparison.OrdinalIgnoreCase) >= 0) return "WPS";
            if (monikerOrProgId.IndexOf("wpp", StringComparison.Ordinal) >= 0) return "WPS";
            if (monikerOrProgId.IndexOf("PowerPoint", StringComparison.OrdinalIgnoreCase) >= 0) return "PowerPoint";
            return "unknown";
        }

        /// <summary>
        /// 这个 COM 实例"值不值得用"：
        ///   3 = 打开着**有文件**的课件（FullName 有值）—— 最好的；
        ///   2 = 有课件但都没存过（WPS 一启动自带的"演示文稿1"就是这样）—— 次好；
        ///   1 = 活着，但一个课件都没打开；
        ///   0 = 连 Application.Name 都问不出来（多半是已经死掉的服务器）。
        /// 2026-10-15 实测的坑：原来只拿"第一个答话的实例"，而老师双击文件打开课件的是**新**实例，
        /// 先答话的却可能是个空壳（或者只是张空白新稿）。插件于是对着它问一辈子，老师看到的就是
        /// "打开 PPT 了，屏幕上什么都没有"。
        /// </summary>
        static int ScoreInstance(object com)
        {
            if (com == null) return 0;
            bool alive = P(com, "Name") != null || P(com, "Presentations") != null;
            if (!alive) return 0;

            var presos = P(com, "Presentations");
            int n = presos == null ? 0 : (AsInt(P(presos, "Count")) ?? 0);
            if (n > 0) return HasFileBackedDeck(com, presos, n) ? 3 : 2;

            // 没有 Presentations 但正在放映（个别版本/时机下会这样），也算好实例。
            var wins = P(com, "SlideShowWindows");
            if (wins != null && (AsInt(P(wins, "Count")) ?? 0) > 0) return 3;

            return 1;
        }

        /// <summary>
        /// 这些文稿里有没有"已经存成文件"的那一份。两个坑都要绕：
        ///   1) FullName 非空 ≠ 有文件 —— 实测 WPS 对没存过的空白稿也塞了个显示名（"演示文稿1"），
        ///      所以必须是**像路径**的字符串；
        ///   2) ActivePresentation 挂在 **Application** 上，不在 Presentations 集合上 —— 只查集合的话，
        ///      PowerPoint 里明明开着的真课件会被算成"空白稿"（评分 2），插件就不再去找对的那个实例了。
        ///      （2026-10-15 实测：PowerPoint 那次评分确实是 2，改成从 app 上取才对。）
        /// </summary>
        static bool HasFileBackedDeck(object app, object presos, int count)
        {
            if (LooksLikePath(P(P(app, "ActivePresentation"), "FullName") as string)) return true;
            for (int i = 1; i <= count; i++)
            {
                var one = PIndex(presos, "Item", i);
                if (LooksLikePath(P(one, "FullName") as string)) return true;
            }
            return false;
        }

        /// <summary>
        /// 像不像"磁盘上的路径"（C:\... 、\\server\... 或含 / 的 Unix 风格）而不是一个显示名。
        /// 公开出来是给引擎用的：WPS 演示刚启动时会自带一份没存过的空白稿，FullName 是"演示文稿1"
        /// 这种显示名 —— 那不是老师要讲的课件，卡片不该拿它当"有课件"（用户 2026-10-15 要求：
        /// 这种情况下应该显示"等待打开课件"）。
        /// </summary>
        public static bool LooksLikePath(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length >= 3 && s[1] == ':') return true;          // C:\...
            if (s.StartsWith("\\\\", StringComparison.Ordinal)) return true;   // UNC
            return s.IndexOf('/') >= 0;
        }

        /// <summary>这个实例现在的"值不值得用"评分，见 <see cref="ScoreInstance"/>。引擎靠它决定要不要换实例。</summary>
        public int DeckScore { get { return ScoreInstance(Com); } }

        /// <summary>
        /// 找出"最值得用"的演示实例：先把 ROT 里的匹配项**全部** bind 一遍（WPS 常见同时注册好几个，
        /// 不同 ProgID 甚至可能是不同实例），谁打开着课件就用谁；都没有课件时，退而求其次用一个活着的
        /// 实例（至少能读放映状态）。找不到任何活实例才返回 false。
        /// </summary>
        static bool TryFindBestInstance(out object com, out string moniker, out string progId, out bool fromRot)
        {
            com = null; moniker = null; progId = null; fromRot = false;
            int best = 0;

            IBindCtx ctx = null; IRunningObjectTable rot = null; IEnumMoniker en = null;
            try
            {
                if (CreateBindCtx(0, out ctx) != 0) return false;
                if (GetRunningObjectTable(0, out rot) != 0) return false;
                rot.EnumRunning(out en);
                if (en == null) return false;

                var monikers = new IMoniker[1];
                var fetched = IntPtr.Zero;
                while (en.Next(1, monikers, fetched) == 0 && monikers[0] != null)
                {
                    string name = null;
                    try { monikers[0].GetDisplayName(ctx, null, out name); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;

                    bool interesting = false;
                    foreach (var pid in MonikerProgIds)
                        if (name.IndexOf(pid, StringComparison.OrdinalIgnoreCase) >= 0) { interesting = true; break; }
                    if (!interesting) continue;

                    object o = null;
                    try { rot.GetObject(monikers[0], out o); } catch { }
                    if (o == null) continue;

                    int s = ScoreInstance(o);
                    if (s > best)
                    {
                        best = s; com = o; moniker = name; progId = name; fromRot = true;
                        if (best >= 2) return true;   // 已经找到开着课件的实例，不用再找了
                    }
                }
            }
            catch { }
            finally
            {
                if (en != null) try { Marshal.ReleaseComObject(en); } catch { }
                if (rot != null) try { Marshal.ReleaseComObject(rot); } catch { }
                if (ctx != null) try { Marshal.ReleaseComObject(ctx); } catch { }
            }
            if (best >= 2) return true;

            // GetActiveObject：WPS 会用多个 ProgID 注册，不同的 ProgID 可能就是不同的实例。
            foreach (var pid in LiveProgIds)
            {
                try
                {
                    Guid clsid;
                    if (CLSIDFromProgID(pid, out clsid) != 0) continue;
                    object o;
                    GetActiveObject(ref clsid, IntPtr.Zero, out o);
                    if (o == null) continue;
                    int s = ScoreInstance(o);
                    if (s > best)
                    {
                        best = s; com = o; moniker = null; progId = pid; fromRot = false;
                        if (best >= 2) return true;
                    }
                }
                catch { }
            }

            // 一个活实例都没有（ROT 空、GetActiveObject 全失败）→ 调用方按"没挂上"处理。
            return best >= 1;
        }

        /// <summary>
        /// List every moniker in the Running Object Table. Written because a live slideshow
        /// once produced "no presentation app in the ROT" and guessing why is a waste of
        /// everyone's time -- this prints the ground truth.
        /// </summary>
        public static string DumpRot()
        {
            var sb = new StringBuilder();
            IBindCtx ctx = null; IRunningObjectTable rot = null; IEnumMoniker en = null;
            try
            {
                if (CreateBindCtx(0, out ctx) != 0) return "CreateBindCtx failed";
                if (GetRunningObjectTable(0, out rot) != 0) return "GetRunningObjectTable failed";
                rot.EnumRunning(out en);
                if (en == null) return "EnumRunning returned null";
                var monikers = new IMoniker[1];
                var fetched = IntPtr.Zero;
                int i = 0;
                while (en.Next(1, monikers, fetched) == 0 && monikers[0] != null)
                {
                    i++;
                    string name = null;
                    try { monikers[0].GetDisplayName(ctx, null, out name); } catch (Exception ex) { name = "<GetDisplayName threw " + ex.GetType().Name + ">"; }
                    string cls = "<none>";
                    try { object o; rot.GetObject(monikers[0], out o); cls = o == null ? "<GetObject null>" : o.GetType().FullName; } catch (Exception ex) { cls = "<GetObject threw " + ex.GetType().Name + ">"; }
                    sb.AppendLine("[" + i + "] " + name);
                    sb.AppendLine("      -> " + cls);
                }
                sb.AppendLine("(" + i + " moniker(s) total)");
            }
            catch (Exception ex) { sb.AppendLine("ROT dump failed: " + ex.GetType().Name + " " + ex.Message); }
            finally
            {
                if (en != null) try { Marshal.ReleaseComObject(en); } catch { }
                if (rot != null) try { Marshal.ReleaseComObject(rot); } catch { }
                if (ctx != null) try { Marshal.ReleaseComObject(ctx); } catch { }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ late binding

        internal static object P(object target, string name)
        {
            if (target == null) return null;
            try { return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null); }
            catch { return null; }
        }

        internal static object PIndex(object target, string name, object index)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, new object[] { index });
            }
            catch { return null; }
        }

        internal static int? AsInt(object v)
        {
            if (v == null) return null;
            try { return Convert.ToInt32(v); } catch { return null; }
        }

        static object Call(object target, string name, params object[] args)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, args);
            }
            catch { return null; }
        }

        /// <summary>Latest error from a COM call, for the diagnostics pane.</summary>
        public string LastError { get; private set; }

        object TryCall(object target, string name, params object[] args)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, args);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                LastError = name + ": " + inner.GetType().Name + " " + inner.Message;
                return null;
            }
        }

        // ------------------------------------------------------------------ reading state

        /// <summary>
        /// 拿到"当前放映视图"。2026-10-13 本机 Office16 实测的坑：通过 IDispatch 按名字取
        /// SlideShowWindows.Item(1).View 会拿到 null，而 Presentation.SlideShowWindow.View 可以
        /// ——快照日志里那句 "via Presentation.SlideShowWindow.View" 就是被逼出来的退路。
        /// 早先 GotoSlide 只用第一条路，于是跳页永远返回"拿不到放映视图对象"，而与此同时快照
        /// 却能把页码读得好好的（同一次放映、同一个 COM 对象）。两条路都试，并把成功的路线记下来。
        /// </summary>
        object ResolveShowView(out string route)
        {
            route = "";
            try
            {
                var windows = P(Com, "SlideShowWindows");
                int n = AsInt(P(windows, "Count")) ?? 0;
                if (n > 0)
                {
                    var w = PIndex(windows, "Item", 1) ?? windows;
                    var v = P(w, "View");
                    if (v != null) { route = "SlideShowWindows.Item(1).View"; return v; }
                    var pres = P(w, "Presentation");
                    if (pres != null)
                    {
                        var win = P(pres, "SlideShowWindow");
                        var v2 = P(win, "View");
                        if (v2 != null) { route = "SlideShowWindows.Item(1).Presentation.SlideShowWindow.View"; return v2; }
                    }
                }
                var active = FindActivePresentation();
                if (active != null)
                {
                    var win2 = P(active, "SlideShowWindow");
                    var v3 = P(win2, "View");
                    if (v3 != null) { route = "ActivePresentation.SlideShowWindow.View"; return v3; }
                }
            }
            catch { }
            return null;
        }

        public PptSnapshot Snapshot()
        {
            var s = new PptSnapshot { AppFound = true, AppKind = label, ProgId = ProgId };
            try
            {
                var sb = new StringBuilder();
                var windows = P(Com, "SlideShowWindows");
                int n = AsInt(P(windows, "Count")) ?? 0;
                sb.Append("SlideShowWindows=").Append(n).Append("; ");
                if (n <= 0)
                {
                    s.Showing = false;
                    // 没在放映也要说出"打开着哪份课件"：老师刚把课件打开、还没按放映，
                    // 卡片就该显示"上次停在第 N 页"让他一点就接着讲。
                    var open = FindActivePresentation();
                    var openName = P(open, "FullName") as string;
                    if (string.IsNullOrEmpty(openName)) openName = P(open, "Name") as string;
                    s.FilePath = openName ?? "";
                    s.SlideCount = AsInt(P(P(open, "Slides"), "Count")) ?? 0;
                    s.Diag = sb.ToString().TrimEnd() + "; openDeck=" + (string.IsNullOrEmpty(s.FilePath) ? "-" : "yes");
                    return s;
                }

                var w = PIndex(windows, "Item", 1) ?? windows;
                string route;
                var view = ResolveShowView(out route);
                if (view == null) view = P(w, "View");   // 老路子兜底：别让快照因为换个写法而退化
                s.Showing = true;

                // Where the file lives: several routes exist depending on suite and version.
                var pres = P(view, "Presentation") ?? P(w, "Presentation") ?? FindActivePresentation();
                var fullName = P(pres, "FullName") as string;
                if (string.IsNullOrEmpty(fullName)) fullName = P(pres, "Name") as string;
                s.FilePath = fullName ?? "";
                var slides = P(pres, "Slides");
                s.SlideCount = AsInt(P(slides, "Count")) ?? 0;

                // Current slide: try the documented Office member first, then the WPS-shaped
                // alternatives. Which one answered is recorded so the log tells the truth
                // instead of us guessing.
                object pos = P(view, "CurrentShowPosition");
                string how = "View.CurrentShowPosition";
                if (pos == null)
                {
                    var sl = P(view, "Slide");
                    pos = sl != null ? P(sl, "SlideIndex") : null;
                    how = "View.Slide.SlideIndex";
                }
                if (pos == null)
                {
                    var p2 = P(view, "Presentation") ?? pres;
                    var win2 = P(p2, "SlideShowWindow");
                    var view2 = P(win2, "View");
                    pos = P(view2, "CurrentShowPosition");
                    how = "Presentation.SlideShowWindow.View.CurrentShowPosition";
                }
                if (pos == null)
                {
                    pos = P(view, "State");
                    how = "View.State (unresolved)";
                }
                s.Current = AsInt(pos) ?? 0;
                sb.Append("view=").Append(string.IsNullOrEmpty(route) ? "?" : route).Append("; via ").Append(how);
                s.Diag = sb.ToString();
                return s;
            }
            catch (Exception ex)
            {
                s.Diag = "snapshot failed: " + ex.GetType().Name + " " + ex.Message;
                return s;
            }
        }

        /// <summary>Jump to a slide in whatever presentation is on screen. Returns "" on success.</summary>
        public string GotoSlide(int index, bool resetAnimation)
        {
            LastError = null;
            try
            {
                var windows = P(Com, "SlideShowWindows");
                int n = AsInt(P(windows, "Count")) ?? 0;
                if (n <= 0) return "当前没有正在放映的演示文稿";
                string route;
                var view = ResolveShowView(out route);
                if (view == null) return "拿不到放映视图对象";
                LastViewRoute = route;
                // GotoSlide(Index, ResetSlide): ResetSlide=false resumes the target slide's
                // animation instead of restarting it. msoFalse == 0, msoTrue == -1.
                var r = TryCall(view, "GotoSlide", index, resetAnimation ? -1 : 0);
                if (r == null && LastError != null) return LastError;
                return "";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + " " + ex.Message;
            }
        }

        /// <summary>
        /// Drive an already-running suite. These exist for the self test and for the classroom
        /// edge case where the deck is open but the show has not started yet; they are not on
        /// the hot path.
        /// </summary>
        public string OpenDeck(string path)
        {
            LastError = null;
            try
            {
                var presos = P(Com, "Presentations");
                if (presos == null) return "拿不到 Presentations 集合";
                // 最后一个参数 WithWindow 必须是 -1（msoTrue）：无窗口打开的演示文稿不会进入
                // Presentations 集合，也不会出现在 ActivePresentation 里，后面所有调用都找不到它。
                // ReadOnly 用 0（正常打开）：老师点“开始放映”是想接着上课，给他一个只读副本没意义。
                var opened = TryCall(presos, "Open", path, 0, 0, -1);
                if (opened == null) return LastError ?? "Open 失败";
                // 必须留住这个引用：Office 在最后一个自动化引用被释放时会关掉由自动化打开的文档，
                // 表现就是“刚打开放映，两秒后自己结束了”。
                keepAlive = opened;
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + " " + ex.Message; }
        }

        /// <summary>Start the slideshow for whatever presentation is active.</summary>
        public string StartShow()
        {
            LastError = null;
            try
            {
                var pres = P(Com, "ActivePresentation");
                if (pres == null)
                {
                    // Nothing active: fall back to the first open deck so a freshly opened file
                    // with no window focus can still be presented.
                    var presos = P(Com, "Presentations");
                    var n = AsInt(P(presos, "Count")) ?? 0;
                    if (n > 0) pres = PIndex(presos, "Item", 1);
                }
                if (pres == null) return "没有打开的演示文稿";
                var settings = P(pres, "SlideShowSettings");
                if (settings == null) return "拿不到 SlideShowSettings";
                var w = TryCall(settings, "Run");
                if (w == null) return LastError ?? "Run 失败";
                keepAliveShow = w;
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + " " + ex.Message; }
        }

        /// <summary>
        /// 演示软件自己不会抢前台（Windows 前台锁），但我们的进程刚刚接到过老师点卡片的那一下，
        /// 有资格把它抬到最前面，所以这里主动抬两次：一次立刻，一次等放映过渡动画结束之后。
        /// 抬不上来也无所谓——放映窗口本来就是全屏的，只是可能压在浏览器后面。
        /// </summary>
        public static void RaiseShowWindow()
        {
            try
            {
                var h = FindWindowW("screenClass", null);
                if (h != IntPtr.Zero) SetForegroundWindow(h);
            }
            catch { }
        }

        static void RaiseShowWindowSoon()
        {
            RaiseShowWindow();
            try
            {
                System.Threading.Timer t = null;
                t = new System.Threading.Timer(_ =>
                {
                    try { RaiseShowWindow(); } catch { }
                    finally { try { t.Dispose(); } catch { } }
                }, null, 800, System.Threading.Timeout.Infinite);
            }
            catch { }
        }

        /// <summary>Pick the deck the teacher means: the exact file first, then whatever is active.</summary>
        public object FindPresentation(string path)
        {
            try
            {
                var presos = P(Com, "Presentations");
                int n = AsInt(P(presos, "Count")) ?? 0;
                if (!string.IsNullOrEmpty(path))
                {
                    for (int i = 1; i <= n; i++)
                    {
                        var p = PIndex(presos, "Item", i);
                        var full = P(p, "FullName") as string;
                        if (full != null && string.Equals(full, path, StringComparison.OrdinalIgnoreCase)) return p;
                    }
                }
                var act = P(Com, "ActivePresentation");
                if (act != null) return act;
                if (n > 0) return PIndex(presos, "Item", 1);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 放映刚结束时课件停在编辑视图的第几页（1 起，取不到返回 0）。
        /// 2026-10-16 用户第 6 条：老师在最后一面直接结束放映，放映窗口一消失页码就再也问不到了；
        /// 而 PowerPoint/WPS 结束放映后编辑视图会停在这一页，趁收尾的 2 秒窗口把它读回来。
        /// </summary>
        public int EditingSlide()
        {
            try
            {
                var pres = FindPresentation(null);          // 当前活动课件
                var windows = pres == null ? null : P(pres, "Windows");
                var w = windows == null ? null : PIndex(windows, "Item", 1);
                var view = w == null ? null : P(w, "View");
                var slide = view == null ? null : P(view, "Slide");
                return AsInt(P(slide, "SlideIndex")) ?? 0;
            }
            catch { return 0; }
        }

        object TrySet(object target, string name, object value)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, target, new object[] { value });
            }
            catch (Exception ex)
            {
                LastError = name + "(set): " + (ex.InnerException ?? ex).Message;
                return null;
            }
        }

        /// <summary>
        /// Take the teacher to <paramref name="index"/> whatever state the suite is in: a show that
        /// is already on screen is jumped, a deck that is open but not presenting is started at that
        /// page, and a deck that is not open at all is opened first. Returns "" on success, or a
        /// Chinese reason on failure (which the card shows verbatim).
        /// </summary>
        public string JumpToSlide(int index, bool resetAnimation, string filePath)
        {
            LastError = null;
            if (index <= 0) return "记录里的页码无效";

            // Already presenting: the teacher expects the screen to move right now.
            var windows = P(Com, "SlideShowWindows");
            if ((AsInt(P(windows, "Count")) ?? 0) > 0)
            {
                var g = GotoSlide(index, resetAnimation);
                // 放映窗口刚冒出来时 View 还取不到，别把这当成失败，往下走再等一轮。
                if (g.Length == 0 || g != "拿不到放映视图对象")
                {
                    if (g.Length == 0) RaiseShowWindowSoon();
                    return g;
                }
            }

            // Open but not presenting (or not open at all): start the show at the saved page.
            var pres = FindPresentation(filePath);
            if (pres == null && !string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
                // “拿不到 Presentations 集合”多半只是 Office 还没准备好，不要立刻判死刑。
                string oerr = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    oerr = OpenDeck(filePath);
                    if (oerr == null) break;
                    if (oerr != "拿不到 Presentations 集合") break;
                    System.Threading.Thread.Sleep(1500);
                }
                if (oerr != null) return "打不开课件：" + oerr;
                pres = FindPresentation(filePath);
            }
            if (pres == null) return "没有打开的课件";

            // 课件刚打开时 Slides 还是空的；这时 Run() 会起一个空放映，屏幕上什么都不出现。
            WaitFor(() => (AsInt(P(P(pres, "Slides"), "Count")) ?? 0) > 0, 10000, 250);

            // 2026-10-13 实测（本机 Office16）：只要在 Run() 之前给 SlideShowSettings.StartingSlide
            // 赋过值，Run() 就再也起不来放映——SlideShowWindows.Count 恒为 0，屏幕上什么都不出现。
            // 证据（同一台机器、同一份课件、连续三次）：
            //   U1 全新实例 + StartingSlide=4 + Run            -> maxWins=0
            //   U2 全新实例 + Run（不碰 StartingSlide）        -> maxWins=1 pos=1，Exit 后再 Run 仍 maxWins=1
            //   T2/T3 先 Run 成功、Exit 之后设 StartingSlide 再 Run -> maxWins=0
            // 所以这里绝不设 StartingSlide：先 Run()，再由下面的 GotoSlide 在 200ms 内跳到目标页。
            // 宁可让第 1 页闪一下，也不能让放映根本起不来。
            var settings = P(pres, "SlideShowSettings");
            if (settings == null) return "拿不到 SlideShowSettings";

            string last = "放映没有起来";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                // 已经在放映就别再 Run() 一次：PowerPoint 会直接报错，反而把状态行弄成失败。
                var w0 = P(Com, "SlideShowWindows");
                if ((AsInt(P(w0, "Count")) ?? 0) <= 0 && TryCall(settings, "Run") == null)
                    return LastError ?? "Run 失败";
                // A show window takes a few seconds to appear on a freshly launched PowerPoint
                // (13:24 实测：Run() 之后 2 秒内什么都没有，窗口十秒后才稳定)。
                if (WaitFor(() =>
                    {
                        var w = P(Com, "SlideShowWindows");
                        if ((AsInt(P(w, "Count")) ?? 0) <= 0) return false;
                        last = GotoSlide(index, resetAnimation);
                        return last.Length == 0;
                    }, 9000, 200)) { RaiseShowWindowSoon(); return ""; }
                if (!last.StartsWith("拿不到") && !last.StartsWith("放映没有")) return last;
            }
            return last;
        }

        /// <summary>End the show that is on screen.</summary>
        public string ExitShow()        {
            LastError = null;
            try
            {
                var windows = P(Com, "SlideShowWindows");
                int n = AsInt(P(windows, "Count")) ?? 0;
                if (n <= 0) return "当前没有放映";
                var w = PIndex(windows, "Item", 1) ?? windows;
                var view = P(w, "View");
                if (view == null) return "拿不到放映视图";
                TryCall(view, "Exit");
                return LastError;
            }
            catch (Exception ex) { return ex.GetType().Name + " " + ex.Message; }
        }

        /// <summary>Dump every property we know of, so an unknown suite can be mapped.</summary>
        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("progId        : " + ProgId + "   (" + label + ")");
            void Line(string k, object v) { sb.AppendLine(k.PadRight(14) + ": " + (v == null ? "<null>" : v.ToString())); }
            Line("Name", P(Com, "Name"));
            Line("Version", P(Com, "Version"));
            Line("Path", P(Com, "Path"));
            Line("Visible", P(Com, "Visible"));
            var pres = P(Com, "ActivePresentation");
            Line("ActivePresentation.FullName", P(pres, "FullName"));
            var presos = P(Com, "Presentations");
            Line("Presentations.Count", P(presos, "Count"));
            var w = P(Com, "SlideShowWindows");
            int n = AsInt(P(w, "Count")) ?? 0;
            Line("SlideShowWindows.Count", n);
            if (n > 0)
            {
                var win = PIndex(w, "Item", 1);
                var view = P(win, "View");
                Line("  Window.Caption", P(win, "Caption"));
                Line("  Window.HWND", P(win, "HWND"));
                Line("  View.State", P(view, "State"));
                Line("  View.CurrentShowPosition", P(view, "CurrentShowPosition"));
                var sl = P(view, "Slide");
                Line("  View.Slide.SlideIndex", P(sl, "SlideIndex"));
                Line("  View.Slide.Name", P(sl, "Name"));
                var vpres = P(view, "Presentation");
                Line("  View.Presentation.FullName", P(vpres, "FullName"));
                Line("  View.Presentation.Slides.Count", P(P(vpres, "Slides"), "Count"));
                Line("  View.Type", P(view, "Type"));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            // 2026-10-16 用户第 5 条（上完课关 WPS 时直接卡死）：这里原来只释放了 Com。
            // keepAlive / keepAliveShow 这两个"防止 Office 顺手关掉自动化文档"的引用一直挂着，
            // 每丢一次桥就漏一组 RCW；而 WPS 退出时要等所有外部客户端放手，于是它自己关不掉。
            Release(ref keepAliveShow);
            Release(ref keepAlive);
            try { if (Com != null && Marshal.IsComObject(Com)) Marshal.FinalReleaseComObject(Com); } catch { }
            Com = null;

            // 2026-10-17：这里原来跟着一句 GC.Collect() + WaitForPendingFinalizers() 想催终结器。
            // 强回收本身没必要（FinalReleaseComObject 已经把引用计数打到 0，COM 服务器当场就知道
            // 客户端走了，不用等 GC），而且它把 NativeOverlayCard 的 WndProc 委托一起收走了 ——
            // 事件日志：A callback was made on a garbage collected delegate of type
            // …NativeOverlayCard+WndProcDelegate::Invoke（at DispatchMessage / Run），
            // 运行时直接 FailFast，把 ClassIsland.Desktop 整个进程带崩。所以强回收不再做。
        }

        static void Release(ref object o)
        {
            if (o == null) return;
            try { if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
            o = null;
        }
    }
}
