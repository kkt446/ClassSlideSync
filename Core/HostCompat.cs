using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace ClassSlideSync.Plugin.Core;

/// <summary>
/// 宿主能力集：ClassIsland 2.x 各版本到底提供了哪些我们需要的 API。
///
/// 这个类型刻意**不引用任何 ClassIsland / Avalonia 类型**：全部用字符串 + 反射探测。
/// 原因很直接 —— 插件按 2.1.0.1 的 SDK 编译，IL 里写死了 2.1 才有的类型/成员；
/// 一旦某个成员在旧宿主上不存在，JIT 在编译方法体时就会抛 TypeLoadException，
/// 而这个异常发生在 try 块开始执行之前，方法内部的 try/catch 根本接不住。
/// 所以「能不能用」必须在调用点之前用反射问清楚，不能靠调用点自己兜。
/// </summary>
internal sealed class HostCapabilities
{
    /// <summary>宿主版本（AppBase.Current.AppVersion 优先，其次 ClassIsland.Core 程序集版本，最后 unknown）。</summary>
    public string HostVersion { get; init; } = "unknown";

    /// <summary>版本号从哪来的：AppVersion / assembly / unknown。写进报告，便于排查。</summary>
    public string VersionSource { get; init; } = "unknown";

    /// <summary>是否找到了 ClassIsland.Core 程序集（找不到时下面全是 false）。</summary>
    public bool HostAssemblyFound { get; init; }

    /// <summary>教程中心：ITutorialService + 静态 RegisteredTutorialGroups（2.0.3 起）。</summary>
    public bool Tutorials { get; init; }

    /// <summary>AddSettingsPageGroup(services, id, icon, name) 三参重载（2.0.1 起；本插件未使用）。</summary>
    public bool SettingsPageGroups { get; init; }

    /// <summary>AddAction&lt;T&gt; + ActionBase。</summary>
    public bool Actions { get; init; }

    /// <summary>AddAction&lt;T, TSettingsControl&gt;（带设置抽屉的行动）。</summary>
    public bool ActionsWithSettings { get; init; }

    /// <summary>AddRule(id, name, glyph, handle) 非泛型重载。</summary>
    public bool Rules { get; init; }

    /// <summary>AddTrigger&lt;T&gt; + TriggerBase。</summary>
    public bool Triggers { get; init; }

    /// <summary>AddTrigger&lt;T, TSettingsControl&gt;</summary>
    public bool TriggersWithSettings { get; init; }

    /// <summary>AddComponent&lt;T&gt; + ComponentBase（主界面组件）。</summary>
    public bool Components { get; init; }

    /// <summary>AddComponent&lt;T, TSettingsControl&gt;</summary>
    public bool ComponentsWithSettings { get; init; }

    /// <summary>AddSettingsPage&lt;T&gt; + SettingsPageBase。</summary>
    public bool SettingsPages { get; init; }

    /// <summary>IAutomationService（启动后自检注册结果用）。</summary>
    public bool AutomationService { get; init; }

    /// <summary>IThemeService（卡片跟随主题用）。</summary>
    public bool ThemeService { get; init; }

    /// <summary>IUriNavigationService（跳设置页/行动设置用）。</summary>
    public bool UriNavigation { get; init; }

    /// <summary>ILessonsService 的 CurrentSubject / CurrentClassPlan / CurrentTimeLayoutItem。</summary>
    public bool LessonsService { get; init; }

    /// <summary>IProfileService（教案档案）。</summary>
    public bool ProfileService { get; init; }

    /// <summary>2.1 上应该全绿；测试拿它当「全功能宿主」的基准。</summary>
    public static HostCapabilities AllPresent(string version = "2.1.0.1") => new()
    {
        HostVersion = version,
        VersionSource = "test",
        HostAssemblyFound = true,
        Tutorials = true,
        SettingsPageGroups = true,
        Actions = true,
        ActionsWithSettings = true,
        Rules = true,
        Triggers = true,
        TriggersWithSettings = true,
        Components = true,
        ComponentsWithSettings = true,
        SettingsPages = true,
        AutomationService = true,
        ThemeService = true,
        UriNavigation = true,
        LessonsService = true,
        ProfileService = true,
    };

