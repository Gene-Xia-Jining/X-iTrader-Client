using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using XTrader.Shell.Core;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>
/// 仪表盘：模拟 / 实盘两张账户汇总卡片，单列纵向排列，可拖动互换顺序（对 mac 壳的 DashboardView）。
/// 汇总数据来自 trading.records 的当日资金快照，与「账户信息」tab 同一份来源。
/// </summary>
public sealed partial class DashboardPage : Page
{

    // -------- 页面公共逻辑（状态 / 宿主窗口 / 主题画刷）--------

    private readonly ThemedPage _theme;

    private AppState State => _theme.State;
    private MainWindow Host => _theme.Host;
    private Palette Palette => _theme.Palette;

    private SolidColorBrush Brush(Windows.UI.Color color) => _theme.Brush(color);

    private const string OrderPrefKey = "dashboard.cardOrder";

    /// <summary>卡片左右顺序；持久化到 ShellPrefs，重启后保持</summary>
    private List<string> _order;
    /// <summary>每类账户的当日汇总（key 为账户 kind）</summary>
    private readonly Dictionary<string, DashboardSummary> _summaries = [];
    private bool _loading;
    private string _errorText = "";
    /// <summary>请求代号：只有最新一次请求的结果会被采用</summary>
    private int _requestSeq;
    /// <summary>渲染签名：数据/主题/顺序/空态变化才重建卡片内容</summary>
    private string _renderKey = "";
    /// <summary>账户集合签名：账户增删改时重新取数</summary>
    private string _accountsSignature = "";
    /// <summary>拖拽中的卡片 kind</summary>
    private string? _dragging;
    private AppState? _subscribedState;

    public DashboardPage()
    {
        InitializeComponent();
        _order = LoadOrder();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
        SetupDrag(SimCard, DashboardSummary.SimKind);
        SetupDrag(LiveCard, DashboardSummary.LiveKind);
        Unloaded += (_, _) => UnsubscribeRecords();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Attach 同步触发 OnStateChanged：那里按账户签名发一次 LoadAsync，进页面即重取
        _theme.Attach(e);
    }

    private void SubscribeRecords(AppState? state)
    {
        if (_subscribedState == state) return;
        UnsubscribeRecords();
        _subscribedState = state;
        if (_subscribedState != null)
        {
            _subscribedState.RecordsUpdated += OnRecordsUpdated;
        }
    }

    private void UnsubscribeRecords()
    {
        if (_subscribedState != null)
        {
            _subscribedState.RecordsUpdated -= OnRecordsUpdated;
            _subscribedState = null;
        }
    }

    private void OnRecordsUpdated(string kind, IReadOnlyList<AccountRecords> records)
    {
        if (State?.ConnState != ConnState.Connected) return;
        if (!DashboardSummary.Kinds.Contains(kind)) return;
        // 与 AccountInfoView 一致：事件由 Post 投递到 UI 线程，这里再排一次队保证渲染线程安全
        DispatcherQueue.TryEnqueue(() =>
        {
            if (State?.ConnState != ConnState.Connected) return;
            var summary = GetSummary(kind);
            summary.ConfiguredCount = State.Accounts.Count(a => a.Kind == kind);
            summary.Records = records;
            Render();
        });
    }

    private void ApplyPalette()
    {
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        ErrorText.Foreground = Brush(Palette.Danger);

        foreach (var card in new[] { SimCard, LiveCard })
        {
            card.Background = Brush(Palette.Card);
            card.BorderBrush = Brush(Palette.Hairline);
        }

        _renderKey = "";  // 配色变了，卡片内容需要重建
        Render();
    }

    private void OnStateChanged()
    {
        if (State is null)
        {
            return;
        }
        SubscribeRecords(State);

        // 账户增删改或连接状态变化都会改变汇总口径，重取一次
        var signature = $"{State.ConnState}:"
            + string.Join("|", State.Accounts.Select(a => $"{a.Id}:{a.Kind}:{a.Enabled}"));
        if (signature != _accountsSignature)
        {
            _accountsSignature = signature;
            _ = LoadAsync();
        }
        Render();
    }

