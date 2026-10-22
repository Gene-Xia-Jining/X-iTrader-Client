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

    public DashboardPage()
    {
        InitializeComponent();
        _order = LoadOrder();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
        SetupDrag(SimCard, DashboardSummary.SimKind);
        SetupDrag(LiveCard, DashboardSummary.LiveKind);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
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
                summary.Records = records.Where(r => r.Kind == kind).ToList();
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
            + $"|{Host.IsDark}|{_errorText}|{_loading}|"
            + string.Join(
                ";",
                DashboardSummary.Kinds.Select(kind =>
                {
                    var summary = GetSummary(kind);
                    return $"{kind}:{summary.ConfiguredCount}:"
                        + string.Join(",", summary.Records.Select(r =>
                            $"{r.Id}{r.Error}{r.Account.Balance}{r.Account.Available}{r.Account.Margin}{r.Account.FloatProfit}"));
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
        body.Children.Add(BuildBalanceBlock(summary));
        body.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = Brush(Palette.Divider),
        });
        body.Children.Add(BuildMetrics(summary));
        body.Children.Add(BuildStatus(kind, summary));
        return body;
    }

    private UIElement BuildHeader(string kind, DashboardSummary summary)
    {
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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

        return head;
    }

    private UIElement BuildBalanceBlock(DashboardSummary summary)
    {
        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(new TextBlock
        {
            Text = "账户权益",
            FontSize = 13,
            Foreground = Brush(Palette.Dim),
        });
        block.Children.Add(new TextBlock
        {
            Text = FormatMoney(summary.Total(a => a.Balance)),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Palette.Ink),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return block;
    }

    private UIElement BuildMetrics(DashboardSummary summary)
    {
        var floatProfit = summary.Total(a => a.FloatProfit);
        var grid = new Grid { ColumnSpacing = 12 };
        var items = new (string Title, string Value, Windows.UI.Color Color)[]
        {
            ("可用资金", FormatMoney(summary.Total(a => a.Available)), Palette.Ink),
            ("保证金", FormatMoney(summary.Total(a => a.Margin)), Palette.Ink),
            ("浮动盈亏", FormatSigned(floatProfit), ProfitColor(floatProfit) ?? Palette.Ink),
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
                FontWeight = FontWeights.Medium,
                Foreground = Brush(color),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private UIElement BuildStatus(string kind, DashboardSummary summary)
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
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(failed.Count > 0 ? Palette.Danger : Palette.Dim),
        };
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
}