    /// <summary>一行摘要，写进 app.log，用户贴日志时一眼能看出宿主底细。</summary>
    public string Describe() =>
        "compat: host " + HostVersion + " (" + VersionSource + ")" +
        " tutorials=" + YesNo(Tutorials) +
        " action=" + YesNo(Actions) +
        " action+settings=" + YesNo(ActionsWithSettings) +
        " rule=" + YesNo(Rules) +
        " trigger=" + YesNo(Triggers) +
        " trigger+settings=" + YesNo(TriggersWithSettings) +
        " component=" + YesNo(Components) +
        " settings-page=" + YesNo(SettingsPages) +
        " lessons=" + YesNo(LessonsService) +
        " theme=" + YesNo(ThemeService) +
        " nav=" + YesNo(UriNavigation);

    static string YesNo(bool v) => v ? "yes" : "no";
}

/// <summary>
/// 探测面：把「宿主提供什么」抽象成几个问题，生产环境走 <see cref="ReflectionHostProbe"/>，
/// 离屏断言里可以换成假宿主（模拟 2.0.0.1/2.0.2 这类缺教程系统的版本）。
/// </summary>
internal interface IHostProbe
{
    /// <summary>AppBase.Current.AppVersion 的字符串值；拿不到返回 null。</summary>
    string? AppVersion { get; }

    /// <summary>ClassIsland.Core 程序集版本；找不到宿主返回 null。</summary>
    string? AssemblyVersion { get; }

    /// <summary>宿主程序集里有没有这个类型（全名）。</summary>
    bool HasType(string fullName);

    /// <summary>宿主里有没有这个静态方法（按名字 + 泛型参数个数 + 形参个数匹配，忽略 IServiceCollection 前缀差异）。</summary>
    bool HasStaticMethod(string name, int genericArity, int parameterCount);

    /// <summary>指定类型上有没有公开静态属性。</summary>
    bool HasStaticProperty(string typeFullName, string propertyName);
}

/// <summary>
/// 生产环境的探测面：对当前进程里已经加载的 ClassIsland 程序集做反射。
/// 只读、无副作用，任何一步失败都退化到 false 而不是抛异常。
/// </summary>
internal sealed class ReflectionHostProbe : IHostProbe
{
    readonly Assembly[] _hosts;
    Dictionary<string, List<(int Gen, int Params)>>? _staticMethods;

    ReflectionHostProbe(Assembly[] hosts) => _hosts = hosts;

