using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Views.SettingsPages;

/// <summary>
/// 设置页里那几个"颜色 / 圆角"控件需要真正的 Color、double 和"跟随还是自定义"三态，
/// 而 <see cref="SyncSettings"/> 里存的是十六进制字符串和可空整数。
/// 这个小视图模型负责翻译，并在设置变化时让绑定重新取值（PropertyChanged 传空字符串 = 全部刷新）。
/// </summary>
public sealed class SlideSyncAppearanceViewModel : INotifyPropertyChanged
{
    readonly SyncSettings _settings;
    readonly SlideSyncService _service;

    public SlideSyncAppearanceViewModel(SyncSettings settings, SlideSyncService service)
    {
        _settings = settings;
        _service = service;
        _settings.PropertyChanged += (_, _) => Refresh();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>当前实际生效的外观；服务还没起来时现算一份，设置页也不会显示成空白。</summary>
    CardAppearance Look
    {
        get { return _service != null && _service.Appearance != null ? _service.Appearance : CardAppearance.Resolve(_settings, null); }
    }

    public string ThemeSummary
    {
        get
        {
            var theme = _service != null ? _service.Theme : null;
            if (theme == null || !theme.Loaded)
            {
                return "还没读到 ClassIsland 的外观设置，暂时用内置配色。";
            }
            return "来自 ClassIsland：" + theme.Describe();
        }
    }

    public string CardSummary
    {
        get
        {
            var look = Look;
            return string.Format(
                CultureInfo.CurrentCulture,
                "当前卡片：{0} × {1} 像素，不透明度 {2}%，圆角 {3}，正文字号 {4}，{5}，垂直位置 {6}%。",
                look.CardWidth, look.CardHeight, (int)Math.Round(look.BgAlpha / 255.0 * 100), look.Radius,
                look.FontBody + look.FontDelta, look.OnRight ? "贴屏幕右边" : "贴屏幕左边", look.VerticalPercent);
        }
    }

    /// <summary>插件此刻是待命还是空闲 —— 老师一眼能看出「上课前 2 分钟自动开启」有没有生效。</summary>
    public string RuntimeSummary
    {
        get
        {
            if (_service == null) return "插件还没启动。";
            string hideNote = _settings.ShowCardOutsideClass
                ? "只要课件打开着就显示，不管是不是上课时间。"
                : "课表时段外不显示，下课铃一响就隐藏（后台照旧记页码）。";
            if (_service.Armed)
            {
                var why = string.IsNullOrEmpty(_service.ArmReason) ? "" : "（" + _service.ArmReason + "）";
                return "插件正在待命" + why + "：打开课件先弹小窗，15 秒后自己贴边收起；" + hideNote
                     + "关闭课件或时段结束后自动退出。";
            }
            return "插件当前空闲：不轮询、不显示任何东西；上课前 " + _settings.PreClassLeadSeconds + " 秒自动开启。";
        }
    }

    /// <summary>0 = 贴右边，1 = 贴左边。</summary>
    public int SideIndex
    {
        get { return _settings.CardOnLeft ? 1 : 0; }
        set { _settings.CardOnLeft = value == 1; }
    }

    public Color AccentColor
    {
        get { return FromHex(_settings.AccentColor, Look.AccentR, Look.AccentG, Look.AccentB); }
        set { _settings.AccentColor = ToHex(value); }
    }

    public Color BackgroundColor
    {
        get { return FromHex(_settings.CardBackgroundColor, Look.BgR, Look.BgG, Look.BgB); }
        set { _settings.CardBackgroundColor = ToHex(value); }
    }

    public bool CustomRadius
    {
        get { return _settings.CardCornerRadius >= 0; }
        set
        {
            _settings.CardCornerRadius = value ? Math.Max(0, (int)Math.Round((double)Look.Radius)) : -1;
            Refresh();
        }
    }

    public double CornerRadius
    {
        get { return _settings.CardCornerRadius >= 0 ? _settings.CardCornerRadius : Math.Round((double)Look.Radius); }
        set
        {
            // 拖动滑块本身就是在说"我要自己定圆角"，顺手把开关扳到自定义，省得老师先找开关。
            _settings.CardCornerRadius = (int)Math.Round(Math.Clamp(value, 0, 24));
            Refresh();
        }
    }

    public void Refresh()
    {
        var handler = PropertyChanged;
        if (handler != null) handler(this, new PropertyChangedEventArgs(string.Empty));
    }

    static Color FromHex(string hex, int fr, int fg, int fb)
    {
        Color parsed;
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out parsed))
        {
            return parsed;
        }
        return Color.FromRgb((byte)fr, (byte)fg, (byte)fb);
    }

    static string ToHex(Color c)
    {
        return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
    }
}
