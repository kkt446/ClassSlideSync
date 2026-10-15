using System;
using System.IO;
using System.Text.Json;

namespace ClassSlideSync.Plugin.Core
{
    /// <summary>
    /// ClassIsland 此刻在用的主题，直接从 <c>data\Settings.json</c> 读。
    ///
    /// 为什么要读文件而不是问 ClassIsland 要：2.1.0.1 的 <c>IThemeService</c> 只暴露
    /// ThemeUpdated / CurrentRealThemeMode / SetTheme / AnimationLevel，<b>没有</b>任何取当前主色的
    /// 属性；<c>Application.Current.Resources</c> 在运行期也只有 7 个转换器和字体键，没有主题色。
    /// 而 Settings.json 是这些值的唯一真源，读它才可能做到"老师一换主题色，卡片跟着变"。
    ///
    /// 颜色字符串是 <c>#RRGGBBAA</c>（Alpha 在<b>最后</b>）。这是实测结论，不是猜的：
    /// 本机 Settings.json 里 CustomForegroundColor=#1E90FFFF 正好是标准色 DodgerBlue、
    /// SecondaryColor=#7FFFD4FF 正好是标准色 aquamarine、PrimaryColor=#FF9D11FF 是 ClassIsland
    /// 的品牌橙；按 AARRGGBB 解释则会得到"半透明蓝 / 半透明淡紫 / 全透明蓝"，讲不通。
    /// </summary>
    public sealed class ClassIslandTheme
    {
        /// <summary>主题值的来源（写进日志，方便判断"卡片颜色不对"是读不到还是解析错）。</summary>
        public string Source = "(未读到 ClassIsland 的 Settings.json，使用内置默认值)";

        public bool Loaded;

        // 原始字符串，保留下来写日志用
        public string PrimaryHex = "#FF9D11FF";
        public string SecondaryHex = "#7FFFD4FF";
        public string BackgroundHex = "#000000FF";
        public string ForegroundHex = "";

        // 解析后的主色 / 底色
        public int AccentR = 0xFF, AccentG = 0x9D, AccentB = 0x11;
        public int BgR = 0x00, BgG = 0x00, BgB = 0x00;

        /// <summary>主界面（岛）自身的不透明度，0-1。</summary>
        public double IslandOpacity = 0.5;
        public int Radius = 8;
        public double Scale = 1.0;
        public int AnimationLevel = 2;

        public int SecondaryFontSize = 14;
        public int BodyFontSize = 16;
        public int EmphasizedFontSize = 18;
        public int LargeFontSize = 20;

        /// <summary>ClassIsland 自己在用的字体族名（HarmonyOS Sans SC）。</summary>
        public string FontFamily = "Microsoft YaHei UI";

        /// <summary>字体文件路径；找到了才能让卡片用上 ClassIsland 的同款字体。</summary>
        public string FontFile = "";

        /// <summary>
        /// 强调色最终是不是取自 Avalonia 运行时的 SystemAccentColor。
        /// ClassIsland 的“主题色来源”选“系统主题色”时，Settings.json 里的 PrimaryColor 根本不生效，
        /// 屏幕上真正的强调色只有运行时才拿得到（见 Services/SlideSyncService.ProbeUiTheme）。
        /// </summary>
        public bool AccentFromUi;
        public string AccentNote = "settings.json:PrimaryColor";

        public string Describe()
        {
            return string.Format("accent={0} bg={1} radius={2} opacity={3} font={4}{5} accentSrc={6} from {7}",
                AccentFromUi ? string.Format("#{0:X2}{1:X2}{2:X2}", AccentR, AccentG, AccentB) : PrimaryHex,
                BackgroundHex, Radius, IslandOpacity.ToString("0.##"),
                FontFamily, FontFile.Length > 0 ? "(ttf)" : "(系统回退)", AccentNote, Source);
        }

