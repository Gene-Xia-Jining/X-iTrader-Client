using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>
/// 单个实盘账户卡片（对应 mac 壳的 AccountCardForm）。
///
/// 期货公司用 RadioButton + 每组独立 GroupName 实现「每卡片一组、跨账户不互斥」；
/// 构造时传 null 表示新建草稿卡片。
/// 账户数据变化由宿主页重建卡片处理，卡片自身在状态刷新时只更新主题与品种，
/// 避免覆盖用户正在输入的内容。
/// </summary>
public sealed partial class AccountCard : UserControl
{
    /// <summary>期货公司分组（与 mac 壳 BROKER_GROUPS、Qt 版保持一致）</summary>
    internal static readonly (string Group, string[] Brokers)[] BrokerGroups =
    [
        ("天勤免费版", ["宏源期货", "徽商期货", "银河期货"]),
        ("天勤专业版", ["东方汇金", "光大期货", "国泰君安"]),
    ];

    private readonly string? _accountId;
    private readonly string _groupSuffix = $"card{Guid.NewGuid():N}"[..14];
    private bool _dark;
    private bool _filling;
    private string _broker = "";
    private string _selectedSymbol = "";
    private string _symbolKey = "";

    /// <summary>草稿卡保存成功或删除后通知宿主移除</summary>
    public event Action? Dismissed;

    public AccountCard(string? accountId, AccountInfo? account)
    {
        InitializeComponent();
        _accountId = accountId;
        Account = account;
        BuildBrokerRows();
        Fill();
    }

    public AccountInfo? Account { get; }

    /// <summary>由托管页在主题/状态变化时调用（重新着色并回填）。</summary>
    internal void Refresh(bool dark)
    {
        _dark = dark;
        var palette = ThemeTokens.For(dark);
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Card.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Card.Background = new SolidColorBrush(palette.Card);
        Card.BorderBrush = new SolidColorBrush(palette.Hairline);

        foreach (var label in new[]
                 {
                     TradeAccountLabel, TradePasswordLabel, TqAccountLabel, TqPasswordLabel, SymbolLabel,
                 })
        {
            label.Foreground = new SolidColorBrush(palette.Dim);
            label.FontSize = 13;
        }
        SymbolHint.Foreground = new SolidColorBrush(palette.Dim);
        DeleteButton.Foreground = new SolidColorBrush(palette.Danger);

        // 期货公司行前缀与各卡片自身的组名标签
        foreach (var row in BrokerRows.Children.OfType<StackPanel>())
        {
            if (row.Children.FirstOrDefault() is TextBlock prefix)
            {
                prefix.Foreground = new SolidColorBrush(palette.Dim);
                prefix.FontSize = 13;
            }
            foreach (var radio in row.Children.OfType<RadioButton>())
            {
                radio.Foreground = new SolidColorBrush(palette.Ink);
            }
        }

        foreach (var box in new Control[] { LabelBox, TradeAccountBox, TqAccountBox })
        {
            box.Background = new SolidColorBrush(palette.InputBg);
            box.BorderBrush = new SolidColorBrush(palette.Hairline);
        }
        foreach (var box in new Control[] { TradePasswordBox, TqPasswordBox })
        {
            box.Background = new SolidColorBrush(palette.InputBg);
            box.BorderBrush = new SolidColorBrush(palette.Hairline);
        }

        RenderSymbols();
        SymbolHint.Text = PickerHint;
    }

    /// <summary>品种数据变化后由页面调用（服务器品种列表、获取状态）。</summary>
    internal void RefreshSymbols()
    {
        RenderSymbols();
        SymbolHint.Text = PickerHint;
    }

    // -------- 期货公司 --------

    private void BuildBrokerRows()
    {
        foreach (var (group, brokers) in BrokerGroups)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock
            {
                Text = $"{group}:",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            foreach (var name in brokers)
            {
                // 每组独立 GroupName：同一卡片内互斥，不同卡片互不影响
                var radio = new RadioButton
                {
                    Content = name,
                    GroupName = $"{_groupSuffix}-{group}",
                    MinWidth = 0,
                    Padding = new Thickness(0, 0, 8, 0),
                };
                radio.Checked += (_, _) => _broker = name;
                row.Children.Add(radio);
            }
            BrokerRows.Children.Add(row);
        }
    }

    private void SyncBrokerRadios()
    {
        foreach (var row in BrokerRows.Children.OfType<StackPanel>())
        {
            foreach (var radio in row.Children.OfType<RadioButton>())
            {
                if (radio.Content as string == _broker)
                {
                    radio.IsChecked = true;
                    return;
                }
            }
        }
    }

    // -------- 品种 --------

    private void RenderSymbols()
    {
        var dark = _dark;
        var palette = ThemeTokens.For(dark);
        var symbols = AllSymbols;
        var key = string.Join(",", symbols) + $";{_selectedSymbol};{dark}";
        if (key == _symbolKey)
        {
            return;
        }
        _symbolKey = key;

        SymbolPanel.Children.Clear();
        foreach (var symbol in symbols)
        {
            var isOn = _selectedSymbol == symbol;
            var button = new Button
            {
                Content = DisplaySymbol(symbol),
                Style = WrapPanel.CapsuleStyle(),
                Background = new SolidColorBrush(isOn ? palette.Primary : Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(isOn ? palette.Primary : palette.Hairline),
                Foreground = new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : palette.Ink),
            };
            // 实盘品种为多选一单选语义：再次点击不会取消，只能换选
            button.Click += (_, _) =>
            {
                _selectedSymbol = symbol;
                RenderSymbols();
            };
            SymbolPanel.Children.Add(button);
        }
    }

