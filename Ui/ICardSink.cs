using System;
using ClassSlideSync.Plugin.Core;

namespace ClassSlideSync.Plugin.Ui
{
    /// <summary>
    /// 浮层卡片的「接收端」接口：SlideSyncService 只通过它跟卡片说话。
    ///
    /// 为什么要抽这一层：同一份 dll 要同时跑在 Windows 和 Linux 上。Windows 的卡片是
    /// <see cref="NativeOverlayCard"/>（自建 Win32 分层窗口 + GDI 绘制，2200+ 行），
    /// Linux 的卡片是 <see cref="LinuxCardWindow"/>（Avalonia Window，同一套版式）。
    /// 服务不该知道这些差别 —— 它只做「配置、推状态、显示/收起、跳页/面板事件」。
    ///
    /// 约定：
    ///   * 所有方法都可以从任意线程调用；实现自己负责切到自己的 UI 线程（Win32 卡片有自己的
    ///     消息线程，Avalonia 卡片刻意 Post 到 Dispatcher.UIThread）。
    ///   * 实现不得把异常抛回服务：卡片出问题只该是「这一帧没画出来」，不能是「插件崩了」。
    ///   * <see cref="Show"/> / <see cref="Hide"/> 是语义化别名：Windows 分别落到
    ///     Expand() / HideForAutomation()，行为与重构前完全一致。
    ///   * <see cref="IsCreated"/> / <see cref="IsHidden"/> 是自动化「切换悬浮卡片」判断用的
    ///     真实可见性（armed 为 true 不代表卡片真的在屏幕上）。
    /// </summary>
    public interface ICardSink : IDisposable
    {
        /// <summary>点「从第 N 页接着讲」。</summary>
        event Action? JumpRequested;

        /// <summary>点右上角齿轮（打开控制面板）。</summary>
        event Action? PanelRequested;

        /// <summary>拖动卡片松手后：(是否贴右侧, 竖直百分比 0..100)。</summary>
        event Action<bool, int>? PositionChanged;

        /// <summary>历史面板里点某一份课件（Linux 卡片暂不提供历史面板，事件保留）。</summary>
        event Action<string, int>? HistoryJumpRequested;

        /// <summary>「打开课件并接着讲」（Linux 没有外部打开器，事件保留）。</summary>
        event Action<string, int>? OpenDeckRequested;

        /// <summary>状态日志出口，由服务接到 app.log。可空；实现内部要判空。</summary>
        Action<string>? Log { get; set; }

        /// <summary>窗口是否已经真的建出来（Windows: hwnd 非空；Linux: Avalonia Window 已 Show）。</summary>
        bool IsCreated { get; }

        /// <summary>是否被「收起」（老师的自动隐藏/自动化隐藏），不代表 Disarm。</summary>
        bool IsHidden { get; }

        /// <summary>卡片自述，诊断日志用。</summary>
        string Diag();

        /// <summary>启动卡片（Windows 起消息线程；Linux 只准备 Avalonia 窗口，不显示）。</summary>
        void Start();

        /// <summary>
        /// 外观 + 行为配置。autoHideSeconds 在 5..600 之间；showPanel=false 时齿轮不响应。
        /// </summary>
        void Configure(CardAppearance appearance, bool showOnStart, bool autoHide, int autoHideSeconds, bool showPanel = true);

        /// <summary>只换外观（ClassIsland 换主题/设置页改颜色时）。</summary>
        void SetAppearance(CardAppearance appearance);

        /// <summary>推一帧完整状态（Windows 引擎每秒好几次）。</summary>
        void Apply(CardState state);

        /// <summary>临时状态行（「正在跳转…」「打不开课件」这类），不覆盖整帧数据。</summary>
        void ShowStatus(string text);

        /// <summary>现在显示卡片（对应 Windows 的 Expand：清掉抑制、20 秒预览期）。</summary>
        void Show();

        /// <summary>收起卡片（对应 Windows 的 HideForAutomation：只收卡片，不拆引擎）。</summary>
        void Hide();

        /// <summary>展开/唤回卡片（自动化「显示悬浮卡片」用的那条路）。</summary>
        void Expand();
    }
}
