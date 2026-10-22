using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>
/// 一类账户的交易记录（资金 / 持仓 / 当日委托与成交）。
///
/// 按账户类别取数并渲染，实盘交易页与模拟交易页的「交易记录」tab
/// 共用这一份实现。宿主页负责在进入页面时 <see cref="Reload"/>、
/// 主题或状态变化时 <see cref="Refresh"/>。
/// </summary>
public sealed partial class AccountRecordsView : UserControl
{
    private readonly string _kind;
    private readonly string _notConfiguredHint;

    private AppState? _state;
    private bool _dark;
    private Palette Palette => ThemeTokens.For(_dark);

    private bool _loading;
    /// <summary>是否至少完成过一次取数：未取过就不显示「暂无数据」，避免误导</summary>
    private bool _hasLoaded;
    /// <summary>请求代号：只有最新一次请求的结果会被采用</summary>
    private int _requestSeq;
    private string _errorText = "";
    private IReadOnlyList<AccountRecords> _records = [];
    /// <summary>渲染签名：数据/主题/空态变化才重建账户卡片</summary>
    private string _renderKey = "";

    public AccountRecordsView(string kind, string notConfiguredHint)
    {
        InitializeComponent();
        _kind = kind;
        _notConfiguredHint = notConfiguredHint;
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
            var cached = _state.GetCachedRecords(_kind);
            if (cached.Count > 0)
            {
                _records = cached;
                _hasLoaded = true;
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
        RefreshButton.BorderBrush = Brush(palette.Hairline);
        RefreshButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Hint.Text = HintText();
        // 签名里已含深浅色，配色变化时卡片会自动重建
        RenderRecords();
    }

    private void OnRecordsUpdated(string kind, IReadOnlyList<AccountRecords> list)
    {
        if (kind != _kind) return;
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
            var resp = await _state.CallAsync("trading.records", new JsonObject { ["kind"] = _kind });
            if (requestId != _requestSeq)
            {
                return;  // 已被更新的请求（再次刷新）取代
            }
            _records = JsonHelpers.ObjectList(resp["accounts"])
                .Select(AccountRecords.From)
                .OfType<AccountRecords>()
                .ToList();
            _state.SetCachedRecords(_kind, _records);
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
        return "数据来自快期账户，仅包含当前交易日";
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
            _errorText);
        if (key == _renderKey)
        {
            return;
        }
        _renderKey = key;

        ErrorCard.Visibility = _errorText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = _errorText;

        AccountPanel.Children.Clear();
        foreach (var record in _records)
        {
            AccountPanel.Children.Add(BuildAccountCard(record));
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

    private Border BuildAccountCard(AccountRecords record)
    {
        var body = new StackPanel { Spacing = 14 };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = record.Label,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Ink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(NewBadge(record.Kind == "sim" ? "模拟" : "实盘"));
        body.Children.Add(head);

        if (record.Error.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"读取失败: {record.Error}",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(Palette.Danger),
            });
        }
        else
        {
            body.Children.Add(BuildMetrics(record.Account));
            body.Children.Add(BuildSection("持仓", BuildPositionTable(record.Positions)));
            body.Children.Add(BuildSection("当日委托", BuildOrderTable(record.Orders)));
            body.Children.Add(BuildSection("当日成交", BuildTradeTable(record.Trades)));
        }
        return BuildCard(body);
    }

