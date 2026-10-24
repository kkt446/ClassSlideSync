using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 插件行动设置控件的共同底座。
    ///
    /// 为什么用纯 C# 搭界面而不是 .axaml：插件工程没有 <c>AvaloniaResource Include="**\*.axaml"</c>
    /// 的默认 glob，新增 .axaml 必须改 .csproj；本任务的改动范围不含工程文件，所以这里用
    /// StackPanel/TextBox 直接搭。ClassIsland 只要求控件继承
    /// <see cref="ActionSettingsControlBase{TSettings}"/>，并不要求它来自 XAML。
    ///
    /// 另一个坑：<c>Settings</c> 的 getter 在 SettingsInternal 还没注入时会抛
    /// ArgumentNullException（宿主是先 new 控件、再 set_SettingsInternal、最后挂到可视树）。
    /// 所以这里统一用 <see cref="TrySettings"/> 包一层，绝不在构造函数里碰 Settings。
    /// </summary>
    /// <typeparam name="TSettings">行动设置 POCO，必须能公共无参构造。</typeparam>
    public abstract class PluginActionSettingsControl<TSettings> : ActionSettingsControlBase<TSettings>
        where TSettings : class
    {
        /// <summary>安全地拿设置对象；宿主还没注入时返回 null，不抛异常。</summary>
        protected TSettings? TrySettings()
        {
            try
            {
                return Settings;
            }
            catch
            {
                return null;
            }
        }

        protected static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            };
        }

        protected static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                Opacity = 0.7,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        protected static TextBox Editor(string watermark = "")
        {
            return new TextBox
            {
                Watermark = watermark,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 220,
            };
        }
    }

    /// <summary>「发一条 Windows 提示」的设置界面：标题、正文、停留时长、是否置顶。</summary>
    public sealed class NotifyActionSettingsControl : PluginActionSettingsControl<NotifyActionSettings>
    {
        readonly TextBox maskBox = Editor("例如：课件续页");
        readonly TextBox contentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            Watermark = "例如：{deck} 停在第 {page} 页，还剩 {remaining} 页",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        readonly TextBox durationBox = new TextBox { Width = 120, Watermark = "3 - 120" };
        readonly CheckBox topmostBox = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "置顶显示（优先系统横幅；关闭时用托盘气泡，停留时长才生效）",
                TextWrapping = TextWrapping.Wrap,
            },
        };

        bool loading;

        public NotifyActionSettingsControl()
        {
            var panel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 8),
            };

            panel.Children.Add(Label("标题"));
            panel.Children.Add(maskBox);

            panel.Children.Add(Label("正文"));
            panel.Children.Add(contentBox);
            panel.Children.Add(Hint("可用占位符：{deck} 课件名、{page} 上次页码、{total} 总页数、{remaining} 剩余页数；取不到时替换为空。"));

            panel.Children.Add(Label("停留时长（秒）"));
            panel.Children.Add(durationBox);
            panel.Children.Add(Hint("托盘气泡会保留这么久；系统横幅的停留时长由 Windows 统一决定。"));

            panel.Children.Add(topmostBox);

            Content = panel;

            maskBox.TextChanged += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings != null) settings.Mask = maskBox.Text ?? "";
            };

            contentBox.TextChanged += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings != null) settings.Content = contentBox.Text ?? "";
            };

            durationBox.TextChanged += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings == null) return;
                if (int.TryParse((durationBox.Text ?? "").Trim(), out int seconds))
                {
                    settings.DurationSeconds = Math.Clamp(seconds, 3, 120);
                }
            };

            durationBox.LostFocus += (_, _) => NormalizeDuration();

            topmostBox.Click += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings != null) settings.IsTopmostEnabled = topmostBox.IsChecked == true;
            };

            // 宿主在 GetInstance 里先 set_SettingsInternal 再把控件挂上树，所以只能在这里读设置；
            // 构造函数里读会拿到 ArgumentNullException。
            AttachedToVisualTree += (_, _) => LoadFromSettings();
        }

        void LoadFromSettings()
        {
            var settings = TrySettings();
            if (settings == null) return;

            loading = true;
            try
            {
                maskBox.Text = settings.Mask ?? "";
                contentBox.Text = settings.Content ?? "";
                int seconds = Math.Clamp(settings.DurationSeconds, 3, 120);
                durationBox.Text = seconds.ToString();
                topmostBox.IsChecked = settings.IsTopmostEnabled;
            }
            finally
            {
                loading = false;
            }
        }

        void NormalizeDuration()
        {
            var settings = TrySettings();
            if (settings == null) return;

            int seconds = Math.Clamp(settings.DurationSeconds, 3, 120);
            settings.DurationSeconds = seconds;

            loading = true;
            try
            {
                durationBox.Text = seconds.ToString();
            }
            finally
            {
                loading = false;
            }
        }
    }

    /// <summary>「跳到指定页」的设置：只要一个页码。</summary>
    public sealed class JumpToPageSettingsControl : PluginActionSettingsControl<JumpToPageSettings>
    {
        readonly TextBox pageBox = new TextBox { Width = 120, Watermark = "1 - 999" };
        bool loading;

        public JumpToPageSettingsControl()
        {
            var panel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 8),
            };

            panel.Children.Add(Label("页码"));
            panel.Children.Add(pageBox);
            panel.Children.Add(Hint("只对当前正在放映的课件生效；没有在放映时安静跳过，只记日志。"));

            Content = panel;

            pageBox.TextChanged += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings == null) return;
                if (int.TryParse((pageBox.Text ?? "").Trim(), out int page))
                {
                    settings.Page = Math.Clamp(page, 1, 999);
                }
            };

            pageBox.LostFocus += (_, _) => NormalizePage();
            AttachedToVisualTree += (_, _) => LoadFromSettings();
        }

        void LoadFromSettings()
        {
            var settings = TrySettings();
            if (settings == null) return;

            loading = true;
            try
            {
                pageBox.Text = Math.Clamp(settings.Page, 1, 999).ToString();
            }
            finally
            {
                loading = false;
            }
        }

        void NormalizePage()
        {
            var settings = TrySettings();
            if (settings == null) return;

            int page = Math.Clamp(settings.Page, 1, 999);
            settings.Page = page;

            loading = true;
            try
            {
                pageBox.Text = page.ToString();
            }
            finally
            {
                loading = false;
            }
        }
    }
}
