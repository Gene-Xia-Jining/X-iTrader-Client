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

/// <summary>显示模式：交易记录（资金、持仓、当日委托） / 历史成交（成交记录与时间范围筛选）</summary>
public enum AccountRecordsMode
{
    Records,
    Trades,
}

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
    private readonly AccountRecordsMode _mode;

    private AppState? _state;
    private bool _dark;
    private Palette Palette => ThemeTokens.For(_dark);

    private bool _loading;
    /// <summary>是否至少完成过一次取数：未取过就不显示「暂无数据」，避免误导</summary>
    private bool _hasLoaded;
    /// <summary>请求代号：只有最新一次请求的结果会被采用</summary>
    private int _requestSeq;
    private string _errorText = "";
    private string _selectedDateRange = "today";
    private IReadOnlyList<AccountRecords> _records = [];
    /// <summary>渲染签名：数据/主题/空态变化才重建账户卡片</summary>
    private string _renderKey = "";

    public AccountRecordsView(string kind, string notConfiguredHint, AccountRecordsMode mode = AccountRecordsMode.Records)
    {
        InitializeComponent();
        _kind = kind;
        _notConfiguredHint = notConfiguredHint;
        _mode = mode;
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
        DateRangePanel.Visibility = _mode == AccountRecordsMode.Trades ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == AccountRecordsMode.Trades)
        {
            RenderDateRangeButtons();
        }
        Hint.Text = HintText();
        // 签名里已含深浅色，配色变化时卡片会自动重建
        RenderRecords();
    }

    private void RenderDateRangeButtons()
    {
        DateRangePanel.Children.Clear();
        var options = new (string Key, string Label)[]
        {
            ("today", "今日"),
            ("3d", "近3天"),
            ("7d", "近7天"),
            ("all", "全部"),
        };
        foreach (var (key, label) in options)
        {
            var isSelected = _selectedDateRange == key;
            var btn = new Button
            {
                Content = label,
                FontSize = 13,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
                Background = isSelected ? Brush(Palette.Soft) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Foreground = isSelected ? Brush(Palette.Ink) : Brush(Palette.Dim),
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            btn.Click += (_, _) =>
            {
                if (_selectedDateRange == key) return;
                _selectedDateRange = key;
                RenderDateRangeButtons();
                _ = LoadAsync(force: true);
            };
            DateRangePanel.Children.Add(btn);
        }
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
            var dateRange = _mode == AccountRecordsMode.Trades ? _selectedDateRange : "today";
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
        if (_mode == AccountRecordsMode.Trades)
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

        if (_mode != AccountRecordsMode.Trades)
        {
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
        }

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
        else if (_mode == AccountRecordsMode.Records)
        {
            body.Children.Add(BuildMetrics(record.Account));
            body.Children.Add(BuildSection("持仓", BuildPositionTable(record.Positions)));
            body.Children.Add(BuildSection("当日委托", BuildOrderTable(record.Orders)));
        }
        else
        {
            body.Children.Add(BuildTradeTable(record.Trades, record.Label));
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
        string empty,
        string? summary = null,
        (string Text, Windows.UI.Color? Color)? summaryHighlight = null,
        Action<int>? onRowClick = null)
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
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                body.Children.Add(new Rectangle { Height = 1, Fill = Brush(Palette.Divider), Margin = new Thickness(12, 0, 12, 0) });
                var rowGrid = BuildTableRow(row, columns, header: false);
                if (onRowClick is not null)
                {
                    var capturedIndex = rowIndex;
                    rowGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    rowGrid.PointerEntered += (s, e) => rowGrid.Background = Brush(Palette.Soft);
                    rowGrid.PointerExited += (s, e) => rowGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    rowGrid.Tapped += (s, e) => onRowClick(capturedIndex);
                }
                body.Children.Add(rowGrid);
            }

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

            UIElement element;
            if (cell.IsCapsule)
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
            Left("委托时间"), Left("合约"), Left("买卖", 36), Left("开平", 40),
            Right("手数", 40), Right("成交", 40), Right("委托价"), Right("成交均价"), Left("状态", 60), Left("来源", 84),
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
                NewSourceBadge(DisplaySource(row.Source)),
            };
        }).ToList();

        var totalOrigin = rows.Sum(r => r.VolumeOrigin);
        var filledCount = rows.Count(r => r.VolumeOrigin > r.VolumeLeft);
        var canceledCount = rows.Count(r => r.VolumeLeft > 0 && r.StatusText == "已完");
        var summary = rows.Count > 0
            ? $"共 {rows.Count} 笔委托 · 报单 {totalOrigin} 手 · 已成 {filledCount} 笔" + (canceledCount > 0 ? $" · 已撤/废单 {canceledCount} 笔" : "")
            : null;

        return BuildTable(columns, cells, "无委托记录", summary: summary);
    }

    private string DisplayVariety(string symbol)
    {
        var clean = symbol.Split('.').LastOrDefault() ?? symbol;
        var letters = new string(clean.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length == 0) return symbol;
        if (_state?.ServerSymbolNames.TryGetValue(letters, out var name) == true && !string.IsNullOrEmpty(name))
        {
            return name;
        }
        return letters;
    }

    private string DisplaySource(string source)
    {
        var trimmed = source?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed) || trimmed is "手动/外部" or "手动")
        {
            return "手动";
        }
        if (trimmed.StartsWith("策略｜")) return trimmed;
        if (trimmed.StartsWith("策略|")) return "策略｜" + trimmed[3..];
        if (trimmed.StartsWith("策略 | ")) return "策略｜" + trimmed[5..];

        if (_state is not null)
        {
            var matched = _state.ServerStrategies.FirstOrDefault(s =>
                string.Equals(s.Id, trimmed, StringComparison.OrdinalIgnoreCase) || s.Name == trimmed);
            if (matched is not null)
            {
                var id = !string.IsNullOrEmpty(matched.Id) ? matched.Id : matched.Name;
                return $"策略｜{id}";
            }
        }
        if (trimmed == "智能信号")
        {
            var acc = _state?.Accounts.FirstOrDefault(a => a.Kind == _kind);
            if (!string.IsNullOrEmpty(acc?.Strategy))
            {
                return $"策略｜{acc.Strategy}";
            }
            var first = _state?.ServerStrategies.FirstOrDefault();
            if (!string.IsNullOrEmpty(first?.Id))
            {
                return $"策略｜{first.Id}";
            }
            return "策略｜智能信号";
        }
        return $"策略｜{trimmed}";
    }

    private Border BuildTradeTable(IReadOnlyList<TradeRecord> rows, string accountLabel)
    {
        var columns = new[]
        {
            Center("编号", 36),
            Left("日期", 80),
            Left("时间", 92),
            Left("品种", 60),
            Center("来源", 84),
            Center("买卖", 36),
            Center("开平", 40),
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
                new((index + 1).ToString(CultureInfo.InvariantCulture), null),
                new(dateStr, null),
                new(clockStr, null),
                new(variety, null),
                NewSourceBadge(sourceDisplay),
                new(row.DirectionText, SideColor(row.DirectionText)),
                new(row.OffsetText, null),
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
        });
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