    /// <summary>已选但服务器未返回的品种保留显示（对应 Qt set_symbols 的 merge 规则）</summary>
    private List<string> AllSymbols
    {
        get
        {
            var result = State?.ServerSymbols.ToList() ?? [];
            if (_selectedSymbol.Length > 0 && !result.Contains(_selectedSymbol))
            {
                result.Add(_selectedSymbol);
            }
            return result;
        }
    }

    private string PickerHint
    {
        get
        {
            if (State is null) return "";
            if (State.SymbolsFetching) return "正在获取品种...";
            if (State.SymbolsMessage.StartsWith("获取失败")) return State.SymbolsMessage;
            var merged = AllSymbols;
            if (merged.Count > 0) return $"共 {merged.Count} 个品种，选择一个订阅，保存后生效";
            return "服务器暂无可订阅品种";
        }
    }

    private string DisplaySymbol(string symbol) => State?.DisplaySymbol(symbol) ?? symbol;

    /// <summary>页面注入的状态引用（构造后立即赋值，供品种列表与显示名使用）。</summary>
    internal AppState? State { get; set; }

    // -------- 回填与动作 --------

    private void Fill()
    {
        if (_filling)
        {
            return;
        }
        _filling = true;
        try
        {
            var names = BrokerGroups.SelectMany(g => g.Brokers).ToList();
            if (Account is null)
            {
                // 新卡片默认选中第一个期货公司（与 mac 壳一致）
                if (_broker.Length == 0)
                {
                    _broker = names.FirstOrDefault() ?? "";
                    SyncBrokerRadios();
                }
                return;
            }
            LabelBox.Text = Account.Label;
            _broker = names.Contains(Account.Broker) ? Account.Broker : names.FirstOrDefault() ?? "";
            SyncBrokerRadios();
            TradeAccountBox.Text = Account.TradeAccount;
            TqAccountBox.Text = Account.TqAccount;
            EnabledCheck.IsChecked = Account.Enabled;
            _selectedSymbol = Account.Symbols.FirstOrDefault() ?? "";
        }
        finally
        {
            _filling = false;
        }
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        // 启用状态随保存提交，这里不做即时请求（与 mac 壳一致）
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var tradeAccount = TradeAccountBox.Text.Trim();
        var tradePassword = TradePasswordBox.Password;
        var tqAccount = TqAccountBox.Text.Trim();
        var tqPassword = TqPasswordBox.Password;
        var isNew = Account is null;
        var hasStoredTqPassword = Account?.HasTqPassword ?? false;

        // 校验顺序与文案对应 mac 壳 _on_save_clicked；密码留空 = 沿用旧值（仅新卡必填）
        if (_broker.Length == 0)
        {
            await ShowInfoAsync("输入错误", "请选择期货公司");
            return;
        }
        if ((tradeAccount.Length > 0 && tradePassword.Length == 0 && isNew)
            || (tradeAccount.Length == 0 && tradePassword.Length > 0))
        {
            await ShowInfoAsync("输入错误", "资金账号与交易密码需同时填写");
            return;
        }
        if (tqAccount.Length == 0)
        {
            await ShowInfoAsync("输入错误", "快期账号不能为空");
            return;
        }
        if (tqPassword.Length == 0 && (isNew || !hasStoredTqPassword))
        {
            await ShowInfoAsync("输入错误", "快期密码不能为空");
            return;
        }
        if (AllSymbols.Count > 0 && _selectedSymbol.Length == 0)
        {
            await ShowInfoAsync("输入错误", "请选择一个交易品种");
            return;
        }

        var parameters = new JsonObject
        {
            // 草稿卡用前端生成的 id（对应 mac 壳 live-{uuid8}），保存后成为正式账户
            ["account_id"] = _accountId ?? NewDraftId(),
            ["kind"] = "live",
            ["label"] = LabelBox.Text.Trim(),
            ["broker"] = _broker,
            ["trade_account"] = tradeAccount,
            ["tq_account"] = tqAccount,
            ["enabled"] = EnabledCheck.IsChecked == true,
        };
        if (tradePassword.Length > 0) parameters["trade_password"] = tradePassword;
        if (tqPassword.Length > 0) parameters["tq_password"] = tqPassword;
        if (AllSymbols.Count > 0 && _selectedSymbol.Length > 0)
        {
            parameters["symbols"] = new JsonArray(_selectedSymbol);
        }

        var error = await (State?.SaveAccountAsync(parameters) ?? Task.FromResult<string?>("应用状态未就绪"));
        if (error is not null)
        {
            await ShowInfoAsync("输入错误", error);
            return;
        }

        await ShowInfoAsync("保存成功", "实盘账户已保存");
        if (isNew)
        {
            Dismissed?.Invoke();
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Account is null)
        {
            Dismissed?.Invoke();
            return;
        }
        var error = await (State?.DeleteAccountAsync(Account.Id) ?? Task.FromResult<string?>("应用状态未就绪"));
        if (error is not null)
        {
            await ShowInfoAsync("删除失败", error);
        }
    }

    /// <summary>草稿卡 id：对应 mac 壳的 live-{uuid8}，保存后即成为正式账户 id</summary>
    private static string NewDraftId() => $"live-{Guid.NewGuid():N}"[..13];

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }
}
