using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using ClassSlideSync.Plugin.Core;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Views.SettingsPages;

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
