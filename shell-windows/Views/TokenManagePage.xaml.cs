using XTrader.Shell.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// Token 管理页：申请、刷新并查看服务器审批状态（对应 mac 壳的 TokenManageView）。
///
/// 列表为手写表格：WinUI 的 GridView 没有列头语义且不好对齐固定列宽，
/// 与 mac 壳一样用「列头 + 行」的 Grid 逐行构建，列宽常量集中在此。
/// </summary>
public sealed partial class TokenManagePage : Page
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
    // 列宽与 mac 壳逐项一致（编号 / 说明 / 申请时间 / 过期时间 / 已使用 / 状态 / 操作）
    private const double IndexWidth = 52;
    private const double TimeWidth = 130;
    private const double UseCountWidth = 65;
    private const double StatusWidth = 70;
    private const double ActionWidth = 96;
    private const double ColumnSpacing = 12;

    private string _lastRenderKey = "";
    private bool _switching;

    public TokenManagePage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
        if (State is null)
        {
            return;
        }
        // 与 mac 壳一致：进入页面即刷新一次审批状态
        State.RefreshTokenStatus();
    }

    private void ApplyPalette()
    {
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);

        foreach (var card in new[] { FormCard, RecordsCard })
        {
            card.Background = Brush(Palette.Card);
            card.BorderBrush = Brush(Palette.Hairline);
        }

        foreach (var label in new[] { StatusHint, EmptyHint })
        {
            label.Foreground = Brush(Palette.Dim);
        }
        foreach (var label in new[] { DescLabel, StatusLabel, RecordsTitle })
        {
            label.Foreground = Brush(Palette.Ink);
        }

        FormDivider.Fill = Brush(Palette.Divider);
        foreach (var box in new[] { DescriptionBox })
        {
            box.Background = Brush(Palette.InputBg);
            box.BorderBrush = Brush(Palette.Hairline);
        }

        _lastRenderKey = "";  // 配色变了，表格需要重建
        RenderRecords();
    }

    private void OnStateChanged()
    {
        TokenStatusText.Text = string.IsNullOrEmpty(State.TokenStatus) ? "未申请" : State.TokenStatus;
        TokenStatusText.Foreground = Brush(StatusColor(State.TokenStatus));
        ViewStatusButton.IsEnabled = State.TokenStatus.Contains("已通过");

        RenderRecords();
    }

    /// <summary>状态文案语义色（已通过绿 / 失败红 / 其余黄），与 mac 壳 statusColor 一致</summary>
    private Windows.UI.Color StatusColor(string status)
    {
        if (status.Length == 0 || status == "未申请") return Palette.Warning;
        if (status.Contains("已通过")) return Palette.Success;
        if (status.Contains("失败") || status.Contains("错误")) return Palette.Danger;
        return Palette.Warning;
    }

    private Windows.UI.Color RecordColor(string status) => status switch
    {
        "approved" => Palette.Success,
        "rejected" => Palette.Danger,
        _ => Palette.Warning,
    };

    // -------- 记录表 --------

    private void RenderRecords()
    {
        var records = State.TokenRecords;
        // 状态变更（尤其日志追加）会频繁触发 OnStateChanged，签名一致时跳过重建
        var key = string.Join(
            "|",
            records.Select(r => $"{r.Id}:{r.Status}:{r.UseCount}:{r.Claimable}"))
            + $";{State.ActiveTokenId};{string.Join(",", State.CachedTokenIds)};{Host.IsDark}";
        if (key == _lastRenderKey)
        {
            return;
        }
        _lastRenderKey = key;

        EmptyHint.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordsPanel.Children.Clear();
        if (records.Count == 0)
        {
            return;
        }

        RecordsPanel.Children.Add(BuildHeader());
        for (var i = 0; i < records.Count; i++)
        {
            RecordsPanel.Children.Add(BuildDivider());
            // 记录按申请时间倒序（最新在前），正序编号才是「历史第几笔」
            RecordsPanel.Children.Add(BuildRow(records.Count - i, records[i]));
        }
    }

    private Grid NewTableGrid() => new()
    {
        ColumnSpacing = ColumnSpacing,
        ColumnDefinitions =
        {
            new ColumnDefinition { Width = new GridLength(IndexWidth) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(TimeWidth) },
            new ColumnDefinition { Width = new GridLength(TimeWidth) },
            new ColumnDefinition { Width = new GridLength(UseCountWidth) },
            new ColumnDefinition { Width = new GridLength(StatusWidth) },
            new ColumnDefinition { Width = new GridLength(ActionWidth) },
        },
    };

    private Rectangle BuildDivider() => new()
    {
        Height = 1,
        Fill = Brush(Palette.Divider),
        Margin = new Thickness(0, 4, 0, 4),
    };

    private Grid BuildHeader()
    {
        var grid = NewTableGrid();
        var headers = new (string Title, string Help)[]
        {
            ("编号", "该笔申请的序号，按申请时间从早到晚排列：最早申请的为 1 号。列表本身仍按时间倒序显示，最新记录在最上面。"),
            ("申请说明", "向服务器提交 Token 申请时填写的用途或备注说明。"),
            ("申请时间", "向服务器发起该笔 Token 申请的提交时间。"),
            ("过期时间", "Token 的有效期截止时间，过期后需重新申请。"),
            ("已使用", "该 Token 签发后已被客户端用于连接与鉴权的累计调用次数。"),
            ("状态", "当前 Token 的审批状态：\n• 待审核：申请已提交，等待管理员审批\n• 已通过：审批通过，Token 有效可用\n• 已拒绝：申请未获批准"),
            ("操作", "切换：把该条记录的 Token 设为当前使用。\n删除：移除这条申请记录；若删掉的是当前生效的 Token，本地凭据会随之失效。"),
        };

        for (var i = 0; i < headers.Length; i++)
        {
            var (title, help) = headers[i];
            var button = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = BuildHeaderContent(title),
            };
            ToolTipService.SetToolTip(button, $"点击查看「{title}」含义");
            button.Click += async (_, _) => await ShowInfoAsync(title, help);
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
        }
        return grid;
    }

    private StackPanel BuildHeaderContent(string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(Palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "?",
            FontSize = 11,
            Foreground = Brush(Palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private Grid BuildRow(int index, TokenRecord record)
    {
        var grid = NewTableGrid();
        var textColor = Brush(Palette.Ink);

        void AddCell(int column, string text, double? width = null, Brush? foreground = null)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = foreground ?? textColor,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (width is { } w)
            {
                block.Width = w;
            }
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        AddCell(0, index.ToString());
        AddCell(1, record.Description.Length == 0 ? "—" : record.Description);
        AddCell(2, record.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—");
        AddCell(3, record.ExpiresAt?.ToString("yyyy-MM-dd HH:mm") ?? "—");
        AddCell(4, record.UseCount.ToString());
        AddCell(5, record.StatusText, StatusWidth, Brush(RecordColor(record.Status)));

        var actions = BuildActionCell(record);
        Grid.SetColumn(actions, 6);
        grid.Children.Add(actions);
        return grid;
    }

    /// <summary>操作列：当前生效显示「使用中」，否则给「切换」；删除按钮每行都有</summary>
    private StackPanel BuildActionCell(TokenRecord record)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (record.Id == State.ActiveTokenId)
        {
            var inUse = new TextBlock
            {
                Text = "使用中",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(Palette.Success),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(inUse, "该记录对应的 Token 正在被客户端使用");
            panel.Children.Add(inUse);
        }
        else
        {
            panel.Children.Add(BuildSwitchButton(record));
        }

        panel.Children.Add(BuildDeleteButton(record));
        return panel;
    }

    private Button BuildSwitchButton(TokenRecord record)
    {
        var canSwitch = CanSwitch(record);
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = "切换",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(canSwitch ? Palette.Primary : Palette.Dim),
            },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            // 不可切换时也保持启用：禁用态会把悬停提示一起吞掉，
            // 而提示正是告诉用户「为什么不能切」的唯一入口
            IsTabStop = canSwitch && !_switching,
        };
        ToolTipService.SetToolTip(button, SwitchHint(record));
        button.Click += async (_, _) =>
        {
            if (canSwitch && !_switching)
            {
                await ConfirmSwitchAsync(record);
            }
        };
        return button;
    }

    /// <summary>只有已通过、且本地缓存或服务器仍留有明文的记录才能切过去</summary>
    private bool CanSwitch(TokenRecord record) =>
        record.Status == "approved" && (record.Claimable || State.HasCachedToken(record.Id));

    private string SwitchHint(TokenRecord record)
    {
        if (record.Status != "approved") return "只有审批通过的记录可以切换为当前使用";
        if (CanSwitch(record)) return "把这条 Token 设为当前使用";
        return "该记录的明文已不可获取（首次领取后服务器不再保留），无法切换";
    }

    private Button BuildDeleteButton(TokenRecord record)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE74D",  // Segoe MDL2：垃圾桶
                FontSize = 12,
                Foreground = Brush(Palette.Danger),
            },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(button, "删除这条申请记录");
        button.Click += async (_, _) => await ConfirmDeleteAsync(record);
        return button;
    }

    // -------- 动作 --------

    private void OnRequestClick(object sender, RoutedEventArgs e)
    {
        var text = DescriptionBox.Text.Trim();
        State.RequestToken(text.Length == 0 ? "iTrader 智能交易系统" : text);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => State.RefreshTokenStatus();


    private async void OnViewStatusClick(object sender, RoutedEventArgs e)
    {
        await ShowInfoAsync("Token 审批状态", State.TokenStatus);
    }

    /// <summary>切换确认文案：切换会停掉自动交易（信号流要带新凭据重建），必须提前说明</summary>
    private async Task ConfirmSwitchAsync(TokenRecord record)
    {
        var summary = record.Description.Length == 0 ? "（无说明）" : record.Description;
        var message = $"将「{summary}」这条记录的 Token 设为当前使用。";
        if (State.EngineStatus.TradingActive)
        {
            message += "\n\n当前自动交易正在运行，切换后会被停止，需要重新开启（信号流必须用新 Token 重新连接）。";
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "切换当前使用的 Token",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "切换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _switching = true;
        _lastRenderKey = "";  // 按钮置灰状态需要重绘
        RenderRecords();
        var error = await State.SwitchTokenAsync(record.Id);
        _switching = false;
        _lastRenderKey = "";
        RenderRecords();
        if (error is not null)
        {
            await ShowInfoAsync("切换失败", error);
        }
    }

    /// <summary>删除确认文案：删掉已通过的记录会让本地 Token 失效，需在确认前讲清楚</summary>
    private async Task ConfirmDeleteAsync(TokenRecord record)
    {
        var summary = record.Description.Length == 0 ? "（无说明）" : record.Description;
        var message = record.Status == "approved"
            ? $"将删除申请记录「{summary}」。\n\n这条记录对应的 Token 已通过审批，删除后服务器不再认可它；若本地正在使用该 Token，需要重新申请并等待审批。"
            : $"将删除申请记录「{summary}」。删除后不可恢复。";

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "删除申请记录",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var error = await State.DeleteTokenRecordAsync(record.Id);
        if (error is not null)
        {
            await ShowInfoAsync("删除失败", error);
        }
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 24,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }
}
