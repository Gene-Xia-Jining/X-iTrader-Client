using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using XTrader.Shell.Core;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>显示模式：账户信息（资金、持仓、当日委托各独立成框） / 历史成交（成交记录与时间范围筛选）</summary>
public enum AccountInfoMode
{
    Info,
    Trades,
}

/// <summary>
/// 一类账户的账户信息与历史成交。
///
/// 按账户类别取数并渲染，实盘交易页与模拟交易页的「账户信息」tab
/// 共用这一份实现。宿主页负责在进入页面时 <see cref="Reload"/>、
/// 主题或状态变化时 <see cref="Refresh"/>。
/// </summary>
public sealed partial class AccountInfoView : UserControl
{
    private readonly string _kind;
    private readonly string _notConfiguredHint;
    private readonly AccountInfoMode _mode;

    private AppState? _state;
    private bool _dark;
    private Palette Palette => ThemeTokens.For(_dark);

    private bool _loading;
    /// <summary>是否至少完成过一次取数：未取过就不显示「暂无数据」，避免误导</summary>
    private bool _hasLoaded;
    /// <summary>请求代号：只有最新一次请求的结果会被采用</summary>
    private int _requestSeq;
    private string _errorText = "";
    /// <summary>历史成交的时间范围；交易记录模式固定按 today 取当日快照</summary>
    private string _selectedDateRange = "today";
    private string _filterVariety = "";
    private string _filterStrategy = "";
    private bool _newestFirst = true;
    private IReadOnlyList<AccountRecords> _records = [];
    /// <summary>渲染签名：数据/主题/空态变化才重建账户卡片</summary>
    private string _renderKey = "";
    /// <summary>当前悬停的工具栏按钮：重建着色时保持悬停态不丢</summary>
    private readonly HashSet<Button> _hoveredToolbarButtons = [];

    private string DateRangeKey => $"trades.dateRange.{_kind}";
    private string VarietyKey => $"trades.filterVariety.{_kind}";
    private string StrategyKey => $"trades.filterStrategy.{_kind}";
    private string NewestFirstKey => $"trades.newestFirst.{_kind}";

    private const string FloatProfitHelp =
        "按当前市场最新价与开仓均价计算的未实现盈亏。反映当前所有未平仓持仓以最新市价全部平仓时预估的累计盈亏金额。";
    private const string PositionProfitHelp =
        "按当前最新价与持仓成本（历史持仓按上一交易日结算价、当日开仓按开仓均价）计算的盯市盈亏。用于反映当日持仓所产生的盈亏变动及每日结算。";
    private const string RiskRatioHelp =
        "保证金占用占账户总权益的比例（计算公式：保证金 ÷ 账户权益 × 100%）。风险度越高代表仓位与杠杆越重；当风险度达到或超过 100% 时，可用资金将为负，可能面临追加保证金或被强行平仓的风险。";
    private const string OpenPriceHelp =
        "当前持仓在建立仓位时的实际成交均价。若同一合约多次开仓，按各笔成交手数加权平均计算，不会随每日结算价变动。";
    private const string PositionPriceHelp =
        "当前持仓用于每日结算与盯市的基准成本价。当日新开仓等于开仓均价；跨交易日的历史持仓在每日结算后调整为上一交易日的结算价。";
    private const string CloseProfitHelp =
        "本交易日内已平仓合约实现的累计盈亏金额（平仓价与持仓成本价之差）。当日未进行平仓操作时为 0.00。";
    private const string CommissionHelp =
        "本交易日内因开仓、平仓等交易产生的累计手续费支出。隔夜历史持仓产生的手续费已在以往结算中扣减并计入昨日权益；若当日无新成交则为 0.00。";

    public AccountInfoView(string kind, string notConfiguredHint, AccountInfoMode mode = AccountInfoMode.Info)
    {
        InitializeComponent();
        _kind = kind;
        _notConfiguredHint = notConfiguredHint;
        _mode = mode;

        if (_mode == AccountInfoMode.Trades)
        {
            // 历史成交首次进入默认「全部日期」，之后沿用上次的选择
            _selectedDateRange = "all";
            var savedRange = ShellPrefs.Get(DateRangeKey);
            if (!string.IsNullOrEmpty(savedRange) && (savedRange is "all" or "1y" or "30d" or "today"))
            {
                _selectedDateRange = savedRange;
            }
            _filterVariety = ShellPrefs.Get(VarietyKey) ?? "";
            _filterStrategy = ShellPrefs.Get(StrategyKey) ?? "";
            var savedOrder = ShellPrefs.Get(NewestFirstKey);
            _newestFirst = savedOrder != "0";
        }
    }

