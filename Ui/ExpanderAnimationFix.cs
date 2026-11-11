using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FluentAvalonia.UI.Controls;

namespace ClassSlideSync.Plugin.Ui;

/// <summary>
/// FluentAvalonia 2.4.1 会给每个 SettingsExpander 模板里的 Expander 挂 FluentV2 动画。
/// 这张卡片作为另一个 SettingsExpander 的内容、第一次在外层展开动画里被 Measure 时，
/// 动画代码去取还没挂到合成器上的 Border 的 visual（null）并解引用，
/// 抛 NullReferenceException（日志里点“高级选项”就崩）。
/// 在子卡片模板应用时把动画关掉：setter 本地值为 null，优先于主题里的 FluentV2，
/// 展开/收起照常，只是没有位移过渡。
/// </summary>
internal static class ExpanderAnimationFix
{
    /// <summary>
    /// 关掉 <paramref name="panel"/> 里每个 SettingsExpander 的展开动画。
    /// 只用于“高级选项”这种嵌套卡片组；正常平级卡片保持原有动画。
    /// </summary>
    public static void DisableForChildren(Panel? panel)
    {
        if (panel == null)
            return;

        foreach (var child in panel.Children)
        {
            if (child is SettingsExpander expander)
                expander.TemplateApplied += OnTemplateApplied;
        }
    }

    private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        // 内层 Expander 刚从模板里创建，还没开始自己的 Measure/ApplyTemplate；
        // 此时清掉动画，ExpanderExt 来不及订阅它的 TemplateApplied。
        var inner = e.NameScope.Find<Expander>("Expander");
        inner?.SetValue(ExpanderExt.ExpanderAnimationTypeProperty, null);
    }
}