    // -------- 取数 --------

    /// <summary>
    /// 取全部账户的当日资金快照。trading.records 不传 kind 即返回全部账户，
    /// 一次请求即可填满两张卡片；未配置账户时不发请求，直接显示空态。
    /// </summary>
    private async Task LoadAsync()
    {
        var configured = State.Accounts;
        foreach (var kind in DashboardSummary.Kinds)
        {
            var summary = GetSummary(kind);
            summary.ConfiguredCount = configured.Count(a => a.Kind == kind);
            var cached = State.GetCachedRecords(kind);
            if (cached.Count > 0)
            {
                summary.Records = cached;
            }
        }

        var requestId = ++_requestSeq;
        if (State.ConnState != ConnState.Connected
            || !configured.Any(a => a.Kind is DashboardSummary.SimKind or DashboardSummary.LiveKind))
        {
            _loading = false;
            _errorText = "";
            ClearRecords();
            Render();
            return;
        }

        _loading = true;
        _errorText = "";
        Render();
        try
        {
            var resp = await State.CallAsync("trading.records");
            if (requestId != _requestSeq)
            {
                return;  // 已被更新的请求取代
            }
            var records = JsonHelpers.ObjectList(resp["accounts"])
                .Select(AccountRecords.From)
                .OfType<AccountRecords>()
                .ToList();
            foreach (var kind in DashboardSummary.Kinds)
            {
                var summary = GetSummary(kind);
                summary.ConfiguredCount = configured.Count(a => a.Kind == kind);
                var cardRecords = records.Where(r => r.Kind == kind).ToList();
                summary.Records = cardRecords;
                State.SetCachedRecords(kind, cardRecords);
            }
        }
        catch (Exception ex)
        {
            if (requestId != _requestSeq)
            {
                return;
            }
            _errorText = ex.Message;
        }
        finally
        {
            if (requestId == _requestSeq)
            {
                _loading = false;
                Render();
            }
        }
    }

    private DashboardSummary GetSummary(string kind)
    {
        if (_summaries.TryGetValue(kind, out var summary))
        {
            return summary;
        }
        summary = new DashboardSummary();
        _summaries[kind] = summary;
        return summary;
    }

    /// <summary>断开或没有可查询账户时清空旧数据，避免显示上一次连接的汇总</summary>
    private void ClearRecords()
    {
        foreach (var kind in DashboardSummary.Kinds)
        {
            GetSummary(kind).Records = [];
        }
    }

    // -------- 拖动排序 --------

    private void SetupDrag(Border card, string kind)
    {
        card.CanDrag = true;
        card.AllowDrop = true;

        card.DragStarting += (_, args) =>
        {
            _dragging = kind;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(kind);
            card.Opacity = 0.5;
        };
        card.DragOver += (_, args) =>
        {
            if (_dragging is not null && _dragging != kind)
            {
                args.AcceptedOperation = DataPackageOperation.Move;
            }
            args.Handled = true;
        };
        card.DragEnter += (_, _) => SetDropHighlight(kind, true);
        card.DragLeave += (_, _) => SetDropHighlight(kind, false);
        card.DropCompleted += (_, _) =>
        {
            // 拖拽取消（Esc）不会走 Drop，这里兜底清掉状态并恢复不透明度
            _dragging = null;
            SimCard.Opacity = 1.0;
            LiveCard.Opacity = 1.0;
            foreach (var other in DashboardSummary.Kinds)
            {
                SetDropHighlight(other, false);
            }
        };
        card.Drop += (_, args) =>
        {
            args.Handled = true;
            SetDropHighlight(kind, false);
            var source = _dragging;
            _dragging = null;
            SimCard.Opacity = 1.0;
            LiveCard.Opacity = 1.0;
            if (source is null || source == kind)
            {
                return;
            }
            var from = _order.IndexOf(source);
            var to = _order.IndexOf(kind);
            if (from < 0 || to < 0)
            {
                return;
            }
            (_order[from], _order[to]) = (_order[to], _order[from]);
            ShellPrefs.Set(OrderPrefKey, string.Join(",", _order));
            _renderKey = "";
            Render();
        };
    }

