using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace ClassSlideSync.Plugin.Views.SettingsPages;

/// <summary>
/// 非 Windows 平台上注册的唯一设置页：只读，一句话说明「本平台不可用」。
///
/// 为什么要有它：插件 dll 在 Linux 上照样会被加载（宿主不按 TFM/OS 过滤），如果什么都不注册，
/// 设置 → 插件 里就只剩一个空条目，老师会以为插件坏了。注册一个只读页既解释了原因，也给了
/// 「这个插件确实被识别到了」的证据。
///
/// 刻意不用 XAML：这个页面只有两行字，用代码搭比再加一个 .axaml 更不容易被这台机器上的
/// Avalonia XAML 编译器挑刺（插件里其它页面已经有 AVLN3000/AVLN3001 的历史问题）。
/// 页面里没有任何可交互控件 —— 「只读」是字面意思，改不了任何设置。
/// </summary>
[SettingsPageInfo("classslidesync.unavailable", "课件续页", "\uE7C3", "\uE7C3")]
public sealed class PlatformUnavailablePage : SettingsPageBase
{
    public PlatformUnavailablePage()
    {
        var title = new TextBlock
        {
            Text = "课件续页",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        };

        var body = new TextBlock
        {
            Text = "本插件依赖 Windows 的窗口与 COM 自动化，当前平台不可用。",
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24),
        };
        panel.Children.Add(title);
        panel.Children.Add(body);

        Content = panel;
    }
}