    /// <summary>在 AppDomain 里找 ClassIsland.* 程序集；一个都没有时返回空探测面（全 false）。</summary>
    public static ReflectionHostProbe ForRunningHost()
    {
        var hosts = new List<Assembly>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name;
            try { name = asm.GetName().Name; } catch { continue; }
            if (name != null && name.StartsWith("ClassIsland.", StringComparison.Ordinal)) hosts.Add(asm);
        }
        return new ReflectionHostProbe(hosts.ToArray());
    }

    /// <summary>测试用：直接给一批程序集（可以是 %TEMP% 里解出来的旧版 ClassIsland.Core）。</summary>
    public static ReflectionHostProbe ForAssemblies(params Assembly[] hosts) => new(hosts ?? Array.Empty<Assembly>());

    public string? AppVersion
    {
        get
        {
            var appBase = FindType("ClassIsland.Core.AppBase");
            if (appBase == null) return null;
            try
            {
                var current = appBase.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (current == null) return null;
                var v = current.GetType().GetProperty("AppVersion", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
                var s = v?.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
        }
    }

    public string? AssemblyVersion
    {
        get
        {
            var core = FindCoreAssembly();
            try { return core?.GetName().Version?.ToString(); } catch { return null; }
        }
    }

    Assembly? FindCoreAssembly()
    {
        foreach (var a in _hosts)
        {
            try { if (string.Equals(a.GetName().Name, "ClassIsland.Core", StringComparison.Ordinal)) return a; } catch { }
        }
        return null;
    }

    public bool HasType(string fullName)
    {
        var t = FindType(fullName);
        return t != null;
    }

    Type? FindType(string fullName)
    {
        foreach (var a in _hosts)
        {
            try
            {
                var t = a.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    public bool HasStaticProperty(string typeFullName, string propertyName)
    {
        var t = FindType(typeFullName);
        if (t == null) return false;
        try
        {
            var p = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            return p?.GetMethod != null;
        }
        catch { return false; }
    }

    public bool HasStaticMethod(string name, int genericArity, int parameterCount)
    {
        _staticMethods ??= IndexStaticMethods();
        if (!_staticMethods.TryGetValue(name, out var list)) return false;
        return list.Any(x => x.Gen == genericArity && x.Params == parameterCount);
    }

    Dictionary<string, List<(int Gen, int Params)>> IndexStaticMethods()
    {
        var index = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (var asm in _hosts)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t == null) continue;
                // 注册方法都是 static class 里的扩展方法；只看静态方法就够了。
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (var m in methods)
                {
                    if (!m.IsStatic || m.IsSpecialName) continue;
                    if (!index.TryGetValue(m.Name, out var list)) index[m.Name] = list = new List<(int, int)>();
                    int gen, pars;
                    try { gen = m.GetGenericArguments().Length; pars = m.GetParameters().Length; }
                    catch { continue; }
                    list.Add((gen, pars));
                }
            }
        }
        return index;
    }
}

/// <summary>
/// 运行时能力探测 + 安全注册 + 去重日志。
///
/// 日志格式（写进 app.log，也写 Debug）：
///   compat: skipped &lt;feature&gt; (host &lt;version&gt; lacks &lt;api&gt;)
/// 同一个 feature 只记一行 —— 注册代码可能被多个入口调到，日志不能刷屏。
/// </summary>
internal static class HostCompat
{
    static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    /// <summary>日志出口。Plugin.Initialize 里换成 Ledger（app.log），测试里换成内存列表。</summary>
    public static Action<string>? Sink { get; set; } = line => Debug.WriteLine("[ClassSlideSync] " + line);

    /// <summary>当前宿主能力集。未初始化时全部为 false（等价于「什么都不注册」而不是崩）。</summary>
    public static HostCapabilities Capabilities { get; private set; } = new();

    /// <summary>已经记过的 feature，测试用来验证「同一 feature 只记一次」。</summary>
    public static IReadOnlyCollection<string> LoggedFeatures => Logged;

    /// <summary>生产入口：探测当前进程里的宿主并记一行能力摘要。</summary>
    public static HostCapabilities Initialize(Action<string>? sink)
    {
        if (sink != null) Sink = sink;
        return Initialize(ReflectionHostProbe.ForRunningHost());
    }

    /// <summary>测试入口：用给定的探测面做一次探测。</summary>
    public static HostCapabilities Initialize(IHostProbe probe)
    {
        Capabilities = Detect(probe);
        Emit("host", Capabilities.Describe());
        return Capabilities;
    }

    /// <summary>测试入口：直接给定能力集（插件注册逻辑的离屏断言走这个入口，不需要真宿主）。</summary>
    public static HostCapabilities Initialize(HostCapabilities caps, Action<string>? sink = null)
    {
        if (sink != null) Sink = sink;
        Capabilities = caps;
        Emit("host", caps.Describe());
        return caps;
    }

    /// <summary>纯函数：把探测面的答案翻译成能力集。离屏断言直接调它（所以是 internal 而不是 private）。</summary>
    internal static HostCapabilities Detect(IHostProbe probe)
    {
        string? appVersion = null, asmVersion = null;
        try { appVersion = probe.AppVersion; } catch { }
        try { asmVersion = probe.AssemblyVersion; } catch { }

        string version = !string.IsNullOrWhiteSpace(appVersion) ? appVersion!
            : !string.IsNullOrWhiteSpace(asmVersion) ? asmVersion!
            : "unknown";
        string source = !string.IsNullOrWhiteSpace(appVersion) ? "AppBase.Current.AppVersion"
            : !string.IsNullOrWhiteSpace(asmVersion) ? "ClassIsland.Core assembly"
            : "unknown";

        bool Has(string type) => Safe(() => probe.HasType(type));
        bool HasStatic(string type, string prop) => Safe(() => probe.HasStaticProperty(type, prop));
        bool HasMethod(string name, int gen, int pars) => Safe(() => probe.HasStaticMethod(name, gen, pars));

        bool actionsBase = Has("ClassIsland.Core.Abstractions.Automation.ActionBase");
        bool triggerBase = Has("ClassIsland.Core.Abstractions.Automation.TriggerBase");
        bool componentBase = Has("ClassIsland.Core.Abstractions.Controls.ComponentBase");
        bool settingsPageBase = Has("ClassIsland.Core.Abstractions.Controls.SettingsPageBase");

        bool actions = actionsBase && HasMethod("AddAction", 1, 1);
        bool triggers = triggerBase && HasMethod("AddTrigger", 1, 1);
        bool components = componentBase && HasMethod("AddComponent", 1, 1);

        return new HostCapabilities
        {
            HostVersion = version,
            VersionSource = source,
            HostAssemblyFound = Has("ClassIsland.Core.AppBase") || Has("ClassIsland.Core.Abstractions.PluginBase"),
            Tutorials = Has("ClassIsland.Core.Abstractions.Services.ITutorialService")
                        && HasStatic("ClassIsland.Core.Abstractions.Services.ITutorialService", "RegisteredTutorialGroups"),
            SettingsPageGroups = HasMethod("AddSettingsPageGroup", 0, 3),
            Actions = actions,
            ActionsWithSettings = actions && HasMethod("AddAction", 2, 1),
            Rules = HasMethod("AddRule", 0, 5),
            Triggers = triggers,
            TriggersWithSettings = triggers && HasMethod("AddTrigger", 2, 1),
            Components = components,
            ComponentsWithSettings = components && HasMethod("AddComponent", 2, 1),
            SettingsPages = settingsPageBase && HasMethod("AddSettingsPage", 1, 1),
            AutomationService = Has("ClassIsland.Core.Abstractions.Services.IAutomationService"),
            ThemeService = Has("ClassIsland.Core.Abstractions.Services.IThemeService"),
            UriNavigation = Has("ClassIsland.Core.Abstractions.Services.IUriNavigationService"),
            LessonsService = Has("ClassIsland.Core.Abstractions.Services.ILessonsService"),
            ProfileService = Has("ClassIsland.Core.Abstractions.Services.IProfileService"),
        };
    }

    static bool Safe(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    /// <summary>
    /// 注册一个功能。available=false 时只记一行 skipped；注册体抛异常时记一行 failed 并吞掉。
    /// 绝不把异常抛给宿主 —— Initialize 抛异常 = 插件加载失败 = 用户看到白屏。
    /// </summary>
    public static bool TryRun(string feature, string api, bool available, Action action)
    {
        if (!available)
        {
            Skip(feature, api);
            return false;
        }

        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Emit(feature, "compat: failed " + feature + " (host " + Capabilities.HostVersion + "): " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>记一行「宿主缺这个东西」。同一 feature 只记一次。</summary>
    public static void Skip(string feature, string api) =>
        Emit(feature, "compat: skipped " + feature + " (host " + Capabilities.HostVersion + " lacks " + api + ")");

    /// <summary>直接记一行（同样按 feature 去重），给无法用 TryRun 描述的场合用。</summary>
    public static void Log(string feature, string line) => Emit(feature, line);

    static void Emit(string feature, string line)
    {
        lock (Logged)
        {
            if (!Logged.Add(feature)) return;
        }
        try { Sink?.Invoke(line); } catch { }
    }

    /// <summary>测试用：清空去重表与能力集（Sink 保持不变，方便断言捕获日志）。</summary>
    public static void ResetForTests()
    {
        lock (Logged) Logged.Clear();
        Capabilities = new HostCapabilities();
    }
}
