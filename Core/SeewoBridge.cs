using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// One reading of 希沃白板5's MCP service. Values are deliberately "best effort":
    /// a failed call leaves Showing=false and puts the reason in Diag, but never throws.
    /// </summary>
    public sealed class SeewoState
    {
        public bool Found;              // the MCP server answered at least one request
        public bool Showing;            // shellContext.workingMode says Display
        public string Title = "";       // courseware name
        public int Current;             // 1-based page, 0 when unknown
        public int SlideCount;          // 0 when unknown
        public string WorkingMode = ""; // raw workingMode ("Display" / "Edit" / ...)
        public string ShellId = "";     // en_find_shells[].id, needed by the other tools
        public string Endpoint = "";    // http://127.0.0.1:7916/mcp
        public string Diag = "";        // one line, already prefixed with "seewo:"
        public bool Running;            // EasiNote process is alive
    }

    /// <summary>
    /// Reads 希沃白板5 (EasiNote) through its in-process MCP server.
    ///
    /// This is a pure external client: it never loads an EasiNote plugin, never starts
    /// EasiNote, and never writes to its install folder. The server is discovered from
    /// %APPDATA%\Seewo\SeewoAIVoiceAssistant\McpServers\EasiNote.mcp.json first, then by
    /// probing a small candidate port list. Nothing is hard-coded as "the" port: a failed
    /// endpoint is forgotten and the whole discovery is repeated later.
    ///
    /// The protocol is MCP's "streamable HTTP" JSON-RPC transport:
    ///   initialize -> notifications/initialized -> tools/list -> tools/call
    /// with Accept: application/json, text/event-stream and Mcp-Session-Id on later calls.
    /// Responses may be plain JSON or SSE; both are accepted.
    ///
    /// All network calls are capped at 1.5 s. Every public method swallows its exceptions
    /// and reports through the return value / Diag instead, because the engine thread must
    /// never die because a whiteboard was updating itself.
    /// </summary>
    public sealed class SeewoBridge : IDisposable
    {
        public const string KeyPrefix = "希沃:";

        const string ProtocolVersion = "2024-11-05";
        const int RequestTimeoutMs = 1500;
        const int ProbeTtlMs = 30000;      // a failed discovery is not repeated for 30 s
        const int RetryProbeMs = 1000;     // a working endpoint that failed is re-probed soon
        const int MaxBodyBytes = 512 * 1024;

        static readonly int[] CandidatePorts = { 7916, 7917, 13247, 19463, 27733 };

        static readonly HttpClient Http = CreateHttp();

        readonly Ledger ledger;
        readonly CancellationTokenSource disposed = new CancellationTokenSource();

        string baseUrl = "";
        string sessionId = "";
        bool initialized;
        bool disposedFlag;
        readonly List<ToolDef> tools = new List<ToolDef>();

        DateTime nextProbeAt = DateTime.MinValue;
        int nextId;
        string lastDiag = "";
        string lastError = "";
        string lastSeewoTitle = "";
        string lastShellId = "";

        public SeewoBridge(Ledger ledger)
        {
            this.ledger = ledger;
        }

        public bool Diag { get; set; }

        // 状态缓存：GetState() 被引擎的高频循环调用，见方法头的节流说明。
        SeewoState cachedState;
        DateTime cachedAt = DateTime.MinValue;

        // 上一次成功的页数：MCP 短暂丢壳时用来兜住卡片，别让"希沃闪了一下"变成"课件关了"。
        int lastSlideCount;

        /// <summary>
        /// Ask EasiNote for its current state. Never throws; on any failure the returned
        /// object simply has Showing=false and a human-readable Diag.
        /// </summary>
        public SeewoState GetState()
        {
            // 节流：一次取状态要发 3 个 HTTP 请求（en_find_shells / courseware_info /
            // slide_info），而引擎每 250 毫秒走一步、有 7 个地方会来问希沃。不拦一下就是
            // 每秒 12 个请求打在本机 MCP 上，白烧 CPU 还把日志刷满。500 毫秒的缓存对翻页
            // 来说是看不出来的延迟，只有"课件刚打开"那一瞬间可能慢半拍。
            var now = DateTime.UtcNow;
            if (cachedState != null && (now - cachedAt).TotalMilliseconds < 500) return cachedState;
            var st = GetStateUncached();
            cachedState = st;
            cachedAt = DateTime.UtcNow;
            return st;
        }

        /// <summary>一次真正的取数：可能发 3 个 HTTP 请求，任何失败都折成 Diag。</summary>
        SeewoState GetStateUncached()
        {
            var st = new SeewoState();
            if (disposedFlag) { st.Diag = "seewo: client disposed"; return st; }

            try
            {
                st.Running = LooksRunning();
                if (!EnsureSessionForCall())
                {
                    st.Diag = "seewo: no mcp server";
                    return st;
                }

                st.Found = true;
                st.Endpoint = baseUrl;
                Interlocked.Exchange(ref lastMcpSeenTicks, DateTime.UtcNow.Ticks);

                // 1) en_find_shells gives us the shell id and the authoritative workingMode.
                //    searchingMode=All so edit-mode courseware is visible too; a Display shell
                //    wins over an edit shell when the teacher has several tabs open.
                var shells = CallTool("en_find_shells", new Dictionary<string, object> { ["searchingMode"] = "All" });
                if (shells.Ok)
                {
                    using (var doc = JsonDocument.Parse(shells.Body)) ParseShells(doc.RootElement, st);
                }
                else
                {
                    HandleCallFailure(shells);   // not fatal by itself; the info call may still work
                }
                if (st.ShellId.Length > 0) lastShellId = st.ShellId;
                else if (lastShellId.Length > 0) st.ShellId = lastShellId;   // 壳重建时别把自己认识的壳丢了

                // 2) Title / slide count / shellContext.workingMode. shellId is required in the
                //    measured build; when discovery somehow missed it, try without and let the
                //    server decide (never assume the call succeeded).
                var infoArgs = new Dictionary<string, object>();
                if (st.ShellId.Length > 0) infoArgs["shellId"] = st.ShellId;
                var info = CallTool("get_current_courseware_info", infoArgs);
                if (info.Ok)
                {
                    using (var doc = JsonDocument.Parse(info.Body)) ParseCourseware(doc.RootElement, st);
                }
                else
                {
                    HandleCallFailure(info);
                    if (st.Title.Length == 0)
                    {
                        // MCP 偶尔会短暂丢掉 shell（实测：退出授课的一瞬间 en_find_shells 返回空、
                        // get_current_courseware_info 报"没有绑定 shellId"）。这时候不能当成
                        // "课件关了"，否则卡片被清空、这堂课也没法收尾 —— 沿用上一次的课件名与页数。
                        if (lastSeewoTitle.Length > 0 && !shells.Ok)
                        {
                            st.Title = lastSeewoTitle;
                            st.SlideCount = lastSlideCount;
                            st.ShellId = lastShellId;
                            st.Diag = "seewo: " + Short(info.Error);
                        }
                        else
                        {
                            st.Diag = "seewo: " + Short(info.Error);
                            return st;
                        }
                    }
                }
                if (st.ShellId.Length > 0) lastShellId = st.ShellId;

                // 3) Current page. get_slide_info returns Markdown; the measured line is
                //    "- 序号(1-base): 4" plus "- 工作模式: Edit|Display".
                var slideArgs = new Dictionary<string, object>();
                if (st.ShellId.Length > 0) slideArgs["shellId"] = st.ShellId;
                var slide = CallTool("get_slide_info", slideArgs);
                if (slide.Ok)
                {
                    using (var doc = JsonDocument.Parse(slide.Body)) ParseSlideInfo(doc.RootElement, st);
                }
                else
                {
                    // A missing get_slide_info is not fatal: title/count from the first call
                    // still let the card show a useful state.
                    HandleCallFailure(slide);
                }

                lastError = "";
                Normalize(st);

                if (st.Title.Length == 0)
                {
                    st.Showing = false;
                    st.Diag = "seewo: no courseware open (" + Mode(st) + ")";
                    LogDiag(st.Diag);
                    return st;
                }

                lastSeewoTitle = st.Title;
                if (st.SlideCount > 0) lastSlideCount = st.SlideCount;
                if (st.Showing)
                {
                    // 实测要求的格式：seewo: <课件名> page 4/10 (Display)
                    st.Diag = "seewo: " + st.Title + " page " + (st.Current > 0 ? st.Current.ToString() : "?")
                        + "/" + (st.SlideCount > 0 ? st.SlideCount.ToString() : "?")
                        + " (" + Mode(st) + ")";
                }
                else
                {
                    st.Diag = "seewo: " + st.Title + " idle "
                        + (st.SlideCount > 0 ? st.SlideCount.ToString() : "?")
                        + " pages (" + Mode(st) + ")";
                }
                LogDiag(st.Diag);
            }
            catch (Exception ex)
            {
                DropEndpoint("state failed");
                LogError("seewo: state failed: " + ex.GetType().Name + " " + Short(ex.Message));
                st.Diag = "seewo: no mcp server";
            }

            return st;
        }

        /// <summary>
        /// Try to jump the current EasiNote show. Returns "" on success, or a short Chinese
        /// message suitable for the card's status line. Never throws.
        /// </summary>
        public string GotoSlide(int index)
        {
            if (index <= 0) return "页码无效";
            if (disposedFlag) return "希沃白板连接已关闭";

            try
            {
                if (!EnsureSessionForCall()) return "希沃白板没有响应";
                if (tools.Count == 0) LoadTools();

                // 台账里只有「希沃:课件名」，没有 shellId；跳页前先读一次当前壳。
                // 这里必须绕过 500ms 缓存：缓存里很可能正是“上一次没找到壳”的结论，
                // 老师刚在希沃里点开课件，缓存就会让跳页继续报“没有绑定 shellId”。
                if (lastShellId.Length == 0)
                {
                    var cur = GetStateUncached();
                    if (cur.ShellId.Length > 0) lastShellId = cur.ShellId;
                }
                if (lastShellId.Length == 0)
                {
                    // 希沃的跳页接口只能作用在“已经打开的课件”上（shellId 来自 en_find_shells），
                    // 没有能力替用户启动一份云课件。与其把 MCP 的英文报错甩给老师，不如说清楚。
                    ledger.Log("seewo: goto ignored -- no shell open");
                    return "希沃里还没打开这份课件：先在希沃白板里打开它，再点跳转";
                }

                // 用户 2026-10-18：在希沃的编辑视图（还没进全屏授课）里点卡片跳转，
                // 结果只切了页、人还留在编辑界面。先让希沃进授课视图，再跳页。
                var before = GetState();
                if (before.Found && before.Title.Length > 0 && !before.Showing
                    && tools.Exists(t => t.Name == "en_goto_app_ui"))
                {
                    var ui = CallTool("en_goto_app_ui", new Dictionary<string, object> { ["ui"] = "Display" });
                    if (ui.Ok)
                    {
                        System.Threading.Thread.Sleep(400);
                        cachedAt = DateTime.MinValue;   // 让下一次 GetState 读到真正的授课态
                        ledger.Log("seewo: enter display before jump");
                    }
                }

                ToolDef tool = FindJumpTool();
                if (tool == null) return "希沃白板没有提供跳页接口";

                var args = BuildJumpArgs(tool, index, lastShellId);
                if (args == null) return "希沃白板的跳页接口参数不兼容";

                var r = CallTool(tool.Name, args);
                if (!r.Ok)
                {
                    HandleCallFailure(r);
                    return "希沃跳页失败：" + Short(r.Error);
                }
                return "";
            }
            catch (Exception ex)
            {
                return "希沃跳页出错：" + Short(ex.Message);
            }
        }

        // ------------------------------------------------------------------ discovery

        bool EnsureEndpoint()
        {
            if (baseUrl.Length > 0) return true;
            if (DateTime.UtcNow < nextProbeAt) return false;

            nextProbeAt = DateTime.UtcNow.AddMilliseconds(ProbeTtlMs);
            return Discover();
        }

        bool Discover()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cfg in ReadConfigEndpoints())
            {
                if (cfg.Url.Length == 0 || !seen.Add(cfg.Url)) continue;
                if (TryEndpoint(cfg.Url)) return true;
            }

            for (int i = 0; i < CandidatePorts.Length; i++)
            {
                string url = "http://127.0.0.1:" + CandidatePorts[i] + "/mcp";
                if (!seen.Add(url)) continue;
                if (TryEndpoint(url)) return true;
            }

            nextProbeAt = DateTime.UtcNow.AddMilliseconds(ProbeTtlMs);
            Log("seewo: no mcp server");
            return false;
        }

        bool TryEndpoint(string url)
        {
            baseUrl = url;
            sessionId = "";
            initialized = false;
            tools.Clear();

            if (Initialize())
            {
                Log("seewo: mcp at " + HostPort(url));
                lastError = "";
                return true;
            }

            baseUrl = "";
            sessionId = "";
            initialized = false;
            return false;
        }

        void DropEndpoint(string why)
        {
            baseUrl = "";
            sessionId = "";
            initialized = false;
            tools.Clear();
            nextProbeAt = DateTime.UtcNow.AddMilliseconds(RetryProbeMs);
            LogError("seewo: " + Short(why));
        }

        /// <summary>
        /// EasiNote's pid/url file. It is only a hint: the service may be starting or may
        /// have crashed, so every URL from here is still handshaken before it is trusted.
        /// </summary>
        static List<McpConfig> ReadConfigEndpoints()
        {
            var list = new List<McpConfig>();
            try
            {
                string appData = Environment.GetEnvironmentVariable("APPDATA");
                if (string.IsNullOrEmpty(appData))
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return list;

                string path = Path.Combine(appData, "Seewo", "SeewoAIVoiceAssistant", "McpServers", "EasiNote.mcp.json");
                if (!File.Exists(path)) return list;

                string text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Trim().Length == 0) return list;
                using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }))
                {
                    WalkConfig(doc.RootElement, list, 0);
                }
            }
            catch
            {
                // A half-written config while EasiNote starts up must be ignored, not logged
                // every two seconds. The candidate ports still get probed.
            }
            return list;
        }

        static void WalkConfig(JsonElement e, List<McpConfig> list, int depth)
        {
            if (depth > 8) return;

            if (e.ValueKind == JsonValueKind.Object)
            {
                string url = "";
                string proc = "";
                int pid = 0;

                foreach (var p in e.EnumerateObject())
                {
                    string n = p.Name;
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        string s = p.Value.GetString() ?? "";
                        if (IsUrlName(n)) url = s;
                        else if (n.Equals("processName", StringComparison.OrdinalIgnoreCase)
                              || n.Equals("process", StringComparison.OrdinalIgnoreCase)) proc = s;
                        else if (n.Equals("pid", StringComparison.OrdinalIgnoreCase))
                        {
                            int v;
                            if (int.TryParse(s, out v)) pid = v;
                        }
                    }
                    else if (p.Value.ValueKind == JsonValueKind.Number && n.Equals("pid", StringComparison.OrdinalIgnoreCase))
                    {
                        int v;
                        if (p.Value.TryGetInt32(out v)) pid = v;
                    }

                    WalkConfig(p.Value, list, depth + 1);
                }

                string normalized = NormalizeUrl(url);
                if (normalized.Length > 0)
                {
                    bool dup = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].Url.Equals(normalized, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    }
                    if (!dup) list.Add(new McpConfig { Url = normalized, Pid = pid, ProcessName = proc });
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) WalkConfig(item, list, depth + 1);
            }
        }

        static bool IsUrlName(string n)
        {
            return n.Equals("url", StringComparison.OrdinalIgnoreCase)
                || n.Equals("serverUrl", StringComparison.OrdinalIgnoreCase)
                || n.Equals("endpoint", StringComparison.OrdinalIgnoreCase)
                || n.Equals("address", StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.IndexOf("://", StringComparison.Ordinal) < 0) raw = "http://" + raw;

            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)) return "";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
            if (!uri.IsLoopback) return "";   // never talk to a remote "MCP" server

            var b = new UriBuilder(uri);
            if (string.IsNullOrEmpty(b.Path) || b.Path == "/") b.Path = "/mcp";
            return b.Uri.AbsoluteUri;
        }

        static string HostPort(string url)
        {
            try
            {
                var u = new Uri(url);
                return u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port;
            }
            catch { return url; }
        }

        // ------------------------------------------------------------------ process check

        static readonly object RunGate = new object();
        static DateTime runCheckedAt = DateTime.MinValue;
        static bool runCached;
        // Some hosts (and our own sandboxed test process) deny cross-process enumeration,
        // even though Get-Process in a shell sees EasiNote. A recently successful MCP call
        // is then the strongest available "it is running" signal; it self-expires, so a
        // closed whiteboard still flips to false within a few seconds.
        static long lastMcpSeenTicks;

        /// <summary>
        /// Cheap, non-blocking "is EasiNote running" for the 2-second scheduler. Uses the
        /// pid/processName from the config file when possible, then a small name list.
        /// </summary>
        public static bool LooksRunning()
        {
            lock (RunGate)
            {
                if ((DateTime.UtcNow - runCheckedAt).TotalSeconds < 3) return runCached;
                runCheckedAt = DateTime.UtcNow;
                runCached = DetectRunning();
                if (!runCached)
                {
                    long seen = Interlocked.Read(ref lastMcpSeenTicks);
                    if (seen != 0 && (DateTime.UtcNow.Ticks - seen) < TimeSpan.TicksPerSecond * 5)
                        runCached = true;
                }
                return runCached;
            }
        }

        static bool DetectRunning()
        {
            try
            {
                foreach (var cfg in ReadConfigEndpoints())
                {
                    if (cfg.Pid > 0)
                    {
                        try
                        {
                            using (var p = Process.GetProcessById(cfg.Pid))
                            {
                                if (!p.HasExited) return true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            string[] names = { "EasiNote", "EasiNote5", "SeewoEasiNote", "SeewoWhiteboard", "ENB5", "ENBoard" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var procs = Process.GetProcessesByName(names[i]);
                    try
                    {
                        for (int j = 0; j < procs.Length; j++)
                        {
                            if (procs[j] != null && !procs[j].HasExited) return true;
                        }
                    }
                    finally
                    {
                        for (int j = 0; j < procs.Length; j++) { try { procs[j].Dispose(); } catch { } }
                    }
                }
                catch { }
            }
            return false;
        }

        public static bool IsSeewoKey(string key)
        {
            return !string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal);
        }

        public static string KeyFor(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            string t = title.Trim();
            return t.StartsWith(KeyPrefix, StringComparison.Ordinal) ? t : KeyPrefix + t;
        }

        public static string DisplayName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return IsSeewoKey(key) ? key.Substring(KeyPrefix.Length) : key;
        }

        // ------------------------------------------------------------------ MCP transport

        bool EnsureSessionForCall()
        {
            if (disposedFlag) return false;
            if (baseUrl.Length == 0 && !EnsureEndpoint()) return false;
            if (initialized) return true;
            if (!Initialize())
            {
                DropEndpoint("initialize failed");
                return false;
            }
            return true;
        }

        bool Initialize()
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = NextId(),
                    ["method"] = "initialize",
                    ["params"] = new Dictionary<string, object>
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new Dictionary<string, object>(),
                        ["clientInfo"] = new Dictionary<string, object>
                        {
                            ["name"] = "ClassSlideSync",
                            ["version"] = "1.0"
                        }
                    }
                };

                // baseUrl 可能刚被另一个线程的 DropEndpoint 清掉（引擎有多个地方并发来问希沃，
                // 一个超时正在 DropEndpoint，另一个已经走到这里）。拿局部变量再判一次：
                // 否则 Post("") 会抛 InvalidOperationException，日志里一串 fake 错误很误导。
                var url = baseUrl;
                if (url.Length == 0) return false;
                var r = Post(url, JsonSerializer.Serialize(payload), false);
                if (!r.Ok)
                {
                    LogError("seewo: initialize post failed: " + Short(r.Error));
                    return false;
                }
                if (r.SessionId.Length > 0) sessionId = r.SessionId;

                bool hasResult = false;
                using (var doc = JsonDocument.Parse(r.Body))
                {
                    JsonElement result;
                    hasResult = doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("result", out result);
                }
                if (!hasResult)
                {
                    LogError("seewo: initialize response has no result: " + Short(r.Body));
                    return false;
                }

                initialized = true;

                // Notification: no id, server normally answers 202 with an empty body.
                var note = new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/initialized"
                };
                try { Post(baseUrl, JsonSerializer.Serialize(note), true); } catch { }

                LoadTools();
                return true;
            }
            catch (Exception ex)
            {
                LogError("seewo: initialize failed: " + Short(ex.Message));
                return false;
            }
        }

        void LoadTools()
        {
            if (!initialized) return;
            var r = CallRpc("tools/list", null, false);
            if (!r.Ok)
            {
                LogError("seewo: tools/list: " + Short(r.Error));
                return;
            }

            try
            {
                using (var doc = JsonDocument.Parse(r.Body))
                {
                    JsonElement result;
                    if (doc.RootElement.ValueKind != JsonValueKind.Object
                        || !doc.RootElement.TryGetProperty("result", out result)
                        || result.ValueKind != JsonValueKind.Object) return;

                    JsonElement arr;
                    if (!result.TryGetProperty("tools", out arr) || arr.ValueKind != JsonValueKind.Array) return;

                    tools.Clear();
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        JsonElement nameEl;
                        if (!item.TryGetProperty("name", out nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                        string name = nameEl.GetString();
                        if (string.IsNullOrEmpty(name)) continue;

                        string schema = "";
                        JsonElement schemaEl;
                        if (item.TryGetProperty("inputSchema", out schemaEl)) schema = schemaEl.GetRawText();
                        tools.Add(new ToolDef { Name = name, SchemaJson = schema });
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("seewo: tools/list parse failed: " + Short(ex.Message));
            }
        }

        RpcResult CallTool(string name, Dictionary<string, object> args)
        {
            var prms = new Dictionary<string, object>
            {
                ["name"] = name,
                ["arguments"] = args ?? new Dictionary<string, object>()
            };

            var r = CallRpc("tools/call", prms, true);
            if (!r.Ok) return r;

            try
            {
                using (var doc = JsonDocument.Parse(r.Body))
                {
                    JsonElement result;
                    string error;
                    if (!TryGetResult(doc.RootElement, out result, out error))
                        return RpcResult.Fail(false, error, 200);

                    JsonElement isError;
                    if (result.ValueKind == JsonValueKind.Object
                        && result.TryGetProperty("isError", out isError)
                        && isError.ValueKind == JsonValueKind.True)
                    {
                        string text = CollectText(result).Trim();
                        return RpcResult.Fail(false, text.Length > 0 ? text : "tool returned isError", 200);
                    }

                    return RpcResult.Success(result.GetRawText());
                }
            }
            catch (Exception ex)
            {
                return RpcResult.Fail(false, "bad tool response: " + Short(ex.Message), 200);
            }
        }

        RpcResult CallRpc(string method, object prms, bool allowReinit)
        {
            var payload = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = NextId(),
                ["method"] = method
            };
            if (prms != null) payload["params"] = prms;
            string json = JsonSerializer.Serialize(payload);

            var r = Post(baseUrl, json, true);
            if (!r.Ok && allowReinit && (r.Transport || r.Status == 400 || r.Status == 404 || LooksLikeSessionError(r.Body)))
            {
                initialized = false;
                sessionId = "";
                if (Initialize()) r = Post(baseUrl, json, true);
            }
            return r;
        }

        RpcResult Post(string url, string payload, bool withSession)
        {
            if (disposedFlag) return RpcResult.Fail(true, "client disposed");

            HttpRequestMessage req = null;
            try
            {
                req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                req.Headers.TryAddWithoutValidation("User-Agent", "ClassSlideSync/1.0 SeewoMCP");
                // 注意：不要在 initialize 上带 MCP-Protocol-Version。实测 EasiNote 5.2.4
                // 会用它去校验协议版本并直接 400 "Unsupported protocol version"，
                // 而它协商出来的版本（2025-11-25）又比我们请求的 2024-11-05 新。
                // 会话建立后服务端也不要求这个头，带 Mcp-Session-Id 就够了。
                if (withSession && sessionId.Length > 0)
                    req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(disposed.Token))
                {
                    cts.CancelAfter(RequestTimeoutMs);

                    HttpResponseMessage resp;
                    try
                    {
                        resp = Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                            .GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return RpcResult.Fail(true, "timeout after " + RequestTimeoutMs + " ms");
                    }
                    catch (Exception ex)
                    {
                        return RpcResult.Fail(true, ex.GetType().Name + ": " + Short(ex.Message));
                    }

                    using (resp)
                    {
                        int status = (int)resp.StatusCode;
                        string sess = "";
                        IEnumerable<string> vals;
                        if (resp.Headers.TryGetValues("Mcp-Session-Id", out vals))
                        {
                            foreach (var v in vals) { sess = v; break; }
                        }

                        string body;
                        try
                        {
                            body = resp.Content == null
                                ? ""
                                : resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            return RpcResult.Fail(true, "timeout reading body after " + RequestTimeoutMs + " ms");
                        }
                        catch (Exception ex)
                        {
                            return RpcResult.Fail(true, "read failed: " + Short(ex.Message));
                        }

                        if (body.Length > MaxBodyBytes) body = body.Substring(0, MaxBodyBytes);
                        string mediaType = "";
                        try { mediaType = resp.Content.Headers.ContentType.MediaType ?? ""; } catch { }

                        var result = new RpcResult
                        {
                            Status = status,
                            SessionId = sess,
                            Body = ExtractJson(body, mediaType)
                        };

                        if (status >= 200 && status < 300)
                        {
                            result.Ok = true;
                            return result;
                        }

                        result.Error = "http " + status + (result.Body.Length > 0 ? ": " + Short(result.Body) : "");
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                return RpcResult.Fail(true, ex.GetType().Name + ": " + Short(ex.Message));
            }
            finally
            {
                if (req != null) { try { req.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// Streamable HTTP servers answer either application/json or text/event-stream.
        /// For SSE, concatenate each event's data: lines and return the first JSON object.
        /// </summary>
        static string ExtractJson(string body, string mediaType)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string trimmed = body.Trim();
            bool sse = (!string.IsNullOrEmpty(mediaType)
                        && mediaType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
                       || trimmed.StartsWith("data:", StringComparison.Ordinal)
                       || trimmed.IndexOf("\ndata:", StringComparison.Ordinal) >= 0;
            if (!sse) return trimmed;

            var events = new List<string>();
            var sb = new StringBuilder();
            string[] lines = body.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line.Substring(5).TrimStart());
                }
                else if (line.Length == 0)
                {
                    if (sb.Length > 0) { events.Add(sb.ToString()); sb.Clear(); }
                }
            }
            if (sb.Length > 0) events.Add(sb.ToString());

            for (int i = 0; i < events.Count; i++)
            {
                string ev = events[i].Trim();
                if (ev.StartsWith("{", StringComparison.Ordinal)
                    || ev.StartsWith("[", StringComparison.Ordinal)) return ev;
            }
            if (events.Count > 0) return events[events.Count - 1].Trim();
            return trimmed;   // server claimed SSE but sent plain JSON
        }

        static bool LooksLikeSessionError(string body)
        {
            return !string.IsNullOrEmpty(body)
                && body.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool TryGetResult(JsonElement root, out JsonElement result, out string error)
        {
            result = default(JsonElement);
            error = "";
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "response is not a JSON object";
                return false;
            }

            JsonElement err;
            if (root.TryGetProperty("error", out err))
            {
                string msg = "";
                JsonElement m;
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out m)
                    && m.ValueKind == JsonValueKind.String) msg = m.GetString();
                if (string.IsNullOrEmpty(msg)) msg = err.GetRawText();
                error = "rpc error: " + Short(msg);
                return false;
            }

            if (!root.TryGetProperty("result", out result))
            {
                error = "no result in response";
                return false;
            }
            return true;
        }

        void HandleCallFailure(RpcResult r)
        {
            if (r.Transport || r.Status == 400 || r.Status == 404) DropEndpoint("request failed");
            LogError("seewo: " + Short(r.Error));
        }

        int NextId() { return Interlocked.Increment(ref nextId); }

        // ------------------------------------------------------------------ parsing

        static void ParseShells(JsonElement root, SeewoState st)
        {
            // Measured shape: result.structuredContent.shells = [{id, coursewareName, workingMode}].
            // Parse that first; the generic walk below is only a fallback for other builds.
            string pickId = "";
            string pickName = "";
            string pickMode = "";

            JsonElement sc;
            if (TryStructured(root, out sc))
            {
                JsonElement arr;
                if (sc.TryGetProperty("shells", out arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    string firstId = "", firstName = "", firstMode = "";
                    bool haveFirst = false;
                    foreach (var sh in arr.EnumerateArray())
                    {
                        if (sh.ValueKind != JsonValueKind.Object) continue;
                        string id = Str(sh, "id");
                        string name = Clean(Str(sh, "coursewareName"));
                        string mode = Str(sh, "workingMode");
                        if (name.Length == 0) continue;

                        if (!haveFirst)
                        {
                            firstId = id; firstName = name; firstMode = mode; haveFirst = true;
                        }
                        if (pickId.Length == 0 && ModeIsDisplay(mode))
                        {
                            pickId = id; pickName = name; pickMode = mode;
                        }
                    }
                    if (pickId.Length == 0 && firstId.Length > 0)
                    {
                        pickId = firstId; pickName = firstName; pickMode = firstMode;
                    }

                    if (pickName.Length > 0)
                    {
                        st.ShellId = pickId;
                        if (st.Title.Length == 0) st.Title = pickName;
                        if (st.WorkingMode.Length == 0) st.WorkingMode = pickMode;
                        if (ModeIsDisplay(pickMode)) st.Showing = true;
                        return;
                    }
                }
            }

            // Fallback: any coursewareName anywhere in the payload.
            string first = "";
            WalkFields(root, delegate (string name, JsonElement value)
            {
                if (value.ValueKind != JsonValueKind.String) return;
                if (name.Equals("coursewareName", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("coursewareTitle", StringComparison.OrdinalIgnoreCase))
                {
                    string s = Clean(value.GetString());
                    if (s.Length > 0 && first.Length == 0) first = s;
                }
            }, 0);

            if (st.Title.Length == 0 && first.Length > 0) st.Title = first;
        }

        static void ParseCourseware(JsonElement root, SeewoState st)
        {
            // shellContext carries the display flag and a second copy of the title/id.
            JsonElement sc;
            if (TryStructured(root, out sc))
            {
                JsonElement ctx;
                if (sc.TryGetProperty("shellContext", out ctx) && ctx.ValueKind == JsonValueKind.Object)
                {
                    string id = Str(ctx, "id");
                    string name = Clean(Str(ctx, "coursewareName"));
                    string mode = Str(ctx, "workingMode");
                    if (st.ShellId.Length == 0 && id.Length > 0) st.ShellId = id;
                    if (st.Title.Length == 0 && name.Length > 0) st.Title = name;
                    if (st.WorkingMode.Length == 0 && mode.Length > 0) st.WorkingMode = mode;
                }
            }

            string text = CollectText(root);
            WalkFields(root, delegate (string name, JsonElement value) { ApplyField(name, value, st); }, 0);

            string trimmed = text.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(trimmed))
                        WalkFields(doc.RootElement, delegate (string name, JsonElement value) { ApplyField(name, value, st); }, 0);
                }
                catch { }
            }
            else
            {
                ParseMarkdown(text, st, true);
            }
            Normalize(st);
        }

        static void ParseSlideInfo(JsonElement root, SeewoState st)
        {
            string text = CollectText(root);
            WalkFields(root, delegate (string name, JsonElement value) { ApplyField(name, value, st); }, 0);

            string trimmed = text.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(trimmed))
                        WalkFields(doc.RootElement, delegate (string name, JsonElement value) { ApplyField(name, value, st); }, 0);
                }
                catch { }
            }
            else
            {
                ParseMarkdown(text, st, false);
            }
            Normalize(st);
        }

        /// <summary>result.structuredContent, when the server sent one.</summary>
        static bool TryStructured(JsonElement result, out JsonElement structured)
        {
            structured = default(JsonElement);
            if (result.ValueKind != JsonValueKind.Object) return false;
            return result.TryGetProperty("structuredContent", out structured)
                && structured.ValueKind == JsonValueKind.Object;
        }

        static string Str(JsonElement obj, string name)
        {
            JsonElement v;
            if (!obj.TryGetProperty(name, out v)) return "";
            if (v.ValueKind == JsonValueKind.String) return (v.GetString() ?? "").Trim();
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            return "";
        }

        static void ApplyField(string name, JsonElement value, SeewoState st)
        {
            if (string.IsNullOrEmpty(name)) return;
            string n = name.ToLowerInvariant();

            if (value.ValueKind == JsonValueKind.String)
            {
                string s = value.GetString();
                if (string.IsNullOrEmpty(s)) return;
                s = s.Trim();

                if (n == "title" || n == "coursewarename" || n == "coursewaretitle" || n == "coursename")
                {
                    if (st.Title.Length == 0) st.Title = Clean(s);
                }
                else if (n == "workingmode" || n == "workmode" || n == "shellworkingmode")
                {
                    if (st.WorkingMode.Length == 0) st.WorkingMode = s;
                }
                else if (n == "shellid")
                {
                    if (st.ShellId.Length == 0) st.ShellId = s;
                }
                else if (n == "currentslide" || n == "currentpage" || n == "pagenumber" || n == "page")
                {
                    int v;
                    if (int.TryParse(s, out v)) st.Current = v;
                }
                else if (n == "slidecount" || n == "pagecount" || n == "totalpages" || n == "totalslides")
                {
                    int v;
                    if (int.TryParse(s, out v)) st.SlideCount = v;
                }
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                int v = Number(value);
                if (n == "slidecount" || n == "pagecount" || n == "totalpages" || n == "totalslides")
                    st.SlideCount = v;
                else if (n == "currentslide" || n == "currentpage" || n == "pagenumber" || n == "page")
                    st.Current = v;
                else if (n == "slideindex")
                    st.Current = v + 1;   // 0-based; many MCP servers expose this name
            }
            else if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                if (n == "isshowing" || n == "showing" || n == "presenting" || n == "ispresenting")
                    st.Showing = value.GetBoolean();
            }
        }

        static void ParseMarkdown(string text, SeewoState st, bool courseware)
        {
            if (string.IsNullOrEmpty(text)) return;

            string[] lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal)) continue;
                line = line.TrimStart('#', '-', '*', '>', ' ').Trim();
                line = line.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
                if (line.Length == 0) continue;

                if (courseware)
                {
                    if (st.Title.Length == 0)
                    {
                        var m = Regex.Match(line, @"(?:课件名称|课件名|课程名称|标题|courseware\s*name|title)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (!m.Success)
                            m = Regex.Match(line, @"^(?:课件|courseware)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (m.Success) st.Title = Clean(m.Groups[1].Value);
                    }

                    if (st.SlideCount <= 0)
                    {
                        var m = Regex.Match(line, @"(?:总页数|幻灯片总数|页数|共)\s*[:：]?\s*(\d+)\s*页?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (m.Success) st.SlideCount = ParseInt(m.Groups[1].Value);
                    }
                }

                if (st.WorkingMode.Length == 0)
                {
                    var m = Regex.Match(line, @"(?:workingMode|工作模式|当前模式)\s*[:：]\s*([A-Za-z\u4e00-\u9fa5]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (m.Success)
                    {
                        st.WorkingMode = m.Groups[1].Value.Trim();
                        if (ModeIsDisplay(st.WorkingMode)) st.Showing = true;
                    }
                }

                if (st.Current <= 0)
                {
                    // 实测 get_slide_info 的关键行：`- 序号(1-base): 4`
                    var m = Regex.Match(line, @"序号\s*\(\s*1\s*-\s*base\s*\)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!m.Success)
                        m = Regex.Match(line, @"(?:当前页码|当前页|页码|current\s*page|page\s*number|page)\s*[:：]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (m.Success) st.Current = ParseInt(m.Groups[1].Value);
                    else
                    {
                        m = Regex.Match(line, @"第\s*(\d+)\s*页", RegexOptions.CultureInvariant);
                        if (m.Success) st.Current = ParseInt(m.Groups[1].Value);
                    }
                }

                if (st.SlideCount <= 0)
                {
                    var m = Regex.Match(line, @"(\d+)\s*/\s*(\d+)", RegexOptions.CultureInvariant);
                    if (m.Success)
                    {
                        if (st.Current <= 0) st.Current = ParseInt(m.Groups[1].Value);
                        st.SlideCount = ParseInt(m.Groups[2].Value);
                    }
                    else
                    {
                        m = Regex.Match(line, @"(?:共|总页数|总页)\s*[:：]?\s*(\d+)\s*页?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (m.Success) st.SlideCount = ParseInt(m.Groups[1].Value);
                    }
                }
            }
        }

        static void WalkFields(JsonElement e, Action<string, JsonElement> visit, int depth)
        {
            if (depth > 12) return;
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    visit(p.Name, p.Value);
                    WalkFields(p.Value, visit, depth + 1);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) WalkFields(item, visit, depth + 1);
            }
        }

        static string CollectText(JsonElement e)
        {
            var sb = new StringBuilder();
            CollectText(e, sb, 0);
            return sb.ToString();
        }

        static void CollectText(JsonElement e, StringBuilder sb, int depth)
        {
            if (depth > 12) return;
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.Equals("text", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.String)
                    {
                        string s = p.Value.GetString();
                        if (!string.IsNullOrEmpty(s)) sb.AppendLine(s);
                    }
                    else
                    {
                        CollectText(p.Value, sb, depth + 1);
                    }
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) CollectText(item, sb, depth + 1);
            }
        }

        static void Normalize(SeewoState st)
        {
            if (st.Current < 0) st.Current = 0;
            if (st.SlideCount < 0) st.SlideCount = 0;
            if (st.SlideCount > 0 && st.Current > st.SlideCount) st.Current = st.SlideCount;

            // The known API puts the display flag in shellContext.workingMode ("Display").
            // If it is absent, an explicit isShowing=true (handled in ApplyField) is the only
            // evidence we accept; guessing from "a page number exists" would record edit-mode
            // browsing as a lesson.
            if (ModeIsDisplay(st.WorkingMode)) st.Showing = true;
            if (string.IsNullOrEmpty(st.WorkingMode)) st.WorkingMode = st.Showing ? "Display" : "";
        }

        static bool ModeIsDisplay(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return false;
            string m = mode.ToLowerInvariant();
            return m.Contains("display") || m.Contains("show") || m.Contains("present")
                || m.Contains("fullscreen") || mode.Contains("放映");
        }

        static string Mode(SeewoState st)
        {
            return st.WorkingMode.Length > 0 ? st.WorkingMode : "unknown";
        }

        static int Number(JsonElement v)
        {
            int i;
            if (v.TryGetInt32(out i)) return i;
            double d;
            if (v.TryGetDouble(out d)) return (int)Math.Round(d);
            return 0;
        }

        static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().Trim('"', '\'', '“', '”', '`').Trim();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }

        // ------------------------------------------------------------------ jump tool lookup

        ToolDef FindJumpTool()
        {
            // The measured build exposes goto_slide(shellId, slideNumber). Prefer it over
            // goto_next_slide / goto_prev_slide, which have no page argument.
            for (int i = 0; i < tools.Count; i++)
            {
                if (tools[i].Name.Equals("goto_slide", StringComparison.OrdinalIgnoreCase)) return tools[i];
            }

            for (int i = 0; i < tools.Count; i++)
            {
                string n = tools[i].Name.ToLowerInvariant();
                if (n.Contains("get") || n.Contains("list") || n.Contains("info")) continue;
                if ((n.Contains("goto") || n.Contains("go_to") || n.Contains("jump")
                     || n.Contains("navigate") || n.Contains("seek")
                     || (n.Contains("slide") && (n.Contains("show") || n.Contains("set") || n.Contains("select"))))
                    && !n.Contains("next") && !n.Contains("prev"))
                {
                    return tools[i];
                }
            }
            return null;
        }

        static Dictionary<string, object> BuildJumpArgs(ToolDef tool, int index, string shellId)
        {
            string[] candidates = { "slideNumber", "page", "pageNumber", "index", "slide", "number", "slideIndex" };

            if (!string.IsNullOrEmpty(tool.SchemaJson))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(tool.SchemaJson))
                    {
                        JsonElement props;
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("properties", out props)
                            && props.ValueKind == JsonValueKind.Object)
                        {
                            string pageName = "";
                            for (int i = 0; i < candidates.Length; i++)
                            {
                                JsonElement ignored;
                                if (props.TryGetProperty(candidates[i], out ignored)) { pageName = candidates[i]; break; }
                            }
                            if (pageName.Length == 0) return null;   // schema exists but no page-like argument

                            var args = new Dictionary<string, object> { [pageName] = index };
                            // goto_slide in the measured build needs shellId as well.
                            JsonElement ignoredShell;
                            if (shellId.Length > 0 && props.TryGetProperty("shellId", out ignoredShell))
                                args["shellId"] = shellId;
                            return args;
                        }
                    }
                }
                catch { }
            }

            var fallback = new Dictionary<string, object> { ["page"] = index };
            if (shellId.Length > 0) fallback["shellId"] = shellId;
            return fallback;
        }

        // ------------------------------------------------------------------ logging

        void Log(string msg)
        {
            if (ledger == null) return;
            try { ledger.Log(msg); } catch { }
        }

        void LogDiag(string msg)
        {
            if (msg == lastDiag) return;
            lastDiag = msg;
            Log(msg);
        }

        void LogError(string msg)
        {
            if (msg == lastError) return;
            lastError = msg;
            Log(msg);
        }

        // ------------------------------------------------------------------ plumbing

        static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,                       // loopback must never go through a proxy
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromMilliseconds(500),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 4
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public void Dispose()
        {
            if (disposedFlag) return;
            disposedFlag = true;
            try { disposed.Cancel(); } catch { }
            try { disposed.Dispose(); } catch { }
        }

        sealed class ToolDef
        {
            public string Name = "";
            public string SchemaJson = "";
        }

        sealed class McpConfig
        {
            public string Url = "";
            public int Pid;
            public string ProcessName = "";
        }

        sealed class RpcResult
        {
            public bool Ok;
            public bool Transport;
            public int Status;
            public string Body = "";
            public string Error = "";
            public string SessionId = "";

            public static RpcResult Success(string body)
            {
                return new RpcResult { Ok = true, Status = 200, Body = body ?? "" };
            }

            public static RpcResult Fail(bool transport, string error, int status = 0)
            {
                return new RpcResult { Ok = false, Transport = transport, Error = error ?? "", Status = status };
            }
        }
    }
}
