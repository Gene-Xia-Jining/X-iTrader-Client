using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>
/// 仪表盘：状态指标卡 + 最近信号 + 最近日志（对应 mac 壳的 DashboardView）。
/// </summary>
public sealed partial class DashboardPage : Page
{

    // -------- 页面公共逻辑（状态 / 宿主窗口 / 主题画刷）--------

    private readonly ThemedPage _theme;

    private AppState State => _theme.State;
    private MainWindow Host => _theme.Host;
    private Palette Palette => _theme.Palette;

    private SolidColorBrush Brush(Windows.UI.Color color) => _theme.Brush(color);

    private static Windows.UI.Color Hex(uint value) => ThemedPage.Hex(value);

    private static Windows.UI.Color LogLevelColor(string level, bool dark) =>
        ThemedPage.LogLevelColor(level, dark);
    public DashboardPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
    }

    private void ApplyPalette()
    {
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);

        foreach (var card in new[] { ServerCard, TradingCard, AccountsCard, SymbolsCard, SignalCard, LogCard })
        {
            card.Background = Brush(Palette.Card);
            card.BorderBrush = Brush(Palette.Hairline);
        }

        foreach (var label in new[]
                 {
                     ServerCardTitle, TradingCardTitle, AccountsCardTitle, SymbolsCardTitle,
                     ServerSecondary, TradingSecondary, AccountsSecondary, SymbolsSecondary,
                     SignalTitle, SignalEmpty, LogTitle,
                 })
        {
            label.Foreground = Brush(Palette.Dim);
        }

        AccountsValue.Foreground = Brush(Palette.Ink);
        SymbolsValue.Foreground = Brush(Palette.Ink);

        ServerPill.Background = Brush(Palette.Soft);
        TradingPill.Background = Brush(Palette.Soft);
        ServerPill.BorderBrush = Brush(Palette.Hairline);
        TradingPill.BorderBrush = Brush(Palette.Hairline);
        ServerPill.BorderThickness = new Thickness(1);
        TradingPill.BorderThickness = new Thickness(1);

        LogPreviewScroll.Background = Brush(Palette.LogBg);
        LogPreviewScroll.BorderBrush = Brush(Palette.Hairline);
    }

    private void OnStateChanged()
    {
        var engine = State.EngineStatus;

        // 服务器状态
        var online = engine.ServerOnline;
        var onlineColor = online ? ThemeTokens.StatusGreen : ThemeTokens.StatusRed;
        ServerValue.Text = online ? "已连接" : "未连接";
        ServerValue.Foreground = Brush(onlineColor);
        ServerDot.Fill = Brush(onlineColor);
        ServerSecondary.Text = State.Config.ServerUrl.Length == 0 ? "未配置服务器地址" : State.Config.ServerUrl;

        // 交易状态
        var running = engine.Accounts.Count(a => a.Running);
        var tradingColor = engine.TradingActive ? ThemeTokens.StatusGreen : ThemeTokens.StatusRed;
        TradingValue.Text = running > 0 ? $"运行中 ({running})" : "已停止";
        TradingValue.Foreground = Brush(tradingColor);
        TradingDot.Fill = Brush(tradingColor);
        TradingSecondary.Text = engine.TradingActive ? "自动交易运行中" : "自动交易未启动";

        // 账户资金
        var liveCount = State.Accounts.Count(a => a.Kind == "live" && a.Enabled);
        var simCount = State.Accounts.Count(a => a.Kind == "sim" && a.Enabled);
        AccountsValue.Text = $"{liveCount} 实盘 / {simCount} 模拟";

        // 活跃品种（启用账户的品种并集）
        var symbols = new HashSet<string>();
        foreach (var account in State.Accounts.Where(a => a.Enabled))
        {
            symbols.UnionWith(account.Symbols);
        }
        SymbolsValue.Text = symbols.Count.ToString();

        RefreshLogPreview();
    }

    private void RefreshLogPreview()
    {
        LogPreviewPanel.Children.Clear();
        foreach (var line in State.Logs.TakeLast(4))
        {
            LogPreviewPanel.Children.Add(new TextBlock
            {
                Text = $"{line.Time:HH:mm:ss} [{line.Level}] {line.Message}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brush(LogLevelColor(line.Level, Host.IsDark)),
                TextWrapping = TextWrapping.NoWrap,
            });
        }
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e) => State.Logs.Clear();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Host.NavigateToTag("settings");

    private void OnTokenClick(object sender, RoutedEventArgs e) => Host.NavigateToTag("token");
}
