using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSlideSync.ToastHelper
{
    /// <summary>
    /// 造一个「带 System.AppUserModel.ID 的开始菜单快捷方式」。
    ///
    /// 为什么非要这个快捷方式：未打包的 Win32 程序没有包标识（package identity），
    /// Windows 只把「开始菜单里有带 AUMID 属性的快捷方式」的进程当成一个能被通知的应用；
    /// 少了它，ToastNotificationManager.Show() 会直接抛（常见 0x803E0105 / E_INVALIDARG）。
    /// 这是微软给桌面应用弹 toast 的官方做法，不是偏方。
    ///
    /// 实现走 IShellLinkW + IPersistFile（COM），而不是 WScript.Shell：后者依赖 Windows
    /// Script Host，被组策略关掉的机器上反而更脆；IShellLink 是 shell 自己的接口，稳定。
    /// </summary>
    internal static class Shortcut
    {
        internal const string ShortcutFileName = "课件续页通知.lnk";

        // 开始菜单「所有用户」下的 Programs 需要管理员；当前用户自己的不需要，所以用 ApplicationData。
        internal static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs",
            ShortcutFileName);

        /// <summary>已存在、指向本 exe、并且 AUMID 属性对得上 → 不用重建。</summary>
        internal static bool IsCurrent(string lnkPath, string targetExe, string aumid, Log log)
        {
            try
            {
                if (!System.IO.File.Exists(lnkPath)) return false;

                var link = (IShellLinkW)new ShellLink();
                var pf = (IPersistFile)link;
                int hr = pf.Load(lnkPath, STGM_READ);
                if (hr != 0)
                {
                    log.Write("读取旧快捷方式失败 hr=0x" + hr.ToString("X8") + "，将重建");
                    return false;
                }

                var sb = new StringBuilder(MAX_PATH * 2);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                string actual = sb.ToString();

                // 先放掉快捷方式对象：IPersistFile.Load 之后它仍开着 .lnk 的文件句柄，
                // 紧接着打开属性存储会撞上 ERROR_SHARING_VIOLATION（0x80070020），读回就成了空。
                Marshal.ReleaseComObject(pf);
                Marshal.ReleaseComObject(link);

                string? actualAumid = PropertyStore.ReadAumid(lnkPath, log);
                bool same = string.Equals(Normalize(actual), Normalize(targetExe), StringComparison.OrdinalIgnoreCase)
                            && string.Equals(actualAumid, aumid, StringComparison.Ordinal);

                if (!same)
                {
                    log.Write("旧快捷方式对不上（target=" + actual + " aumid=" + (actualAumid ?? "无") +
                              "），将重建");
                }
                return same;
            }
            catch (Exception ex)
            {
                log.Write("检查旧快捷方式时异常: " + ex.Message + "，将重建");
                return false;
            }
        }

        internal static bool Create(string lnkPath, string targetExe, string aumid, string? iconPath, Log log)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

                var link = (IShellLinkW)new ShellLink();
                link.SetPath(targetExe);
                // 描述只影响鼠标悬停提示；写清楚它是系统组件，免得老师以为是可点的功能。
                link.SetDescription("课件续页 · 下课提醒横幅（系统通知组件，无需手动打开）");
                string? workDir = System.IO.Path.GetDirectoryName(targetExe);
                if (!string.IsNullOrEmpty(workDir)) link.SetWorkingDirectory(workDir);
                if (iconPath != null) link.SetIconLocation(iconPath, 0);

                var pf = (IPersistFile)link;
                int hr = pf.Save(lnkPath, true);
                if (hr != 0)
                {
                    log.Write("IPersistFile.Save 失败 hr=0x" + hr.ToString("X8") + " path=" + lnkPath);
                    return false;
                }

                // IPersistFile 在释放前会一直占着 .lnk 文件句柄；此时再打开属性存储，shell 读到的
                // 还是保存前的状态（实测读回空）。必须先把 COM 引用放掉，让文件句柄关掉。
                Marshal.ReleaseComObject(pf);
                Marshal.ReleaseComObject(link);
                System.Threading.Thread.Sleep(80);

                // Save 之后才有文件可打开，属性必须在文件存在后写。
                if (!PropertyStore.WriteAumid(lnkPath, aumid, log)) return false;
                return true;
            }
            catch (Exception ex)
            {
                log.Write("建快捷方式异常: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        static string Normalize(string path)
        {
            try { return System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
            catch { return path.TrimEnd('\\'); }
        }

        const int MAX_PATH = 260;
        const uint STGM_READ = 0;

        // ---- COM 互操作声明 ----
        // 注意：这些接口的**方法顺序**必须和 shobjidl_core.h 里的 vtable 完全一致，
        // 少一个/换一个顺序都会调错函数指针（表现为 AccessViolation 或行为诡异）。

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                int cch, IntPtr pfd, uint fFlags);
            [PreserveSig] int GetIDList(out IntPtr ppidl);
            [PreserveSig] int SetIDList(IntPtr pidl);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            [PreserveSig] int GetHotkey(out short pwHotkey);
            [PreserveSig] int SetHotkey(short wHotkey);
            [PreserveSig] int GetShowCmd(out int piShowCmd);
            [PreserveSig] int SetShowCmd(int iShowCmd);
            [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                int cch, out int piIcon);
            [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
            [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            [PreserveSig] int GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint dwMode);
            [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, bool fRemember);
            [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
        }
    }

    /// <summary>
    /// 快捷方式的 System.AppUserModel.ID 没法用「文件属性」那种方式写，只能打开 shell 的
    /// 属性存储（IPropertyStore），按 PROPERTYKEY 写一个 PROPVARIANT。这是唯一可行的办法。
    /// </summary>
    internal static class PropertyStore
    {
        // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
        static readonly PropertyKey AppUserModelId = new PropertyKey
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            Pid = 5,
        };

        const uint GPS_READWRITE = 0x2;
        const ushort VT_LPWSTR = 31;

        internal static bool WriteAumid(string lnkPath, string aumid, Log log)
        {
            try
            {
                int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, GPS_READWRITE,
                    typeof(IPropertyStore).GUID, out IPropertyStore? store);
                if (hr != 0 || store == null)
                {
                    log.Write("打开快捷方式属性存储失败 hr=0x" + hr.ToString("X8"));
                    return false;
                }

                // ref 参数不能直接传静态只读字段，拷一份局部变量。
                var key = AppUserModelId;
                var pv = new PropVariant { Vt = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(aumid) };
                try
                {
                    hr = store.SetValue(ref key, ref pv);
                    if (hr != 0)
                    {
                        log.Write("SetValue(AppUserModel.ID) 失败 hr=0x" + hr.ToString("X8"));
                        return false;
                    }
                    hr = store.Commit();
                    if (hr != 0)
                    {
                        log.Write("Commit 属性存储失败 hr=0x" + hr.ToString("X8"));
                        return false;
                    }
                }
                finally
                {
                    // SetValue 内部会自己拷一份 PROPVARIANT（COM 约定），所以这里可以立刻释放。
                    if (pv.Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.Pointer);
                    // 写用的属性存储会一直以写方式开着 .lnk；不放掉它，下面读回会拿到
                    // 0x80070020（ERROR_SHARING_VIOLATION，共享冲突）。
                    Marshal.ReleaseComObject(store);
                }

                // 写完读回来验一遍：某些系统上 Commit 返回 S_OK 但值没落盘（lnk 只读、AV 拦截），
                // 与其等下一次 Show() 抛异常，不如现在就判失败去回退托盘气泡。
                // 但「读回空」也可能是 shell 属性缓存还没刷新（同一进程里刚写完时实测会这样），
                // 所以再多等几轮；只要最终读到的是错的值才真算失败。
                string? actual = ReadAumid(lnkPath, log);
                if (string.Equals(actual, aumid, StringComparison.Ordinal)) return true;

                if (actual != null)
                {
                    log.Write("AUMID 写进去读回来不一致（读回 " + actual + "）");
                    return false;
                }

                // 读不到（不是读错）：SetValue/Commit 都返回了 S_OK，值其实已经落盘，
                // 只是本进程看不到。不在这里拦死，交给下一次 Show() 自己判成败。
                log.Write("AUMID 已写入，但本进程暂时读不回来（shell 属性缓存），继续");
                return true;
            }
            catch (Exception ex)
            {
                log.Write("写 AUMID 异常: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        internal static string? ReadAumid(string lnkPath, Log? log = null)
        {
            // shell 的属性存储有缓存：刚被本进程写过的 .lnk，第一次打开可能读到空值。
            // 重试几轮再下结论，避免把「暂时读不到」当成「没写成功」。
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) System.Threading.Thread.Sleep(120);
                string? v = ReadAumidOnce(lnkPath, log);
                if (v != null) return v;
            }
            return null;
        }

        static string? ReadAumidOnce(string lnkPath, Log? log)
        {
            try
            {
                int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0,
                    typeof(IPropertyStore).GUID, out IPropertyStore? store);
                if (hr != 0 || store == null)
                {
                    log?.Write("读 AUMID: 打开属性存储失败 hr=0x" + hr.ToString("X8"));
                    return null;
                }

                try
                {
                    var key = AppUserModelId;
                    hr = store.GetValue(ref key, out PropVariant pv);
                    if (hr != 0)
                    {
                        log?.Write("读 AUMID: GetValue 失败 hr=0x" + hr.ToString("X8"));
                        return null;
                    }
                    try
                    {
                        if (pv.Vt != VT_LPWSTR || pv.Pointer == IntPtr.Zero) return null;
                        return Marshal.PtrToStringUni(pv.Pointer);
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }
                finally
                {
                    // 读用的属性存储也要放掉，否则句柄会一直挂到进程退出。
                    Marshal.ReleaseComObject(store);
                }
            }
            catch (Exception ex)
            {
                log?.Write("读 AUMID 异常: " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey
        {
            public Guid FormatId;
            public uint Pid;
        }

        /// <summary>
        /// PROPVARIANT 在 x64 上 24 字节（vt + 3 个保留字 + 16 字节联合体），
        /// 显式给 Size 是为了防止 CLR 按字段算出一个偏小的结构，让 GetValue 写越界。
        /// 只用到 VT_LPWSTR，所以联合体里只声明这一个指针。
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort Vt;
            [FieldOffset(2)] public ushort Reserved1;
            [FieldOffset(4)] public ushort Reserved2;
            [FieldOffset(6)] public ushort Reserved3;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            uint flags,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pv);
    }
}
