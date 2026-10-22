using XTrader.Shell.Theme;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XTrader.Shell.Core;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 实盘交易页：账户配置（快速开始）+ 实盘账户的交易记录，两个 tab 切换。
/// 每张卡片持有独立的期货公司单选（跨账户不互斥），支持多个实盘账户并存。
/// </summary>
public sealed partial class LiveAccountPage : Page
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
    private const string BrokerUrl = "https://www.shinnytech.com/articles/reference/tqsdk-brokers";

    /// <summary>新建未保存卡片的 id（前端生成，保存时传给后端）</summary>
    private readonly List<string> _draftIds = [];
    private readonly List<AccountCard> _cards = [];

    /// <summary>已建卡片的签名：账户列表变化才重建，避免打字时被状态刷新打断</summary>
    private string _accountsKey = "";

    // -------- 三个 tab：快速开始（账户配置）/ 交易记录（资金、持仓与当日委托）/ 历史成交（成交记录明细）--------

    private const string TabPrefKey = "live.tab";

    private enum Tab { Start, Records, Trades }

    private Tab _tab = Tab.Start;
    /// <summary>「交易记录」tab 的主体：懒建一次，切走再切回沿用它自己的数据</summary>
    private AccountRecordsView? _recordsView;
    /// <summary>「历史成交」tab 的主体：懒建一次，切走再切回沿用它自己的数据</summary>
    private AccountRecordsView? _tradesView;

    public LiveAccountPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
        _tab = ShellPrefs.Get(TabPrefKey) switch
        {
            "trades" => Tab.Trades,
            "records" => Tab.Records,
            _ => Tab.Start,
        };
        // 构造时就按上次的 tab 定好两栏的可见性，避免首帧闪一下配置卡片
        ApplyTabStyles();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
        if (State is null)
        {
            return;
        }
        RebuildCards();
        // 交易记录 / 历史成交 tab 每次进入都重新取数（TqSdk 只给当日快照）
        if (_tab == Tab.Records)
        {
            EnsureRecordsView();
            _recordsView?.Reload();
        }
        else if (_tab == Tab.Trades)
        {
            EnsureTradesView();
            _tradesView?.Reload();
        }
    }

    private void ApplyPalette()
    {
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyTabStyles();
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);
        Card.Background = Brush(Palette.Card);
        Card.BorderBrush = Brush(Palette.Hairline);
        RebuildBrokerHint();
        foreach (var card in _cards)
        {
            card.Refresh(Host.IsDark);
        }
        if (_recordsView is not null && State is not null)
        {
            _recordsView.Refresh(State, Host.IsDark);
        }
        if (_tradesView is not null && State is not null)
        {
            _tradesView.Refresh(State, Host.IsDark);
        }
    }

    private void OnStateChanged()
    {
        RebuildCards();
        foreach (var card in _cards)
        {
            card.RefreshSymbols();
        }
        // 账户配置变化后提示文案要跟着变（如刚保存了实盘账户）
        if (_recordsView is not null && State is not null)
        {
            _recordsView.Refresh(State, Host.IsDark);
        }
        if (_tradesView is not null && State is not null)
        {
            _tradesView.Refresh(State, Host.IsDark);
        }
    }

    // -------- tab --------

    private void OnStartTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Start);

    private void OnRecordsTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Records);

    private void OnTradesTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Trades);

    private async void OnTradesHelpClick(object sender, RoutedEventArgs e)
    {
        await _theme.ShowInfoAsync(
            "历史成交",
            "数据包含本地 SQLite 历史沉淀成交记录。\n\n"
            + "• 本地沉淀：快期账户通常仅保留当日实时成交推送，客户端将每笔成交自动持久化至本地 SQLite 数据库，跨交易日长期留存。\n"
            + "• 盈亏计算：自动按先进先出原则匹配开平仓，统计单笔平仓盈亏与累计平仓收益。\n"
            + "• 多维追溯：支持今日、近3天、近7天及全部历史筛选，点击任意成交行可查看合约与订单详细信息。");
    }

    private void SwitchTab(Tab tab)
    {
        if (_tab == tab)
        {
            return;
        }
        _tab = tab;
        ShellPrefs.Set(TabPrefKey, tab switch
        {
            Tab.Trades => "trades",
            Tab.Records => "records",
            _ => "start",
        });
        ApplyTabStyles();
        if (tab == Tab.Records)
        {
            EnsureRecordsView();
            _recordsView?.Reload();
        }
        else if (tab == Tab.Trades)
        {
            EnsureTradesView();
            _tradesView?.Reload();
        }
    }

    /// <summary>「交易记录」tab 的主体在本页第一次切过去时创建</summary>
    private void EnsureRecordsView()
    {
        if (_recordsView is not null)
        {
            return;
        }
        _recordsView = new AccountRecordsView("live", "尚未配置实盘账户，请先在「快速开始」里添加账户", AccountRecordsMode.Records);
        RecordsPanel.Children.Add(_recordsView);
        if (State is not null)
        {
            _recordsView.Refresh(State, Host.IsDark);
        }
    }

    /// <summary>「历史成交」tab 的主体在本页第一次切过去时创建</summary>
    private void EnsureTradesView()
    {
        if (_tradesView is not null)
        {
            return;
        }
        _tradesView = new AccountRecordsView("live", "尚未配置实盘账户，请先在「快速开始」里添加账户", AccountRecordsMode.Trades);
        TradesPanel.Children.Add(_tradesView);
        if (State is not null)
        {
            _tradesView.Refresh(State, Host.IsDark);
        }
    }

    private void ApplyTabStyles()
    {
        _theme.StyleCapsuleTab(StartTab, _tab == Tab.Start);
        _theme.StyleCapsuleTab(RecordsTab, _tab == Tab.Records);
        _theme.StyleCapsuleTab(TradesTab, _tab == Tab.Trades);
        TradesHelpButton.Foreground = Brush(Palette.Primary);
        TradesHelpButton.BorderBrush = Brush(Palette.Hairline);
        TradesHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Card.Visibility = _tab == Tab.Start ? Visibility.Visible : Visibility.Collapsed;
        RecordsPanel.Visibility = _tab == Tab.Records ? Visibility.Visible : Visibility.Collapsed;
        TradesPanel.Visibility = _tab == Tab.Trades ? Visibility.Visible : Visibility.Collapsed;
    }

    // -------- 卡片列表 --------

    private void RebuildCards()
    {
        var saved = State.Accounts.Where(a => a.Kind == "live").ToList();
        var key = string.Join("|", saved.Select(a => $"{a.Id}:{a.Label}:{a.Broker}:{a.TradeAccount}:{a.TqAccount}:{a.Enabled}:{string.Join(",", a.Symbols)}:{a.Strategy}"))
            + $";drafts:{string.Join(",", _draftIds)}";
        // 主题变化不进签名：卡片已有 Refresh(dark) 负责重新着色，重建会丢掉正在编辑的内容
        if (key == _accountsKey)
        {
            return;
        }
        _accountsKey = key;

        AccountsPanel.Children.Clear();
        _cards.Clear();
        foreach (var account in saved)
        {
            AddCard(new AccountCard(account.Id, account));
        }
        foreach (var draftId in _draftIds)
        {
            var card = new AccountCard(draftId, null);
            card.Dismissed += () => RemoveDraft(draftId);
            AddCard(card);
        }
    }

    private void AddCard(AccountCard card)
    {
        card.State = State;
        card.Refresh(Host.IsDark);
        _cards.Add(card);
        AccountsPanel.Children.Add(card);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var id = $"live-{Guid.NewGuid():N}"[..13];
        _draftIds.Add(id);
        _accountsKey = "";  // 签名失效，强制重建
        RebuildCards();
    }

    private void RemoveDraft(string draftId)
    {
        _draftIds.Remove(draftId);
        _accountsKey = "";
        RebuildCards();
    }

    /// <summary>期货公司支持范围说明（带 tqsdk-brokers 外链）</summary>
    private void RebuildBrokerHint()
    {
        var link = new Hyperlink { Foreground = new SolidColorBrush(ThemeTokens.Link) };
        link.Inlines.Add(new Run { Text = "tqsdk-brokers" });
        link.Click += async (_, _) =>
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(BrokerUrl));
            }
            catch (Exception ex)
            {
                // 打不开浏览器时退回纯文本提示，避免静默失败
                BrokerHint.Inlines.Clear();
                BrokerHint.Inlines.Add(new Run { Text = $"打开链接失败: {ex.Message}" });
            }
        };

        BrokerHint.Inlines.Clear();
        BrokerHint.FontSize = 13;
        BrokerHint.FontWeight = FontWeights.Normal;
        BrokerHint.Inlines.Add(new Run
        {
            Text = "期货公司按天勤版本分组，各账户可独立选择；受限于 TqSdk，支持范围参见：",
            Foreground = Brush(Palette.Dim),
        });
        BrokerHint.Inlines.Add(link);
    }
}