    /// <summary>拖动中的卡片不响应把自己拖回自身：DragOver 已按这点过滤，这里只管高亮。</summary>
    private void SetDropHighlight(string kind, bool highlighted)
    {
        var card = CardOf(kind);
        if (highlighted && _dragging != kind)
        {
            card.BorderBrush = Brush(Palette.Primary);
            card.BorderThickness = new Thickness(2);
        }
        else
        {
            card.BorderBrush = Brush(Palette.Hairline);
            card.BorderThickness = new Thickness(1);
        }
    }

    private Border CardOf(string kind) => kind == DashboardSummary.SimKind ? SimCard : LiveCard;

    private static List<string> LoadOrder()
    {
        var saved = ShellPrefs.Get(OrderPrefKey)?.Split(',') ?? [];
        var parsed = saved.Where(k => DashboardSummary.Kinds.Contains(k)).Distinct().ToList();
        return parsed.Count == DashboardSummary.Kinds.Length
            ? parsed
            : [DashboardSummary.SimKind, DashboardSummary.LiveKind];
    }

    // -------- 渲染 --------

    private void Render()
    {
        if (State is null)
        {
            return;
        }

        var key = string.Join(
            ",",
            _order)
            + $"|{Host.IsDark}|{_errorText}|{_loading}|{State.StartChecking}|"
            + string.Join(
                ";",
                DashboardSummary.Kinds.Select(kind =>
                {
                    var summary = GetSummary(kind);
                    return $"{kind}:{summary.ConfiguredCount}:{State.IsKindRunning(kind)}:"
                        + string.Join(",", summary.Records.Select(r =>
                            $"{r.Id}{r.Error}{r.Account.Balance}{r.Account.Available}{r.Account.Margin}{r.Account.FloatProfit}{r.Account.Commission}{r.Account.RiskRatio}"
                            + string.Join(";", r.Positions.Select(p => $"{p.Symbol}{p.Direction}{p.Volume}{p.OpenPrice}{p.PositionPrice}{p.LastPrice}{p.FloatProfit}{p.Margin}"))));
                }));
        if (key == _renderKey)
        {
            return;
        }
        _renderKey = key;

        ErrorText.Text = _errorText.Length > 0 ? $"账户数据读取失败: {_errorText}" : "";
        ErrorText.Visibility = _errorText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 上下顺序来自 _order
        CardsContainer.Children.Clear();
        foreach (var kind in _order)
        {
            CardsContainer.Children.Add(CardOf(kind));
        }

        SimCard.Child = BuildCardBody(DashboardSummary.SimKind);
        LiveCard.Child = BuildCardBody(DashboardSummary.LiveKind);
    }

