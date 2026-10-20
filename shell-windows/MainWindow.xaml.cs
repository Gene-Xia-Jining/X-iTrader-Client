using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    /// <summary>版本/更新弹窗是否已打开（窗口刷新会重复触发，需防重入）</summary>
    private bool _versionDialogOpen;

    /// <summary>导航项（与 <see cref="_navRoutes"/> 同序），索引即路由</summary>
    private readonly List<Button> _navItems = [];
    private readonly List<TextBlock> _navLabels = [];
    /// <summary>当前路由标签（导航项着色据此判定选中态）</summary>
    private string _currentTag = "";
    /// <summary>鼠标悬停中的导航项标签：主题切换后重刷配色时仍要保留悬停态</summary>
    private readonly HashSet<string> _hoveredTags = [];

    /// <summary>路由标签与标题（顺序与 mac 壳的 AppRoute 一致）</summary>
    private static readonly (string Tag, string Title)[] _routes =
    [
        ("dashboard", "仪表盘"),
        ("token", "Token 管理"),
        ("sim", "模拟交易"),
        ("live", "实盘交易"),
        ("records", "交易记录"),
        ("log", "系统日志"),
        ("settings", "设置"),
    ];

    /// <summary>
    /// 侧边栏实际展示的路由。Token 管理暂时隐藏（Token 已在模拟页就地申请、查看），
    /// 页面与路由分发保留，后续需要时把 "token" 从排除项去掉即可。
    /// </summary>
    private static readonly (string Tag, string Title)[] _navRoutes =
        _routes.Where(r => r.Tag != "token").ToArray();

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

        IsDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        BuildNav();
        ApplyThemeToChrome();

        _state.PropertyChanged += (_, _) => RefreshStatus();
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

    /// <summary>按当前路由与悬停状态刷新导航项配色。</summary>
    private void ApplyNavItemState(string tag)
    {
        var index = Array.FindIndex(_navRoutes, r => r.Tag == tag);
        if (index < 0)
        {
            return;
        }

        var palette = ThemeTokens.For(IsDark);
        var selected = tag == _currentTag;
        _navLabels[index].Foreground = new SolidColorBrush(selected ? Microsoft.UI.Colors.White : palette.Dim);
        _navItems[index].Background = new SolidColorBrush(
            selected
                ? palette.SidebarSelected
                : _hoveredTags.Contains(tag) ? palette.SidebarHover : Microsoft.UI.Colors.Transparent);
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
            "records" => typeof(TradeRecordPage),
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
        ApplyThemeToChrome();
        ThemeChanged?.Invoke();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        // 与 mac 壳一致：刷新即重新拉取品种/策略，当前页保持不变
        _state.FetchSymbols();
        _state.FetchStrategies();
    }

    /// <summary>服务器状态胶囊里的问号：说明这里指的是哪个服务器（对应用户在 mac 壳的 popover）。</summary>
    private async void OnServerHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowHelpAsync("服务器状态",
            "这里的服务器指策略信号服务器，地址在「设置」中配置。"
            + "客户端从它获取交易品种与交易信号，并每 10 秒探测一次连通性。");
    }

    /// <summary>自动交易开关旁的问号：说明开关控制什么、关闭是否平仓。</summary>
    private async void OnAutoTradeHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowHelpAsync("自动交易",
            "开启后客户端接收服务器推送的交易信号并自动下单，开启前会先校验服务器连通、账户配置、Token 和天勤握手。"
            + "关闭只会停止接收信号与下单，已持有的仓位不会被自动平仓。");
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
            },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 版本弹窗（对应 mac 壳的 VersionSheet）：当前版本 + 检查更新 + 确认更新。
    ///
    /// 确认一次即可：点「确认更新」后自动下载并在完成后安装重启，不需要用户再去
    /// 设置页点第二次。设置页原有的检查更新入口保留，两处共用同一份 UpdatePhase——
    /// 弹窗打开期间订阅状态变化就地刷新文案与按钮。
    /// </summary>
    private async void OnVersionClick(object sender, RoutedEventArgs e)
    {
        if (_versionDialogOpen || RootGrid.XamlRoot is null)
        {
            return;
        }
        _versionDialogOpen = true;
        try
        {
            var palette = ThemeTokens.For(IsDark);

            var current = new TextBlock
            {
                Text = VersionLine(),
                Foreground = new SolidColorBrush(palette.Dim),
                FontSize = 13,
            };
            var message = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                MinHeight = 48,
            };
            var progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
            var progressText = new TextBlock
            {
                FontSize = 13,
                Visibility = Visibility.Collapsed,
                Foreground = new SolidColorBrush(palette.Dim),
            };
            var check = new Button { Content = "检查更新" };
            var accent = PrimaryButtonStyle();
            var confirm = new Button { Visibility = Visibility.Collapsed, Style = accent };
            var install = new Button { Visibility = Visibility.Collapsed, Style = accent };
            var cancel = new Button { Content = "取消下载", Visibility = Visibility.Collapsed };

            check.Click += (_, _) => _state.CheckUpdate();
            confirm.Click += (_, _) => _state.ConfirmUpdate();
            install.Click += (_, _) => _state.ApplyUpdate();
            cancel.Click += (_, _) => _state.CancelUpdate();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(check);
            buttons.Children.Add(confirm);
            buttons.Children.Add(install);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
            panel.Children.Add(current);
            panel.Children.Add(message);
            panel.Children.Add(progress);
            panel.Children.Add(progressText);
            panel.Children.Add(buttons);

            void Refresh()
            {
                var phase = _state.UpdatePhase;
                current.Text = VersionLine();
                check.IsEnabled = !phase.IsBusy;
                confirm.Visibility = phase is UpdatePhase.Available ? Visibility.Visible : Visibility.Collapsed;
                install.Visibility = phase is UpdatePhase.Ready && !_state.UpdateInstalling
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                cancel.Visibility = phase is UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
                progress.Visibility = Visibility.Collapsed;
                progressText.Visibility = Visibility.Collapsed;

                switch (phase)
                {
                    case UpdatePhase.Available available:
                        confirm.Content = $"确认更新 v{available.Version}";
                        SetDialogMessage(message, palette.Ink,
                            $"发现新版本 v{available.Version}（{(available.HasPatch ? "差量更新包" : "完整更新包")}）。"
                            + "确认后自动下载并安装，完成后客户端会自动重启。");
                        break;
                    case UpdatePhase.Checking:
                        SetDialogMessage(message, palette.Warning, "正在检查更新…");
                        break;
                    case UpdatePhase.Downloading:
                        SetDialogMessage(message, palette.Warning, "正在下载更新…");
                        if (_state.UpdateProgress >= 0)
                        {
                            progress.Visibility = Visibility.Visible;
                            progress.Value = _state.UpdateProgress;
                            progressText.Visibility = Visibility.Visible;
                            progressText.Text = $"{_state.UpdateProgress}%";
                        }
                        break;
                    case UpdatePhase.Ready ready:
                        install.Content = $"安装并重启（{ready.Kind}）";
                        SetDialogMessage(message, palette.Warning,
                            _state.UpdateInstalling ? "正在安装并重启…" : "更新包已下载，可以安装。");
                        break;
                    case UpdatePhase.Latest latest:
                        SetDialogMessage(message, palette.Success, latest.Message);
                        break;
                    case UpdatePhase.Failed failed:
                        SetDialogMessage(message, palette.Danger, failed.Message);
                        break;
                    default:
                        SetDialogMessage(message, palette.Dim, "点击「检查更新」查看是否有新版本。");
                        break;
                }
            }

            Refresh();
            void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
            _state.PropertyChanged += OnStateChanged;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "软件更新",
                    Content = panel,
                    CloseButtonText = "关闭",
                    DefaultButton = ContentDialogButton.Close,
                };
                await dialog.ShowAsync();
            }
            finally
            {
                _state.PropertyChanged -= OnStateChanged;
            }
        }
        finally
        {
            _versionDialogOpen = false;
        }
    }

    private static Style? PrimaryButtonStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) ? (Style)style : null;

    private string VersionLine() =>
        _state.BackendVersion.Length > 0 ? $"当前版本 v{_state.BackendVersion}" : "当前版本未知";

    private static void SetDialogMessage(TextBlock block, Windows.UI.Color color, string text)
    {
        block.Text = text;
        block.Foreground = new SolidColorBrush(color);
    }

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

        // 预检（含天勤握手）在跑时让用户看到进度，而不是以为按钮没反应
        var toggleContent = _state.StartChecking ? "自动交易：检查中…" : "自动交易";
        AutoTradeToggle.OnContent = toggleContent;
        AutoTradeToggle.OffContent = toggleContent;

        RefreshButton.IsEnabled = !(_state.SymbolsFetching || _state.StrategiesFetching);
        RefreshButton.Content = _state.SymbolsFetching || _state.StrategiesFetching ? "刷新中…" : "刷新";

        var online = engine.ServerOnline;
        ServerStatusText.Text = $"服务器状态：{(online ? "已连接" : "未连接")}";
        ServerStatusText.Foreground = new SolidColorBrush(
            online ? ThemeTokens.StatusGreen : ThemeTokens.StatusRed);

        // 版本胶囊：文字取后端上报的版本，红点表示已发现新版本
        VersionText.Text = _state.BackendVersion.Length > 0 ? $"v{_state.BackendVersion}" : "v-";
        VersionText.Foreground = new SolidColorBrush(ThemeTokens.For(IsDark).Dim);
        VersionBadge.Visibility = _state.UpdateBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        VersionBadge.Fill = new SolidColorBrush(ThemeTokens.For(IsDark).Danger);

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
        ServerHelpButton.Foreground = new SolidColorBrush(palette.Primary);
        ServerHelpButton.BorderBrush = new SolidColorBrush(palette.Hairline);
        ServerHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutoTradeHelpButton.Foreground = new SolidColorBrush(palette.Primary);
        AutoTradeHelpButton.BorderBrush = new SolidColorBrush(palette.Hairline);
        AutoTradeHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutoTradePill.BorderBrush = new SolidColorBrush(palette.Hairline);
        QuitButton.Foreground = new SolidColorBrush(palette.Danger);
        ThemeButton.Content = IsDark ? "浅色" : "深色";

        // 版本胶囊与角标（文字/圆点配色随主题）
        VersionText.Foreground = new SolidColorBrush(palette.Dim);
        VersionBadge.Fill = new SolidColorBrush(palette.Danger);

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
