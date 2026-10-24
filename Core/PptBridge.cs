using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSlideSync.Plugin.Core
{
    public sealed class PptSnapshot
    {
        public bool AppFound;
        public string AppKind = "";    // "PowerPoint" / "WPS" / ""
        public string ProgId = "";     // the moniker that answered
        public bool Showing;
        public string FilePath = "";   // the file being shown ("" when unknown)
        public int Current;
        public int SlideCount;
        public string Diag = "";       // what worked / what failed

        public override string ToString()
        {
            return "app=" + (AppFound ? AppKind : "none") + " showing=" + Showing
                + " file=" + FilePath + " slide=" + Current + "/" + SlideCount;
        }
    }

    public sealed class PptBridge : IDisposable
    {
        [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
        [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable pprot);
        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object ppunk);
        [DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

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

        static readonly string[] MonikerProgIds = { "PowerPoint.Application", "Kwpp.Application", "WPP.Application" };
        static readonly string[] LiveProgIds = { "PowerPoint.Application", "Kwpp.Application", "WPP.Application", "wpp.Application" };

        static readonly string[] SuiteProcessNames = { "POWERPNT", "wpp" };

        static readonly string[] SuiteWindowClasses = { "PPTFrameClass", "PP12FrameClass", "screenClass" };

        static readonly string[] SuiteQtFrameClasses = { "Qt5QWindowIcon" };

        static readonly string[] SuiteWindowOwnerNames = { "POWERPNT", "wpp", "wps" };

        public object Com { get; private set; }
        public string ProgId { get; private set; }
        readonly string label;

        object keepAlive;
        object keepAliveShow;

        PptBridge(object com, string progId, string label)
        {
            Com = com; ProgId = progId; this.label = label;
        }

        public static PptBridge TestOverride;

        public static PptBridge Wrap(object com, string progId, string label)
        {
            return new PptBridge(com, progId, label) { AttachMode = "test" };
        }

        public string Label { get { return label; } }

        public string LastViewRoute { get; private set; } = "";

        public string AttachMode { get; private set; }

        public static PptBridge Connect()
        {
            if (TestOverride != null) return TestOverride;
            object com;
            string moniker;
            string progId;
            bool fromRot;
            if (TryFindBestInstance(out com, out moniker, out progId, out fromRot))
            {
                string label = fromRot ? ClassifyLabel(moniker) : ClassifyLabel(progId) + " (GetActiveObject)";
                return new PptBridge(com, fromRot ? moniker : progId, label) { AttachMode = fromRot ? "rot" : "live" };
            }

            return null;
        }

        public static bool SuiteIsRunning()
        {
            for (int i = 0; i < SuiteProcessNames.Length; i++)
                if (ProcessNamed(SuiteProcessNames[i])) return true;
            return false;
        }

        public static string SuiteName()
        {
            if (ProcessNamed("wpp")) return "WPS 演示";
            if (ProcessNamed("POWERPNT")) return "PowerPoint";
            return "";
        }

        public static bool SuiteActive()
        {
            return SuiteIsRunning() || SuiteOnScreen();
        }

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
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    string n = p.ProcessName;
                    if (n.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase)
                        || n.Equals("wpp", StringComparison.OrdinalIgnoreCase)) return true;
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

        static bool ProcessNamed(string name)
        {
            try
            {
                var ps = System.Diagnostics.Process.GetProcessesByName(name);
                if (ps == null || ps.Length == 0) return false;
                for (int i = 0; i < ps.Length; i++) { try { ps[i].Dispose(); } catch { } }
                return true;
            }
            catch { return false; }
        }

        public static PptBridge Launch(string filePath)
        {
            if (TestOverride != null) return TestOverride;

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
                    b.TrySet(com, "Visible", true);
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

        static int ScoreInstance(object com)
        {
            if (com == null) return 0;
            bool alive = P(com, "Name") != null || P(com, "Presentations") != null;
            if (!alive) return 0;

            var presos = P(com, "Presentations");
            int n = presos == null ? 0 : (AsInt(P(presos, "Count")) ?? 0);
            if (n > 0) return HasFileBackedDeck(com, presos, n) ? 3 : 2;

            var wins = P(com, "SlideShowWindows");
            if (wins != null && (AsInt(P(wins, "Count")) ?? 0) > 0) return 3;

            return 1;
        }

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

        public static bool LooksLikePath(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length >= 3 && s[1] == ':') return true;          // C:\...
            if (s.StartsWith("\\\\", StringComparison.Ordinal)) return true;   // UNC
            return s.IndexOf('/') >= 0;
        }

        public int DeckScore { get { return ScoreInstance(Com); } }

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
                        if (best >= 2) return true;
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

            return best >= 1;
        }

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

                var pres = P(view, "Presentation") ?? P(w, "Presentation") ?? FindActivePresentation();
                var fullName = P(pres, "FullName") as string;
                if (string.IsNullOrEmpty(fullName)) fullName = P(pres, "Name") as string;
                s.FilePath = fullName ?? "";
                var slides = P(pres, "Slides");
                s.SlideCount = AsInt(P(slides, "Count")) ?? 0;

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
                var r = TryCall(view, "GotoSlide", index, resetAnimation ? -1 : 0);
                if (r == null && LastError != null) return LastError;
                return "";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + " " + ex.Message;
            }
        }

        public string OpenDeck(string path)
        {
            LastError = null;
            try
            {
                var presos = P(Com, "Presentations");
                if (presos == null) return "拿不到 Presentations 集合";
                var opened = TryCall(presos, "Open", path, 0, 0, -1);
                if (opened == null) return LastError ?? "Open 失败";
                keepAlive = opened;
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + " " + ex.Message; }
        }

        public string StartShow()
        {
            LastError = null;
            try
            {
                var pres = P(Com, "ActivePresentation");
                if (pres == null)
                {
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

        public int EditingSlide()
        {
            try
            {
                var pres = FindPresentation(null);
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

        public string JumpToSlide(int index, bool resetAnimation, string filePath)
        {
            LastError = null;
            if (index <= 0) return "记录里的页码无效";

            var windows = P(Com, "SlideShowWindows");
            if ((AsInt(P(windows, "Count")) ?? 0) > 0)
            {
                var g = GotoSlide(index, resetAnimation);
                if (g.Length == 0 || g != "拿不到放映视图对象")
                {
                    if (g.Length == 0) RaiseShowWindowSoon();
                    return g;
                }
            }

            var pres = FindPresentation(filePath);
            if (pres == null && !string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
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

            WaitFor(() => (AsInt(P(P(pres, "Slides"), "Count")) ?? 0) > 0, 10000, 250);

            var settings = P(pres, "SlideShowSettings");
            if (settings == null) return "拿不到 SlideShowSettings";

            string last = "放映没有起来";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var w0 = P(Com, "SlideShowWindows");
                if ((AsInt(P(w0, "Count")) ?? 0) <= 0 && TryCall(settings, "Run") == null)
                    return LastError ?? "Run 失败";
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
            Release(ref keepAliveShow);
            Release(ref keepAlive);
            try { if (Com != null && Marshal.IsComObject(Com)) Marshal.FinalReleaseComObject(Com); } catch { }
            Com = null;

        }

        static void Release(ref object o)
        {
            if (o == null) return;
            try { if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
            o = null;
        }
    }
}
