using XTrader.Shell.Theme;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 实盘交易页：账户卡片列表（对应 mac 壳的 AccountView）。
/// 每张卡片持有独立的期货公司单选（跨账户不互斥），支持多个实盘账户并存。
/// </summary>
public sealed partial class LiveAccountPage : ThemedPageBase
{
    private const string BrokerUrl = "https://www.shinnytech.com/articles/reference/tqsdk-brokers";

    /// <summary>新建未保存卡片的 id（前端生成，保存时传给后端）</summary>
    private readonly List<string> _draftIds = [];
    private readonly List<AccountCard> _cards = [];

    /// <summary>已建卡片的签名：账户列表变化才重建，避免打字时被状态刷新打断</summary>
    private string _accountsKey = "";

    public LiveAccountPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (State is null)
        {
            return;
        }
        RebuildCards();
    }

    protected override void ApplyPalette()
    {
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);
        Card.Background = Brush(Palette.Card);
        Card.BorderBrush = Brush(Palette.Hairline);
        RebuildBrokerHint();
        foreach (var card in _cards)
        {
            card.Refresh(Host.IsDark);
        }
    }

    protected override void OnStateChanged()
    {
        RebuildCards();
        foreach (var card in _cards)
        {
            card.RefreshSymbols();
        }
    }

    // -------- 卡片列表 --------

    private void RebuildCards()
    {
        var saved = State.Accounts.Where(a => a.Kind == "live").ToList();
        var key = string.Join("|", saved.Select(a => $"{a.Id}:{a.Label}:{a.Broker}:{a.TradeAccount}:{a.TqAccount}:{a.Enabled}:{string.Join(",", a.Symbols)}"))
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
