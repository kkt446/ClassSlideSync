using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;

namespace ClassSlideSync.Plugin.Automation
{
    /// <summary>
    /// 触发器「课件讲到第 N 页」的设置界面：一个页码 + 一个「任何课件都算」的勾。
    ///
    /// 纯 C# 搭界面（不写 .axaml）：插件工程没有 <c>AvaloniaResource Include="**\*.axaml"</c> 的
    /// 默认 glob，新增 .axaml 必须改 .csproj，而本次改动范围不含工程文件。
    /// ClassIsland 只要求控件继承 <see cref="TriggerSettingsControlBase{TSettings}"/>。
    ///
    /// 注册方式与行动一致：<c>services.AddTrigger&lt;TTrigger, TSettingsControl&gt;()</c> 把触发器
    /// 和这个控件各注册成一条 keyed transient，key = TriggerInfo.Id；宿主打开"触发器设置抽屉"时
    /// 按这个 Id 取控件，再调 <c>GetInstance(info, ref settings)</c> 把设置对象回填进来。
    ///
    /// 时序坑（和 Automation\ActionSettingsControls.cs 里记的是同一个）：宿主先 new 控件、
    /// 后注入 SettingsInternal，所以构造函数里读 <c>Settings</c> 只能读到 null，
    /// 必须等挂上可视树（AttachedToVisualTree）再回填，并且每次写回设置前都要重新取一遍。
    /// </summary>
    public sealed class SlidePageReachedSettingsControl : TriggerSettingsControlBase<SlidePageReachedSettings>
    {
        readonly TextBox pageBox = new TextBox
        {
            Width = 120,
            Watermark = "1 - 999",
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        readonly CheckBox anyDeckBox = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "任何课件都算（不限于正在放映的那一份）",
                TextWrapping = TextWrapping.Wrap,
            },
        };

        // 回填设置时会把 TextChanged 触发一遍，那个守卫防止"读设置"被当成"老师改设置"再写回去。
        bool loading;

        public SlidePageReachedSettingsControl()
        {
            var panel = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 8),
            };

            panel.Children.Add(Label("讲到第几页"));
            panel.Children.Add(pageBox);
            panel.Children.Add(Hint(
                "1 - 999，默认 1。同一节课的同一份课件只触发一次：老师翻回这一页再看一遍不会重复触发，"
                + "换课件或换下一节课后重新算。"));

            panel.Children.Add(anyDeckBox);
            panel.Children.Add(Hint(
                "不勾 = 只认正在放映的那份课件（推荐，避免编辑视图里翻页把自动化点着）。"
                + "勾上 = 演示软件里打开着的课件翻到这一页也算。"));

            Content = panel;

            pageBox.TextChanged += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings == null) return;
                if (int.TryParse((pageBox.Text ?? "").Trim(), out int page))
                {
                    settings.Page = Math.Clamp(page, SlidePageReachedTrigger.MinPage, SlidePageReachedTrigger.MaxPage);
                }
            };

            // 失焦时把输入框里的字改成夹过范围的值（老师填 0 或 5000 时要看得到它变成了几）。
            pageBox.LostFocus += (_, _) => NormalizePage();

            anyDeckBox.Click += (_, _) =>
            {
                if (loading) return;
                var settings = TrySettings();
                if (settings != null) settings.AnyDeck = anyDeckBox.IsChecked == true;
            };

            AttachedToVisualTree += (_, _) => LoadFromSettings();
        }

        SlidePageReachedSettings? TrySettings()
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

        void LoadFromSettings()
        {
            var settings = TrySettings();
            if (settings == null) return;

            loading = true;
            try
            {
                pageBox.Text = Math.Clamp(settings.Page, SlidePageReachedTrigger.MinPage, SlidePageReachedTrigger.MaxPage)
                    .ToString();
                anyDeckBox.IsChecked = settings.AnyDeck;
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

            int page = Math.Clamp(settings.Page, SlidePageReachedTrigger.MinPage, SlidePageReachedTrigger.MaxPage);
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

        static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            };
        }

        static TextBlock Hint(string text)
        {
            return new TextBlock
            {
                Text = text,
                Opacity = 0.7,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            };
        }
    }
}
