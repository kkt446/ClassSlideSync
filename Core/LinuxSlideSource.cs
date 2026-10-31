using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// Linux 上的「页码来源」：读适配器写出的 JSON 文件，得到正在放映的课件、第几页、共几页。
    ///
    /// Windows 端页码来自 COM（<see cref="PptBridge"/>）和希沃 MCP（<see cref="SeewoBridge"/>），
    /// 这两条路在 Linux 上都不存在（没有 COM，也没有希沃客户端）。所以 Linux 端换成一个
    /// **文件协议**：
    ///
    ///     ~/.local/share/classslidesync/current.json
    ///     {"file":"/home/u/课件.pptx","page":4,"total":10,"showing":true,
    ///      "updated":"2026-10-31T07:00:00Z","app":"libreoffice"}
    ///
    /// 谁写这个文件？两个适配器（都在 tools/linux-adapters/ 下）：
    ///   · LibreOffice Impress：Python + UNO，连 --accept 的 socket，每秒轮询；
    ///   · WPS Linux：JS 加载项，setInterval 轮询。
    /// 插件只读 current.json、不写它（除了 <see cref="Maintain"/> 建目录和 README.txt）；
    /// 反向的「跳页」走同目录的 command.json，由 <see cref="TryWriteCommand"/> 原子写入。
    ///
    /// 为什么用文件而不是 D-Bus / 剪贴板 / 轮询进程：适配器是不同进程、不同语言，
    /// 文件是唯一一个两端都零依赖、断了也不会把插件卡死的通道。10 秒过期是安全阀：
    /// 适配器崩了、老师强退 LibreOffice 之后，卡片不会一直停在最后一页骗人。
    ///
    /// 线程模型：<see cref="TryRead"/> 可以随便从哪条线程调（调度器 500ms、卡片点按、测试）。
    /// 方法自己 catch 所有异常，绝不把 IO/JSON 问题抛给调用方 —— 页码读不到只该是
    /// 「这一拍没有数据」，不能是「插件崩了」。
    /// </summary>
    public static class LinuxSlideSource
    {
        /// <summary>updated 超过这么多秒就算过期（任务书规定 10 秒）。</summary>
        public const int StaleSeconds = 10;

        /// <summary>数据目录：$XDG_DATA_HOME/classslidesync（通常是 ~/.local/share/classslidesync）。</summary>
        public static string DataDirectory { get { return dataDir; } }

        /// <summary>适配器写的 JSON 全路径。</summary>
        public static string CurrentJsonPath { get { return jsonPath; } }

        /// <summary>插件写给适配器的跳页命令文件全路径。</summary>
        public static string CommandJsonPath { get { return commandPath; } }

        /// <summary>协议说明文件的全路径（<see cref="Maintain"/> 会写它）。</summary>
        public static string ReadmePath { get { return readmePath; } }

        static readonly string dataDir = ResolveDataDirectory();
        static readonly string jsonPath = Path.Combine(dataDir, "current.json");
        static readonly string commandPath = Path.Combine(dataDir, "command.json");
        static readonly string readmePath = Path.Combine(dataDir, "README.txt");

        /// <summary>
        /// 日志出口。插件启动时接到 Ledger（app.log）；没接时退回 stderr，保证「记日志」这件事
        /// 在最裸的环境（手工跑测试）里也成立。写日志本身绝不允许抛。
        /// </summary>
        public static Action<string>? LogSink { get; set; }

        /// <summary>最近一次**成功**读到的快照。永不返回 null（没读到过就是 Ok=false 的空快照）。</summary>
        public static LinuxSlideRead Current { get { return current; } }

        static volatile LinuxSlideRead current = LinuxSlideRead.Empty;

        static readonly object logGate = new object();
        static readonly Dictionary<string, DateTime> lastLogAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>
        /// 读一次 current.json。成功返回 true，并给出四个主字段；过期/不存在/坏 JSON/IO 错误
        /// 一律返回 false（原因写日志），绝不抛。
        /// </summary>
        public static bool TryRead(out string file, out int page, out int total, out bool showing)
        {
            file = "";
            page = 0;
            total = 0;
            showing = false;
            try
            {
                LinuxSlideRead read;
                if (!TryReadDetailed(out read)) return false;
                file = read.File;
                page = read.Page;
                total = read.Total;
                showing = read.Showing;
                return true;
            }
            catch (Exception ex)
            {
                // 双保险：TryReadDetailed 已经全包了，这里兜住「记日志本身」之类的意外。
                Log("read", "bridge: 读取页码失败 " + ex.GetType().Name + " " + ex.Message, TimeSpan.FromSeconds(30));
                return false;
            }
        }

        /// <summary>
        /// 同 <see cref="TryRead"/>，但把「文件 / 页数 / 更新于 / app / 年龄」一起带出来，
        /// 卡片想显示「数据来自 LibreOffice，1.2 秒前」时用得到。
        /// </summary>
        public static bool TryReadDetailed(out LinuxSlideRead read)
        {
            read = LinuxSlideRead.Empty;
            try
            {
                if (!File.Exists(jsonPath))
                {
                    Log("missing", "bridge: 没有 current.json（适配器还没运行）: " + jsonPath,
                        TimeSpan.FromSeconds(60));
                    return false;
                }

                string text;
                try
                {
                    text = File.ReadAllText(jsonPath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Log("io", "bridge: 读不了 current.json（" + ex.GetType().Name + "）: " + ex.Message,
                        TimeSpan.FromSeconds(10));
                    return false;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Log("empty", "bridge: current.json 是空的（适配器可能正在写）", TimeSpan.FromSeconds(10));
                    return false;
                }

                string file;
                int page, total;
                bool showing;
                string app;
                string updatedText;
                try
                {
                    using (var doc = JsonDocument.Parse(text))
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            Log("json", "bridge: current.json 不是对象", TimeSpan.FromSeconds(10));
                            return false;
                        }

                        file = GetString(root, "file") ?? "";
                        page = GetInt(root, "page", 0);
                        total = GetInt(root, "total", 0);
                        showing = GetBool(root, "showing", false);
                        app = GetString(root, "app") ?? "";
                        updatedText = GetString(root, "updated") ?? "";
                    }
                }
                catch (Exception ex)
                {
                    // 适配器用「临时文件 + rename」原子替换，正常不会读到半截；这里兜住第三方
                    // 直接写文件的场景：坏 JSON 只丢这一拍，下一拍（500ms 后）再试。
                    Log("json", "bridge: current.json 解析失败（" + ex.GetType().Name + "）: " + ex.Message,
                        TimeSpan.FromSeconds(5));
                    return false;
                }

                if (string.IsNullOrWhiteSpace(file))
                {
                    Log("nofile", "bridge: current.json 缺 file 字段", TimeSpan.FromSeconds(30));
                    return false;
                }
                if (page < 0 || total < 0)
                {
                    Log("bounds", "bridge: current.json 的 page/total 是负数（page=" + page + ", total=" + total + "）",
                        TimeSpan.FromSeconds(30));
                    return false;
                }

                // updated 是协议的「心跳」：解析不出来就没法判断数据新不新，按过期处理。
                DateTimeOffset updated = default(DateTimeOffset);
                bool hasUpdated = !string.IsNullOrWhiteSpace(updatedText)
                    && DateTimeOffset.TryParse(updatedText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out updated);
                if (!hasUpdated)
                {
                    Log("noupdated", "bridge: 数据过期（updated 字段缺失或无法解析: \"" + updatedText + "\"）",
                        TimeSpan.FromSeconds(30));
                    return false;
                }

                double age = (DateTimeOffset.UtcNow - updated).TotalSeconds;
                // 时钟回拨/适配器用了未来时间：不把它当成过期（否则老师刚打开就被判死）。
                // 但负得太多说明两台机器的钟差很大，记一次日志方便排查。
                if (age < -60)
                {
                    Log("future", "bridge: current.json 的 updated 在未来 " + (int)(-age) + " 秒，请检查系统时间",
                        TimeSpan.FromSeconds(60));
                }
                if (age > StaleSeconds)
                {
                    Log("stale", "bridge: 数据过期 " + (int)age + " 秒（" + jsonPath + "）",
                        TimeSpan.FromSeconds(30));
                    return false;
                }

                var result = new LinuxSlideRead(
                    true, file, page, total, showing, app, updated, age);
                current = result;
                read = result;
                return true;
            }
            catch (Exception ex)
            {
                Log("unexpected", "bridge: 读取页码时发生意外 " + ex.GetType().Name + " " + ex.Message,
                    TimeSpan.FromSeconds(30));
                return false;
            }
        }

        /// <summary>
        /// 写一次「跳到第 page 页」的命令（插件 → 适配器）。文件协议：
        ///
        ///     ~/.local/share/classslidesync/command.json
        ///     {"page":3,"ts":"2026-10-31T07:00:00.123Z"}
        ///
        /// 适配器（LibreOffice 的 slidesync_lo.py / WPS 的 main.js）每秒读一次这个文件，
        /// 发现 ts 比上次处理的更新、且 page 在 1..total 内，就调用演示软件自己的 gotoSlide。
        /// 插件不直接碰 GUI/UNO —— 和读页码一样，文件是两端唯一零依赖的通道。
        ///
        /// 原子写：先写同目录临时文件（带随机后缀，两个线程同时点也不会互相覆盖），
        /// 再 File.Move(overwrite:true)。适配器永远读不到半截 JSON。
        /// 失败（目录不可写、磁盘满、权限）返回 false 并给出人话原因，绝不抛。
        /// </summary>
        public static bool TryWriteCommand(int page, out string error)
        {
            error = "";
            if (page <= 0)
            {
                error = "页码必须从 1 开始（收到 " + page.ToString(CultureInfo.InvariantCulture) + "）";
                return false;
            }

            string ts = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            string json = "{\"page\":" + page.ToString(CultureInfo.InvariantCulture) + ",\"ts\":\"" + ts + "\"}";
            string tmp = commandPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(dataDir);
                // UTF-8 无 BOM：适配器用标准 json 解析，有没有 BOM 都能读，但没有更干净。
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, commandPath, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                Log("command", "command.json 写入失败（" + commandPath + "）：" + error,
                    TimeSpan.FromSeconds(10));
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 建好数据目录，并写一份中文协议说明（README.txt）。幂等：目录已在就不动，
        /// README 内容一样就不重写（避免每次插件启动都改 mtime）。
        /// 插件启动时调一次；适配器安装脚本也可以调。任何失败只记日志，绝不抛。
        /// </summary>
        public static void Maintain()
        {
            try
            {
                Directory.CreateDirectory(dataDir);

                string readme = BuildReadme();
                bool write = true;
                try
                {
                    if (File.Exists(readmePath))
                    {
                        string old = File.ReadAllText(readmePath, Encoding.UTF8);
                        write = !string.Equals(old, readme, StringComparison.Ordinal);
                    }
                }
                catch
                {
                    // 读旧 README 失败（权限/坏文件）就直接覆盖，说明文件比旧内容重要。
                    write = true;
                }

                if (write)
                {
                    // 明确 UTF-8 无 BOM：Linux 的 cat/编辑器都不会在开头看到 \uFEFF。
                    File.WriteAllText(readmePath, readme, new UTF8Encoding(false));
                    Log("maintain", "bridge: 已写协议说明 " + readmePath, TimeSpan.Zero);
                }
            }
            catch (Exception ex)
            {
                Log("maintain", "bridge: 建目录/写 README 失败（" + ex.GetType().Name + "）: " + ex.Message,
                    TimeSpan.FromSeconds(60));
            }
        }

        // ---- 内部：JSON 取值（宽容一点：数字/布尔写成字符串也认） ----

        static string? GetString(JsonElement root, string name)
        {
            JsonElement v;
            if (!root.TryGetProperty(name, out v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Null) return null;
            return v.ToString();
        }

        static int GetInt(JsonElement root, string name, int fallback)
        {
            JsonElement v;
            if (!root.TryGetProperty(name, out v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number)
            {
                int n;
                if (v.TryGetInt32(out n)) return n;
                double d;
                if (v.TryGetDouble(out d)) return (int)Math.Round(d);
                return fallback;
            }
            if (v.ValueKind == JsonValueKind.String)
            {
                int n;
                if (int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            }
            return fallback;
        }

        static bool GetBool(JsonElement root, string name, bool fallback)
        {
            JsonElement v;
            if (!root.TryGetProperty(name, out v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.Number)
            {
                int n;
                return v.TryGetInt32(out n) && n != 0;
            }
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = (v.GetString() ?? "").Trim();
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            }
            return fallback;
        }

        // ---- 内部：目录与日志 ----

        static string ResolveDataDirectory()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                {
                    // 极简容器里 LocalApplicationData 可能返回空串；按 XDG 规范手工拼。
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("HOME") ?? ".";
                    local = Path.Combine(home, ".local", "share");
                }
                return Path.Combine(local, "classslidesync");
            }
            catch
            {
                // 连 home 都拿不到时退到 /tmp：宁可写在临时目录，也不要让静态构造抛异常。
                return Path.Combine(Path.GetTempPath(), "classslidesync");
            }
        }

        /// <summary>
        /// 按 key 限频的日志。调度器 500ms 读一次，过期/缺文件这类状态会持续很久，
        /// 不限频会把 app.log 刷爆（老师的机器不是开发机）。
        /// </summary>
        static void Log(string key, string message, TimeSpan minInterval)
        {
            try
            {
                lock (logGate)
                {
                    DateTime now = DateTime.UtcNow;
                    DateTime last;
                    if (minInterval > TimeSpan.Zero && lastLogAt.TryGetValue(key, out last)
                        && now - last < minInterval)
                    {
                        return;
                    }
                    lastLogAt[key] = now;
                }
            }
            catch
            {
                // 限频表坏了也要把日志写出去——日志比去重重要。
            }

            try
            {
                var sink = LogSink;
                if (sink != null) sink(message);
                else Console.Error.WriteLine(message);
            }
            catch
            {
                // 日志出口自己出错（宿主日志文件被占用等）时保持静默：绝不能因为记日志把页码弄挂。
            }
        }

        static string BuildReadme()
        {
            return string.Join("\n", new[]
            {
                "ClassSlideSync Linux 页码桥 —— 协议说明",
                "========================================",
                "",
                "这个目录是 ClassSlideSync 插件和 Linux 演示软件适配器之间的唯一通道。",
                "适配器每秒把「现在讲到哪一页」写成 current.json；插件每 500ms 读一次。",
                "",
                "文件位置",
                "--------",
                "  $XDG_DATA_HOME/classslidesync/current.json",
                "  通常就是 ~/.local/share/classslidesync/current.json",
                "",
                "JSON 字段（UTF-8，无 BOM）",
                "-------------------------",
                "  file     字符串，课件绝对路径（如 /home/teacher/课件/第3节.pptx）",
                "  page     整数，当前页，从 1 开始",
                "  total    整数，总页数",
                "  showing  布尔，true = 正在全屏放映；false = 只是打开了文档",
                "  updated  字符串，UTC ISO-8601，写到秒，例如 2026-10-31T07:00:00Z",
                "  app      字符串，写这份数据的适配器：libreoffice 或 wps",
                "",
                "示例",
                "----",
                "  {\"file\":\"/home/teacher/deck.pptx\",\"page\":4,\"total\":10,",
                "   \"showing\":true,\"updated\":\"2026-10-31T07:00:00Z\",\"app\":\"libreoffice\"}",
                "",
                "过期规则",
                "--------",
                "  updated 超过 10 秒没有刷新，插件就当成「没有数据」，避免适配器崩了以后",
                "  卡片一直停在最后一页骗人。适配器只要在运行，就必须每秒重写一次 updated。",
                "",
                "写入方式（重要）",
                "----------------",
                "  请先写同目录的临时文件，再 rename 覆盖 current.json（POSIX rename 是原子的）。",
                "  直接往 current.json 里写会让插件读到半截 JSON——虽然插件会吞掉错误、",
                "  500ms 后重试，但那一拍就丢页了。",
                "",
                "  反例（不要这样）：open(current.json, 'w') 后慢慢 write。",
                "  正解：open(current.json.tmp, 'w'), flush, close, os.replace(tmp, current.json)。",
                "",
                "反向通道：command.json（插件 → 适配器）",
                "--------------------------------------",
                "  卡片上点「从第 N 页接着讲」时，插件写这个文件，适配器读到后调用演示软件",
                "  自己的跳页 API：",
                "    {\"page\":3,\"ts\":\"2026-10-31T07:00:00.123Z\"}",
                "  page  整数，目标页，从 1 开始（适配器负责检查 1..total）",
                "  ts    字符串，UTC ISO-8601（带毫秒），适配器只处理比上次更新的命令",
                "",
                "  适配器跳完后应立刻重写一次 current.json（刷新 updated），并把新页码写进去，",
                "  这样卡片几百毫秒内就能看到「跳过去了」；插件自己不会改 current.json。",
                "  命令文件可以一直留着；超过 2 分钟的命令会被适配器当成过期、不执行。",
                "",
                "适配器在哪",
                "----------",
                "  LibreOffice Impress: tools/linux-adapters/libreoffice/slidesync_lo.py",
                "  WPS Linux:           tools/linux-adapters/wps/main.js",
                "  两者的安装步骤见各自目录下的 README.md。",
                "",
                "插件不写 current.json，只读（跳页命令写在 command.json）；删掉这些文件不会影响插件启动。",
                "本文件由插件自动生成，可以随意改动，下次启动时内容不同会被覆盖。",
                "",
            });
        }
    }

    /// <summary>
    /// 一次成功读到的页码快照。不可变：拿到手之后适配器再翻页也不会改到它。
    /// </summary>
    public sealed class LinuxSlideRead
    {
        internal static readonly LinuxSlideRead Empty = new LinuxSlideRead(
            false, "", 0, 0, false, "", default(DateTimeOffset), 0);

        public LinuxSlideRead(
            bool ok, string file, int page, int total, bool showing,
            string app, DateTimeOffset updated, double ageSeconds)
        {
            Ok = ok;
            File = file ?? "";
            Page = page;
            Total = total;
            Showing = showing;
            App = app ?? "";
            UpdatedUtc = updated;
            AgeSeconds = ageSeconds;
        }

        /// <summary>true = 这一份是有效数据（不是空快照）。</summary>
        public bool Ok { get; }

        /// <summary>课件绝对路径。</summary>
        public string File { get; }

        /// <summary>当前页，1 起；0 = 适配器还没拿到页码。</summary>
        public int Page { get; }

        /// <summary>总页数，0 = 未知。</summary>
        public int Total { get; }

        /// <summary>true = 正在全屏放映。</summary>
        public bool Showing { get; }

        /// <summary>写数据的适配器（libreoffice / wps），未知时为空串。</summary>
        public string App { get; }

        /// <summary>适配器写下的 UTC 时间。</summary>
        public DateTimeOffset UpdatedUtc { get; }

        /// <summary>读到的那一刻，数据已经放了多久（秒）。</summary>
        public double AgeSeconds { get; }

        /// <summary>日志里的一句话，例如「课件.pptx 第 4/10 页（放映中，libreoffice）」。</summary>
        public string Describe()
        {
            string name;
            try { name = Path.GetFileName(File); }
            catch { name = File; }
            if (string.IsNullOrEmpty(name)) name = "(课件名未知)";
            string state = Showing ? "放映中" : "打开着";
            return name + " 第 " + Page + "/" + (Total > 0 ? Total.ToString(CultureInfo.InvariantCulture) : "?")
                + " 页（" + state + (App.Length > 0 ? "，" + App : "") + "）";
        }
    }
}