    /// <summary>按当前主题重新着色并刷新提示文案（卡片按渲染签名决定是否重建）。</summary>
    public void Refresh(AppState state, bool dark)
    {
        if (_state != state)
        {
            if (_state != null)
            {
                _state.RecordsUpdated -= OnRecordsUpdated;
            }
            _state = state;
            if (_state != null)
            {
                _state.RecordsUpdated += OnRecordsUpdated;
            }
        }
        _dark = dark;
        if (_records.Count == 0 && _state != null)
        {
            if (_selectedDateRange == "today")
            {
                var cached = _state.GetCachedRecords(_kind);
                if (cached.Count > 0)
                {
                    _records = cached;
                    _hasLoaded = true;
                }
            }
        }
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        var palette = Palette;
        Hint.Foreground = Brush(palette.Dim);
        ErrorCard.Background = Brush(palette.Card);
        ErrorCard.BorderBrush = Brush(palette.Hairline);
        ErrorText.Foreground = Brush(palette.Danger);
        RefreshButton.Foreground = Brush(palette.Primary);
        OrderButton.Foreground = Brush(palette.Primary);
        // 悬停底/描边由 ApplyToolbarButtonState 统一处理，深浅色切换后重算一次
        ApplyToolbarButtonState(RefreshButton);
        ApplyToolbarButtonState(OrderButton);
        OrderButton.Content = _newestFirst ? "最新在前" : "最新在后";
        OrderButton.Visibility = _mode == AccountInfoMode.Trades ? Visibility.Visible : Visibility.Collapsed;
        FiltersPanel.Visibility = _mode == AccountInfoMode.Trades ? Visibility.Visible : Visibility.Collapsed;
        Hint.Visibility = _mode == AccountInfoMode.Trades ? Visibility.Collapsed : Visibility.Visible;
        Hint.Text = HintText();
        // 签名里已含深浅色，配色变化时卡片会自动重建
        RenderRecords();
    }

    private void OnRecordsUpdated(string kind, IReadOnlyList<AccountRecords> list)
    {
        if (kind != _kind) return;
        if (_selectedDateRange != "today") return; // 历史模式下不被当日推送冲刷
        DispatcherQueue.TryEnqueue(() =>
        {
            _records = list;
            _hasLoaded = true;
            RenderRecords();
        });
    }

    /// <summary>进入页面时取一次最新数据（TqSdk 只给当日快照，停留期间可能已成交）。</summary>
    public void Reload() => _ = LoadAsync(force: true);

    private SolidColorBrush Brush(Windows.UI.Color color) => new(color);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = LoadAsync(force: true);

