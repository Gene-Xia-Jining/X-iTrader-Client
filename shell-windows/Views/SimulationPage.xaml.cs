using XTrader.Shell.Theme;
using System.Text.Json.Nodes;
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
/// 模拟交易页：快期模拟账户七步引导卡片（对应 mac 壳的 SimulationView）。
///
/// 已存密码不回传明文：后端只给「是否已设置」和长度，密码框按真实长度
/// 自绘圆点掩码；点眼睛时才通过 accounts.get_password 受控取一次明文。
/// </summary>
public sealed partial class SimulationPage : Page
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
    // 引导文案里的外链（与 mac 壳一致）
    private const string UrlMobile = "https://www.shinnytech.com/products/app";
    private const string UrlAppStore = "https://itunes.apple.com/us/app/快期小q/id1187762307?l=zh&ls=1&mt=8";
    private const string UrlWindows = "https://www.shinnytech.com/products/q73";
    private const string UrlRegister = "https://account.shinnytech.com/";

    private string _selectedSymbol = "";
    private string _selectedStrategy = "";
    private readonly List<string> _keptSymbols = [];

    private bool _hasStoredPassword;
    private int _storedPasswordLength;
    private bool _passwordVisible;
    private bool _testing;
    /// <summary>眼睛按钮取回的已存明文：按此值回填时不算用户修改</summary>
    private string _revealedPassword = "";
    /// <summary>上一次从账号回填的值：未被编辑时才跟随 accounts 刷新</summary>
    private string _lastAccountPhone = "";
    /// <summary>第 3 步胶囊文字与颜色的签名：没变就不重建 Content，避免状态事件反复重绘按钮</summary>
    private string _accountCapsuleKey = "";
    private bool _accountHintError;
    private bool _serverTesting;
    /// <summary>第 5 步 Token 申请进行中（按钮置灰防重复点击）</summary>
    private bool _tokenRequesting;
    /// <summary>本次申请的回执；为空时提示行改为跟随后端实时状态</summary>
    private string _tokenMessage = "";
    /// <summary>胶囊文字与颜色的签名：没变就不重建 Content，避免状态事件反复重绘按钮</summary>
    private string _tokenCapsuleKey = "";
    /// <summary>上一次从配置回填的服务器地址：输入框未被编辑时才跟随配置刷新</summary>
    private string _lastConfigServerUrl = "";
    private bool _filling;

    /// <summary>品种/策略胶囊的重建签名：数据或选中态变化才重建，避免频繁刷界面</summary>
    private string _symbolKey = "";
    private string _strategyKey = "";

    // -------- 三个 tab：快速开始（原 7 步引导）/ 交易记录（资金、持仓与当日委托）/ 历史成交（成交记录明细）--------

    private const string TabPrefKey = "simulation.tab";

    private enum Tab { Start, Records, Trades }

    private Tab _tab = Tab.Start;
    /// <summary>「交易记录」tab 的主体：懒建一次，切走再切回沿用它自己的数据</summary>
    private AccountRecordsView? _recordsView;
    /// <summary>「历史成交」tab 的主体：懒建一次，切走再切回沿用它自己的数据</summary>
    private AccountRecordsView? _tradesView;

    // -------- 常见问题（展开状态存 ShellPrefs；展开后只列问题，点问题弹窗看详细）--------

    private const string FaqPrefKey = "simulation.faqExpanded";

    /// <summary>单条问答：列表里只显示问题，答案在 ContentDialog 里展开</summary>
    private sealed record FaqItem(string Question, string Answer);

    private static readonly FaqItem[] FaqItems =
    [
        new(
            "模拟交易和实盘交易有什么区别？",
            "模拟交易用的是快期模拟账户，资金是虚拟的，与快期客户端 / APP / 专业版里的模拟账户互通。"
            + "把回测和模拟当作实盘前的体检，确认下单、平仓、信号接收都正常后再转到实盘。"),
        new(
            "为什么点击「自动交易」没有开启？",
            "开启前客户端会先做一次启动前预检：服务器连通、账户的手机号 / 密码 / 品种 / 策略是否齐全、"
            + "Token 是否已领取、天勤账户能否登录。任一项不通过都会弹出问题清单，按清单逐项处理后重新开启即可。"),
        new(
            "为什么改了配置要重新开启自动交易？",
            "自动交易运行中修改账户、品种或策略不会立即生效，客户端会在日志里提示「下次启动生效」，"
            + "避免正在持仓的账户策略被中途换掉。想让改动生效，先关掉自动交易再重新开启。"),
        new(
            "自动交易关着的时候收到信号会怎样？",
            "信号会照常记录到交易记录里，但不会下单。这些信号不会在重新开启后补做——"
            + "恢复自动交易只对之后新到的信号生效。"),
        new(
            "交易价格是怎么定的？",
            "方向、目标仓位都由服务器决定，客户端只负责执行；成交价用的是客户端实时行情，"
            + "以对手价优先成交，不沿用信号里的参考价。"),
        new(
            "如何选择交易品种？",
            "第 6 步选的是品种（如螺纹钢、甲醇），不是具体合约：具体月份合约由服务器信号给出，"
            + "客户端只按实时行情以对手价成交。一个账户同一时间只能选一个品种。\n\n"
            + "选品种可以按三点筛：\n"
            + "1，权限。模拟账户只支持国内商品和股指 / 国债，不支持组合、套利和期权。"
            + "铁矿石、棕榈油、PTA、豆粕、原油属于特定品种，股指和国债也要单独开通，"
            + "转实盘前要先在期货公司确认权限，否则下单会报权限不足。\n"
            + "2，流动性。看持仓量和盘口深度，不要只看成交量——成交量最大的品种盘口未必厚，滑点会更大。"
            + "螺纹钢、甲醇、玻璃、纯碱、玉米、热卷、白糖、PVC 等持仓量大、盘口厚，适合起步。\n"
            + "3，单手规模。保证金 = 价格 × 合约单位 × 保证金比例。黄金一手保证金约 18–20 万，"
            + "纯碱只要约 2000 元，相差 90 倍：同样手数下风险完全不同，资金不多时先避开黄金、原油、股指这类大合约。\n\n"
            + "建议先用一个自己熟悉、流动性好的普通商品跑顺流程，稳定后再换成其他品种。"),
        new(
            "期货的交易时间是什么时候？",
            "国内期货按北京时间交易，分白盘和夜盘，周末和法定节假日休市。模拟账户的时段与实盘一致。\n\n"
            + "1，商品期货白盘：09:00–10:15、10:30–11:30、13:30–15:00，上午 10:15–10:30 休息。\n"
            + "2，商品期货夜盘：21:00 开始，收盘时间按品种不同——螺纹钢、热卷、甲醇、玻璃、纯碱、白糖、PVC 等 23:00 收；"
            + "铜、铝、锌、镍等 01:00 收；黄金、白银、原油 02:30 收。\n"
            + "3，中金所股指 / 国债：09:30–11:30、13:00–15:00，国债到 15:15，都没有夜盘。\n\n"
            + "休市时段下单不会成交，会被记为执行失败，开盘后不会自动补做。"
            + "鸡蛋、苹果、红枣、硅铁、锰硅等品种没有夜盘，具体时段以交易所公告为准。"),
        new(
            "同一个信号会被重复执行吗？",
            "不会。每条信号在本地 SQLite 里按账户登记，已执行过的信号再次收到时只记一条日志，不会重复下单。"),
        new(
            "关闭客户端后会继续交易吗？",
            "不会。自动交易不会在下次启动时自动开启，需要手动再点一次；"
            + "关闭客户端期间也不会接收信号，重新开启后不会补做关闭期间的信号。"),
        new(
            "出问题的时候去哪里看？",
            "「系统日志」页有完整的运行日志，包含连接、信号、下单与错误信息；"
            + "交易执行明细可以在本页「交易记录」tab 查看模拟账户，实盘账户在「实盘交易」页的「交易记录」tab 查看。"),
    ];

    private bool _faqExpanded;
    /// <summary>问答详情弹窗：同一个 ContentDialog 不能并发 ShowAsync，重复点击直接忽略</summary>
    private ContentDialog? _faqDialog;
    private bool _faqDialogOpen;

    // -------- 第 1 步的下载链接弹窗（控件在代码里构建，对应 mac 壳的 downloadLinks）--------

    private TextBlock LinkMobile = null!;
    private TextBlock LinkAppStore = null!;
    private TextBlock LinkWindows = null!;
    /// <summary>标题行与链接之间的分隔线</summary>
    private Microsoft.UI.Xaml.Shapes.Rectangle _downloadDivider = null!;

    // -------- 第 3 步的账号弹窗（控件在代码里构建，对应 mac 壳的 accountEditor）--------

    private ContentDialog? _accountDialog;
    /// <summary>同一个 ContentDialog 不能并发 ShowAsync，重复点击直接忽略</summary>
    private bool _accountDialogOpen;
    private TextBlock PhoneLabel = null!;
    private TextBox PhoneBox = null!;
    private TextBlock PasswordLabel = null!;
    private Border PasswordFrame = null!;
    private StackPanel PasswordMask = null!;
    private PasswordBox PasswordHidden = null!;
    private TextBox PasswordShown = null!;
    private Button EyeButton = null!;
    private TextBlock AccountHint = null!;
    /// <summary>弹窗内的「测试连接」结果：ContentDialog 打开时不能再叠一层对话框，只能就地显示</summary>
    private TextBlock TestHint = null!;
    /// <summary>标题行与表单之间的分隔线</summary>
    private Microsoft.UI.Xaml.Shapes.Rectangle _accountDivider = null!;

    // -------- 第 6 步的品种选择弹层（页面内遮罩层，对应 mac 壳的 symbolPicker）--------

    private WrapPanel SymbolPanel = null!;
    private TextBlock SymbolHint = null!;
    private readonly Dictionary<SymbolFilter, Button> _symbolFilterButtons = [];
    /// <summary>品类过滤器（全部品种 / 国内商品 / 股指 / 国债）：单选</summary>
    private SymbolFilter _symbolFilter = SymbolFilter.All;
    /// <summary>「全部品种」那一档的列表高度：弹窗顶边以它为准，切换过滤器只收缩下边距</summary>
    private double _symbolMaxListHeight;

    // -------- 第 5 步的 Token 申请记录弹窗（已申请过时点胶囊打开）--------

    private ContentDialog? _tokenRecordsDialog;
    /// <summary>同一个 ContentDialog 不能并发 ShowAsync，重复点击直接忽略</summary>
    private bool _tokenRecordsDialogOpen;
    private TextBlock TokenRecordsStatus = null!;
    private TextBlock TokenRecordsEmptyHint = null!;
    private Microsoft.UI.Xaml.Shapes.Rectangle TokenRecordsDivider = null!;
    private StackPanel TokenRecordsPanel = null!;

    public SimulationPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
        BuildDownloadDialog();
        BuildAccountEditor();
        BuildSymbolDialog();
        BuildTokenRecordsDialog();
        // 展开状态在构造时恢复：页面切走再回来不该被重置成收起
        _faqExpanded = ShellPrefs.Get(FaqPrefKey) is "1";
        RenderFaq();
        _tab = ShellPrefs.Get(TabPrefKey) switch
        {
            "trades" => Tab.Trades,
            "records" => Tab.Records,
            _ => Tab.Start,
        };
        // 构造时就按上次的 tab 定好两栏的可见性，避免首帧闪一下引导卡片
        ApplyTabStyles();

        ServerBox.LostFocus += (_, _) => SaveServerIfChanged();
        ServerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveServerIfChanged();
            }
        };
    }

    // -------- 第 3 步的账号弹窗（手机号 / 密码，输入完成即保存）--------

    /// <summary>
    /// 账号弹窗的控件只在构造时建一次：页面本身不再常驻表单，
    /// 关闭弹窗只是隐藏，再次打开沿用同一批控件和已填内容。
    /// </summary>
    private void BuildAccountEditor()
    {
        PhoneBox = new TextBox { PlaceholderText = "手机号", MinHeight = 36, VerticalAlignment = VerticalAlignment.Center };

        PasswordHidden = new PasswordBox { BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(12, 0, 12, 0), MinHeight = 34 };
        PasswordShown = new TextBox { BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(12, 0, 12, 0), MinHeight = 34, Visibility = Visibility.Collapsed };
        PasswordMask = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), IsHitTestVisible = false };
        EyeButton = new Button
        {
            Width = 40,
            MinWidth = 40,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };
        EyeButton.Click += OnEyeClick;

        // 密码框 + 眼睛按钮合成一组，边框与底色统一画在外层
        var passwordInner = new Grid();
        passwordInner.Children.Add(PasswordMask);
        passwordInner.Children.Add(PasswordHidden);
        passwordInner.Children.Add(PasswordShown);
        var passwordGrid = new Grid();
        passwordGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        passwordGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        passwordGrid.Children.Add(passwordInner);
        Grid.SetColumn(EyeButton, 1);
        passwordGrid.Children.Add(EyeButton);
        PasswordFrame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            MinHeight = 36,
            Child = passwordGrid,
        };

        var phoneRow = new Grid { ColumnSpacing = 8 };
        phoneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        phoneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        phoneRow.Children.Add(PhoneLabel = AccountFieldLabel("手机号："));
        Grid.SetColumn(PhoneBox, 1);
        phoneRow.Children.Add(PhoneBox);

        var passwordRow = new Grid { ColumnSpacing = 8 };
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        passwordRow.Children.Add(PasswordLabel = AccountFieldLabel("密码："));
        Grid.SetColumn(PasswordFrame, 1);
        passwordRow.Children.Add(PasswordFrame);

        AccountHint = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        // 测试结果落在内容区最下方一行、靠左；系统按钮行（测试连接 / 关闭）在其下方贴右
        TestHint = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Spacing = 12, Width = 400 };
        // 说明文案在胶囊右侧问号里，弹窗内只放字段；
        // 标题仍由 ContentDialog 绘制，分隔线放在内容顶部即标题行之下
        content.Children.Add(_accountDivider = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1 });
        content.Children.Add(phoneRow);
        content.Children.Add(passwordRow);
        content.Children.Add(AccountHint);
        content.Children.Add(TestHint);

        var dialog = new ContentDialog
        {
            Title = "填入快期/天勤用户信息",
            Content = content,
            // 测试连接摆在关闭按钮之前：用 Secondary 占位，关闭仍走 Close
            SecondaryButtonText = "测试连接",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        // 测试连接只跑测试，不关闭弹窗（Secondary 按钮默认会关，需 Cancel）
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await TestAccountAsync();
        };
        // 关闭前输入框可能尚未失焦，显式补一次保存
        dialog.Closing += (_, _) => SaveAccountIfChanged();
        _accountDialog = dialog;

        // 输入完成即保存：回车或失焦都算
        PhoneBox.LostFocus += (_, _) => SaveAccountIfChanged();
        PhoneBox.KeyDown += (_, e) => OnCommitKey(e, SaveAccountIfChanged);
        PasswordHidden.LostFocus += (_, _) => SaveAccountIfChanged();
        PasswordShown.LostFocus += (_, _) => SaveAccountIfChanged();
        PasswordHidden.KeyDown += (_, e) => OnCommitKey(e, SaveAccountIfChanged);
        PasswordShown.KeyDown += (_, e) => OnCommitKey(e, SaveAccountIfChanged);

        PasswordHidden.PasswordChanged += (_, _) => UpdatePasswordMask();
        PasswordShown.TextChanged += (_, _) => UpdatePasswordMask();
        PhoneBox.TextChanged += (_, _) => UpdateAccountEntryLabel();
        PasswordHidden.PasswordChanged += (_, _) => UpdateAccountEntryLabel();
        PasswordShown.TextChanged += (_, _) => UpdateAccountEntryLabel();
        PasswordHidden.GotFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Primary);
        PasswordHidden.LostFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Hairline);
        PasswordShown.GotFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Primary);
        PasswordShown.LostFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Hairline);
    }

    private static void OnCommitKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e, Action commit)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            commit();
        }
    }

    /// <summary>弹窗内字段标签：固定宽度右对齐，让两个输入框左边界对齐</summary>
    private static TextBlock AccountFieldLabel(string text) => new()
    {
        Text = text,
        Width = 62,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right,
    };

    /// <summary>
    /// 第 3 步胶囊文案：手机号与密码都填好后改为「当前用户：手机号」，
    /// 免得必须打开弹窗才知道当前登录的是谁。已存密码时输入框留空，按已设置计算。
    /// 测试连接通过后文字转绿；手机号或密码被改动即回落主题色。
    /// </summary>
    private void UpdateAccountEntryLabel()
    {
        var account = PhoneBox.Text.Trim();
        var hasPassword = CurrentPassword.Length > 0 || _hasStoredPassword;
        var title = account.Length > 0 && hasPassword
            ? $"当前用户：{account}"
            : "填入快期/天勤用户信息";
        // 文字颜色放在 TextBlock 上：Button 模板在悬停/按下时会覆盖 Foreground，
        // 直接给 Button 上色会让绿色在鼠标移上去时消失（与第 5 步胶囊一致）
        var color = AccountVerified ? Palette.Success : Palette.Primary;
        var key = $"{title}|{color}";
        if (key == _accountCapsuleKey)
        {
            return;
        }
        _accountCapsuleKey = key;
        AccountEditorButton.Content = new TextBlock
        {
            Text = title,
            FontSize = 13,
            Foreground = Brush(color),
        };
    }

    /// <summary>
    /// 只有「测试连接」通过的那组手机号 / 密码还在时才算已验证：
    /// 改了任一项胶囊立刻回到主题色，避免把旧验证结果当成本次凭据有效。
    /// </summary>
    private bool AccountVerified =>
        PhoneBox.Text.Trim().Length > 0
        && (CurrentPassword.Length > 0 || _hasStoredPassword)
        && State.VerifiedTqCredentials?.Matches(PhoneBox.Text.Trim(), ComparablePassword) == true;

    /// <summary>
    /// 眼睛按钮取回的已存明文与输入框留空指的是同一份凭据，
    /// 比较前统一成空串，免得只是点了一下眼睛就把绿色消掉。
    /// </summary>
    private string ComparablePassword
    {
        get
        {
            var password = CurrentPassword;
            return password == _revealedPassword ? "" : password;
        }
    }

    private async void OnAccountHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowInfoAsync(
            "填入快期/天勤用户信息",
            "填写快期（天勤）账号的注册手机号与密码，客户端用它登录模拟交易账户。输入完成即自动保存。");
    }

    private async void OnAccountEditorClick(object sender, RoutedEventArgs e)
    {
        var dialog = _accountDialog;
        if (dialog is null || _accountDialogOpen)
        {
            return;
        }
        SetTestHint("", isError: false);
        // XamlRoot 在构造时还拿不到（页面尚未挂到窗口），打开时再补
        dialog.XamlRoot = Root.XamlRoot;
        _accountDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _accountDialogOpen = false;
        }
    }

    // -------- 第 6 步的品种入口弹窗（胶囊列表，品种多时弹窗内滚动）--------

    /// <summary>
    /// 品种弹层的控件只在构造时建一次：页面本身不再常驻品种列表，
    /// 关闭弹层只是隐藏，再次打开沿用同一批控件。
    /// 遮罩空白处、标题行「关闭」按钮、Esc 都能关闭；选中品种时自动关闭。
    /// </summary>
    private void BuildSymbolDialog()
    {
        SymbolPanel = new WrapPanel { Spacing = 10 };
        SymbolHint = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed,
        };

        // 品类过滤器只在构造时建一次，切换只是重排胶囊
        foreach (var filter in SymbolFilters.All)
        {
            var chip = new Button
            {
                FontSize = 13,
                MinHeight = 30,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(15),
            };
            chip.Click += (_, _) => SelectSymbolFilter(filter);
            _symbolFilterButtons[filter] = chip;
            SymbolFilterBar.Children.Add(chip);
        }

        SymbolDialogBody.Children.Add(SymbolPanel);
        SymbolDialogBody.Children.Add(SymbolHint);

        // 点卡片外的遮罩空白即关闭：只处理命中的是遮罩本身（点到卡片不算）
        SymbolOverlay.Tapped += (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, SymbolOverlay))
            {
                HideSymbolDialog();
            }
        };
        // 窗口可以拖得比弹层小，卡片与列表都跟着可用空间收缩
        SymbolOverlay.SizeChanged += (_, args) =>
        {
            SymbolDialogCard.MaxWidth = Math.Max(args.NewSize.Width - 48, 320);
            // 149 = 上下遮罩边距（48）+ 卡片内边距（40）+ 顶栏（36）+ 分隔线（1）+ 两处间距（24）
            SymbolScroll.MaxHeight = Math.Clamp(args.NewSize.Height - 149, 120, 460);
            UpdateSymbolDialogPlacement();
        };
        // 弹层没有独立窗口，Esc 由遮罩层接管（打开时焦点在「关闭」按钮上，事件冒泡到遮罩）
    }

    /// <summary>单选：点哪颗就只看哪一类，「全部品种」不过滤</summary>
    private void SelectSymbolFilter(SymbolFilter filter)
    {
        _symbolFilter = filter;
        // 过滤条件变了必须重建胶囊，跳过「签名没变」的缓存判断
        _symbolKey = "";
        RenderCapsules();
    }

    /// <summary>
    /// 顶边固定：按「全部品种」那一档的卡片高度居中算出上边距，
    /// 之后切换过滤器卡片从同一条顶边往下收，只有下边距变化。
    /// </summary>
    private void UpdateSymbolDialogPlacement()
    {
        var overlayHeight = SymbolOverlay.ActualHeight;
        if (overlayHeight <= 0)
        {
            return;
        }

        // 101 = 卡片上下内边距（40）+ 顶栏（36）+ 分隔线（1）+ 两处间距（24）
        var listHeight = Math.Min(Math.Max(_symbolMaxListHeight, 31), SymbolScroll.MaxHeight);
        var cardHeight = listHeight + 101;
        var top = Math.Max((overlayHeight - cardHeight) / 2, 24);
        SymbolDialogCard.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>
    /// 量出「全部品种」那一档的列表高度：临时按全部品种排一次胶囊、量完再恢复当前过滤器。
    /// 只在打开弹窗时做，保证顶边始终按最高档算，切换过滤器只收缩下边距。
    /// </summary>
    private void RefreshSymbolMaxListHeight()
    {
        var current = _symbolFilter;
        if (current != SymbolFilter.All)
        {
            _symbolFilter = SymbolFilter.All;
            _symbolKey = "";
            RenderCapsules();
        }
        // 卡片刚显示时布局可能还没跑完，强制排一次再量
        SymbolPanel.UpdateLayout();
        var height = SymbolPanel.ActualHeight;
        if (height > 0)
        {
            _symbolMaxListHeight = height;
        }
        if (current != SymbolFilter.All)
        {
            _symbolFilter = current;
            _symbolKey = "";
            RenderCapsules();
        }
        UpdateSymbolDialogPlacement();
    }

    /// <summary>选中态实心主题色，未选中透明描边（与品种胶囊一致）</summary>
    private void UpdateSymbolFilterStyles()
    {
        var all = AllSymbols;
        foreach (var (filter, chip) in _symbolFilterButtons)
        {
            var isOn = _symbolFilter == filter;
            // Content 只在数量变化时重设：每次赋新字符串会让按钮重新测量
            var text = $"{filter.Title()} {SymbolFilters.Count(filter, all)}";
            if ((chip.Content as string) != text)
            {
                chip.Content = text;
            }
            chip.Background = new SolidColorBrush(isOn ? Palette.Primary : Microsoft.UI.Colors.Transparent);
            chip.BorderBrush = new SolidColorBrush(isOn ? Palette.Primary : Palette.Hairline);
            chip.BorderThickness = new Thickness(1);
            chip.Foreground = new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Palette.Ink);
            chip.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void OnSymbolOverlayKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HideSymbolDialog();
            e.Handled = true;
        }
    }

    // -------- 常见问题（展开后只列问题，点问题弹窗看详细）--------

    private void OnFaqHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _faqExpanded = !_faqExpanded;
        ShellPrefs.Set(FaqPrefKey, _faqExpanded ? "1" : "0");
        RenderFaq();
    }

    /// <summary>按展开状态重建标题右侧的「展开 / 收起」与问题列表</summary>
    private void RenderFaq()
    {
        FaqToggleText.Text = _faqExpanded ? "收起" : "展开";
        FaqChevron.Glyph = _faqExpanded ? "\uE70E" : "\uE70D";
        FaqBody.Visibility = _faqExpanded ? Visibility.Visible : Visibility.Collapsed;
        FaqBody.Children.Clear();

        if (!_faqExpanded)
        {
            return;
        }
        // 标题行与列表之间、以及每两个问题之间各一条发丝线
        FaqBody.Children.Add(BuildFaqDivider());
        for (var i = 0; i < FaqItems.Length; i++)
        {
            if (i > 0)
            {
                FaqBody.Children.Add(BuildFaqDivider());
            }
            FaqBody.Children.Add(BuildFaqRow(FaqItems[i]));
        }
    }

    private Microsoft.UI.Xaml.Shapes.Rectangle BuildFaqDivider() =>
        new() { Height = 1, Fill = Brush(Palette.Divider), Margin = new Thickness(0, 4, 0, 4) };

    /// <summary>一行问题：整行可点（问题较长时会换行），右侧一个右箭头提示可展开</summary>
    private Button BuildFaqRow(FaqItem item)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = item.Question,
            FontSize = 15,
            Foreground = Brush(Palette.Ink),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = Brush(Palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var row = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        row.Click += (_, _) => _ = ShowFaqDetailAsync(item);
        return row;
    }

    /// <summary>问答详情弹窗：问题作标题，答案按正文；「关闭」放在标题行右侧（同其它弹窗）</summary>
    private async Task ShowFaqDetailAsync(FaqItem item)
    {
        if (_faqDialogOpen)
        {
            return;
        }

        var question = new TextBlock
        {
            Text = item.Question,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(Palette.Ink),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "关闭",
            FontSize = 13,
            MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(8),
        };
        close.Click += (_, _) => _faqDialog?.Hide();

        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(question);
        Grid.SetColumn(close, 1);
        titleRow.Children.Add(close);

        var content = new StackPanel { Spacing = 12, Width = 460 };
        content.Children.Add(titleRow);
        content.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = Brush(Palette.Divider) });
        content.Children.Add(new TextBlock
        {
            Text = item.Answer,
            FontSize = 15,
            Foreground = Brush(Palette.Dim),
            TextWrapping = TextWrapping.Wrap,
            // 答案多为长段落，默认行距偏挤；26 ≈ 15px 自然行高 + 6，与 mac 的 lineSpacing(6) 对齐
            LineHeight = 26,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
            IsTextSelectionEnabled = true,
        });

        // 不再用 ContentDialog 的 Title / CloseButtonText，系统按钮行会另起一行
        var dialog = new ContentDialog { Content = content };
        _faqDialog = dialog;
        dialog.XamlRoot = Root.XamlRoot;
        _faqDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _faqDialogOpen = false;
            _faqDialog = null;
        }
    }

    private void OnSymbolPickerClick(object sender, RoutedEventArgs e)
    {
        RenderCapsules();
        SymbolOverlay.Visibility = Visibility.Visible;
        // 先按「全部品种」量一遍，顶边据此固定；之后切过滤器只收缩下边距
        RefreshSymbolMaxListHeight();
        SymbolDialogClose.Focus(FocusState.Programmatic);
    }

    private void OnSymbolDialogCloseClick(object sender, RoutedEventArgs e) => HideSymbolDialog();

    private void HideSymbolDialog() => SymbolOverlay.Visibility = Visibility.Collapsed;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
        if (State is null)
        {
            return;
        }
        ApplyTabStyles();
        FillFromAccount();
        FillServerUrl();
        // 第 5 步要在胶囊后显示当前审批状态，进页面时拉一次
        State.RefreshTokenStatus();
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

    // -------- tab --------

    private void OnStartTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Start);

    private void OnRecordsTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Records);

    private void OnTradesTabClick(object sender, RoutedEventArgs e) => SwitchTab(Tab.Trades);

    private async void OnTradesHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowHelpAsync(
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
        // 切到交易记录/历史成交时收起品种弹层，避免遮罩留在记录列表上
        if (tab != Tab.Start)
        {
            HideSymbolDialog();
        }
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
        _recordsView = new AccountRecordsView("sim", "尚未配置模拟账户，请先在「快速开始」里保存账户", AccountRecordsMode.Records);
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
        _tradesView = new AccountRecordsView("sim", "尚未配置模拟账户，请先在「快速开始」里保存账户", AccountRecordsMode.Trades);
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
        Card.Visibility = _tab == Tab.Start ? Visibility.Visible : Visibility.Collapsed;
        FaqCard.Visibility = _tab == Tab.Start ? Visibility.Visible : Visibility.Collapsed;
        RecordsPanel.Visibility = _tab == Tab.Records ? Visibility.Visible : Visibility.Collapsed;
        TradesPanel.Visibility = _tab == Tab.Trades ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPalette()
    {
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyTabStyles();
        Card.Background = Brush(Palette.Card);
        Card.BorderBrush = Brush(Palette.Hairline);

        foreach (var text in new[] { Intro, Step1, Step2, Step3, Step4, Step5, Step6, Step7 })
        {
            text.Foreground = Brush(Palette.Ink);
            text.FontSize = 15;
        }
        // 问号按钮的悬停底与描边随主题
        SymbolHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ServerHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        TokenHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // 第 1 步与第 3 步的入口胶囊：外描边单色，内部文字按钮透明、只有主题蓝文字
        DownloadCapsule.BorderBrush = Brush(Palette.Hairline);
        DownloadButton.Foreground = Brush(Palette.Primary);
        AccountCapsule.BorderBrush = Brush(Palette.Hairline);
        // 兜底给 Button 一个前景色；实际文字色由 UpdateAccountEntryLabel 重建的 TextBlock 决定
        AccountEditorButton.Foreground = Brush(Palette.Primary);
        AccountHelpButton.Foreground = Brush(Palette.Primary);
        AccountHelpButton.BorderBrush = Brush(Palette.Hairline);
        SymbolCapsule.BorderBrush = Brush(Palette.Hairline);
        SymbolPickerButton.Foreground = Brush(Palette.Primary);
        SymbolHelpButton.Foreground = Brush(Palette.Primary);
        SymbolHelpButton.BorderBrush = Brush(Palette.Hairline);
        TokenCapsule.BorderBrush = Brush(Palette.Hairline);
        // 兜底给 Button 一个前景色；实际文字色由 UpdateTokenHint 重建的 TextBlock 决定
        TokenRequestButton.Foreground = Brush(Palette.Primary);
        TokenHelpButton.Foreground = Brush(Palette.Primary);
        TokenHelpButton.BorderBrush = Brush(Palette.Hairline);

        SymbolHint.Foreground = Brush(Palette.Dim);
        StrategyHint.Foreground = Brush(Palette.Dim);
        UpdateAccountHint();

        // 常见问题卡片：外观随主题，列表整体重建（分隔线与文字色都在渲染时取）
        FaqCard.Background = Brush(Palette.Card);
        FaqCard.BorderBrush = Brush(Palette.Hairline);
        FaqTitle.Foreground = Brush(Palette.Ink);
        FaqToggleText.Foreground = Brush(Palette.Primary);
        FaqChevron.Foreground = Brush(Palette.Primary);
        RenderFaq();

        foreach (var label in new[] { PhoneLabel, PasswordLabel })
        {
            label.Foreground = Brush(Palette.Ink);
            label.FontSize = 15;
        }
        PhoneBox.Background = Brush(Palette.InputBg);
        PhoneBox.BorderBrush = Brush(Palette.Hairline);
        PhoneBox.Foreground = Brush(Palette.Ink);
        ServerBox.Background = Brush(Palette.InputBg);
        ServerBox.BorderBrush = Brush(Palette.Hairline);
        PasswordFrame.Background = Brush(Palette.InputBg);
        PasswordFrame.BorderBrush = Brush(Palette.Hairline);
        TestHint.Foreground = Brush(Palette.Dim);
        // 弹窗在独立的 popup 根下，不继承页面根的主题，需要单独指定
        if (_accountDialog is not null)
        {
            _accountDialog.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }
        if (_downloadDialog is not null)
        {
            _downloadDialog.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }
        if (_tokenRecordsDialog is not null)
        {
            _tokenRecordsDialog.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }
        // 下载 / 账号弹窗标题行下的分隔线（控件在构造时建好，这里只刷颜色）
        _downloadDivider.Fill = Brush(Palette.Divider);
        _accountDivider.Fill = Brush(Palette.Divider);

        // 品种弹层画在页面内（不是 ContentDialog），直接按 Palette 上色
        SymbolDialogCard.Background = Brush(Palette.Card);
        SymbolDialogCard.BorderBrush = Brush(Palette.Hairline);
        SymbolDialogClose.Foreground = Brush(Palette.Primary);
        SymbolDialogClose.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SymbolDialogClose.BorderBrush = Brush(Palette.Hairline);
        SymbolDialogDivider.Fill = Brush(Palette.Divider);

        foreach (var help in new[] { SymbolHelpButton, ServerHelpButton, TokenHelpButton, TradesHelpButton })
        {
            help.Foreground = Brush(Palette.Primary);
            help.BorderBrush = Brush(Palette.Hairline);
            help.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        BuildLinks();
        BuildDownloadLinks();
        UpdateEyeButton();
        UpdatePasswordMask();
        // 主题切换后语义色也跟着换：清掉签名强制重建胶囊文字
        _tokenCapsuleKey = "";
        UpdateTokenHint();
        // 第 3 步胶囊的绿/蓝同样随主题走，需要按新配色重建一次
        _accountCapsuleKey = "";
        UpdateAccountEntryLabel();

        // 配色变化后胶囊需要重绘
        _symbolKey = "";
        _strategyKey = "";
        RenderCapsules();

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
        FillFromAccount();
        FillServerUrl();
        RefreshTransientState();
        // 账户配置变化会改变记录 tab 的提示文案（如刚保存了模拟账户）
        if (_recordsView is not null && State is not null)
        {
            _recordsView.Refresh(State, Host.IsDark);
        }
        if (_tradesView is not null && State is not null)
        {
            _tradesView.Refresh(State, Host.IsDark);
        }
    }

    /// <summary>
    /// 只刷新会随网络请求变化的瞬时状态（品种/策略加载、测试连接按钮），
    /// 不动表单字段——请求返回时的状态广播不该把用户正在输入的内容冲掉。
    /// </summary>
    private void RefreshTransientState()
    {
        RenderCapsules();
        SymbolHint.Text = SymbolHintText();
        SymbolHint.Visibility = SymbolHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StrategyHint.Text = StrategyHintText();
        StrategyHint.Visibility = StrategyHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTokenHint();
        // 验证结果存在 AppState 里，测试连接返回后要跟着刷一次胶囊颜色
        UpdateAccountEntryLabel();
        if (_accountDialog is not null)
        {
            _accountDialog.SecondaryButtonText = _testing ? "测试中..." : "测试连接";
            _accountDialog.IsSecondaryButtonEnabled = !_testing;
        }
        ServerTestButton.Content = _serverTesting ? "测试中..." : "测试连接";
        ServerTestButton.IsEnabled = !_serverTesting;
    }

    // -------- 账户（手机号 / 密码，输入完成即保存）--------

    /// <summary>第 3 步自动保存的结果提示：成功绿 / 失败红，空文案不显示</summary>
    private void UpdateAccountHint()
    {
        AccountHint.Visibility = AccountHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountHint.Foreground = Brush(_accountHintError ? Hex(0xE3342F) : Hex(0x34C759));
    }

    private void SetAccountHint(string message, bool isError)
    {
        _accountHintError = isError;
        AccountHint.Text = message;
        UpdateAccountHint();
    }

    /// <summary>
    /// 输入完成即保存（回车或失焦）：手机号/密码任一有变化就提交。
    /// 信息还不完整时静默跳过（不打断输入），失败才在行下方提示。
    /// </summary>
    private void SaveAccountIfChanged()
    {
        var account = PhoneBox.Text.Trim();
        if (PhoneBox.Text != account)
        {
            PhoneBox.Text = account;
        }
        if (account.Length == 0)
        {
            SetAccountHint("", isError: false);
            return;
        }

        // 眼睛按钮取回的已存明文不算用户修改，避免失焦时把同一密码再提交一遍
        var password = CurrentPassword;
        if (password == _revealedPassword)
        {
            password = "";
        }
        var isNew = !State.Accounts.Any(a => a.Kind == "sim");
        // 新账户必须设置密码；已存且已有密码的账户留空沿用旧密码（后端约定）
        if (password.Length == 0 && (isNew || !_hasStoredPassword))
        {
            SetAccountHint("", isError: false);
            return;
        }

        var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
        if (sim is not null && sim.TqAccount == account && password.Length == 0)
        {
            SetAccountHint("", isError: false);
            return;
        }

        var parameters = new JsonObject
        {
            ["kind"] = "sim",
            ["label"] = "模拟账户",
            ["tq_account"] = account,
        };
        if (sim is not null)
        {
            parameters["account_id"] = sim.Id;
        }
        if (password.Length > 0)
        {
            parameters["tq_password"] = password;
        }
        // 账户尚未建立时品种/策略只在本地选中，随首次保存一起提交
        var symbols = AllSymbols;
        if (symbols.Count > 0 && _selectedSymbol.Length > 0)
        {
            parameters["symbols"] = ToJsonArray([_selectedSymbol]);
        }
        if (_selectedStrategy.Length > 0)
        {
            parameters["strategy"] = _selectedStrategy;
        }

        _ = SaveAccountFieldsAsync(parameters, password);
    }

    private async Task SaveAccountFieldsAsync(JsonObject parameters, string newPassword)
    {
        var error = await State.SaveAccountAsync(parameters);
        if (error is not null)
        {
            SetAccountHint(error, isError: true);
            return;
        }
        if (newPassword.Length > 0)
        {
            // 保存成功后清空输入框、改为掩码显示，长度用刚提交的值
            PasswordHidden.Password = "";
            PasswordShown.Text = "";
            _revealedPassword = "";
            _hasStoredPassword = true;
            _storedPasswordLength = newPassword.Length;
            _passwordVisible = false;
            PasswordHidden.Visibility = Visibility.Visible;
            PasswordShown.Visibility = Visibility.Collapsed;
            UpdateEyeButton();
            UpdatePasswordMask();
            // 输入框已清空、改为沿用已存密码：落库的是刚验证过的那份就继续保持绿色
            State.SettleVerifiedTqCredentials(newPassword, PhoneBox.Text.Trim());
        }
        SetAccountHint("已保存", isError: false);
        UpdateAccountEntryLabel();
    }

    // -------- 服务器地址（与设置页共用同一份配置）--------

    /// <summary>用配置回填服务器地址；输入框已被编辑（与上次回填值不同）时不覆盖</summary>
    private void FillServerUrl()
    {
        var current = State.Config.ServerUrl;
        if (ServerBox.Text.Length == 0 || ServerBox.Text == _lastConfigServerUrl)
        {
            ServerBox.Text = current;
        }
        _lastConfigServerUrl = current;
    }

    /// <summary>
    /// 输入完成即保存（回车或失焦）：与当前生效地址相同则不提交，空值视为放弃修改。
    /// </summary>
    private void SaveServerIfChanged()
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0)
        {
            // 空值不落库，回退到仍在生效的地址
            ServerBox.Text = State.Config.ServerUrl;
            _lastConfigServerUrl = ServerBox.Text;
            return;
        }
        if (ServerBox.Text != url)
        {
            ServerBox.Text = url;
        }
        // 与上次回填/提交的值相同则不重复提交
        if (url == _lastConfigServerUrl)
        {
            return;
        }
        // 记为「已回填值」，避免 config_changed 回填把刚保存的地址又覆盖掉
        _lastConfigServerUrl = url;
        // 只提交服务器地址，代理与自动更新沿用设置页里的现有值（后端按未提供字段保留）
        State.SaveConfig(url, null);
    }

    private async void OnTestServerClick(object sender, RoutedEventArgs e)
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0)
        {
            await ShowInfoAsync("输入错误", "服务器地址不能为空");
            return;
        }
        _serverTesting = true;
        RefreshTransientState();
        var message = await State.TestServerAsync(url);
        _serverTesting = false;
        RefreshTransientState();
        await ShowInfoAsync("测试连接", message);
    }

    private async void OnServerHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowInfoAsync(
            "服务器地址",
            "这是 iTrader 智能交易服务器的地址，与顶栏「服务器状态」弹窗是同一份配置，输入完成（回车或点到别处）即时保存生效。");
    }

    // -------- Token（未申请时直接提交；已申请过则查看申请记录）--------

    /// <summary>第 5 步胶囊的三种状态：可直接申请 / 已提交待审核 / 已有生效 Token</summary>
    private enum TokenCapsuleState
    {
        NotApplied,
        Pending,
        Active,
    }

    /// <summary>
    /// 已提交未审核、或已有有效 Token 时，胶囊文字改为「Token 管理」并用语义色，
    /// 点击改为弹出申请记录——此时再点「申请」只会重复提交同一用途的申请。
    /// </summary>
    private TokenCapsuleState CurrentTokenState()
    {
        var status = State.TokenStatus;
        // 后端只在本地确实持有凭据时才报「已通过…已加密保存」；
        // 「已通过，但服务器未返回 token」还没拿到，继续按可申请对待
        if (status.Contains("已通过") && !status.Contains("未返回"))
        {
            return TokenCapsuleState.Active;
        }
        // 旧数据可能没有 token_id，凭据本身仍在，按有效处理
        if (State.ActiveTokenId is not null)
        {
            return TokenCapsuleState.Active;
        }
        // 待审核的两种文案：「待审核」「已提交申请，等待服务器审核」
        if (status.Contains("审核"))
        {
            return TokenCapsuleState.Pending;
        }
        // 刚申请完、后端还未推送记录时，靠记录列表兜底
        if (State.TokenRecords.FirstOrDefault()?.Status == "pending")
        {
            return TokenCapsuleState.Pending;
        }
        return TokenCapsuleState.NotApplied;
    }

    private async void OnTokenHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowInfoAsync(
            "申请 Token",
            "客户端连接服务器需要有效的 Token。点「申请 Token」直接向服务器提交申请，"
            + "等待审批通过后客户端会自动领取并保存；已提交或已通过后，点「Token 管理」查看申请记录。");
    }

    private async void OnTokenRequestClick(object sender, RoutedEventArgs e)
    {
        if (_tokenRequesting)
        {
            return;
        }
        // 已申请过就不再重复提交，改为查看进度
        if (CurrentTokenState() != TokenCapsuleState.NotApplied)
        {
            await ShowTokenRecordsAsync();
            return;
        }
        _tokenRequesting = true;
        _tokenMessage = "";
        RefreshTransientState();
        try
        {
            // 走与后端 Token 申请相同的入口：说明留默认值
            _tokenMessage = await State.RequestTokenResultAsync("iTrader 智能交易系统");
        }
        finally
        {
            _tokenRequesting = false;
            RefreshTransientState();
        }
    }

    /// <summary>
    /// 胶囊后的提示：只显示失败回执与「已拒绝」这类异常状态。
    /// 待审核 / 已通过已由胶囊文字与颜色表达，不再占一行重复说明。
    /// </summary>
    private string TokenHintText()
    {
        if (_tokenMessage.Contains("失败"))
        {
            return _tokenMessage;
        }
        var status = State.TokenStatus;
        if (status.Contains("审核") || status.Contains("已通过"))
        {
            return "";
        }
        if (status.Length == 0 || status == "未申请")
        {
            return "";
        }
        return _tokenMessage.Length > 0 ? _tokenMessage : status;
    }

    private void UpdateTokenHint()
    {
        var text = TokenHintText();
        TokenHint.Text = text;
        TokenHint.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TokenHint.Foreground = Brush(
            text.Contains("失败") || text.Contains("拒绝") || text.Contains("错误")
                ? Hex(0xE3342F)
                : text.Contains("已通过") ? Hex(0x34C759) : Palette.Dim);

        var state = CurrentTokenState();
        var title = _tokenRequesting
            ? "申请中..."
            : state == TokenCapsuleState.NotApplied ? "申请 Token" : "Token 管理";
        // 文字颜色放在 TextBlock 上：Button 模板在悬停/按下时会把 Foreground 换成
        // 默认资源色，直接给 Button 上色会让黄/绿在鼠标移上去时消失
        var capsuleColor = _tokenRequesting
            ? Palette.Primary
            : state switch
            {
                TokenCapsuleState.Pending => Palette.Warning,
                TokenCapsuleState.Active => Palette.Success,
                _ => Palette.Primary,
            };
        // 状态事件很频繁，文案与颜色没变时不重建 Content，避免按钮反复重绘
        var capsuleKey = $"{title}|{capsuleColor}";
        if (capsuleKey != _tokenCapsuleKey)
        {
            _tokenCapsuleKey = capsuleKey;
            TokenRequestButton.Content = new TextBlock
            {
                Text = title,
                FontSize = 13,
                Foreground = Brush(capsuleColor),
            };
        }
        // 申请中虽不重复提交，但也没必要弹记录，直接置灰防误触
        TokenRequestButton.IsEnabled = !_tokenRequesting;
        // 只有弹窗开着时才需要重建记录列表（审批状态变化时同步刷新）
        if (_tokenRecordsDialogOpen)
        {
            RenderTokenRecords();
        }
    }

    /// <summary>Token 申请记录弹窗：只读展示进度，申请入口在步骤 5 的胶囊上。</summary>
    private void BuildTokenRecordsDialog()
    {
        TokenRecordsStatus = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        TokenRecordsEmptyHint = new TextBlock
        {
            Text = "暂无申请记录",
            FontSize = 13,
            Visibility = Visibility.Collapsed,
        };
        TokenRecordsPanel = new StackPanel { Spacing = 0 };
        TokenRecordsDivider = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1 };

        var content = new StackPanel { Spacing = 12, Width = 460 };
        // 标题行自己画在内容顶部：只有这样才能把「刷新状态」「关闭」和标题放进同一行
        content.Children.Add(BuildTokenRecordsTitleRow());
        content.Children.Add(TokenRecordsStatus);
        content.Children.Add(TokenRecordsDivider);
        content.Children.Add(TokenRecordsEmptyHint);
        // 记录多时弹窗内滚动，避免窗口被撑高
        content.Children.Add(new ScrollViewer { Content = TokenRecordsPanel, MaxHeight = 320 });

        // 不再用 ContentDialog 的 Title / CloseButtonText，系统按钮行会另起一行
        _tokenRecordsDialog = new ContentDialog { Content = content };
    }

    /// <summary>
    /// 标题行：标题靠左，「刷新状态」「关闭」靠右（与 mac 壳同布局），
    /// 因此不再使用 ContentDialog 自带的底部按钮行。
    /// </summary>
    private Grid BuildTokenRecordsTitleRow()
    {
        var title = new TextBlock
        {
            Text = "Token 申请记录",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var refresh = new Button
        {
            Content = "刷新状态",
            FontSize = 13,
            MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(8),
        };
        refresh.Click += (_, _) => State.RefreshTokenStatus();
        var close = new Button
        {
            Content = "关闭",
            FontSize = 13,
            MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(8),
        };
        // 按钮不再在底部，关闭只能走这里
        close.Click += (_, _) => _tokenRecordsDialog?.Hide();
        actions.Children.Add(refresh);
        actions.Children.Add(close);

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(title);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private async Task ShowTokenRecordsAsync()
    {
        var dialog = _tokenRecordsDialog;
        if (dialog is null || _tokenRecordsDialogOpen)
        {
            return;
        }
        // 打开时拉一次最新审批状态，记录随状态事件刷新
        State.RefreshTokenStatus();
        RenderTokenRecords();
        dialog.XamlRoot = Root.XamlRoot;
        _tokenRecordsDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _tokenRecordsDialogOpen = false;
        }
    }

    /// <summary>记录行：编号 + 说明/时间 + 状态（与 Token 页同口径，这里只读）</summary>
    private void RenderTokenRecords()
    {
        if (_tokenRecordsDialog is null)
        {
            return;
        }
        TokenRecordsStatus.Text = State.TokenStatus.Length == 0 ? "未申请" : State.TokenStatus;
        TokenRecordsStatus.Foreground = Brush(TokenStatusColor(State.TokenStatus));
        TokenRecordsDivider.Fill = Brush(Palette.Divider);

        var records = State.TokenRecords;
        TokenRecordsEmptyHint.Foreground = Brush(Palette.Dim);
        TokenRecordsEmptyHint.Visibility = records.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        TokenRecordsPanel.Children.Clear();
        for (var i = 0; i < records.Count; i++)
        {
            if (i > 0)
            {
                TokenRecordsPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Height = 1,
                    Fill = Brush(Palette.Divider),
                });
            }
            // 记录按申请时间倒序（最新在前），正序编号才是「历史第几笔」
            TokenRecordsPanel.Children.Add(BuildTokenRecordRow(records.Count - i, records[i]));
        }
    }

    private Grid BuildTokenRecordRow(int index, TokenRecord record)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Text = index.ToString(),
            FontSize = 13,
            Foreground = Brush(Palette.Dim),
            TextAlignment = TextAlignment.Right,
        };
        grid.Children.Add(number);

        var detail = new StackPanel { Spacing = 4 };
        detail.Children.Add(new TextBlock
        {
            Text = record.Description.Length == 0 ? "（无说明）" : record.Description,
            FontSize = 13,
            Foreground = Brush(Palette.Ink),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        detail.Children.Add(new TextBlock
        {
            Text = "申请 " + (record.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—")
                + (record.ExpiresAt is { } expires ? "，过期 " + expires.ToString("yyyy-MM-dd HH:mm") : ""),
            FontSize = 13,
            Foreground = Brush(Palette.Dim),
        });
        Grid.SetColumn(detail, 1);
        grid.Children.Add(detail);

        var status = new TextBlock
        {
            Text = record.StatusText,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(record.Status switch
            {
                "approved" => Palette.Success,
                "rejected" => Palette.Danger,
                _ => Palette.Warning,
            }),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        return grid;
    }

    /// <summary>状态文案语义色（已通过绿 / 失败红 / 其余黄），与 Token 页一致</summary>
    private Windows.UI.Color TokenStatusColor(string status)
    {
        if (status.Length == 0 || status == "未申请") return Palette.Warning;
        if (status.Contains("已通过")) return Palette.Success;
        if (status.Contains("失败") || status.Contains("错误")) return Palette.Danger;
        return Palette.Warning;
    }

    // -------- 引导文案与外链 --------

    private void BuildLinks()
    {
        Intro.Text = "在开始实盘交易之前，您应该通过 1-3 个月的模拟交易建立信心。\n\n我们支持快期的模拟交易，您需要：";
        // 官网链接跟在正文后面，不单独占一行；正文继承 Step2 的主题色与 15px 字号
        SetTextWithLink(Step2, "2，在快期客户端或天勤官网注册：", UrlRegister);
    }

    // -------- 步骤 1 的下载入口弹窗（手机端 / App Store / Windows 三个链接）--------

    private ContentDialog? _downloadDialog;
    /// <summary>同一个 ContentDialog 不能并发 ShowAsync，重复点击直接忽略</summary>
    private bool _downloadDialogOpen;

    /// <summary>
    /// 弹窗内容只在构造时建一次：标题 + 三行链接（配色随主题，链接文字在打开时重建）
    /// </summary>
    private void BuildDownloadDialog()
    {
        // ContentDialog 自身宽度有上限，App Store 那行链接较长，允许换行而不是被裁掉
        LinkMobile = new TextBlock { IsTextSelectionEnabled = true, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        LinkAppStore = new TextBlock { IsTextSelectionEnabled = true, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        LinkWindows = new TextBlock { IsTextSelectionEnabled = true, FontSize = 13, TextWrapping = TextWrapping.Wrap };

        var links = new StackPanel { Spacing = 6 };
        links.Children.Add(LinkMobile);
        links.Children.Add(LinkAppStore);
        links.Children.Add(LinkWindows);

        var content = new StackPanel { Spacing = 12 };
        // 标题行自己画在内容顶部，才能把「关闭」和标题放进同一行（同 Token 申请记录弹窗）
        content.Children.Add(BuildDownloadTitleRow());
        content.Children.Add(_downloadDivider = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1 });
        content.Children.Add(links);

        // 不再用 ContentDialog 的 Title / CloseButtonText，系统按钮行会另起一行
        _downloadDialog = new ContentDialog { Content = content };
    }

    /// <summary>标题行：标题靠左，「关闭」靠右（与 mac 壳同布局）</summary>
    private Grid BuildDownloadTitleRow()
    {
        var title = new TextBlock
        {
            Text = "下载快期客户端",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "关闭",
            FontSize = 13,
            MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // 标题行是自绘的，没有系统底部按钮行，关闭只能走这里
        close.Click += (_, _) => _downloadDialog?.Hide();

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(title);
        Grid.SetColumn(close, 1);
        row.Children.Add(close);
        return row;
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var dialog = _downloadDialog;
        if (dialog is null || _downloadDialogOpen)
        {
            return;
        }
        // XamlRoot 在构造时还拿不到（页面尚未挂到窗口），打开时再补
        dialog.XamlRoot = Root.XamlRoot;
        _downloadDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _downloadDialogOpen = false;
        }
    }

    /// <summary>下载弹窗里的三行链接：构建时与主题切换时各重建一次</summary>
    private void BuildDownloadLinks()
    {
        if (_downloadDialog is null)
        {
            return;
        }
        SetLink(LinkMobile, "手机端：", UrlMobile);
        // App Store 是手机端 iOS 渠道的子项，保留 Qt 版的 4 空格缩进
        SetLink(LinkAppStore, "    App Store：", UrlAppStore);
        SetLink(LinkWindows, "Windows：", UrlWindows);
    }

    /// <summary>
    /// 正文 + 紧跟的外链放在同一个 TextBlock 里：正文颜色跟随 TextBlock（主题切换时统一刷），
    /// 链接本身用主题蓝并带下划线。
    /// </summary>
    private void SetTextWithLink(TextBlock block, string text, string url)
    {
        block.Inlines.Clear();
        block.Inlines.Add(new Run { Text = text });
        block.Inlines.Add(CreateHyperlink(url));
    }

    private Hyperlink CreateHyperlink(string url)
    {
        var link = new Hyperlink { Foreground = new SolidColorBrush(ThemeTokens.Link) };
        link.Inlines.Add(new Run { Text = url });
        link.Click += async (_, _) =>
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                await ShowInfoAsync("打开链接失败", ex.Message);
            }
        };
        return link;
    }

    /// <summary>链接行：13px 灰前缀 + 主题蓝下划线链接（点击走系统浏览器）</summary>
    private void SetLink(TextBlock block, string prefix, string url)
    {
        block.Inlines.Clear();
        block.FontSize = 13;
        block.Inlines.Add(new Run { Text = prefix, Foreground = Brush(Palette.Dim) });
        block.Inlines.Add(CreateHyperlink(url));
    }

    // -------- 密码区 --------

    private string CurrentPassword =>
        _passwordVisible ? PasswordShown.Text : PasswordHidden.Password;

    private async void OnEyeClick(object sender, RoutedEventArgs e)
    {
        var nextVisible = !_passwordVisible;
        // 首次查看已存密码：向后端受控取一次明文再切到明文模式
        if (nextVisible && CurrentPassword.Length == 0 && _hasStoredPassword)
        {
            await RevealStoredPasswordAsync();
            return;
        }
        SetPasswordVisible(nextVisible);
    }

    private async Task RevealStoredPasswordAsync()
    {
        var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
        var saved = await State.GetAccountPasswordAsync(sim?.Id, "sim", "tq_password");
        // 先把明文落到两个输入控件，再切到明文模式：SetPasswordVisible 会用
        // 隐藏框的值覆盖明文框，只写明文框会被清掉
        if (saved.Length > 0)
        {
            PasswordHidden.Password = saved;
            PasswordShown.Text = saved;
            // 记为「已取回的明文」：失焦保存时据此判定用户是否真的改了密码
            _revealedPassword = saved;
        }
        // 取回失败时密码仍为空，掩码保留，不让人误以为密码被清空
        SetPasswordVisible(true);
    }

    private void SetPasswordVisible(bool visible)
    {
        _passwordVisible = visible;
        // 两种输入控件各自持有内容，切换时把当前值带过去
        if (visible)
        {
            PasswordShown.Text = PasswordHidden.Password;
            PasswordShown.Visibility = Visibility.Visible;
            PasswordHidden.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordHidden.Password = PasswordShown.Text;
            PasswordHidden.Visibility = Visibility.Visible;
            PasswordShown.Visibility = Visibility.Collapsed;
        }
        UpdateEyeButton();
        UpdatePasswordMask();
    }

    private void UpdateEyeButton()
    {
        EyeButton.Content = new FontIcon
        {
            Glyph = _passwordVisible ? "\uED1A" : "\uE890",  // Hide / View
            FontSize = 16,
            Foreground = new SolidColorBrush(ThemeTokens.Link),
        };
        ToolTipService.SetToolTip(EyeButton, _passwordVisible ? "隐藏密码" : "显示密码");
    }

    /// <summary>
    /// 掩码显隐：已存密码且当前输入为空时画圆点（直径 6.5、中心间距 9），
    /// 尺寸对齐设计稿；有输入内容时让位给真实输入框。
    /// </summary>
    private void UpdatePasswordMask()
    {
        var showMask = _hasStoredPassword && CurrentPassword.Length == 0;
        PasswordMask.Visibility = showMask ? Visibility.Visible : Visibility.Collapsed;
        if (!showMask)
        {
            return;
        }

        var count = _storedPasswordLength > 0 ? _storedPasswordLength : 10;
        if (PasswordMask.Children.Count != count)
        {
            PasswordMask.Children.Clear();
            for (var i = 0; i < count; i++)
            {
                PasswordMask.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 6.5,
                    Height = 6.5,
                    Fill = Brush(Palette.Ink),
                });
            }
        }
        else
        {
            foreach (var child in PasswordMask.Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>())
            {
                child.Fill = Brush(Palette.Ink);
            }
        }
    }

    // -------- 品种 / 策略胶囊 --------

    private void RenderCapsules()
    {
        var symbols = AllSymbols;
        // 入口胶囊：默认「选择交易品种」，选中后优先显示中文品种名（无映射时回退代码）
        SymbolPickerButton.Content = SymbolEntryLabel();
        UpdateSymbolFilterStyles();
        var visible = symbols.Where(s => _symbolFilter.Matches(s)).ToList();
        var symbolKey = string.Join(",", visible) + $";{_selectedSymbol};{Host.IsDark}";
        if (symbolKey != _symbolKey)
        {
            _symbolKey = symbolKey;
            SymbolPanel.Children.Clear();
            foreach (var symbol in visible)
            {
                SymbolPanel.Children.Add(BuildCapsule(
                    State.DisplaySymbol(symbol),
                    isOn: _selectedSymbol == symbol,
                    tooltip: null,
                    onToggle: on => OnSymbolToggled(symbol, on)));
            }
            // 过滤后一个都不剩时给出行内提示（加载中 / 失败的状态行由 SymbolHint 负责）
            if (visible.Count == 0 && symbols.Count > 0)
            {
                SymbolPanel.Children.Add(new TextBlock
                {
                    Text = "该分类下暂无可选品种",
                    FontSize = 13,
                    Foreground = Brush(Palette.Dim),
                });
            }
        }

        var strategies = State.ServerStrategies;
        var strategyKey = string.Join(",", strategies.Select(s => $"{s.Id}:{s.Name}"))
            + $";{_selectedStrategy};{Host.IsDark}";
        if (strategyKey != _strategyKey)
        {
            _strategyKey = strategyKey;
            StrategyPanel.Children.Clear();
            foreach (var strategy in strategies)
            {
                StrategyPanel.Children.Add(BuildCapsule(
                    strategy.Name,
                    isOn: _selectedStrategy == strategy.Id,
                    tooltip: strategy.Description.Length == 0 ? null : strategy.Description,
                    onToggle: on => OnStrategyToggled(strategy.Id, on)));
            }
        }
    }

    /// <summary>入口胶囊文案：未选中显示引导语，选中后与弹窗胶囊一致显示「代码｜中文」</summary>
    private string SymbolEntryLabel()
    {
        return _selectedSymbol.Length == 0
            ? "选择交易品种"
            : $"已选择交易品种：{State.DisplaySymbol(_selectedSymbol)}";
    }

    private Button BuildCapsule(string label, bool isOn, string? tooltip, Action<bool> onToggle)
    {
        var button = new Button
        {
            Content = label,
            Style = WrapPanel.CapsuleStyle(),
            Background = new SolidColorBrush(isOn ? Palette.Primary : Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(isOn ? Palette.Primary : Palette.Hairline),
            Foreground = new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Palette.Ink),
        };
        if (tooltip is not null)
        {
            ToolTipService.SetToolTip(button, tooltip);
        }
        // 再点已选中的胶囊取消选择（与 mac 壳一致）
        button.Click += (_, _) =>
        {
            var next = !isOn;
            onToggle(next);
        };
        return button;
    }

    /// <summary>已有账户时选中/取消即生效；账户尚未建立时只记在本地，等「保存」一起提交</summary>
    private void OnSymbolToggled(string symbol, bool isOn)
    {
        _selectedSymbol = isOn ? symbol : "";
        RenderCapsules();
        // 选中即生效：选完直接关掉弹层（取消选中留给再次打开后取消）
        if (isOn)
        {
            HideSymbolDialog();
        }
        var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
        if (sim is null)
        {
            return;
        }
        _ = SaveFieldsAsync(new JsonObject
        {
            ["kind"] = "sim",
            ["account_id"] = sim.Id,
            ["symbols"] = ToJsonArray(_selectedSymbol.Length == 0 ? [] : [_selectedSymbol]),
        });
    }

    private void OnStrategyToggled(string strategyId, bool isOn)
    {
        _selectedStrategy = isOn ? strategyId : "";
        RenderCapsules();
        var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
        if (sim is null)
        {
            return;
        }
        _ = SaveFieldsAsync(new JsonObject
        {
            ["kind"] = "sim",
            ["account_id"] = sim.Id,
            ["strategy"] = _selectedStrategy,
        });
    }

    private async Task SaveFieldsAsync(JsonObject parameters)
    {
        // 失败原因走对话框；品种弹窗在选中后会先关闭，不会与之叠加
        var error = await State.SaveAccountAsync(parameters);
        if (error is not null)
        {
            await ShowInfoAsync("输入错误", error);
        }
    }

    /// <summary>模拟页可选品种：服务器品种在前；已订阅但不在服务器列表的品种保留显示</summary>
    private List<string> AllSymbols
    {
        get
        {
            var result = State.ServerSymbols.ToList();
            foreach (var symbol in _keptSymbols.Where(s => !result.Contains(s)))
            {
                result.Add(symbol);
            }
            return result;
        }
    }

    /// <summary>品种区状态行：只保留获取中 / 失败 / 尚未获取等瞬时状态，数量提示在问号弹窗内</summary>
    private string SymbolHintText()
    {
        if (State.SymbolsFetching) return "正在获取品种...";
        if (State.SymbolsMessage.StartsWith("获取失败")) return State.SymbolsMessage;
        if (State.ServerSymbols.Count > 0) return "";
        return "尚未获取品种";
    }

    private string StrategyHintText()
    {
        if (State.StrategiesFetching) return "正在获取策略...";
        if (State.StrategiesMessage.StartsWith("获取失败")) return State.StrategiesMessage;
        if (State.ServerStrategies.Count > 0) return "";
        return "尚未获取策略";
    }

    private async void OnSymbolHelpClick(object sender, RoutedEventArgs e)
    {
        var note = "提示：模拟帐户不支持组合/套利和期权交易，仅国内商品和股指国债。";
        if (State.ServerSymbols.Count > 0)
        {
            note += $"\n共 {State.ServerSymbols.Count} 个品种，选择一个订阅，选中即生效";
        }
        await ShowInfoAsync("选择交易品种", note);
    }

    // -------- 数据回填与动作 --------

    /// <summary>
    /// 用已存模拟账户回填（后端不回传密码，密码留空 = 沿用旧值）；
    /// 手机号仅在为空时填充，避免覆盖用户正在编辑的内容。
    /// </summary>
    private void FillFromAccount()
    {
        if (_filling)
        {
            return;
        }
        _filling = true;
        try
        {
            var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
            if (sim is null)
            {
                _hasStoredPassword = false;
                _storedPasswordLength = 0;
                UpdatePasswordMask();
                UpdateAccountEntryLabel();
                return;
            }

            _hasStoredPassword = sim.HasTqPassword;
            _storedPasswordLength = sim.TqPasswordLength;
            UpdatePasswordMask();

            if (PhoneBox.Text.Length == 0 || PhoneBox.Text == _lastAccountPhone)
            {
                PhoneBox.Text = sim.TqAccount;
            }
            _lastAccountPhone = sim.TqAccount;
            UpdateAccountEntryLabel();
            // 只在本地还没记下选择时回填，避免状态刷新把用户刚选的品种/策略冲掉
            if (sim.Symbols.Count > 0 && _selectedSymbol.Length == 0)
            {
                _selectedSymbol = sim.Symbols[0];
                if (!State.ServerSymbols.Contains(_selectedSymbol) && _keptSymbols.Count == 0)
                {
                    _keptSymbols.Add(_selectedSymbol);
                }
            }
            if (sim.Strategy.Length > 0 && _selectedStrategy.Length == 0)
            {
                _selectedStrategy = sim.Strategy;
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private async Task TestAccountAsync()
    {
        var account = PhoneBox.Text.Trim();
        if (account.Length == 0)
        {
            SetTestHint("手机号不能为空", isError: true);
            return;
        }
        if (CurrentPassword.Length == 0 && !_hasStoredPassword)
        {
            SetTestHint("密码不能为空", isError: true);
            return;
        }

        _testing = true;
        SetTestHint("", isError: false);
        RefreshTransientState();
        try
        {
            var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
            // 密码留空时由后端沿用已存密码；验证通过与否由 AppState 记下，
            // 胶囊文字跟着转绿（失败则清掉验证态）
            var message = await State.TestTqAuthAsync(account, CurrentPassword, sim?.Id);
            // 账号弹窗开着时不能再叠一个 ContentDialog，结果直接显示在弹窗里
            SetTestHint(message, isError: message.StartsWith("连接失败"));
            UpdateAccountEntryLabel();
        }
        finally
        {
            _testing = false;
            RefreshTransientState();
        }
    }

    private void SetTestHint(string message, bool isError)
    {
        TestHint.Text = message;
        TestHint.Foreground = Brush(isError ? Hex(0xE3342F) : Hex(0x34C759));
        TestHint.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }
}

/// <summary>品种品类：模拟账户只支持国内商品与中金所股指 / 国债。</summary>
public enum SymbolCategory
{
    Commodity,
    Index,
    Bond,
}

/// <summary>
/// 品类判定：服务器只给品种代码与中文名、没有分类字段，因此按代码判断——
/// 中金所里 IF/IH/IC/IM 是股指，T/TF/TS/TL 是国债，其余都归国内商品。
/// </summary>
public static class SymbolCategories
{
    private static readonly HashSet<string> IndexCodes = ["IF", "IH", "IC", "IM"];
    private static readonly HashSet<string> BondCodes = ["T", "TF", "TS", "TL"];

    /// <summary>完整合约（SHFE.au2510）取品种码；裸品种码（RB）直接查表。空串按国内商品处理</summary>
    public static SymbolCategory Of(string symbol)
    {
        var code = symbol.Length == 0 ? "" : symbol[(symbol.LastIndexOf('.') + 1)..];
        var product = new string(code.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (IndexCodes.Contains(product))
        {
            return SymbolCategory.Index;
        }
        return BondCodes.Contains(product) ? SymbolCategory.Bond : SymbolCategory.Commodity;
    }
}

/// <summary>弹窗顶部的过滤器：「全部品种」+ 三个品类，单选。</summary>
public enum SymbolFilter
{
    All,
    Commodity,
    Index,
    Bond,
}

public static class SymbolFilters
{
    public static readonly SymbolFilter[] All =
        [SymbolFilter.All, SymbolFilter.Commodity, SymbolFilter.Index, SymbolFilter.Bond];

    public static string Title(this SymbolFilter filter) => filter switch
    {
        SymbolFilter.Commodity => "国内商品",
        SymbolFilter.Index => "股指",
        SymbolFilter.Bond => "国债",
        _ => "全部品种",
    };

    public static bool Matches(this SymbolFilter filter, string symbol) => filter switch
    {
        SymbolFilter.Commodity => SymbolCategories.Of(symbol) == SymbolCategory.Commodity,
        SymbolFilter.Index => SymbolCategories.Of(symbol) == SymbolCategory.Index,
        SymbolFilter.Bond => SymbolCategories.Of(symbol) == SymbolCategory.Bond,
        _ => true,
    };

    // 扩展方法定义在 enum（值类型）上，C# 不允许把它当方法组传给 Where/Count（CS1113），
    // 必须包一层 lambda
    public static int Count(SymbolFilter filter, IEnumerable<string> symbols) =>
        symbols.Count(s => filter.Matches(s));
}