    private Border NewBadge(string text) => new()
    {
        Background = Brush(Palette.Soft),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 13, Foreground = Brush(Palette.Dim) },
    };

    private Border BuildMetrics(AccountSnapshot account)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(MetricRow(
            ("账户权益", FormatMoney(account.Balance), null),
            ("可用资金", FormatMoney(account.Available), null),
            ("保证金", FormatMoney(account.Margin), null)));
        panel.Children.Add(MetricRow(
            ("浮动盈亏", FormatSigned(account.FloatProfit), ProfitColor(account.FloatProfit)),
            ("持仓盈亏", FormatSigned(account.PositionProfit), ProfitColor(account.PositionProfit)),
            ("平仓盈亏", FormatSigned(account.CloseProfit), ProfitColor(account.CloseProfit))));
        panel.Children.Add(MetricRow(
            ("手续费", FormatMoney(account.Commission), null),
            ("风险度", FormatPercent(account.RiskRatio), null),
            ("昨日权益", FormatMoney(account.PreBalance), null)));
        return new Border
        {
            Background = Brush(Palette.Soft),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12),
            Child = panel,
        };
    }

    private Grid MetricRow(params (string Title, string Value, Windows.UI.Color? Color)[] items)
    {
        var grid = new Grid { ColumnSpacing = 0 };
        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (title, value, color) = items[i];
            var cell = new StackPanel { Spacing = 3 };
            cell.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Brush(Palette.Dim) });
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

    private StackPanel BuildSection(string title, UIElement table)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Dim),
        });
        section.Children.Add(table);
        return section;
    }

    // -------- 表格 --------

    private sealed record ColumnSpec(string Header, double? Width, HorizontalAlignment Align);

    private static ColumnSpec Left(string header, double? width = null) =>
        new(header, width, HorizontalAlignment.Left);

    private static ColumnSpec Right(string header, double? width = null) =>
        new(header, width, HorizontalAlignment.Right);

    /// <summary>
    /// 等宽列 + 表头 + 数据行的简易表格。列宽由 ColumnSpec 给出，null 表示按权重均分，
    /// 这样长文本（合约、时间）能自适应，短字段（买卖、手数）保持固定宽度对齐。
    /// </summary>
    private Border BuildTable(
        IReadOnlyList<ColumnSpec> columns,
        IReadOnlyList<IReadOnlyList<TableCell>> rows,
        string empty)
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
            foreach (var row in rows)
            {
                body.Children.Add(BuildTableRow(row, columns, header: false));
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
            var block = new TextBlock
            {
                Text = cell.Text,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = spec.Align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
                HorizontalAlignment = spec.Align,
                Foreground = Brush(cell.Color ?? (header ? Palette.Dim : Palette.Ink)),
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Grid.SetColumn(block, i);
            grid.Children.Add(block);
        }
        return grid;
    }

    private readonly record struct TableCell(string Text, Windows.UI.Color? Color);

    private Border BuildPositionTable(IReadOnlyList<PositionRecord> rows)
    {
        var columns = new[]
        {
            Left("合约"), Left("方向", 40), Right("手数", 44), Right("开仓均价"),
            Right("持仓均价"), Right("浮动盈亏"), Right("保证金"),
        };
        var cells = rows.Select(row => new List<TableCell>
        {
            new(row.Symbol, null),
            new(row.Direction, row.Direction == "多" ? Palette.Danger : Palette.Success),
            new(row.Volume.ToString(CultureInfo.InvariantCulture), null),
            new(FormatPrice(row.OpenPrice), null),
            new(FormatPrice(row.PositionPrice), null),
            new(FormatSigned(row.FloatProfit), ProfitColor(row.FloatProfit)),
            new(FormatMoney(row.Margin), null),
        }).ToList();
        return BuildTable(columns, cells, "当前无持仓");
    }

    private Border BuildOrderTable(IReadOnlyList<OrderRecord> rows)
    {
        var columns = new[]
        {
            Left("委托时间"), Left("合约"), Left("买卖", 40), Left("开平", 44),
            Right("委托手数", 60), Right("成交手数", 60), Right("委托价"), Right("成交均价"), Left("状态"),
        };
        var cells = rows.Select(row => new List<TableCell>
        {
            new(row.InsertTime, null),
            new(row.Symbol, null),
            new(row.DirectionText, SideColor(row.DirectionText)),
            new(row.OffsetText, null),
            new(row.VolumeOrigin.ToString(CultureInfo.InvariantCulture), null),
            new((row.VolumeOrigin - row.VolumeLeft).ToString(CultureInfo.InvariantCulture), null),
            new(FormatPrice(row.LimitPrice), null),
            new(FormatPrice(row.TradePrice), null),
            new(row.StatusText, null),
        }).ToList();
        return BuildTable(columns, cells, "当日无委托");
    }

    private Border BuildTradeTable(IReadOnlyList<TradeRecord> rows)
    {
        var columns = new[]
        {
            Left("成交时间"), Left("合约"), Left("买卖", 40), Left("开平", 44),
            Right("成交价"), Right("手数", 50),
        };
        var cells = rows.Select(row => new List<TableCell>
        {
            new(row.TradeTime, null),
            new(row.Symbol, null),
            new(row.DirectionText, SideColor(row.DirectionText)),
            new(row.OffsetText, null),
            new(FormatPrice(row.Price), null),
            new(row.Volume.ToString(CultureInfo.InvariantCulture), null),
        }).ToList();
        return BuildTable(columns, cells, "当日无成交");
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