        public static ClassIslandTheme Load(string pluginConfigFolder, Action<string> log)
        {
            var t = new ClassIslandTheme();
            try
            {
                string dir = FindDataFolder(pluginConfigFolder);
                if (dir != null)
                {
                    string path = Path.Combine(dir, "Settings.json");
                    using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    {
                        var root = doc.RootElement;
                        t.PrimaryHex = Str(root, "PrimaryColor", t.PrimaryHex);
                        t.SecondaryHex = Str(root, "SecondaryColor", t.SecondaryHex);
                        t.BackgroundHex = Str(root, "BackgroundColor", t.BackgroundHex);
                        t.ForegroundHex = Str(root, "CustomForegroundColor", t.ForegroundHex);

                        int r, g, b, a;
                        if (TryParseColor(t.PrimaryHex, out r, out g, out b, out a)) { t.AccentR = r; t.AccentG = g; t.AccentB = b; }
                        if (TryParseColor(t.BackgroundHex, out r, out g, out b, out a)) { t.BgR = r; t.BgG = g; t.BgB = b; }

                        t.IslandOpacity = Num(root, "Opacity", t.IslandOpacity);
                        t.Radius = (int)Num(root, "RadiusX", t.Radius);
                        int radiusY = (int)Num(root, "RadiusY", t.Radius);
                        if (radiusY < t.Radius) t.Radius = radiusY;
                        t.Scale = Num(root, "Scale", t.Scale);
                        t.AnimationLevel = (int)Num(root, "AnimationLevel", t.AnimationLevel);
                        t.SecondaryFontSize = (int)Num(root, "MainWindowSecondaryFontSize", t.SecondaryFontSize);
                        t.BodyFontSize = (int)Num(root, "MainWindowBodyFontSize", t.BodyFontSize);
                        t.EmphasizedFontSize = (int)Num(root, "MainWindowEmphasizedFontSize", t.EmphasizedFontSize);
                        t.LargeFontSize = (int)Num(root, "MainWindowLargeFontSize", t.LargeFontSize);

                        // MainWindowFont 形如 "avares://ClassIsland/Assets/Fonts/#HarmonyOS Sans SC, ..."，
                        // 取第一个 "#" 后面的族名即可。
                        string font = Str(root, "MainWindowFont", "");
                        string family = FirstFamily(font);
                        if (family.Length > 0) t.FontFamily = family;

                        t.Source = path;
                        t.Loaded = true;
                    }
                }
                else if (log != null)
                {
                    log("theme: 没找到 ClassIsland 的 Settings.json（起点 " + pluginConfigFolder + "），外观用内置默认值");
                }

                t.FontFile = FindFontFile(t.FontFamily);
            }
            catch (Exception ex)
            {
                if (log != null) log("theme: 读取失败 " + ex.GetType().Name + " " + ex.Message);
            }
            return t;
        }

