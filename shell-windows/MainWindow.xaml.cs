using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using XTrader.Shell.Core;
using XTrader.Shell.State;
using XTrader.Shell.Theme;
using XTrader.Shell.Tray;
using XTrader.Shell.Views;
using Windows.Graphics;

namespace XTrader.Shell;

/// <summary>
/// 主窗口：顶部操作栏 + 侧边导航 + 底部状态栏（对应 mac 壳的 RootView）。
///
/// 页面切换用 Frame.Navigate；状态指标由 <see cref="AppState"/> 的
/// PropertyChanged 驱动刷新；主题切换与画刷在代码里直接改（不用 x:Bind，
/// 避免 XAML 编译期属性解析的额外风险）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly TrayIcon _tray;
    private bool _syncingToggle;
    /// <summary>启动预检拦截弹窗是否已弹出（避免状态刷新重复打开）</summary>
    private bool _issuesDialogOpen;
    /// <summary>版本胶囊的瞬时提示（已是最新/更新失败）：非空时覆盖版本号</summary>
    private string? _versionHint;
    private string? _versionHintTooltip;
    /// <summary>瞬时提示的序号：定时清除时用它判定期间是否已有新提示</summary>
    private int _versionHintSerial;
    /// <summary>瞬时提示的清除定时器（持引用，避免被 GC 回收后不再触发）</summary>
    private DispatcherQueueTimer? _versionHintTimer;
    /// <summary>「发现新版本」确认框是否已打开（防止重复弹出）</summary>
    private bool _updatePromptOpen;
    /// <summary>本次点击触发的检查是否还在等结果：只有用户主动点出来的新版本才弹确认框</summary>
    private bool _awaitingUpdatePrompt;

    private const string VersionTooltip = "点击检查并更新";

    /// <summary>导航项（与 <see cref="_navRoutes"/> 同序），索引即路由</summary>
    private readonly List<Button> _navItems = [];
    private readonly List<TextBlock> _navLabels = [];
    /// <summary>当前路由标签（导航项着色据此判定选中态）</summary>
    private string _currentTag = "";
    /// <summary>鼠标悬停中的导航项标签：主题切换后重刷配色时仍要保留悬停态</summary>
    private readonly HashSet<string> _hoveredTags = [];
    /// <summary>鼠标悬停中的顶栏按钮：主题切换后重刷配色时仍要保留悬停态</summary>
    private readonly HashSet<Button> _hoveredTopBarButtons = [];

    /// <summary>路由标签与标题（顺序与 mac 壳的 AppRoute 一致）</summary>
    private static readonly (string Tag, string Title)[] _routes =
    [
        ("dashboard", "仪表盘"),
        ("token", "Token 管理"),
        ("sim", "模拟交易"),
        ("live", "实盘交易"),
        ("log", "系统日志"),
        ("settings", "设置"),
    ];

    /// <summary>
    /// 侧边栏实际展示的路由。Token 管理暂时隐藏（Token 已在模拟页就地申请、查看），
    /// 页面与路由分发保留，后续需要时把 "token" 从排除项去掉即可。
    /// </summary>
    private static readonly (string Tag, string Title)[] _navRoutes =
        _routes.Where(r => r.Tag != "token").ToArray();

    /// <summary>主题偏好存储键：持久化到 ShellPrefs，重启后保持</summary>
    private const string ThemePrefKey = "theme_is_dark";

    /// <summary>当前是否深色（页面据此取色）</summary>
    public bool IsDark { get; private set; }

    /// <summary>主题变化通知（页面订阅）</summary>
    public event Action? ThemeChanged;

    public MainWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        Title = "iTrader 智能交易系统";
        SetInitialSize(1020, 760);

        var savedTheme = ShellPrefs.Get(ThemePrefKey);
        IsDark = savedTheme is not null
            ? savedTheme is "1" or "true"
            : Application.Current.RequestedTheme == ApplicationTheme.Dark;
        BuildNav();
        ApplyThemeToChrome();

        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.UpdatePhase))
            {
                NotifyUpdatePhase(_state.UpdatePhase);
            }
            RefreshStatus();
        };
        RefreshStatus();

        // 完整更新装好后，updater 已拉起新壳：旧壳自行退出，不再向后端发 quit
        // （此时后端已由 updater 重启，旧壳的连接早已断开）
        _state.ExitRequested += ExitShellSilently;

        // 初始页面：第一个导航项
        NavigateTo(_routes[0].Tag);

        // 系统托盘：左键唤窗，右键菜单
        _tray = new TrayIcon(
            onShow: Restore,
            onQuitBackend: () => _state.QuitBackend(),
            onQuit: ExitApp);
        _tray.Add();

        Closed += (_, _) =>
        {
            _tray.Dispose();
            if (Application.Current is App app)
            {
                app.OnExiting();
            }
        };
    }

    /// <summary>把窗口唤回前台（托盘左键 / 第二个实例启动时调用）。</summary>
    public void Restore()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
    }

    private void SetInitialSize(int width, int height) =>
        AppWindow.Resize(new SizeInt32(width, height));

    // -------- 导航 --------

    /// <summary>
    /// 自绘侧栏导航项。
    ///
    /// 不用 NavigationView：它的面板宽度是固定值（OpenPaneLength），且选中态由
    /// 主题资源控制、是系统灰底。这里用 Button + 纯边框模板手写（见 App.xaml 的
    /// SidebarNavItemStyle），宽度随最长标题撑开（列定义 Auto），选中态用
    /// Palette.SidebarSelected（主题蓝）。保留 Button 是为了键盘焦点与激活。
    /// </summary>
    private void BuildNav()
    {
        for (var i = 0; i < _navRoutes.Length; i++)
        {
            var (tag, title) = _navRoutes[i];
            var label = new TextBlock
            {
                Text = title,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var item = new Button
            {
                Tag = tag,
                Content = label,
            };
            if (Application.Current.Resources.TryGetValue("SidebarNavItemStyle", out var style))
            {
                item.Style = (Style)style;
            }
            item.PointerEntered += (_, _) => SetNavHover(tag, true);
            item.PointerExited += (_, _) => SetNavHover(tag, false);
            item.Click += (_, _) => NavigateTo(tag);

            _navItems.Add(item);
            _navLabels.Add(label);
            NavItems.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(item, i);
            NavItems.Children.Add(item);
        }
    }

    private void SetNavHover(string tag, bool hovering)
    {
        if (hovering)
        {
            _hoveredTags.Add(tag);
        }
        else
        {
            _hoveredTags.Remove(tag);
        }
        ApplyNavItemState(tag);
    }

    /// <summary>
    /// 按当前路由与悬停状态刷新导航项配色。
    ///
    /// 外观对齐页内分段胶囊（如模拟交易页的「快速开始/账户信息」）：选中为实心主题蓝 + 白字半粗、
    /// 描边同色；未选中为透明底 + 发丝描边，悬停时底色与描边各提一档。
    /// </summary>
    private void ApplyNavItemState(string tag)
    {
        var index = Array.FindIndex(_navRoutes, r => r.Tag == tag);
        if (index < 0)
        {
            return;
        }

        var palette = ThemeTokens.For(IsDark);
        var selected = tag == _currentTag;
        var hovering = _hoveredTags.Contains(tag);
        _navLabels[index].Foreground = new SolidColorBrush(selected ? Microsoft.UI.Colors.White : palette.Dim);
        _navLabels[index].FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        _navItems[index].Background = new SolidColorBrush(
            selected
                ? palette.SidebarSelected
                : hovering ? palette.SidebarHover : Microsoft.UI.Colors.Transparent);
        _navItems[index].BorderBrush = new SolidColorBrush(
            selected
                ? palette.SidebarSelected
                : hovering ? palette.Dim : palette.Hairline);
    }

    /// <summary>供页面内按钮切换导航（如仪表盘的「配置设置」）。</summary>
    public void NavigateToTag(string tag) => NavigateTo(tag);

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "dashboard" => typeof(DashboardPage),
            "token" => typeof(TokenManagePage),
            "log" => typeof(TradingLogPage),
            "sim" => typeof(SimulationPage),
            "live" => typeof(LiveAccountPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };

        // sim/live 进入时预取品种与策略（与 mac 壳一致）
        if (tag is "sim" or "live")
        {
            _state.FetchSymbols();
            _state.FetchStrategies();
        }

        _currentTag = tag;
        foreach (var (routeTag, _) in _navRoutes)
        {
            ApplyNavItemState(routeTag);
        }

        ContentFrame.Navigate(pageType, new PageContext(_state, tag, this));
    }

    // -------- 顶部操作栏 --------

    /// <summary>
    /// 胶囊宽度变化时重算进度填充宽度：文案随阶段变化（如「自动交易：测试天勤连接…」）
    /// 会让胶囊变宽，而进度是按胶囊实际宽度换算的百分比。
    /// </summary>
    private void OnAutoTradePillSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_state.StartChecking && _state.StartProgressPct >= 0)
        {
            AutoTradeProgress.Width = e.NewSize.Width * _state.StartProgressPct / 100;
        }
    }

    private void OnAutoTradeToggled(object sender, RoutedEventArgs e)
    {
        // 初始化/状态回填阶段抑制 Toggled 的副作用，避免把 UI 同步当成用户操作
        if (_syncingToggle)
        {
            return;
        }
        _state.ToggleTrading(AutoTradeToggle.IsOn);
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        IsDark = !IsDark;
        ShellPrefs.Set(ThemePrefKey, IsDark ? "1" : "0");
        ApplyThemeToChrome();
        ThemeChanged?.Invoke();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        // 与 mac 壳一致：刷新即重新拉取品种/策略，当前页保持不变
        _state.FetchSymbols();
        _state.FetchStrategies();
    }

    /// <summary>服务器胶囊悬停：边框提亮一档，暗示整颗可点（同 mac 壳的胶囊悬停）</summary>
    private void OnServerPillPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var palette = ThemeTokens.For(IsDark);
        ServerPill.BorderBrush = new SolidColorBrush(palette.Dim);
        ServerPill.Background = new SolidColorBrush(palette.SidebarHover);
    }

    private void OnServerPillPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var palette = ThemeTokens.For(IsDark);
        ServerPill.BorderBrush = new SolidColorBrush(palette.Hairline);
        ServerPill.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>版本胶囊悬停：与服务器胶囊同一套反馈（描边提亮 + 淡底），暗示整颗可点</summary>
    private void OnVersionPillPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var palette = ThemeTokens.For(IsDark);
        VersionButton.BorderBrush = new SolidColorBrush(palette.Dim);
        VersionButton.Background = new SolidColorBrush(palette.SidebarHover);
    }

    private void OnVersionPillPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var palette = ThemeTokens.For(IsDark);
        VersionButton.BorderBrush = new SolidColorBrush(palette.Hairline);
        VersionButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>
    /// 顶栏普通按钮（深浅色 / 刷新 / 退出）悬停：与侧栏导航项同一套反馈
    /// （SidebarHover 底色 + 描边提亮）。顶栏是 Chrome 底色，Canvas 与它几乎同色，
    /// 拿 Canvas 做悬停底等于没有反馈。
    /// </summary>
    private void OnTopBarButtonPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _hoveredTopBarButtons.Add(button);
            ApplyTopBarButtonState(button);
        }
    }

    private void OnTopBarButtonPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _hoveredTopBarButtons.Remove(button);
            ApplyTopBarButtonState(button);
        }
    }

    private void ApplyTopBarButtonState(Button button)
    {
        var palette = ThemeTokens.For(IsDark);
        var hovering = _hoveredTopBarButtons.Contains(button);
        button.Background = new SolidColorBrush(
            hovering ? palette.SidebarHover : Microsoft.UI.Colors.Transparent);
        button.BorderBrush = new SolidColorBrush(hovering ? palette.Dim : palette.Hairline);
    }

    /// <summary>
    /// 服务器状态胶囊点击：弹出服务器信息（地址、连通性、探测方式、失败原因）。
    /// 连通性读后端心跳（每 10 秒探测 /api/health 后推送），不额外发请求；
    /// 改地址走地址行尾部的「编辑」（同 mac 壳）。代理只作用于天勤连接，留在设置页。
    /// </summary>
    private async void OnServerInfoClick(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var palette = ThemeTokens.For(IsDark);
        var online = _state.EngineStatus.ServerOnline;
        var statusColor = online ? ThemeTokens.StatusGreen : ThemeTokens.StatusRed;
        var serverUrl = _state.Config.ServerUrl.Trim();
        // 后端还没探过时 server_error 为空串，此时只能说「尚未探测到结果」
        var serverError = _state.EngineStatus.ServerError;
        var failureReason = serverError.Length == 0 ? "尚未探测到结果" : serverError;

        var rows = new StackPanel();
        ContentDialog? dialog = null;
        var editRequested = false;

        // 一行「标签 + 取值」：标签定宽对齐，地址等长文本允许选中复制。
        // trailing 用于地址行的「编辑」入口，排在取值右侧。
        void AddRow(string label, string value, Windows.UI.Color valueColor, bool selectable = false, FrameworkElement? trailing = null)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = new SolidColorBrush(palette.Dim),
                VerticalAlignment = VerticalAlignment.Top,
            });
            var valueText = new TextBlock
            {
                Text = value,
                FontSize = 15,
                Foreground = new SolidColorBrush(valueColor),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = selectable,
            };

            if (trailing is null)
            {
                Grid.SetColumn(valueText, 1);
                row.Children.Add(valueText);
            }
            else
            {
                // 地址与「编辑」顺序排、整体靠左（同 mac 壳：按钮紧跟地址之后）。
                // 水平 StackPanel 按无限宽测量，换行必须靠 MaxWidth 兜住，
                // 否则超长地址会把弹窗横向撑开。320 对齐 mac 460 宽弹窗里地址列的可用宽度。
                valueText.MaxWidth = 320;
                var valueRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                valueRow.Children.Add(valueText);
                valueRow.Children.Add(trailing);
                Grid.SetColumn(valueRow, 1);
                row.Children.Add(valueRow);
            }
            rows.Children.Add(row);
        }

        // 地址行尾部的「编辑」：描边胶囊，与模拟页各步骤的入口胶囊同一形态。
        // 二级对话框不能在 ContentDialog 打开期间叠加，所以先收起本弹窗，
        // 等 ShowAsync 返回后再打开编辑器
        var editButton = new Button
        {
            Content = "编辑",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(palette.Primary),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(palette.Hairline),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 0, 14, 0),
            MinWidth = 0,
            MinHeight = 0,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(editButton, "修改服务器地址");
        editButton.Click += (_, _) =>
        {
            editRequested = true;
            dialog?.Hide();
        };

        AddRow("服务器类型", "策略信号服务器", palette.Ink);
        AddRow(
            "服务器地址",
            serverUrl.Length == 0 ? "未配置" : serverUrl,
            serverUrl.Length == 0 ? palette.Warning : palette.Ink,
            selectable: true,
            trailing: editButton);
        AddRow("连接状态", online ? "正常" : "不可达", statusColor);
        // 失败原因单独成行并允许选中复制，方便用户排查或发给运维
        if (!online)
        {
            AddRow("失败原因", failureReason, palette.Danger, selectable: true);
        }
        AddRow("探测方式", "每 10 秒请求 /api/health", palette.Ink);

        var content = new StackPanel { Spacing = 0 };

        // 标题行：只有标题文字，右侧是关闭按钮（状态已在「连接状态」行体现）
        var headerText = new TextBlock
        {
            Text = "服务器信息",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(palette.Ink),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 与 Token 申请记录等弹窗一致：标题行右侧用文字「关闭」，底部不留按钮行
        var closeButton = new Button
        {
            Content = "关闭",
            FontSize = 13,
            MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(closeButton, "关闭");
        closeButton.Click += (_, _) => dialog?.Hide();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(headerText);
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(closeButton);

        content.Children.Add(header);
        content.Children.Add(rows);

        content.Children.Add(new TextBlock
        {
            Text = "客户端从该服务器获取交易品种、交易策略与交易信号。",
            FontSize = 13,
            Foreground = new SolidColorBrush(palette.Dim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            // 弹窗在独立的 popup 根下，不继承窗口根的主题，需要单独指定，
            // 否则深色模式下标签文字与背景会用浅色模板
            RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Content = content,
        };
        // 底部按钮行已移除，关闭只走右上角的叉（同 Token 记录 / 下载快期两个弹窗）
        await dialog.ShowAsync();
        if (editRequested)
        {
            // 上一个 ContentDialog 的关闭动画还没走完时再 ShowAsync 会抛
            // 「同一时间只能打开一个 ContentDialog」，等一帧以上再开编辑器
            await Task.Delay(150);
            await ShowServerEditorAsync();
        }
    }

    /// <summary>
    /// 「编辑服务器」二级对话框：改地址 + 测试连接。
    /// 输入完成（回车或失焦）即保存，与模拟页第 4 步共用同一份配置。
    /// </summary>
    private async Task ShowServerEditorAsync()
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var palette = ThemeTokens.For(IsDark);

        var serverBox = new TextBox
        {
            Text = _state.Config.ServerUrl,
            FontSize = 15,
            PlaceholderText = "https://itrader.redboil.us.ci",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var savedUrl = _state.Config.ServerUrl.Trim();

        // 输入完成（回车或失焦）即保存；空值视为放弃修改，回退到仍在生效的地址
        void SaveUrl()
        {
            var url = serverBox.Text.Trim();
            if (url.Length == 0)
            {
                serverBox.Text = savedUrl;
                return;
            }
            if (serverBox.Text != url)
            {
                serverBox.Text = url;
            }
            if (url == savedUrl)
            {
                return;
            }
            // 只提交服务器地址，代理沿用设置页里的现有值（后端按未提供字段保留）
            savedUrl = url;
            _state.SaveConfig(url, null);
        }

        serverBox.LostFocus += (_, _) => SaveUrl();

        // 测试结果就地显示在地址下方：ContentDialog 打开期间不能再叠一个 ContentDialog
        var testResult = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var testButton = new Button
        {
            Content = "测试连接",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        testButton.Click += async (_, _) =>
        {
            var url = serverBox.Text.Trim();
            if (url.Length == 0)
            {
                testResult.Text = "服务器地址不能为空";
                testResult.Foreground = new SolidColorBrush(palette.Danger);
                testResult.Visibility = Visibility.Visible;
                return;
            }
            testButton.IsEnabled = false;
            testButton.Content = "测试中...";
            testResult.Visibility = Visibility.Collapsed;
            var message = await _state.TestServerAsync(url);
            testButton.Content = "测试连接";
            testButton.IsEnabled = true;
            testResult.Text = message;
            testResult.Foreground = new SolidColorBrush(
                message.StartsWith("连接失败", StringComparison.Ordinal) ? palette.Danger : ThemeTokens.StatusGreen);
            testResult.Visibility = Visibility.Visible;
        };

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        inputRow.Children.Add(serverBox);
        inputRow.Children.Add(testButton);

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(inputRow);
        content.Children.Add(testResult);
        content.Children.Add(new TextBlock
        {
            Text = "这是 iTrader 智能交易服务器的地址，需与服务器部署地址一致。输入完成（回车或点到别处）即时保存生效。",
            FontSize = 13,
            Foreground = new SolidColorBrush(palette.Dim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Title = "编辑服务器",
            Content = content,
            CloseButtonText = "好",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
        // 关闭（点「好」或按 Esc）即视为输入完成：把没失焦的改动一并保存
        SaveUrl();
    }

    private bool _autoTradeHelpHovering;

    /// <summary>自动交易开关旁的问号：说明开关控制什么、启动检查项、关闭是否平仓。
    /// 文案与后端 _preflight_start_checks 的实际检查项逐条对应，改预检时同步这里。</summary>
    private async void OnAutoTradeHelpTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        await ShowHelpAsync("自动交易",
            "开启后客户端接收服务器推送的交易信号并自动下单。\n\n"
            + "开启前会先做启动检查：服务器连通、每个启用账户的品种与策略已选、账户凭据齐全（实盘还需期货公司、资金账号与交易密码）、"
            + "本地 Token 已领取、天勤账户握手通过。任一项不通过会列出全部问题并拒绝启动。\n\n"
            + "关闭只停止接收信号与下单，已持有的仓位不会被自动平仓。运行中修改账户配置需先关闭再重新开启才生效。");
    }

    private void OnAutoTradeHelpPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _autoTradeHelpHovering = true;
        ApplyAutoTradeHelpStyle();
    }

    private void OnAutoTradeHelpPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _autoTradeHelpHovering = false;
        ApplyAutoTradeHelpStyle();
    }

    private void ApplyAutoTradeHelpStyle()
    {
        var palette = ThemeTokens.For(IsDark);
        // 胶囊「开」态或启动进度已走满（文字转白）时，问号也跟着转白，与文案保持一致
        var isOn = _state?.EngineStatus.TradingActive == true
            || (_state?.StartChecking == true && _state.StartProgressPct >= 100);
        if (isOn)
        {
            AutoTradeHelpIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            AutoTradeHelpRing.BorderBrush = new SolidColorBrush(
                _autoTradeHelpHovering ? Windows.UI.Color.FromArgb(204, 255, 255, 255) : Windows.UI.Color.FromArgb(115, 255, 255, 255));
            AutoTradeHelpRing.Background = new SolidColorBrush(
                _autoTradeHelpHovering ? Windows.UI.Color.FromArgb(64, 255, 255, 255) : Microsoft.UI.Colors.Transparent);
        }
        else
        {
            AutoTradeHelpIcon.Foreground = new SolidColorBrush(
                _autoTradeHelpHovering ? palette.PrimaryHover : palette.Primary);
            AutoTradeHelpRing.BorderBrush = new SolidColorBrush(
                _autoTradeHelpHovering ? palette.Primary : palette.Hairline);
            AutoTradeHelpRing.Background = new SolidColorBrush(
                _autoTradeHelpHovering ? Windows.UI.Color.FromArgb((byte)(IsDark ? 55 : 30), palette.Primary.R, palette.Primary.G, palette.Primary.B) : Microsoft.UI.Colors.Transparent);
        }
    }

    private async Task ShowHelpAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                LineHeight = 24,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 版本胶囊点击：平时检查更新（发现新版本先弹确认框询问，确认后才下载）；
    /// 下载中表示取消。进度与结果都落在胶囊上。
    /// </summary>
    private void OnVersionClick(object sender, RoutedEventArgs e)
    {
        // 确认框开着或正在安装，忽略点击
        if (_updatePromptOpen || _state.UpdateInstalling)
        {
            return;
        }
        switch (_state.UpdatePhase)
        {
            // 下载中：点击即取消
            case UpdatePhase.Downloading:
                SetVersionHint(null, null);
                _state.CancelUpdate();
                return;
            // 下载完成即自动安装，此态通常一闪而过；若因取消竞争停在这里，
            // 点击即补上安装，否则用户没有出口
            case UpdatePhase.Ready:
                _state.ApplyUpdate();
                return;
            case UpdatePhase.Checking:
                return;
            // 已知道有新版本（启动静默检查发现的）：直接问，省掉一次网络往返
            case UpdatePhase.Available available:
                SetVersionHint(null, null);
                ShowUpdatePrompt(available);
                return;
        }
        // 清掉上一次的终态提示，进度文案由 RefreshVersionPill 按 phase 显示
        SetVersionHint(null, null);
        _awaitingUpdatePrompt = true;
        _state.CheckUpdate();
    }

    /// <summary>
    /// 「发现新版本」确认框。用户点「更新」才真正开始下载——没有确认就不下载。
    /// </summary>
    private async void ShowUpdatePrompt(UpdatePhase.Available available)
    {
        if (_updatePromptOpen || RootGrid.XamlRoot is null)
        {
            return;
        }
        _updatePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = $"发现新版本 {available.Version}",
                Content = (available.HasPatch ? "差量更新包" : "完整更新包")
                    + "。确认后自动下载并安装，完成后客户端会自动重启。",
                PrimaryButtonText = "更新",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                // 弹窗在独立的 popup 根下，不继承窗口根的主题
                RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _state.ConfirmUpdate();
            }
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    /// <summary>
    /// 更新终态的瞬时提示：已是最新/失败时把胶囊文字换成结论并挂上完整原因，
    /// 5 秒后自动回到版本号。检查、下载、安装阶段由胶囊自身的进度文案表达。
    ///
    /// 发现新版本只在「用户刚点了胶囊」时弹确认框；启动静默检查只亮红点。
    /// </summary>
    private void NotifyUpdatePhase(UpdatePhase phase)
    {
        switch (phase)
        {
            case UpdatePhase.Available available:
                if (_awaitingUpdatePrompt)
                {
                    _awaitingUpdatePrompt = false;
                    ShowUpdatePrompt(available);
                }
                break;
            case UpdatePhase.Latest latest:
                _awaitingUpdatePrompt = false;
                SetVersionHint("已是最新", latest.Message);
                break;
            case UpdatePhase.Failed failed:
                _awaitingUpdatePrompt = false;
                SetVersionHint("更新失败", failed.Message);
                break;
        }
    }

    /// <summary>设置胶囊瞬时提示；<paramref name="hint"/> 为 null 表示恢复版本号。</summary>
    private void SetVersionHint(string? hint, string? tooltip)
    {
        _versionHint = hint;
        _versionHintTooltip = tooltip;
        var serial = ++_versionHintSerial;
        RefreshVersionPill();
        if (hint is null)
        {
            return;
        }
        // 5 秒后自动恢复；期间若又出现新提示，serial 不匹配则放弃清除
        _versionHintTimer?.Stop();
        var timer = RootGrid.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (serial != _versionHintSerial)
            {
                return;
            }
            _versionHint = null;
            _versionHintTooltip = null;
            RefreshVersionPill();
        };
        _versionHintTimer = timer;
        timer.Start();
    }

    /// <summary>按当前更新状态刷新版本胶囊（版本号/进度/提示文案 + 红点）。</summary>
    private void RefreshVersionPill()
    {
        var palette = ThemeTokens.For(IsDark);
        var phase = _state.UpdatePhase;
        string text;
        var color = palette.Dim;
        var tooltip = _versionHintTooltip ?? VersionTooltip;
        switch (phase)
        {
            case UpdatePhase.Checking:
                text = "检查中…";
                break;
            case UpdatePhase.Available available:
                // 只发现未更新：保留版本号，靠红点与 tooltip 提示，不打断阅读
                text = VersionTextOf();
                tooltip = $"发现新版本 {available.Version}，点击更新";
                break;
            case UpdatePhase.Downloading:
                text = _state.UpdateProgress >= 0 ? $"取消 {_state.UpdateProgress}%" : "取消下载";
                color = palette.Warning;
                tooltip = "正在下载更新，点击取消";
                break;
            case UpdatePhase.Ready:
                text = "安装中…";
                color = palette.Warning;
                break;
            case UpdatePhase.Latest:
            case UpdatePhase.Failed:
                // 终态由瞬时提示接管；提示已过期则回到版本号
                text = _versionHint ?? VersionTextOf();
                color = _versionHint is null
                    ? palette.Dim
                    : (phase is UpdatePhase.Failed ? palette.Danger : palette.Success);
                break;
            default:
                text = _versionHint ?? VersionTextOf();
                break;
        }
        VersionText.Text = text;
        VersionText.Foreground = new SolidColorBrush(color);
        VersionBadge.Visibility = _state.UpdateBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        VersionBadge.Fill = new SolidColorBrush(palette.Danger);

        // 下载中在胶囊底部显示进度条；总量未知（-1）时不显示
        if (phase is UpdatePhase.Downloading && _state.UpdateProgress >= 0)
        {
            VersionProgress.Value = _state.UpdateProgress;
            VersionProgress.Foreground = new SolidColorBrush(palette.Primary);
            VersionProgress.Visibility = Visibility.Visible;
        }
        else
        {
            VersionProgress.Visibility = Visibility.Collapsed;
        }

        ToolTipService.SetToolTip(VersionButton, tooltip);
    }

    private string VersionTextOf() => _state.BackendVersion.Length > 0 ? _state.BackendVersion : "-";

    private async void OnQuitClick(object sender, RoutedEventArgs e)
    {
        // 交易运行中退出需确认（对应 mac 壳的确认弹窗）
        if (_state.EngineStatus.TradingActive)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "确认退出",
                Content = "自动交易正在运行，确定要退出吗？",
                PrimaryButtonText = "退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        ExitApp();
    }

    private void ExitApp()
    {
        _state.QuitBackend();
        _tray.Dispose();
        Application.Current.Exit();
    }

    /// <summary>
    /// 完整更新后的退出：新壳已由 updater 启动并接管后端，旧壳不能再发 app.quit
    /// （会把新壳刚连上的后端关掉），只做本地收尾。
    /// </summary>
    private void ExitShellSilently()
    {
        _tray.Dispose();
        Application.Current.Exit();
    }

    // -------- 状态刷新 --------

    private void RefreshStatus()
    {
        var engine = _state.EngineStatus;

        // 自动交易开关与引擎实际状态同步（回填时抑制 Toggled 副作用）
        _syncingToggle = true;
        AutoTradeToggle.IsEnabled = _state.ConnState == ConnState.Connected && !_state.StartChecking;
        AutoTradeToggle.IsOn = engine.TradingActive;
        _syncingToggle = false;

        // 预检（含天勤握手）在跑时让用户看到具体阶段与进度，而不是以为按钮没反应
        var toggleContent = _state.StartChecking
            ? (_state.StartProgressText.Length > 0 ? $"自动交易：{_state.StartProgressText}" : "自动交易：检查中…")
            : "自动交易";
        AutoTradeToggle.OnContent = toggleContent;
        AutoTradeToggle.OffContent = toggleContent;

        // 进度填充是绿色：进行中文字用 ink 保证在填充上可读，走满转白字与「开」态一致
        if (_state.StartChecking)
        {
            var palette = ThemeTokens.For(IsDark);
            AutoTradeToggle.Foreground = new SolidColorBrush(
                _state.StartProgressPct >= 100 ? Microsoft.UI.Colors.White : palette.Ink);
        }
        else
        {
            AutoTradeToggle.ClearValue(Control.ForegroundProperty);
        }

        // 后端已按各阶段权重折算成整次启动的百分比，从 0 起连续推进。
        // 进度直接填在胶囊里（整颗从左向右），宽度按胶囊实际宽度换算；
        // pct<0（如停止操作）不填充，只显示文案
        if (_state.StartChecking && _state.StartProgressPct >= 0 && AutoTradePill.ActualWidth > 0)
        {
            AutoTradeProgress.Width = AutoTradePill.ActualWidth * _state.StartProgressPct / 100;
            // 未走满时压低不透明度保证文字可读，走满转实色与「开」态衔接
            AutoTradeProgress.Opacity = _state.StartProgressPct >= 100 ? 1.0 : 0.55;
            AutoTradeProgress.Visibility = Visibility.Visible;
        }
        else
        {
            AutoTradeProgress.Visibility = Visibility.Collapsed;
        }

        RefreshButton.IsEnabled = !(_state.SymbolsFetching || _state.StrategiesFetching);
        RefreshButton.Content = _state.SymbolsFetching || _state.StrategiesFetching ? "刷新中…" : "刷新";

        var online = engine.ServerOnline;
        ServerStatusText.Text = $"服务器：{(online ? "已连接" : "未连接")}";
        ServerStatusText.Foreground = new SolidColorBrush(
            online ? ThemeTokens.StatusGreen : ThemeTokens.StatusRed);

        // 版本胶囊：平时显示后端版本，检查/下载/安装中或刚出结果时显示状态文案
        RefreshVersionPill();

        ApplyAutoTradeHelpStyle();

        ShowStartBlockedIssuesIfNeeded();
    }

    /// <summary>
    /// 启动预检未通过时弹出问题清单（对应 mac 壳的 StartBlockedSheet）。
    /// 后端把服务器、账户字段、token、天勤握手的问题一次返回，这里逐条列出
    /// ——用户通常要按提示去多个页面补配置。关闭即清空，下次点击重新预检。
    /// </summary>
    private async void ShowStartBlockedIssuesIfNeeded()
    {
        var issues = _state.StartBlockedIssues;
        if (issues.Count == 0 || _issuesDialogOpen || RootGrid.XamlRoot is null)
        {
            return;
        }

        _issuesDialogOpen = true;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"请先处理以下 {issues.Count} 项问题，再重新开启自动交易：",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ThemeTokens.For(IsDark).Dim),
        });
        var list = new StackPanel { Spacing = 6 };
        for (var i = 0; i < issues.Count; i++)
        {
            list.Children.Add(new Border
            {
                Background = new SolidColorBrush(ThemeTokens.For(IsDark).Soft),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Child = new TextBlock
                {
                    Text = $"{i + 1}. {issues[i]}",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            });
        }
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 320,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "自动交易未启动",
            Content = panel,
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
        _issuesDialogOpen = false;
        _state.ClearStartBlockedIssues();
    }

    // -------- 主题 --------

    private void ApplyThemeToChrome()
    {
        var palette = ThemeTokens.For(IsDark);
        RootGrid.RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light;
        RootGrid.Background = new SolidColorBrush(palette.Canvas);
        TopBar.Background = new SolidColorBrush(palette.Chrome);
        TopBar.BorderBrush = new SolidColorBrush(palette.Hairline);
        ServerPill.BorderBrush = new SolidColorBrush(palette.Hairline);
        ServerPill.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // 切主题时指针可能正悬在版本胶囊上：hover 态一并复位，避免残留旧主题的底色
        VersionButton.BorderBrush = new SolidColorBrush(palette.Hairline);
        VersionButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ApplyAutoTradeHelpStyle();
        AutoTradePill.BorderBrush = new SolidColorBrush(palette.Hairline);
        AutoTradeProgress.Fill = new SolidColorBrush(palette.Success);
        // 顶栏普通按钮：默认态透明底 + 发丝描边，悬停中的保持悬停配色（换主题不丢态）
        foreach (var button in new[] { ThemeButton, RefreshButton, QuitButton })
        {
            ApplyTopBarButtonState(button);
        }
        QuitButton.Foreground = new SolidColorBrush(palette.Danger);
        ThemeButton.Content = IsDark ? "浅色" : "深色";

        // 版本胶囊与角标（文字/圆点配色随主题）
        RefreshVersionPill();

        // 侧栏底色与分割线，导航项配色随之重算（选中/未选中/悬停都依主题取色）
        Sidebar.Background = new SolidColorBrush(palette.Canvas);
        Sidebar.BorderBrush = new SolidColorBrush(palette.Hairline);
        foreach (var (tag, _) in _navRoutes)
        {
            ApplyNavItemState(tag);
        }
    }
}

/// <summary>页面导航参数：状态、路由标签、宿主窗口（供主题与唤窗）。</summary>
public sealed record PageContext(AppState State, string Tag, MainWindow Window);
