using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassSlideSync.Plugin.Automation;
using ClassSlideSync.Plugin.Services;

namespace ClassSlideSync.Plugin.Components
{
    /// <summary>
    /// 主界面组件「课件进度」：在课堂大屏角落显示当前课件讲到第几页，正文形如 <c>12 / 24</c>。
    ///
    /// 取证（ClassIsland 2.1.0.1 程序集，Mono.Cecil 只读元数据，详见 stability-report-component.txt）：
    ///   * 组件基类 <c>ClassIsland.Core.Abstractions.Controls.ComponentBase</c>（abstract，: UserControl），
    ///     只有一个 internal 的 <c>SettingsInternal</c> 与 <c>OnMigrated</c>，没有 OnInitialized 之类的虚方法
    ///     —— 所以生命周期只能挂在 Avalonia 的 <c>OnAttachedToVisualTree</c>/<c>OnDetachedFromVisualTree</c> 上；
    ///   * 注册靠 <c>ComponentRegistryExtensions.AddComponent&lt;TComponent&gt;()</c>，它反射找 <c>[ComponentInfo]</c>、
    ///     校验 GUID 不重复，然后 <c>services.AddTransient(组件类型)</c> —— 组件实例由宿主的 DI 容器创建，
    ///     因此保留一个公共无参构造即可（本组件没有设置项，用非泛型 ComponentBase，和内置「日期」「分割线」同构）；
    ///   * <c>ComponentInfo(string guid, ...)</c> 内部是 <c>Guid.Parse(guid)</c>，第一个参数**必须是合法 GUID 字符串**，
    ///     写 "ClassSlideSync.Plugin.Xxx" 这种带点前缀会直接抛 FormatException。
    ///
    /// 界面为什么用纯 C# 搭：本插件的 csproj 不能改，新增 .axaml 会给构建产物清单引入不确定性，
    /// 而这里只有两行文字，C# 里直接 new 更清楚。
    ///
    /// 字体与主题：不写死 FontSize/Foreground。宿主 <c>ComponentPresenter</c> 会给根 Border 设
    /// <c>TextElement.FontSize = {DynamicResource MainWindowBodyFontSize}</c> 与前景色（继承型属性），
    /// 这里的两个 TextBlock 只额外用 DynamicResource 取了「次要」「强调」两档字号（和内置「时钟」「天气」同款），
    /// 于是用户改主界面字体/主题时本组件自动跟着变。
    ///
    /// 隐藏：宿主没有「子控件隐藏 → 整个 presenter 折叠」的钩子（<c>ComponentSettings.IsVisible</c> 的 setter 是
    /// internal，插件改不了；宿主那套是按规则集隐藏）。插件侧唯一不越权的手段就是组件自身 <c>IsVisible = false</c>：
    /// Avalonia 里隐藏的控件不测量、不布局、不绘制，presenter 的 Border 因此期望尺寸为 0，不会留下占位符。
    /// </summary>
    [ComponentInfo(Id, Name, Glyph, Description)]
    public sealed class DeckProgressComponent : ComponentBase
    {
        /// <summary>
        /// 组件的唯一 Id。<c>[ComponentInfo]</c> 的第一参会被 <c>Guid.Parse</c>，所以必须是 GUID 形式，
        /// 不能像触发器的 <c>TriggerInfo</c> 那样用带点前缀的字符串（那是 string 类型 Id）。
        /// 这个值一旦发布就不能再改：用户的主界面配置按它保存。
        /// </summary>
        public const string Id = "6F2C4B18-9A3D-4E57-8B21-5C7E0D9A4F63";

        /// <summary>组件库里显示的名字（也是组件上的小标题）。</summary>
        public const string Name = "课件进度";

        /// <summary>
        /// 组件库里的图标。Segoe Fluent Icons 的单字符字形（长度 1 时宿主直接包成 FluentIconSource），
        /// \uE8A5 = Document，对应「课件」。
        /// </summary>
        public const string Glyph = "\uE8A5";