        /// <summary>
        /// 从插件自己的配置目录向上找 ClassIsland 的 data 目录：插件是
        /// <c>data\Config\Plugins\&lt;插件&gt;</c>，data 目录的特征是同时有 Settings.json 和 Config 子目录。
        /// </summary>
        static string FindDataFolder(string start)
        {
            try
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Settings.json")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Config")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 找 ClassIsland 自带的 HarmonyOS Sans SC 字体文件。GDI 不认 Avalonia 的 avares 资源，
        /// 必须拿到磁盘上的 ttf 再 AddFontResourceEx 私有加载，卡片才能和宿主用同一种字体。
        /// </summary>
        static string FindFontFile(string family)
        {
            try
            {
                string fileName = family.Replace(" ", "_") + "_Regular.ttf";
                var roots = new System.Collections.Generic.List<string>();
                string baseDir = AppContext.BaseDirectory;
                roots.Add(baseDir);
                roots.Add(Path.Combine(baseDir, "Assets", "Fonts"));

                // ClassIsland 2 的实际程序在 exe 旁边的 app-x.y.z 目录里，两个方向都找一遍。
                foreach (var parent in new[] { baseDir, SafeParent(baseDir) })
                {
                    if (parent == null) continue;
                    foreach (var sub in Directory.GetDirectories(parent, "app-*"))
                    {
                        roots.Add(sub);
                        roots.Add(Path.Combine(sub, "Assets", "Fonts"));
                    }
                }

                foreach (var root in roots)
                {
                    string candidate = Path.Combine(root, "Assets", "Fonts", fileName);
                    if (File.Exists(candidate)) return candidate;
                    candidate = Path.Combine(root, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return "";
        }

        static string SafeParent(string dir)
        {
            try { var p = Directory.GetParent(dir); return p == null ? null : p.FullName; }
            catch { return null; }
        }

        static string FirstFamily(string fontField)
        {
            if (string.IsNullOrEmpty(fontField)) return "";
            int hash = fontField.IndexOf('#');
            if (hash < 0) return "";
            string rest = fontField.Substring(hash + 1);
            int comma = rest.IndexOf(',');
            if (comma >= 0) rest = rest.Substring(0, comma);
            return rest.Trim();
        }

        static string Str(JsonElement e, string name, string fallback)
        {
            JsonElement v;
            if (e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return fallback;
        }

        static double Num(JsonElement e, string name, double fallback)
        {
            JsonElement v;
            if (e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            return fallback;
        }

        /// <summary>#RRGGBB 或 #RRGGBBAA（Alpha 在最后，见类注释）。</summary>
        public static bool TryParseColor(string s, out int r, out int g, out int b, out int a)
        {
            r = g = b = 0; a = 255;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;
            try
            {
                r = Convert.ToInt32(s.Substring(0, 2), 16);
                g = Convert.ToInt32(s.Substring(2, 2), 16);
                b = Convert.ToInt32(s.Substring(4, 2), 16);
                if (s.Length == 8) a = Convert.ToInt32(s.Substring(6, 2), 16);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 卡片最终长什么样：ClassIsland 主题 ⊕ 用户自定义。绘制代码只认这个类，
    /// 于是"跟随主题"和"自定义"的优先级只有一处实现，不会两边打架。
    /// </summary>
    public sealed class CardAppearance
    {
        public int BgR = 24, BgG = 26, BgB = 32;
        /// <summary>整窗不透明度 0-255（GDI 没有逐图元 alpha，只能整窗混合）。</summary>
        public int BgAlpha = 230;

        public int AccentR = 0xFF, AccentG = 0x9D, AccentB = 0x11;
        public int TextR = 0xF2, TextG = 0xF3, TextB = 0xF5;
        public int MutedR = 0xA8, MutedG = 0xAA, MutedB = 0xAE;

        public int Radius = 8;
        public double CardScale = 1.0;
        public int FontDelta = 0;
        public string FontFamily = "Microsoft YaHei UI";
        public string FontFile = "";

        /// <summary>字号沿用 ClassIsland 自己那套（次要 / 正文 / 强调 / 大号），卡片文字才不会比宿主更"胖"。</summary>
        public int FontSecondary = 14;
        public int FontBody = 16;
        public int FontEmphasized = 18;
        public int FontLarge = 20;

        public bool OnRight = true;
        public int VerticalPercent = 50;
        public int TabWidth = 32;
        public int CardWidth = 236;
        public int CardHeight = 150;

        public bool ShowFileName = true;
        public bool ShowSlideCount = true;
        public bool ShowContext = true;
        public bool ShowStatusLine = true;
        public bool ShowJumpLine = true;
        public bool ClickToJump = true;

        /// <summary>跟随主题还是自定义，写进日志用。</summary>
        public bool FollowsTheme = true;

        public string Describe()
        {
            return string.Format(
                "bg=#{0:X2}{1:X2}{2:X2} alpha={3} accent=#{4:X2}{5:X2}{6:X2} radius={7} scale={8:0.##} " +
                "font={9}{10} side={11} v={12}% tab={13} lines={14}{15}{16}{17} click={18} follow={19}",
                BgR, BgG, BgB, BgAlpha, AccentR, AccentG, AccentB, Radius, CardScale,
                FontFamily, FontFile.Length > 0 ? "(ttf)" : "", OnRight ? "right" : "left", VerticalPercent,
                TabWidth,
                ShowFileName ? "title," : "", ShowSlideCount ? "count," : "", ShowContext ? "context," : "",
                ShowStatusLine ? "status," : "",
                ClickToJump, FollowsTheme);
        }

        public static CardAppearance Resolve(SyncSettings s, ClassIslandTheme theme)
        {
            var a = new CardAppearance();
            if (s == null) s = new SyncSettings();
            if (theme == null) theme = new ClassIslandTheme();

            bool follow = s.FollowClassIslandTheme;
            a.FollowsTheme = follow;

            // ---- 底色 ----
            int r, g, b, alpha;
            if (ClassIslandTheme.TryParseColor(s.CardBackgroundColor, out r, out g, out b, out alpha))
            {
                a.BgR = r; a.BgG = g; a.BgB = b;
                a.FollowsTheme = false;
            }
            else if (follow)
            {
                a.BgR = theme.BgR; a.BgG = theme.BgG; a.BgB = theme.BgB;
            }

            int pct = s.CardOpacityPercent;
            if (pct < 20) pct = 20;
            if (pct > 100) pct = 100;
            a.BgAlpha = pct * 255 / 100;

            // ---- 强调色 ----
            if (ClassIslandTheme.TryParseColor(s.AccentColor, out r, out g, out b, out alpha))
            {
                a.AccentR = r; a.AccentG = g; a.AccentB = b;
                a.FollowsTheme = false;
            }
            else if (follow)
            {
                a.AccentR = theme.AccentR; a.AccentG = theme.AccentG; a.AccentB = theme.AccentB;
            }

            // ---- 文字色：按底色亮度决定深字还是浅字，避免老师选了浅色底就看不见字 ----
            double lum = (0.2126 * a.BgR + 0.7152 * a.BgG + 0.0722 * a.BgB) / 255.0;
            if (lum > 0.55) { a.TextR = 0x1B; a.TextG = 0x1C; a.TextB = 0x1E; }
            else { a.TextR = 0xF2; a.TextG = 0xF3; a.TextB = 0xF5; }
            a.MutedR = Mix(a.TextR, a.BgR, 0.40);
            a.MutedG = Mix(a.TextG, a.BgG, 0.40);
            a.MutedB = Mix(a.TextB, a.BgB, 0.40);

            // ---- 圆角 / 缩放 / 字体 ----
            a.Radius = s.CardCornerRadius >= 0 ? s.CardCornerRadius : (follow ? theme.Radius : 8);
            if (a.Radius < 0) a.Radius = 0;
            if (a.Radius > 48) a.Radius = 48;

            a.CardScale = s.CardScale;
            if (a.CardScale < 0.6) a.CardScale = 0.6;
            if (a.CardScale > 2.0) a.CardScale = 2.0;

            a.FontDelta = s.CardFontSizeDelta;

            if (follow)
            {
                a.FontSecondary = theme.SecondaryFontSize;
                a.FontBody = theme.BodyFontSize;
                a.FontEmphasized = theme.EmphasizedFontSize;
                a.FontLarge = theme.LargeFontSize;
            }

            if (!string.IsNullOrWhiteSpace(s.CardFontFamily))
            {
                a.FontFamily = s.CardFontFamily.Trim();
            }
            else if (follow && !string.IsNullOrWhiteSpace(theme.FontFamily))
            {
                a.FontFamily = theme.FontFamily;
                a.FontFile = theme.FontFile;
            }

            // ---- 位置与尺寸 ----
            a.OnRight = !s.CardOnLeft;
            a.VerticalPercent = s.CardVerticalPercent < 0 ? 0 : (s.CardVerticalPercent > 100 ? 100 : s.CardVerticalPercent);
            a.TabWidth = s.CollapsedTabWidth < 16 ? 16 : (s.CollapsedTabWidth > 96 ? 96 : s.CollapsedTabWidth);

            a.ShowFileName = s.CardShowFileName;
            a.ShowSlideCount = s.CardShowSlideCount;
            a.ShowContext = s.CardShowContext;
            a.ShowStatusLine = s.CardShowStatusLine;
            a.ShowJumpLine = s.CardShowJumpLine;
            a.ClickToJump = s.CardClickToJump;

            a.CardWidth = (int)Math.Round(236 * a.CardScale);
            a.CardHeight = (int)Math.Round(EstimatedHeight(a) * a.CardScale);
            return a;
        }

        /// <summary>
        /// 96 dpi 下的卡片高度。行高按 ClassIsland 的字号体系（次要 14 / 正文 16 / 大号 20）估算，
        /// 关掉某一行就少一行高度，卡片不会留一片空白。
        /// </summary>
        static int EstimatedHeight(CardAppearance a)
        {
            int h = 10;                       // 上内边距
            if (a.ShowFileName) h += 22;
            h += 50;                          // 大页码
            h += 22;                          // 正在放映 / 上次停在这一页
            if (a.ShowJumpLine) h += 26;
            if (a.ShowContext) h += 18;
            if (a.ShowStatusLine) h += 18;
            h += 10;                          // 下内边距
            return h;
        }

        static int Mix(int a, int b, double t)
        {
            int v = (int)Math.Round(a * (1 - t) + b * t);
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }
    }
}