    private UIElement BuildCardBody(string kind)
    {
        var summary = GetSummary(kind);
        var body = new StackPanel { Spacing = 14 };

        body.Children.Add(BuildHeader(kind, summary));
        body.Children.Add(BuildMetrics(summary));
        body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = Brush(Palette.Divider),
        });
        body.Children.Add(BuildPositionsSection(summary));
        return body;
    }

    private UIElement BuildHeader(string kind, DashboardSummary summary)
    {
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 拖动手柄固定在左上角，作为卡片可拖的视觉入口
        var handle = new TextBlock
        {
            Text = "≡",
            FontSize = 15,
            Foreground = Brush(Palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(handle, "拖动可调整卡片顺序");
        head.Children.Add(handle);

        var title = new TextBlock
        {
            Text = DashboardSummary.TitleOf(kind),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Ink),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);

        var badge = new Border
        {
            Background = Brush(Palette.Soft),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{summary.ConfiguredCount} 个账户",
                FontSize = 13,
                Foreground = Brush(Palette.Dim),
            },
        };
        Grid.SetColumn(badge, 2);
        head.Children.Add(badge);

        var status = BuildStatus(kind, summary);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 3);
        head.Children.Add(status);

        var isRunning = State?.IsKindRunning(kind) == true;
        var canToggle = summary.ConfiguredCount > 0 && State?.StartChecking != true;

        var pillBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(isRunning ? 0 : 1),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var pillText = new TextBlock
        {
            Text = isRunning ? "自动交易：开" : "自动交易：关",
            FontSize = 12,
            FontWeight = isRunning ? FontWeights.SemiBold : FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!canToggle)
        {
            pillBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            pillBorder.BorderBrush = Brush(Palette.Divider);
            pillText.Foreground = Brush(Palette.Dim);
            pillBorder.Opacity = 0.45;
        }
        else if (isRunning)
        {
            pillBorder.Background = Brush(Palette.Success);
            pillBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            pillText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        else
        {
            pillBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            pillBorder.BorderBrush = Brush(Palette.Hairline);
            pillText.Foreground = Brush(Palette.Ink);
        }

        pillBorder.Child = pillText;

        if (canToggle)
        {
            pillBorder.PointerEntered += (s, e) =>
            {
                if (isRunning)
                {
                    pillBorder.Background = Brush(Palette.SuccessHover);
                }
                else
                {
                    pillBorder.Background = Brush(Palette.SidebarHover);
                    pillBorder.BorderBrush = Brush(Palette.Dim);
                }
            };
            pillBorder.PointerExited += (s, e) =>
            {
                if (isRunning)
                {
                    pillBorder.Background = Brush(Palette.Success);
                }
                else
                {
                    pillBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    pillBorder.BorderBrush = Brush(Palette.Hairline);
                }
            };
            pillBorder.Tapped += (s, e) =>
            {
                State?.ToggleTrading(!isRunning, kind);
            };
        }

        ToolTipService.SetToolTip(
            pillBorder,
            summary.ConfiguredCount == 0
                ? $"尚未配置{DashboardSummary.TitleOf(kind)}账户"
                : (isRunning ? $"点击停止{DashboardSummary.TitleOf(kind)}自动交易" : $"点击开启{DashboardSummary.TitleOf(kind)}自动交易")
        );
        Grid.SetColumn(pillBorder, 4);
        head.Children.Add(pillBorder);

        return head;
    }

    private UIElement BuildMetrics(DashboardSummary summary)
    {
        var floatProfit = summary.Total(a => a.FloatProfit);
        var grid = new Grid { ColumnSpacing = 12 };
        var items = new (string Title, string Value, Windows.UI.Color Color)[]
        {
            ("账户权益", FormatMoney(summary.Total(a => a.Balance)), Palette.Ink),
            ("可用资金", FormatMoney(summary.Total(a => a.Available)), Palette.Ink),
            ("保证金", FormatMoney(summary.Total(a => a.Margin)), Palette.Ink),
            ("风险度", FormatPercent(summary.RiskRatio), Palette.Ink),
            ("浮动盈亏", FormatSigned(floatProfit), ProfitColor(floatProfit) ?? Palette.Ink),
            ("手续费", FormatMoney(summary.Total(a => a.Commission)), Palette.Ink),
        };

        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (title, value, color) = items[i];
            var cell = new StackPanel { Spacing = 3 };
            cell.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                Foreground = Brush(Palette.Dim),
            });
            cell.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 15,
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = Brush(color),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private FrameworkElement BuildStatus(string kind, DashboardSummary summary)
    {
        var failed = summary.Records.Where(r => r.Error.Length > 0).ToList();
        string text;
        if (summary.ConfiguredCount == 0)
        {
            text = $"尚未配置{DashboardSummary.TitleOf(kind)}账户";
        }
        else if (_loading && summary.Records.Count == 0)
        {
            text = "正在读取账户数据…";
        }
        else if (failed.Count > 0)
        {
            text = failed.Count > 1
                ? $"{failed.Count} 个账户读取失败：{failed[0].Error}"
                : $"{failed[0].Label} 读取失败：{failed[0].Error}";
        }
        else
        {
            text = "数据来自快期账户，仅包含当前交易日";
        }

        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush(failed.Count > 0 ? Palette.Danger : Palette.Dim),
        };
    }

    // -------- 持仓表格 --------

    private const string FloatProfitHelp = "按当前市场最新价与开仓均价计算的未实现盈亏。反映当前所有未平仓持仓以最新市价全部平仓时预估的累计盈亏金额。";
    private const string OpenPriceHelp = "当前持仓在建立仓位时的实际成交均价。若同一合约多次开仓，按各笔成交手数加权平均计算，不会随每日结算价变动。";

    private UIElement BuildPositionsSection(DashboardSummary summary)
    {
        var section = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Text = "当前持仓",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Brush(Palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var positions = summary.Records.Where(r => string.IsNullOrEmpty(r.Error)).SelectMany(r => r.Positions).ToList();
        if (positions.Count > 0)
        {
            head.Children.Add(new Border
            {
                Background = Brush(Palette.Soft),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = positions.Count.ToString(CultureInfo.InvariantCulture),
                    FontSize = 12,
                    Foreground = Brush(Palette.Dim),
                },
            });
        }
        section.Children.Add(head);
        section.Children.Add(BuildPositionTable(positions));
        return section;
    }

    private Border BuildPositionTable(IReadOnlyList<PositionRecord> rows)
    {
        var maxVarietyLen = rows.Select(r => DisplayVariety(r.Symbol).Length).DefaultIfEmpty(2).Max();
        var varietyWidth = Math.Max(80, maxVarietyLen * 8.5 + 16);

        var columns = new[]
        {
            Left("品种", varietyWidth), Left("合约"), Center("方向", 40), Right("手数", 44),
            Right("开仓均价", help: OpenPriceHelp),
            Right("当前价"),
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
            new(FormatPrice(row.LastPrice), null),
            new(FormatSigned(row.FloatProfit), ProfitColor(row.FloatProfit)),
            new(FormatMoney(row.Margin), null),
        }).ToList();
        return BuildTable(columns, cells, "当前无持仓");
    }

    private sealed record ColumnSpec(string Header, double? Width, HorizontalAlignment Align, string? Help = null);
    private sealed record TableCell(string Text, Windows.UI.Color? Color = null);

    private static ColumnSpec Left(string header, double? width = null, string? help = null) =>
        new(header, width, HorizontalAlignment.Left, help);

    private static ColumnSpec Center(string header, double? width = null, string? help = null) =>
        new(header, width, HorizontalAlignment.Center, help);

    private static ColumnSpec Right(string header, double? width = null, string? help = null) =>
        new(header, width, HorizontalAlignment.Right, help);

    private Border BuildTable(
        IReadOnlyList<ColumnSpec> columns,
        IReadOnlyList<IReadOnlyList<TableCell>> rows,
        string empty,
        double? maxHeight = null)
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
            body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 1,
                Fill = Brush(Palette.Divider),
                Margin = new Thickness(12, 0, 12, 0)
            });

            var rowsPanel = new StackPanel();
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                if (rowIndex > 0)
                {
                    rowsPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                    {
                        Height = 1,
                        Fill = Brush(Palette.Divider),
                        Margin = new Thickness(12, 0, 12, 0)
                    });
                }
                var row = rows[rowIndex];
                rowsPanel.Children.Add(BuildTableRow(row, columns, header: false));
            }

            if (maxHeight is > 0)
            {
                var scrollViewer = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = maxHeight.Value,
                    Content = rowsPanel,
                };
                body.Children.Add(scrollViewer);
            }
            else
            {
                body.Children.Add(rowsPanel);
            }
        }

        return new Border
        {
            Background = Brush(Palette.Chrome),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(Palette.Hairline),
            CornerRadius = new CornerRadius(12),
            Child = body,
        };
    }

    private Grid BuildTableRow(IReadOnlyList<TableCell> cells, IReadOnlyList<ColumnSpec> columns, bool header)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(12, header ? 7 : 6, 12, header ? 7 : 6),
        };
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            grid.ColumnDefinitions.Add(col.Width is { } w
                ? new ColumnDefinition { Width = new GridLength(w) }
                : new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < cells.Count && i < columns.Count; i++)
        {
            var cell = cells[i];
            var spec = columns[i];

            if (header && !string.IsNullOrEmpty(spec.Help))
            {
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = spec.Align,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                headerPanel.Children.Add(new TextBlock
                {
                    Text = cell.Text,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(Palette.Dim),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                headerPanel.Children.Add(CreateHelpButton(cell.Text, spec.Help));
                Grid.SetColumn(headerPanel, i);
                grid.Children.Add(headerPanel);
                continue;
            }

            var tb = new TextBlock
            {
                Text = cell.Text,
                FontSize = 13,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = header
                    ? Brush(Palette.Dim)
                    : cell.Color is { } c ? Brush(c) : Brush(Palette.Ink),
                HorizontalAlignment = spec.Align,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(tb, i);
            grid.Children.Add(tb);
        }
        return grid;
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
        button.Click += async (_, _) => await ShowHelpInfoAsync(title, helpText);
        return button;
    }

    private async Task ShowHelpInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
            },
            CloseButtonText = "我知道了",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private string DisplayVariety(string symbol)
    {
        var clean = symbol.Split('.').LastOrDefault() ?? symbol;
        var code = clean.ToUpperInvariant();
        var letters = new string(code.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length == 0) return clean;
        if (State is not null && State.ServerSymbolNames.TryGetValue(letters, out var name) && !string.IsNullOrEmpty(name))
        {
            return $"{letters}｜{name}";
        }
        return letters;
    }

    private static string FormatPrice(double? value)
    {
        if (value is not { } v) return "—";
        return v % 1 == 0
            ? v.ToString("F0", CultureInfo.InvariantCulture)
            : v.ToString("F2", CultureInfo.InvariantCulture);
    }

    // -------- 格式化 --------

    private Windows.UI.Color? ProfitColor(double? value) => value switch
    {
        > 0 => Palette.Danger,
        < 0 => Palette.Success,
        _ => null,
    };

    private static string FormatMoney(double? value) =>
        value is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : "—";

    private static string FormatSigned(double? value) =>
        value is { } v ? v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : "—";

    private static string FormatPercent(double? value) =>
        value is { } v ? (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";
}

/// <summary>一类账户的当日汇总：只累加读取成功的账户，失败项单独提示。</summary>
internal sealed class DashboardSummary
{
    internal const string SimKind = "sim";
    internal const string LiveKind = "live";
    internal static readonly string[] Kinds = [SimKind, LiveKind];

    internal static string TitleOf(string kind) => kind == SimKind ? "模拟交易" : "实盘交易";

    internal int ConfiguredCount { get; set; }
    internal IReadOnlyList<AccountRecords> Records { get; set; } = [];

    /// <summary>合计；所有账户该字段都未就绪（或没有账户）时返回 null，显示为「—」</summary>
    internal double? Total(Func<AccountSnapshot, double?> selector)
    {
        var values = Records
            .Where(r => r.Error.Length == 0)
            .Select(r => selector(r.Account))
            .OfType<double>()
            .ToList();
        return values.Count == 0 ? null : values.Sum();
    }

    /// <summary>综合风险度：单账户取其自身风险度，多账户按总保证金 ÷ 总权益计算；未就绪或权益非正时返回 null</summary>
    internal double? RiskRatio
    {
        get
        {
            var ok = Records.Where(r => r.Error.Length == 0).ToList();
            if (ok.Count == 1)
            {
                return ok[0].Account.RiskRatio;
            }
            var margin = Total(a => a.Margin);
            var balance = Total(a => a.Balance);
            if (margin is { } m && balance is { } b && b > 0)
            {
                return m / b;
            }
            return null;
        }
    }
}