    /// <summary>工具栏按钮悬停：与顶栏按钮同一套反馈（SidebarHover 底色 + 描边提亮）</summary>
    private void OnToolbarButtonPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _hoveredToolbarButtons.Add(button);
            ApplyToolbarButtonState(button);
        }
    }

    private void OnToolbarButtonPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _hoveredToolbarButtons.Remove(button);
            ApplyToolbarButtonState(button);
        }
    }

    private void ApplyToolbarButtonState(Button button)
    {
        var palette = Palette;
        var hovering = _hoveredToolbarButtons.Contains(button);
        button.Background = hovering
            ? new SolidColorBrush(palette.SidebarHover)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderBrush = hovering
            ? new SolidColorBrush(palette.Dim)
            : new SolidColorBrush(palette.Hairline);
    }

    private void OnToggleOrderClick(object sender, RoutedEventArgs e)
    {
        _newestFirst = !_newestFirst;
        ShellPrefs.Set(NewestFirstKey, _newestFirst ? "1" : "0");
        OrderButton.Content = _newestFirst ? "最新在前" : "最新在后";
        RenderRecords();
    }

    /// <summary>
    /// 取本类账户的数据。再次刷新时即便上一个请求还在跑也要重新发起
    /// （force=true 绕过防重入），用请求代号丢弃过期结果。
    /// </summary>
    private async Task LoadAsync(bool force = false)
    {
        if (_loading && !force)
        {
            return;
        }
        if (_state is null || _state.ConnState != ConnState.Connected)
        {
            _errorText = "后端未连接";
            if (_records.Count == 0)
            {
                _records = [];
            }
            RenderRecords();
            return;
        }

        var requestId = ++_requestSeq;
        _loading = true;
        _errorText = "";
        Hint.Text = HintText();
        RefreshButton.IsEnabled = false;
        try
        {
            var dateRange = _mode == AccountInfoMode.Trades ? _selectedDateRange : "today";
            var resp = await _state.CallAsync("trading.records", new JsonObject
            {
                ["kind"] = _kind,
                ["date_range"] = dateRange,
            });
            if (requestId != _requestSeq)
            {
                return;  // 已被更新的请求（再次刷新）取代
            }
            _records = JsonHelpers.ObjectList(resp["accounts"])
                .Select(AccountRecords.From)
                .OfType<AccountRecords>()
                .ToList();
            if (_selectedDateRange == "today")
            {
                _state.SetCachedRecords(_kind, _records);
            }
        }
        catch (Exception ex)
        {
            if (requestId != _requestSeq)
            {
                return;
            }
            if (_records.Count == 0)
            {
                _records = [];
            }
            _errorText = $"读取失败: {ex.Message}";
        }
        finally
        {
            if (requestId == _requestSeq)
            {
                _loading = false;
                _hasLoaded = true;
                RefreshButton.IsEnabled = true;
                Hint.Text = HintText();
                RenderRecords();
            }
        }
    }

    private string HintText()
    {
        var count = _state?.Accounts.Count(a => a.Kind == _kind) ?? 0;
        if (count == 0)
        {
            return _notConfiguredHint;
        }
        if (_loading)
        {
            return _records.Count == 0 ? "正在读取账户数据…" : "正在刷新账户数据…";
        }
        if (_mode == AccountInfoMode.Trades)
        {
            return "";
        }
        return "数据来自快期账户，包含当日实时推送";
    }


    // -------- 渲染 --------

    private void RenderRecords()
    {
        // 签名要覆盖行内容本身：只按行数判断的话，刷新后手数/价格变了但条数没变就不重建。
        // _loading 不进入 key，避免仅进入刷新中就闪烁清空重建卡片。
        var key = string.Join(
            "|",
            _records.Select(r =>
                $"{r.Id}:{r.Error}:{Describe(r.Account)}:"
                + string.Join(",", r.Positions.Select(p => $"{p.Symbol}{p.Direction}{p.Volume}{p.FloatProfit}"))
                + string.Join(",", r.Orders.Select(o => $"{o.Id}{o.VolumeLeft}{o.StatusText}"))
                + string.Join(",", r.Trades.Select(t => $"{t.Id}{t.Price}{t.Volume}"))),
            _dark,
            _errorText,
            _selectedDateRange,
            _filterVariety,
            _filterStrategy,
            _newestFirst);
        if (key == _renderKey)
        {
            return;
        }
        _renderKey = key;

        ErrorCard.Visibility = _errorText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = _errorText;

        if (_mode == AccountInfoMode.Trades)
        {
            FiltersPanel.Visibility = Visibility.Visible;
            Hint.Visibility = Visibility.Collapsed;
            List<TradeRecord> allTrades = _records.SelectMany(r => r.Trades).ToList();
            RenderTradeFilters(allTrades);
        }
        else
        {
            FiltersPanel.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            Hint.Text = HintText();
        }

        AccountPanel.Children.Clear();
        foreach (var record in _records)
        {
            foreach (var element in BuildAccountCards(record))
            {
                AccountPanel.Children.Add(element);
            }
        }
        if (_records.Count == 0 && _errorText.Length == 0 && !_loading && _hasLoaded)
        {
            AccountPanel.Children.Add(BuildEmptyCard("暂无数据"));
        }
    }

    private static string Describe(AccountSnapshot account) =>
        $"{account.Balance},{account.Available},{account.Margin},{account.FloatProfit},{account.CloseProfit}";

    private Border BuildCard(UIElement content) => new()
    {
        Background = Brush(Palette.Card),
        BorderBrush = Brush(Palette.Hairline),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18),
        Padding = new Thickness(20),
        Child = content,
    };

    private Border BuildEmptyCard(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brush(Palette.Dim),
            Margin = new Thickness(0, 12, 0, 12),
        };
        return BuildCard(label);
    }

    /// <summary>渲染一个账户：账户信息模式下资金、持仓、当日委托各成一张卡片。</summary>
    private List<UIElement> BuildAccountCards(AccountRecords record)
    {
        // 历史成交不再套一层卡片外框：表格自带底色与描边，直接铺在页面上
        if (_mode == AccountInfoMode.Trades)
        {
            return [BuildTradeTable(FilterTrades(record.Trades), record.Label)];
        }

        // 单账户时省略标题（页面本身已区分模拟/实盘），多账户时只留账户名用于区分归属
        UIElement? head = null;
        if (_records.Count > 1)
        {
            head = new TextBlock
            {
                Text = record.Label,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(Palette.Ink),
            };
        }

        if (record.Error.Length > 0)
        {
            var body = new StackPanel { Spacing = 14 };
            if (head is not null)
            {
                body.Children.Add(head);
            }
            body.Children.Add(new TextBlock
            {
                Text = $"读取失败: {record.Error}",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(Palette.Danger),
            });
            return [BuildCard(body)];
        }

        var metrics = new StackPanel { Spacing = 14 };
        if (head is not null)
        {
            metrics.Children.Add(head);
        }
        metrics.Children.Add(BuildMetrics(record.Account));

        return
        [
            BuildCard(metrics),
            BuildCard(BuildSection("持仓", record.Label, BuildPositionTable(record.Positions))),
            BuildCard(BuildSection("当日委托", record.Label, BuildOrderTable(record.Orders))),
        ];
    }

    /// <summary>
    /// 资金指标：直接铺在账户卡片上（不套内层底色面板），5 列排两行以减少占用高度。
    /// </summary>
    private StackPanel BuildMetrics(AccountSnapshot account)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(MetricRow(
            ("账户权益", FormatMoney(account.Balance), null, null),
            ("可用资金", FormatMoney(account.Available), null, null),
            ("保证金", FormatMoney(account.Margin), null, null),
            ("风险度", FormatPercent(account.RiskRatio), null, RiskRatioHelp),
            ("昨日权益", FormatMoney(account.PreBalance), null, null)));
        panel.Children.Add(MetricRow(
            ("浮动盈亏", FormatSigned(account.FloatProfit), ProfitColor(account.FloatProfit), FloatProfitHelp),
            ("持仓盈亏", FormatSigned(account.PositionProfit), ProfitColor(account.PositionProfit), PositionProfitHelp),
            ("平仓盈亏", FormatSigned(account.CloseProfit), ProfitColor(account.CloseProfit), CloseProfitHelp),
            ("手续费", FormatMoney(account.Commission), null, CommissionHelp),
            // 占位保持两行列宽一致：第二行只有 4 项，缺的这格不能把上面的列挤宽
            ("", "", null, null)));
        return panel;
    }

    private Grid MetricRow(params (string Title, string Value, Windows.UI.Color? Color, string? Help)[] items)
    {
        var grid = new Grid { ColumnSpacing = 0 };
        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (title, value, color, help) = items[i];
            var cell = new StackPanel { Spacing = 3 };

            var headPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            headPanel.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Brush(Palette.Dim), VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(help))
            {
                headPanel.Children.Add(CreateHelpButton(title, help));
            }
            cell.Children.Add(headPanel);

            cell.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                Foreground = Brush(color ?? Palette.Ink),
            });
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
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

    private Button CreateHelpButton(string title, string helpText)
    {
        var ring = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(Palette.Hairline),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "?",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(Palette.Dim),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };

        var button = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = ring,
        };
        ToolTipService.SetToolTip(button, $"点击查看「{title}」说明");
        button.PointerEntered += (s, e) =>
        {
            ring.BorderBrush = Brush(Palette.Primary);
            ring.Background = Brush(Palette.SidebarHover);
            if (ring.Child is TextBlock tb) tb.Foreground = Brush(Palette.Primary);
        };
        button.PointerExited += (s, e) =>
        {
            ring.BorderBrush = Brush(Palette.Hairline);
            ring.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (ring.Child is TextBlock tb) tb.Foreground = Brush(Palette.Dim);
        };
        button.Click += async (_, _) => await ShowInfoAsync(title, helpText);
        return button;
    }

    /// <summary>独立卡片内的分区标题（持仓 / 当日委托）；多账户时补账户名，避免分不清归属。</summary>
    private StackPanel BuildSection(string title, string label, UIElement table)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Ink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (_records.Count > 1)
        {
            head.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Brush(Palette.Dim),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var section = new StackPanel { Spacing = 12 };
        section.Children.Add(head);
        section.Children.Add(table);
        return section;
    }

    // -------- 表格 --------

    private sealed record ColumnSpec(string Header, double? Width, HorizontalAlignment Align, string? Help = null);

    private static ColumnSpec Left(string header, double? width = null, string? help = null) =>
        new(header, width, HorizontalAlignment.Left, help);

    private static ColumnSpec Center(string header, double? width = null, string? help = null) =>
        new(header, width, HorizontalAlignment.Center, help);

    private static ColumnSpec Right(string header, double? width = null, string? help = null) =>
        new(header, width, HorizontalAlignment.Right, help);

    /// <summary>
    /// 等宽列 + 表头 + 数据行的简易表格。列宽由 ColumnSpec 给出，null 表示按权重均分，
    /// 这样长文本（合约、时间）能自适应，短字段（买卖、手数）保持固定宽度对齐。
    /// 支持表头置顶固定、数据行内部滚动（超过 maxHeight 时自动滚动）、汇总栏置底固定。
    /// </summary>
    private Border BuildTable(
        IReadOnlyList<ColumnSpec> columns,
        IReadOnlyList<IReadOnlyList<TableCell>> rows,
        string empty,
        string? summary = null,
        (string Text, Windows.UI.Color? Color)? summaryHighlight = null,
        Action<int>? onRowClick = null,
        double maxHeight = 420)
    {
        var body = new StackPanel();
        body.Children.Add(BuildTableRow(columns.Select(c => new TableCell(c.Header, null)).ToList(), columns, header: true));

        if (rows.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = empty,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brush(Palette.Dim),
                Margin = new Thickness(0, 14, 0, 14),
            });
        }
        else
        {
            body.Children.Add(new Rectangle { Height = 1, Fill = Brush(Palette.Divider), Margin = new Thickness(12, 0, 12, 0) });

            var rowsPanel = new StackPanel();
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                if (rowIndex > 0)
                {
                    rowsPanel.Children.Add(new Rectangle { Height = 1, Fill = Brush(Palette.Divider), Margin = new Thickness(12, 0, 12, 0) });
                }
                var row = rows[rowIndex];
                var rowGrid = BuildTableRow(row, columns, header: false);
                if (onRowClick is not null)
                {
                    var capturedIndex = rowIndex;
                    rowGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    rowGrid.PointerEntered += (s, e) => rowGrid.Background = Brush(Palette.Soft);
                    rowGrid.PointerExited += (s, e) => rowGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    rowGrid.Tapped += (s, e) => onRowClick(capturedIndex);
                }
                rowsPanel.Children.Add(rowGrid);
            }

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = maxHeight,
                Content = rowsPanel,
            };
            body.Children.Add(scrollViewer);

            if (!string.IsNullOrEmpty(summary))
            {
                body.Children.Add(new Rectangle { Height = 1, Fill = Brush(Palette.Divider), Margin = new Thickness(12, 0, 12, 0) });
                var summaryPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Padding = new Thickness(12, 8, 12, 8),
                    Spacing = 4,
                };
                summaryPanel.Children.Add(new TextBlock
                {
                    Text = summary,
                    FontSize = 13,
                    Foreground = Brush(Palette.Dim),
                });
                if (summaryHighlight is { } hl)
                {
                    summaryPanel.Children.Add(new TextBlock
                    {
                        Text = hl.Text,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush(hl.Color ?? Palette.Ink),
                    });
                }
                body.Children.Add(summaryPanel);
            }
        }

        return new Border
        {
            Background = Brush(Palette.Soft),
            BorderBrush = Brush(Palette.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Child = body,
        };
    }

    private Grid BuildTableRow(IReadOnlyList<TableCell> cells, IReadOnlyList<ColumnSpec> columns, bool header)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, header ? 7 : 6, 12, header ? 7 : 6),
            ColumnSpacing = 8,
        };
        for (var i = 0; i < columns.Count; i++)
        {
            var spec = columns[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = spec.Width is { } width ? new GridLength(width) : new GridLength(1, GridUnitType.Star),
            });
            var cell = cells.Count > i ? cells[i] : new TableCell("", null);

            FrameworkElement element;
            if (header && !string.IsNullOrEmpty(spec.Help))
            {
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    HorizontalAlignment = spec.Align,
                };
                headerPanel.Children.Add(new TextBlock
                {
                    Text = cell.Text,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(cell.Color ?? Palette.Dim),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                headerPanel.Children.Add(CreateHelpButton(cell.Text, spec.Help));
                element = headerPanel;
            }
            else if (cell.IsCapsule)
            {
                element = new Border
                {
                    Background = Brush(Palette.Chrome),
                    BorderBrush = Brush(Palette.Hairline),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = spec.Align,
                    Child = new TextBlock
                    {
                        Text = cell.Text,
                        FontSize = 11,
                        FontWeight = FontWeights.Medium,
                        Foreground = Brush(Palette.Ink),
                    },
                };
            }
            else if (cell.IsBadge)
            {
                element = new Border
                {
                    Background = Brush(cell.BadgeBg ?? Palette.Soft),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = spec.Align,
                    Child = new TextBlock
                    {
                        Text = cell.Text,
                        FontSize = 11,
                        FontWeight = FontWeights.Medium,
                        Foreground = Brush(cell.Color ?? Palette.Dim),
                    },
                };
            }
            else
            {
                element = new TextBlock
                {
                    Text = cell.Text,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = spec.Align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
                    HorizontalAlignment = spec.Align,
                    Foreground = Brush(cell.Color ?? (header ? Palette.Dim : Palette.Ink)),
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                };
            }

            if (!string.IsNullOrEmpty(cell.ToolTip) && cell.ToolTip != "全部成交")
            {
                ToolTipService.SetToolTip(element, cell.ToolTip);
            }

            Grid.SetColumn(element, i);
            grid.Children.Add(element);
        }
        return grid;
    }

    private readonly record struct TableCell(
        string Text,
        Windows.UI.Color? Color,
        string? ToolTip = null,
        bool IsBadge = false,
        Windows.UI.Color? BadgeBg = null,
        bool IsCapsule = false);

    private Border BuildPositionTable(IReadOnlyList<PositionRecord> rows)
    {
        var maxVarietyLen = rows.Select(r => DisplayVariety(r.Symbol).Length).DefaultIfEmpty(2).Max();
        var varietyWidth = Math.Max(80, maxVarietyLen * 8.5 + 16);

        var columns = new[]
        {
            Left("品种", varietyWidth), Left("合约"), Center("方向", 40), Right("手数", 44),
            Right("开仓均价", help: OpenPriceHelp),
            Right("持仓均价", help: PositionPriceHelp),
            Right("浮动盈亏", help: FloatProfitHelp),
            Right("保证金"),
        };
        var cells = rows.Select(row => new List<TableCell>
        {
            new(DisplayVariety(row.Symbol), null),
            new(row.Symbol, null),
            new(row.Direction, row.Direction == "多" ? Palette.Danger : Palette.Success),
            new(row.Volume.ToString(CultureInfo.InvariantCulture), null),
            new(FormatPrice(row.OpenPrice), null),
            new(FormatPrice(row.PositionPrice), null),
            new(FormatSigned(row.FloatProfit), ProfitColor(row.FloatProfit)),
            new(FormatMoney(row.Margin), null),
        }).ToList();
        return BuildTable(columns, cells, "当前无持仓", maxHeight: 280);
    }

    private Border BuildOrderTable(IReadOnlyList<OrderRecord> rows)
    {
        var maxOrderSourceLen = rows.Select(r => DisplaySource(r.Source).Length).DefaultIfEmpty(2).Max();
        var orderSourceWidth = Math.Max(70, maxOrderSourceLen * 8.5 + 16);

        var columns = new[]
        {
            Left("委托时间"), Left("合约"), Left("买卖", 36), Left("开平", 40),
            Right("手数", 40), Right("成交", 40), Right("委托价"), Right("成交均价"), Left("状态", 60), Left("来源", orderSourceWidth),
        };
        var cells = rows.Select(row =>
        {
            var isAbnormal = row.VolumeLeft > 0 && row.StatusText == "已完";
            var statusColor = isAbnormal ? Palette.Danger : (Windows.UI.Color?)null;
            return new List<TableCell>
            {
                new(row.InsertTime, null),
                new(row.Symbol, null),
                new(row.DirectionText, SideColor(row.DirectionText)),
                new(row.OffsetText, null),
                new(row.VolumeOrigin.ToString(CultureInfo.InvariantCulture), null),
                new((row.VolumeOrigin - row.VolumeLeft).ToString(CultureInfo.InvariantCulture), null),
                new(FormatPrice(row.LimitPrice), null),
                new(FormatPrice(row.TradePrice), null),
                new(row.StatusText, statusColor, ToolTip: isAbnormal ? row.LastMsg : null),
                new(DisplaySource(row.Source), null),
            };
        }).ToList();

        var totalOrigin = rows.Sum(r => r.VolumeOrigin);
        var filledCount = rows.Count(r => r.VolumeOrigin > r.VolumeLeft);
        var canceledCount = rows.Count(r => r.VolumeLeft > 0 && r.StatusText == "已完");
        var summary = rows.Count > 0
            ? $"共 {rows.Count} 笔委托 · 报单 {totalOrigin} 手 · 已成 {filledCount} 笔" + (canceledCount > 0 ? $" · 已撤/废单 {canceledCount} 笔" : "")
            : null;

        return BuildTable(columns, cells, "无委托记录", summary: summary, maxHeight: 360);
    }

    private string DisplayVariety(string symbol)
    {
        var clean = symbol.Split('.').LastOrDefault() ?? symbol;
        var code = clean.ToUpperInvariant();
        var letters = new string(code.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length == 0) return clean;
        if (_state?.ServerSymbolNames.TryGetValue(letters, out var name) == true && !string.IsNullOrEmpty(name))
        {
            return $"{letters}｜{name}";
        }
        return letters;
    }

    private string StrategyAlias(string key)
    {
        var trimmed = key?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed)) return "";
        if (_state is not null)
        {
            var matched = _state.ServerStrategies.FirstOrDefault(s =>
                string.Equals(s.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Alias, trimmed, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                if (!string.IsNullOrEmpty(matched.Alias)) return matched.Alias;
                if (!string.IsNullOrEmpty(matched.Name) && !string.Equals(matched.Name, matched.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return matched.Name;
                }
                return matched.Id;
            }
        }
        if (string.Equals(trimmed, "supertrend", StringComparison.OrdinalIgnoreCase))
        {
            return "1003";
        }
        return trimmed;
    }

    private string DisplaySource(string source)
    {
        var trimmed = source?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed) || trimmed is "手动/外部" or "手动")
        {
            return "手动";
        }
        if (trimmed.StartsWith("策略｜")) return "策略｜" + StrategyAlias(trimmed[3..]);
        if (trimmed.StartsWith("策略|")) return "策略｜" + StrategyAlias(trimmed[3..]);
        if (trimmed.StartsWith("策略 | ")) return "策略｜" + StrategyAlias(trimmed[5..]);

        if (trimmed == "智能信号")
        {
            var acc = _state?.Accounts.FirstOrDefault(a => a.Kind == _kind);
            if (!string.IsNullOrEmpty(acc?.Strategy))
            {
                return $"策略｜{StrategyAlias(acc.Strategy)}";
            }
            var first = _state?.ServerStrategies.FirstOrDefault();
            if (!string.IsNullOrEmpty(first?.Alias))
            {
                return $"策略｜{first.Alias}";
            }
            if (!string.IsNullOrEmpty(first?.Name))
            {
                return $"策略｜{first.Name}";
            }
            return "策略｜智能信号";
        }
        return $"策略｜{StrategyAlias(trimmed)}";
    }

    private List<string> AvailableVarieties(IReadOnlyList<TradeRecord> trades) =>
        trades.Select(t => DisplayVariety(t.Symbol))
            .Where(v => v.Length > 0)
            .Distinct()
            .OrderBy(v => v, StringComparer.CurrentCulture)
            .ToList();

    private List<string> AvailableStrategies(IReadOnlyList<TradeRecord> trades) =>
        trades.Select(t => DisplaySource(t.Source))
            .Where(s => s.Length > 0)
            .Distinct()
            .OrderBy(s => s, StringComparer.CurrentCulture)
            .ToList();

    private IReadOnlyList<TradeRecord> FilterTrades(IReadOnlyList<TradeRecord> trades)
    {
        var filtered = trades.Where(t =>
        {
            var matchVariety = _filterVariety.Length == 0 || DisplayVariety(t.Symbol) == _filterVariety;
            var matchStrategy = _filterStrategy.Length == 0 || DisplaySource(t.Source) == _filterStrategy;
            return matchVariety && matchStrategy;
        }).ToList();
        if (!_newestFirst)
        {
            filtered.Reverse();
        }
        return filtered;
    }

    /// <summary>历史成交顶部工具栏：日期、品种、策略各过滤器分组呈水平顺序排列</summary>
    private void RenderTradeFilters(IReadOnlyList<TradeRecord> trades)
    {
        FiltersPanel.Children.Clear();

        // 1. 日期过滤器分组
        var dateOptions = new (string Key, string Label)[]
        {
            ("all", "全部日期"),
            ("1y", "1 年"),
            ("30d", "30天"),
            ("today", "今日"),
        };
        FiltersPanel.Children.Add(BuildSegmentGroup(dateOptions, _selectedDateRange, key =>
        {
            if (_selectedDateRange == key) return;
            _selectedDateRange = key;
            ShellPrefs.Set(DateRangeKey, key);
            _ = LoadAsync(force: true);
        }));

        // 2. 品种过滤器分组（若当前有成交记录则展示）
        var varieties = AvailableVarieties(trades);
        if (varieties.Count > 0)
        {
            if (!string.IsNullOrEmpty(_filterVariety) && !varieties.Contains(_filterVariety))
            {
                _filterVariety = "";
                ShellPrefs.Set(VarietyKey, "");
            }
            var varietyOptions = new List<(string Key, string Label)> { ("", "全部品种") };
            varietyOptions.AddRange(varieties.Select(v => (v, v)));
            FiltersPanel.Children.Add(BuildSegmentGroup(varietyOptions, _filterVariety, key =>
            {
                if (_filterVariety == key) return;
                _filterVariety = key;
                ShellPrefs.Set(VarietyKey, key);
                RenderRecords();
            }));
        }

        // 3. 策略过滤器分组（若当前有成交记录则展示）
        var strategies = AvailableStrategies(trades);
        if (strategies.Count > 0)
        {
            if (!string.IsNullOrEmpty(_filterStrategy) && !strategies.Contains(_filterStrategy))
            {
                _filterStrategy = "";
                ShellPrefs.Set(StrategyKey, "");
            }
            var strategyOptions = new List<(string Key, string Label)> { ("", "全部策略") };
            strategyOptions.AddRange(strategies.Select(s => (s, s)));
            FiltersPanel.Children.Add(BuildSegmentGroup(strategyOptions, _filterStrategy, key =>
            {
                if (_filterStrategy == key) return;
                _filterStrategy = key;
                ShellPrefs.Set(StrategyKey, key);
                RenderRecords();
            }));
        }
    }

    /// <summary>
    /// 取分段切片样式。样式定义在 App.xaml；查不到时返回 null 让 Button 退回默认样式，
    /// 而不是在界面构建时抛异常。
    /// </summary>
    private static Style? SegmentSliceStyle() =>
        Application.Current?.Resources.TryGetValue("SegmentSliceButtonStyle", out var style) == true
            ? style as Style
            : null;

    /// <summary>通用分段选择胶囊组（与日期筛选器风格一致：外层深色底边框，内层切片单选）</summary>
    private Border BuildSegmentGroup(
        IReadOnlyList<(string Key, string Label)> items,
        string selectedKey,
        Action<string> onSelect)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };

        foreach (var (key, label) in items)
        {
            var isSelected = selectedKey == key;
            // 用退化模板：默认 Button 模板的 PointerOver 灰底会盖掉这里设的悬停色
            var btn = new Button
            {
                Style = SegmentSliceStyle(),
                Content = label,
                FontSize = 13,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(10),
                Background = isSelected ? Brush(Palette.Soft) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Foreground = isSelected ? Brush(Palette.Ink) : Brush(Palette.Dim),
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            // 未选中的切片悬停提亮一档（选中态本身已有底色，不再叠加）
            if (!isSelected)
            {
                btn.PointerEntered += (_, _) => btn.Background = Brush(Palette.SidebarHover);
                btn.PointerExited += (_, _) => btn.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            btn.Click += (_, _) => onSelect(key);
            panel.Children.Add(btn);
        }

        return new Border
        {
            Background = Brush(Palette.Chrome),
            BorderBrush = Brush(Palette.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2),
            Child = panel,
        };
    }

    /// <summary>成交编号展示：显示编号后 8 位，缺失时用行号兜底</summary>
    private static string DisplayTradeNo(TradeRecord trade, int fallbackIndex)
    {
        var trimmed = trade.Id?.Trim() ?? "";
        if (trimmed.Length > 0)
        {
            return trimmed.Length > 8 ? trimmed[^8..] : trimmed;
        }
        return fallbackIndex.ToString(CultureInfo.InvariantCulture);
    }

    private Border BuildTradeTable(IReadOnlyList<TradeRecord> rows, string accountLabel)
    {
        var maxNoLen = rows.Select((r, i) => DisplayTradeNo(r, i + 1).Length).DefaultIfEmpty(2).Max();
        var noColWidth = Math.Max(42, maxNoLen * 8.0 + 16);

        var maxVarietyLen = rows.Select(r => DisplayVariety(r.Symbol).Length).DefaultIfEmpty(2).Max();
        var varietyWidth = Math.Max(80, maxVarietyLen * 8.5 + 16);

        var maxSourceLen = rows.Select(r => DisplaySource(r.Source).Length).DefaultIfEmpty(2).Max();
        var sourceWidth = Math.Max(70, maxSourceLen * 8.5 + 16);

        var columns = new[]
        {
            Center("编号", noColWidth),
            Left("日期", 80),
            Left("时间", 92),
            Left("品种", varietyWidth),
            Left("来源", sourceWidth),
            Center("开平", 40),
            Center("买卖", 36),
            Right("成交价", 68),
            Right("手数", 40),
            Right("平仓盈亏", 76),
            new ColumnSpec("", null, HorizontalAlignment.Stretch),
        };
        var cells = rows.Select((row, index) =>
        {
            TableCell profitCell = row.OffsetText == "开仓"
                ? new TableCell("—", Palette.Dim)
                : row.RealizedProfit is { } rp
                    ? new TableCell(FormatSigned(rp), ProfitColor(rp))
                    : new TableCell("—", Palette.Dim);

            var timeParts = row.TradeTime.Split(' ');
            var dateStr = timeParts.Length > 0 ? timeParts[0] : row.TradeTime;
            var clockStr = timeParts.Length > 1 ? timeParts[1] : "";
            if (clockStr.Length == 8 && !clockStr.Contains('.'))
            {
                clockStr += ".000";
            }

            var variety = DisplayVariety(row.Symbol);
            var sourceDisplay = DisplaySource(row.Source);

            return new List<TableCell>
            {
                new(DisplayTradeNo(row, index + 1), null),
                new(dateStr, null),
                new(clockStr, null),
                new(variety, null),
                new(sourceDisplay, null),
                new(row.OffsetText, null),
                new(row.DirectionText, SideColor(row.DirectionText)),
                new(FormatPrice(row.Price), null),
                new(row.Volume.ToString(CultureInfo.InvariantCulture), null),
                profitCell,
                new("", null),
            };
        }).ToList();

        var totalVol = rows.Sum(r => r.Volume);
        var openVol = rows.Where(r => r.OffsetText == "开仓").Sum(r => r.Volume);
        var closeVol = rows.Where(r => r.OffsetText != "开仓").Sum(r => r.Volume);
        var closeProfits = rows.Where(r => r.RealizedProfit != null).Select(r => r.RealizedProfit!.Value).ToList();
        var totalRealized = closeProfits.Sum();

        var summary = rows.Count > 0
            ? $"共 {rows.Count} 笔成交 · 累计 {totalVol} 手（开仓 {openVol} 手 / 平仓 {closeVol} 手）"
            : null;
        (string Text, Windows.UI.Color? Color)? hl = closeProfits.Count > 0
            ? ($" · 平仓盈亏合计: {FormatSigned(totalRealized)}", ProfitColor(totalRealized))
            : null;

        return BuildTable(columns, cells, "无成交记录", summary: summary, summaryHighlight: hl, onRowClick: index =>
        {
            if (index >= 0 && index < rows.Count)
            {
                _ = ShowTradeDetailDialogAsync(rows[index], accountLabel);
            }
        }, maxHeight: 460);
    }

    private async Task ShowTradeDetailDialogAsync(TradeRecord trade, string accountLabel)
    {
        if (XamlRoot is null) return;
        var variety = DisplayVariety(trade.Symbol);
        var sourceDisplay = DisplaySource(trade.Source);

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(DetailRow("合约代码", trade.Symbol, Palette.Primary, isBold: true));
        panel.Children.Add(DetailRow("交易品种", variety));
        panel.Children.Add(DetailRow("买卖方向", trade.DirectionText, SideColor(trade.DirectionText)));
        panel.Children.Add(DetailRow("开平性质", trade.OffsetText));
        panel.Children.Add(DetailRow("成交价格", FormatPrice(trade.Price)));
        panel.Children.Add(DetailRow("成交手数", $"{trade.Volume} 手"));
        if (trade.OffsetText != "开仓" && trade.RealizedProfit is { } profit)
        {
            panel.Children.Add(DetailRow("平仓盈亏", FormatSigned(profit), ProfitColor(profit)));
        }
        if (trade.Commission is { } comm)
        {
            panel.Children.Add(DetailRow("手续费", FormatMoney(comm)));
        }
        panel.Children.Add(DetailRow("来源策略", sourceDisplay == "手动" ? "手动下单" : sourceDisplay));
        panel.Children.Add(DetailRow("成交时间", trade.TradeTime));
        if (trade.Id.Length > 0)
        {
            panel.Children.Add(DetailRow("成交编号", trade.Id));
        }
        if (trade.OrderId.Length > 0)
        {
            panel.Children.Add(DetailRow("委托编号", trade.OrderId));
        }
        panel.Children.Add(DetailRow("所属账户", accountLabel));

        var dialog = new ContentDialog
        {
            Title = "成交明细",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private Grid DetailRow(string label, string value, Windows.UI.Color? color = null, bool isBold = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Brush(Palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = isBold ? 14 : 13,
            FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brush(color ?? Palette.Ink),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        return grid;
    }

    private TableCell NewSourceBadge(string source)
    {
        var isSmart = source != "手动/外部" && source != "手动";
        var fg = isSmart ? Palette.Primary : Palette.Dim;
        var bg = isSmart ? Palette.Soft : Palette.Soft;
        return new TableCell(source, fg, IsBadge: true, BadgeBg: bg);
    }

    // -------- 格式化 --------

    private Windows.UI.Color? SideColor(string text) => text switch
    {
        "买" => Palette.Danger,
        "卖" => Palette.Success,
        _ => null,
    };

    /// <summary>盈亏染色：正红负绿（国内期货习惯）</summary>
    private Windows.UI.Color? ProfitColor(double? value) => value switch
    {
        > 0 => Palette.Danger,
        < 0 => Palette.Success,
        _ => null,
    };

    private static string FormatMoney(double? value) =>
        value is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : "—";

    private static string FormatPrice(double? value)
    {
        if (value is not { } v)
        {
            return "—";
        }
        var text = v.ToString("F3", CultureInfo.InvariantCulture);
        text = text.TrimEnd('0').TrimEnd('.');
        return text.Length == 0 ? "0" : text;
    }

    private static string FormatSigned(double? value) =>
        value is { } v ? v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : "—";

    private static string FormatPercent(double? value) =>
        value is { } v ? (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
}
