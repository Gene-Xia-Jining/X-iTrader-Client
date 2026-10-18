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

    /// <summary>当前是否深色（页面据此取色）</summary>
    public bool IsDark { get; private set; }

    /// <summary>主题变化通知（页面订阅）</summary>
    public event Action? ThemeChanged;

    public MainWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        Title = "iTrader 智能交易系统";
        SetInitialSize(1180, 760);

        IsDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        ApplyThemeToChrome();

        _state.PropertyChanged += (_, _) => RefreshStatus();
        RefreshStatus();

        // 初始页面：设 SelectedItem 触发 SelectionChanged 完成首次导航
        Nav.SelectedItem = Nav.MenuItems[0];

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

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    /// <summary>供页面内按钮切换导航（如仪表盘的「配置设置」）。</summary>
    public void NavigateToTag(string tag)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
            {
                Nav.SelectedItem = item;  // 触发 SelectionChanged → NavigateTo
                return;
            }
        }
    }

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

        var running = engine.Accounts.Count(a => a.Running);
        StatusLeft.Text = running > 0 ? $"交易状态: 运行中 ({running})" : "交易状态: 已停止";

        ServerStatusText.Text = engine.ServerOnline ? "服务器: 已连接" : "服务器: 未连接";
        TradingStatusText.Text = engine.TradingActive ? "自动交易运行中" : "已停止";
        TradingDot.Fill = new SolidColorBrush(
            engine.TradingActive ? ThemeTokens.StatusGreen : ThemeTokens.StatusRed);

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
        StatusBar.Background = new SolidColorBrush(palette.Chrome);
        StatusBar.BorderBrush = new SolidColorBrush(palette.Hairline);
        QuitButton.Foreground = new SolidColorBrush(palette.Danger);
        ThemeButton.Content = IsDark ? "浅色" : "深色";
    }
}

/// <summary>页面导航参数：状态、路由标签、宿主窗口（供主题与唤窗）。</summary>
public sealed record PageContext(AppState State, string Tag, MainWindow Window);
