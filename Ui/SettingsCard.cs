using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ClassSlideSync.Plugin.Ui;

/// <summary>
/// 设置卡片。外观照 ClassIsland 设置页的 SettingsExpander，但**不引用 FluentAvalonia**。
///
/// ClassIsland 2.2（2.1.2.0）把 FluentAvalonia 从 2.4.1 升到 3.0，所有控件统一加了 FA 前缀
/// （SettingsExpander → FASettingsExpander）。插件是按 2.1 的 SDK 编译的，XAML 里写死
/// FluentAvalonia.UI.Controls.SettingsExpander，在 2.2 宿主上 InitializeComponent 直接抛
/// TypeLoadException，整个设置页导航失败。宿主旧版有、新版没有的类型都不能再出现在 XAML 里，
/// 所以这里用 Avalonia 原生控件自己拼一张卡片，两代宿主通吃。
/// </summary>
public class SettingsCard : HeaderedContentControl
{
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Description));

    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Glyph));

    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Footer));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsExpanded));

    /// <summary>标题下面那一行灰色说明。</summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>左侧 Fluent 字形（如 "&#xE8A7;"）。空字符串则不显示图标。</summary>
    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>卡片右侧的控件（开关、按钮、滑条…）。</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>只有 Content 不为空时才有展开箭头；默认收起。</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    Button? _chevron;
    ContentPresenter? _body;
    TextBlock? _description;
    Viewbox? _icon;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_chevron != null)
            _chevron.Click -= OnChevronClick;

        _chevron = e.NameScope.Find<Button>("PART_Chevron");
        _body = e.NameScope.Find<ContentPresenter>("PART_Body");
        _description = e.NameScope.Find<TextBlock>("PART_Description");
        _icon = e.NameScope.Find<Viewbox>("PART_Icon");

        if (_chevron != null)
            _chevron.Click += OnChevronClick;

        UpdateParts();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty ||
            change.Property == IsExpandedProperty ||
            change.Property == DescriptionProperty ||
            change.Property == GlyphProperty)
        {
            UpdateParts();
        }
    }

    void OnChevronClick(object? sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    void UpdateParts()
    {
        var hasBody = Content != null;
        if (_chevron != null)
        {
            _chevron.IsVisible = hasBody;
            if (_chevron.RenderTransform is RotateTransform rotate)
                rotate.Angle = IsExpanded ? 180 : 0;
        }

        if (_body != null)
            _body.IsVisible = hasBody && IsExpanded;

        if (_description != null)
            _description.IsVisible = !string.IsNullOrWhiteSpace(Description);

        if (_icon != null)
            _icon.IsVisible = !string.IsNullOrWhiteSpace(Glyph);
    }
}