        /// <summary>组件库里的说明文字。</summary>
        public const string Description = "显示当前课件讲到第几页（如 12 / 24）。没有课件在放映时自动隐藏。";

        readonly TextBlock valueText;

        /// <summary>组件内容。隐藏时整块摘掉，避免宿主把子元素挪进自己的面板后 IsVisible 管不住。</summary>
        readonly StackPanel body;

        /// <summary>
        /// 当前挂着的服务。null = 没订阅（组件刚创建、已卸载，或宿主还没把服务注册进来）。
        /// 用字段而不是每次现取，是为了退订时能精确 -= 掉自己挂的那个实例。
        /// </summary>
        SlideSyncService? service;

        /// <summary>上一次的可见状态，用来只在切换时写一行日志（不然每秒一条）。</summary>

        /// <summary>
        /// 最近一次收到的进度快照。自测读它判断"事件有没有真的进来"；宿主不用它。
        /// 用快照而不用服务上的 Progress，是为了避免测试里真去构造服务。
        /// </summary>
        public SlideProgress? LastProgress { get; private set; }

        /// <summary>刷新了几次界面。自测用，宿主不用。</summary>
        public int RefreshCount { get; private set; }

        /// <summary>现在是否挂着进度事件。自测用。</summary>
        public bool IsAttached => service != null;

        /// <summary>空闲时第一行文案（2026-10-25 用户：「显示个『课件续页』」）。</summary>
        const string IdleTitle = "课件续页";

        /// <summary>空闲时第二行文案。用「等待放映」而不是「正在检测 ppt」：
        /// 这句要和卡片自己空闲时的措辞一致，也不把实现细节（检测）摆给老师看。</summary>
        const string IdleHint = "等待放映";

        /// <summary>课件开着但没在放映（老师在编辑视图）时的文案。</summary>
        const string OpenedHint = "已打开课件";

        public DeckProgressComponent()
        {
            // 两行：小标题 + 页码。字号取宿主资源键，用户改主界面字体时跟着变；取不到时退回继承值。
            var titleText = new TextBlock { Text = IdleTitle, Opacity = 0.85 };
            titleText.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("MainWindowSecondaryFontSize"));

            valueText = new TextBlock { Text = IdleHint };
            valueText.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("MainWindowEmphasizedFontSize"));

