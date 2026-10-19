using XTrader.Shell.Theme;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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
    private bool _accountHintError;
    private bool _serverTesting;
    /// <summary>第 5 步 Token 申请进行中（按钮置灰防重复点击）</summary>
    private bool _tokenRequesting;
    /// <summary>本次申请的回执；为空时提示行改为跟随后端实时状态</summary>
    private string _tokenMessage = "";
    /// <summary>上一次从配置回填的服务器地址：输入框未被编辑时才跟随配置刷新</summary>
    private string _lastConfigServerUrl = "";
    private bool _filling;

    /// <summary>品种/策略胶囊的重建签名：数据或选中态变化才重建，避免频繁刷界面</summary>
    private string _symbolKey = "";
    private string _strategyKey = "";

    // -------- 第 1 步的下载链接弹窗（控件在代码里构建，对应 mac 壳的 downloadLinks）--------

    private TextBlock LinkMobile = null!;
    private TextBlock LinkAppStore = null!;
    private TextBlock LinkWindows = null!;

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

    // -------- 第 6 步的品种选择弹窗（控件在代码里构建，对应 mac 壳的 symbolPicker）--------

    private ContentDialog? _symbolDialog;
    /// <summary>同一个 ContentDialog 不能并发 ShowAsync，重复点击直接忽略</summary>
    private bool _symbolDialogOpen;
    private WrapPanel SymbolPanel = null!;
    private TextBlock SymbolHint = null!;

    public SimulationPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
        BuildDownloadDialog();
        BuildAccountEditor();
        BuildSymbolDialog();
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
        // 说明文案在胶囊右侧问号里，弹窗内只放字段
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
    /// </summary>
    private void UpdateAccountEntryLabel()
    {
        var account = PhoneBox.Text.Trim();
        var hasPassword = CurrentPassword.Length > 0 || _hasStoredPassword;
        AccountEditorButton.Content = account.Length > 0 && hasPassword
            ? $"当前用户：{account}"
            : "填入快期/天勤用户信息";
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
    /// 品种弹窗的控件只在构造时建一次：页面本身不再常驻品种列表，
    /// 关闭弹窗只是隐藏，再次打开沿用同一批控件。
    /// 选中即生效并自动关闭，因此不设按钮（Esc 仍可关闭）。
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

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(SymbolPanel);
        panel.Children.Add(SymbolHint);

        var content = new StackPanel { Spacing = 12, Width = 580 };
        // 尽量一屏放下全部品种：只有在窗口较矮时才滚动
        content.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 560 });

        _symbolDialog = new ContentDialog
        {
            Title = "选择交易品种",
            Content = content,
        };
    }

    private async void OnSymbolPickerClick(object sender, RoutedEventArgs e)
    {
        var dialog = _symbolDialog;
        if (dialog is null || _symbolDialogOpen)
        {
            return;
        }
        RenderCapsules();
        // XamlRoot 在构造时还拿不到（页面尚未挂到窗口），打开时再补
        dialog.XamlRoot = Root.XamlRoot;
        _symbolDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _symbolDialogOpen = false;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
        if (State is null)
        {
            return;
        }
        FillFromAccount();
        FillServerUrl();
        // 第 5 步要在胶囊后显示当前审批状态，进页面时拉一次
        State.RefreshTokenStatus();
    }

    private void ApplyPalette()
    {
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
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
        AccountEditorButton.Foreground = Brush(Palette.Primary);
        AccountHelpButton.Foreground = Brush(Palette.Primary);
        AccountHelpButton.BorderBrush = Brush(Palette.Hairline);
        SymbolCapsule.BorderBrush = Brush(Palette.Hairline);
        SymbolPickerButton.Foreground = Brush(Palette.Primary);
        SymbolHelpButton.Foreground = Brush(Palette.Primary);
        SymbolHelpButton.BorderBrush = Brush(Palette.Hairline);
        TokenCapsule.BorderBrush = Brush(Palette.Hairline);
        TokenRequestButton.Foreground = Brush(Palette.Primary);
        TokenHelpButton.Foreground = Brush(Palette.Primary);
        TokenHelpButton.BorderBrush = Brush(Palette.Hairline);

        SymbolHint.Foreground = Brush(Palette.Dim);
        StrategyHint.Foreground = Brush(Palette.Dim);
        UpdateAccountHint();

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
        if (_symbolDialog is not null)
        {
            _symbolDialog.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        }

        foreach (var help in new[] { SymbolHelpButton, ServerHelpButton, TokenHelpButton })
        {
            help.Foreground = Brush(Palette.Primary);
            help.BorderBrush = Brush(Palette.Hairline);
            help.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        BuildLinks();
        BuildDownloadLinks();
        UpdateEyeButton();
        UpdatePasswordMask();
        UpdateTokenHint();

        // 配色变化后胶囊需要重绘
        _symbolKey = "";
        _strategyKey = "";
        RenderCapsules();
    }

    private void OnStateChanged()
    {
        FillFromAccount();
        FillServerUrl();
        RefreshTransientState();
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

        _ = SaveAccountFieldsAsync(parameters, password.Length);
    }

    private async Task SaveAccountFieldsAsync(JsonObject parameters, int newPasswordLength)
    {
        var error = await State.SaveAccountAsync(parameters);
        if (error is not null)
        {
            SetAccountHint(error, isError: true);
            return;
        }
        if (newPasswordLength > 0)
        {
            // 保存成功后清空输入框、改为掩码显示，长度用刚提交的值
            PasswordHidden.Password = "";
            PasswordShown.Text = "";
            _revealedPassword = "";
            _hasStoredPassword = true;
            _storedPasswordLength = newPasswordLength;
            _passwordVisible = false;
            PasswordHidden.Visibility = Visibility.Visible;
            PasswordShown.Visibility = Visibility.Collapsed;
            UpdateEyeButton();
            UpdatePasswordMask();
        }
        SetAccountHint("已保存", isError: false);
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
            "这是 iTrader 智能交易服务器的地址，与「设置」页是同一份配置，输入完成（回车或点到别处）即时保存生效。");
    }

    // -------- Token（引导用户去 Token 管理页申请）--------

    private async void OnTokenHelpClick(object sender, RoutedEventArgs e)
    {
        await ShowInfoAsync(
            "申请 Token",
            "客户端连接服务器需要有效的 Token。点「申请 Token」直接向服务器提交申请，"
            + "等待审批通过后客户端会自动领取并保存；审批进度可在「Token 管理」页查看。");
    }

    private async void OnTokenRequestClick(object sender, RoutedEventArgs e)
    {
        if (_tokenRequesting)
        {
            return;
        }
        _tokenRequesting = true;
        _tokenMessage = "";
        RefreshTransientState();
        try
        {
            // 与「Token 管理」页同一个入口：说明留默认值
            _tokenMessage = await State.RequestTokenResultAsync("iTrader 智能交易系统");
        }
        finally
        {
            _tokenRequesting = false;
            RefreshTransientState();
        }
    }

    /// <summary>
    /// 胶囊后的提示：本次申请回执优先，其次跟随后端实时状态；
    /// 「未申请 / 已通过」不显示，避免一行状态常驻。
    /// </summary>
    private string TokenHintText()
    {
        // 失败的申请后端不会更新状态，回执必须自己显示，不能被下面的过滤吞掉
        if (_tokenMessage.Contains("失败"))
        {
            return _tokenMessage;
        }
        var status = State.TokenStatus;
        if (status.Length == 0 || status == "未申请" || status.Contains("已通过"))
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
        TokenRequestButton.Content = _tokenRequesting ? "申请中..." : "申请 Token";
        TokenRequestButton.IsEnabled = !_tokenRequesting;
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

        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(LinkMobile);
        content.Children.Add(LinkAppStore);
        content.Children.Add(LinkWindows);

        _downloadDialog = new ContentDialog
        {
            Title = "下载快期客户端",
            Content = content,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
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
        var symbolKey = string.Join(",", symbols) + $";{_selectedSymbol};{Host.IsDark}";
        if (symbolKey != _symbolKey)
        {
            _symbolKey = symbolKey;
            SymbolPanel.Children.Clear();
            foreach (var symbol in symbols)
            {
                SymbolPanel.Children.Add(BuildCapsule(
                    State.DisplaySymbol(symbol),
                    isOn: _selectedSymbol == symbol,
                    tooltip: null,
                    onToggle: on => OnSymbolToggled(symbol, on)));
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
        // 选中即生效：选完直接关掉弹窗（没有关闭按钮）
        if (isOn && _symbolDialogOpen && _symbolDialog is not null)
        {
            _symbolDialog.Hide();
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
            // 密码留空时由后端沿用已存密码
            var message = await State.TestTqAuthAsync(account, CurrentPassword, sim?.Id);
            // 账号弹窗开着时不能再叠一个 ContentDialog，结果直接显示在弹窗里
            SetTestHint(message, isError: message.StartsWith("连接失败"));
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
