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
    public sealed class SeewoState
    {
        public bool Found;
        public bool Showing;
        public string Title = "";       // courseware name
        public int Current;
        public int SlideCount;
        public string WorkingMode = ""; // raw workingMode ("Display" / "Edit" / ...)
        public string ShellId = "";     // en_find_shells[].id, needed by the other tools
        public string Endpoint = "";    // http://127.0.0.1:7916/mcp
        public string Diag = "";        // one line, already prefixed with "seewo:"
        public bool Running;
    }

    public sealed class SeewoBridge : IDisposable
    {
        public const string KeyPrefix = "希沃:";

        const string ProtocolVersion = "2024-11-05";
        const int RequestTimeoutMs = 1500;
        const int ProbeTtlMs = 30000;
        const int RetryProbeMs = 1000;
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

        SeewoState cachedState;
        DateTime cachedAt = DateTime.MinValue;

        int lastSlideCount;

        public SeewoState GetState()
        {
            var now = DateTime.UtcNow;
            if (cachedState != null && (now - cachedAt).TotalMilliseconds < 500) return cachedState;
            var st = GetStateUncached();
            cachedState = st;
            cachedAt = DateTime.UtcNow;
            return st;
        }

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

                var shells = CallTool("en_find_shells", new Dictionary<string, object> { ["searchingMode"] = "All" });
                if (shells.Ok)
                {
                    using (var doc = JsonDocument.Parse(shells.Body)) ParseShells(doc.RootElement, st);
                }
                else
                {
                    HandleCallFailure(shells);
                }
                if (st.ShellId.Length > 0) lastShellId = st.ShellId;
                else if (lastShellId.Length > 0) st.ShellId = lastShellId;

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

                var slideArgs = new Dictionary<string, object>();
                if (st.ShellId.Length > 0) slideArgs["shellId"] = st.ShellId;
                var slide = CallTool("get_slide_info", slideArgs);
                if (slide.Ok)
                {
                    using (var doc = JsonDocument.Parse(slide.Body)) ParseSlideInfo(doc.RootElement, st);
                }
                else
                {
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

        public string GotoSlide(int index)
        {
            if (index <= 0) return "页码无效";
            if (disposedFlag) return "希沃白板连接已关闭";

            try
            {
                if (!EnsureSessionForCall()) return "希沃白板没有响应";
                if (tools.Count == 0) LoadTools();

                if (lastShellId.Length == 0)
                {
                    var cur = GetStateUncached();
                    if (cur.ShellId.Length > 0) lastShellId = cur.ShellId;
                }
                if (lastShellId.Length == 0)
                {
                    ledger.Log("seewo: goto ignored -- no shell open");
                    return "希沃里还没打开这份课件：先在希沃白板里打开它，再点跳转";
                }

                var before = GetState();
                if (before.Found && before.Title.Length > 0 && !before.Showing
                    && tools.Exists(t => t.Name == "en_goto_app_ui"))
                {
                    var ui = CallTool("en_goto_app_ui", new Dictionary<string, object> { ["ui"] = "Display" });
                    if (ui.Ok)
                    {
                        System.Threading.Thread.Sleep(400);
                        cachedAt = DateTime.MinValue;
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

        static readonly object RunGate = new object();
        static DateTime runCheckedAt = DateTime.MinValue;
        static bool runCached;
        static long lastMcpSeenTicks;

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
            return trimmed;
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

        static void ParseShells(JsonElement root, SeewoState st)
        {
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
                    st.Current = v + 1;
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

        ToolDef FindJumpTool()
        {
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
                            if (pageName.Length == 0) return null;

                            var args = new Dictionary<string, object> { [pageName] = index };
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

        static HttpClient CreateHttp()
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
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