            body = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    titleText,
                    valueText,
                },
            };
            Content = body;

            // 不隐藏：宿主会给每个组件套自己的容器，藏了只剩一块空白（2026-10-25 用户实测），
            // 所以常驻显示「课件续页 / 正在检测 ppt」，有课件时再把第二行换成页码。
            IsVisible = true;
        }

        /// <summary>
        /// 正文文字。抽成纯函数是为了自测能直接断言格式：0/24、12/24、24/24 都是 "<c>当前 / 总数</c>"。
        /// 负数（引擎还没读到页码时可能给 0，将来可能出现别的哨兵值）一律按 0 显示，绝不显示 "-1 / 24"。
        /// </summary>
        public static string FormatProgress(int current, int total)
        {
            int c = current < 0 ? 0 : current;
            int t = total < 0 ? 0 : total;
            return c + " / " + t;
        }

        /// <summary>
        /// 该不该显示。判据就两条：演示软件里打开着课件（<see cref="SlideProgress.DeckOpen"/>），
        /// 并且真的在放映（<see cref="SlideProgress.Showing"/>）。
        ///
        /// 只看 Showing 会漏掉"课件已经打开、老师还在编辑视图"这种状态——那时页码是会跳的，但没在讲；
        /// 反过来只看 DeckOpen 又会让"打开了三份课件、正在放其中一份"时显示错的页码。
        /// 总页数为 0（还没解析出来）时仍然显示：这时候显示 "3 / 0" 好过整块消失，老师能看出是识图还没跟上。
        /// </summary>
        public static bool ShouldShow(SlideProgress? progress)
        {
            return progress != null && progress.DeckOpen && progress.Showing;
        }

        /// <summary>
        /// 挂上进度事件，并立刻用当前快照刷一次（组件可能是在课件已经翻到第 12 页之后才被拖到主界面上的，
        /// 那种情况下不该等到下一次翻页才有内容）。
        ///
        /// <paramref name="target"/> 只在自测里显式传入；宿主路径走 <see cref="Attach()"/> 里的统一取法。
        /// 幂等：同一个服务重复挂不会挂两份回调。
        /// </summary>
        public void Attach(SlideSyncService? target = null)
        {
            SlideSyncService? svc;
            try
            {
                svc = target ?? OpenUnfinishedDeckAction.TryService();
            }
            catch (Exception ex)
            {
                Log("组件取服务失败: " + ex.Message);
                return;
            }

            if (svc == null) return;
            if (ReferenceEquals(svc, service))
            {
                // 已经挂着了，只需要按当前状态刷一次（宿主可能刚重建过视觉树）。
                // 这里也要补一次定时器：第一次挂上时可能因为服务没就绪提前返回过。
                    try { Apply(svc.Progress); } catch { }
                return;
            }

            Detach();
            service = svc;
            try
            {
                svc.SlideProgressChanged += OnProgress;
            }
            catch (Exception ex)
            {
                service = null;
                Log("组件订阅进度失败: " + ex.Message);
                return;
            }

            try { Apply(svc.Progress); } catch { }
        }

        /// <summary>
        /// 退订。宿主随时可能把组件从主界面拿掉（换组件配置、进设置页、退出程序），
        /// 不退订就会让服务一直攥着一个已经没人看的控件。
        /// </summary>
        public void Detach()
        {
            var svc = service;
            service = null;
            if (svc == null) return;
            try { svc.SlideProgressChanged -= OnProgress; } catch { }
        }

        /// <summary>
        /// 把一份快照刷到界面上。public 是为了自测直接喂快照（不需要真开 PowerPoint，也不需要跑事件循环）。
        /// 只碰控件、不碰服务，所以可以在没有服务的状态下单独测。
        /// </summary>
        public void Apply(SlideProgress? progress)
        {
            LastProgress = progress;
            bool live = ShouldShow(progress);
            if (live && progress != null)
            {
                valueText.Text = FormatProgress(progress.Current, progress.SlideCount);
            }
            else
            {
                // 没课件时不再留空，也不把「检测」这种实现细节写在屏幕上：
                // 开着课件但没放映 → 「已打开课件」；什么都没有 → 「等待放映」。
                valueText.Text = progress != null && progress.DeckOpen ? OpenedHint : IdleHint;
            }

            IsVisible = true;   // 常驻：宿主容器的空白比这行字更难看
            Opacity = 1;
            try
            {
                if (Content == null) Content = body;
                body.IsVisible = true;
            }
            catch { }

            RefreshCount++;
        }

        /// <summary>
        /// 服务器（引擎线程）每次状态心跳都会抛这个事件，必须切回 UI 线程再改控件。
        /// 没有 UI 线程（自测环境）时退回当前线程直接改——那时本来也没有别的线程在碰控件。
        /// </summary>
        void OnProgress(SlideProgress progress)
        {
            try
            {
                var dispatcher = Dispatcher.UIThread;
                if (dispatcher.CheckAccess()) Apply(progress);
                else dispatcher.Post(() => Apply(progress), DispatcherPriority.Background);
            }
            catch
            {
                try { Apply(progress); } catch { }
            }
        }

        /// <summary>组件挂进视觉树（被摆到主界面/组件设置预览里）时挂事件。</summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Attach();
        }

        /// <summary>组件被摘出视觉树时退订。</summary>
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Detach();
            base.OnDetachedFromVisualTree(e);
        }

        void Log(string line)
        {
            try { service?.Ledger?.Log(line); } catch { }
        }
    }
}
