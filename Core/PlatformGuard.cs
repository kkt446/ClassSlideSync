using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace ClassSlideSync.Plugin.Core;

/// <summary>
/// 平台闸门的唯一实现处。
///
/// 背景：主工程的 TFM 是 <c>net8.0</c>（不是 net8.0-windows），同一个 dll 会被 Windows 和
/// Linux 上的 ClassIsland 原样加载 —— 宿主加载插件时不按 TFM/OS 过滤。所以「这个平台能不能
/// 跑」只能在**运行时**判断，绝不能用 <c>#if WINDOWS</c> 编译期砍代码：那样两端拿到的就不是
/// 同一份二进制，Linux 包在 Windows 上装也会少功能。
///
/// 本类的两件事：
///   1. <see cref="IsWindows"/>：带 <see cref="SupportedOSPlatformGuardAttribute"/> 的运行时判断。
///      调用方写 <c>if (!PlatformGuard.IsWindows) { …; return; }</c> 之后，后面 Windows 专属 API
///      （COM、user32/gdi32、Shell_NotifyIconW）的 CA1416 告警会按「已证明只在 Windows 走」自动
///      消失 —— 这是压 CA1416 的正规做法，不需要 <c>#pragma warning disable</c>，更不是 #if。
///   2. <see cref="Announce"/>：被跳过时记**恰好一行**日志，格式固定
///      <c>platform: skipped (non-windows) &lt;what&gt;</c>，同一个 what 只记一次（调度器每 250 ms
///      一拍，不去重会把 app.log 刷爆）。Windows 上这两件事都不产生任何输出，行为零变化。
/// </summary>
internal static class PlatformGuard
{
    /// <summary>
    /// 当前平台是不是 Windows。
    ///
    /// 注意它必须是「属性 + SupportedOSPlatformGuard」而不是普通 bool 字段：只有标了
    /// SupportedOSPlatformGuard 的成员才能让 Roslyn 的平台分析器把 <c>if (!PlatformGuard.IsWindows)</c>
    /// 认成真正的平台分支，否则 CA1416 照样报。
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows
    {
        get { return OperatingSystem.IsWindows(); }
    }

    static readonly object gateObj = new object();

    /// <summary>已经记过的 what。只做去重，不参与任何功能判断。</summary>
    static readonly HashSet<string> announced = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 记一行「本平台跳过」。同一个 <paramref name="what"/> 只记第一次。
    /// </summary>
    /// <param name="what">被跳过的入口，例如 <c>slide-sync:arm</c>、<c>session-engine:bridge</c>。</param>
    /// <param name="log">日志落点（服务里传 <c>Ledger.Log</c>）。传 null 时退回 stderr，
    /// 保证「一行日志」这件事在最裸的环境里也成立。</param>
    public static void Announce(string what, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(what)) return;

        bool first;
        lock (gateObj)
        {
            first = announced.Add(what);
        }
        if (!first) return;

        string line = "platform: skipped (non-windows) " + what;
        if (log != null)
        {
            try { log(line); } catch { }
            return;
        }
        try { Console.Error.WriteLine(line); } catch { }
    }

    /// <summary>测试用：清空去重表，让同一批断言可以重跑。生产代码不调用。</summary>
    public static void ResetAnnouncements()
    {
        lock (gateObj) announced.Clear();
    }

    /// <summary>测试用：已经记过多少个不同的 what。生产代码不调用。</summary>
    public static int AnnouncementCount
    {
        get { lock (gateObj) return announced.Count; }
    }
}
