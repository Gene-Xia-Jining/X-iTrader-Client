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
    /// <summary>上一次从配置回填的服务器地址：输入框未被编辑时才跟随配置刷新</summary>
    private string _lastConfigServerUrl = "";
    private bool? _serverTestSuccess;
    private bool _filling;

    /// <summary>品种/策略胶囊的重建签名：数据或选中态变化才重建，避免频繁刷界面</summary>
    private string _symbolKey = "";
    private string _strategyKey = "";

    public SimulationPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
        PasswordHidden.PasswordChanged += (_, _) => UpdatePasswordMask();
        PasswordShown.TextChanged += (_, _) => UpdatePasswordMask();
        PasswordHidden.GotFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Primary);
        PasswordHidden.LostFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Hairline);
        PasswordShown.GotFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Primary);
        PasswordShown.LostFocus += (_, _) => PasswordFrame.BorderBrush = Brush(Palette.Hairline);
        // 手机号 / 密码 / 服务器地址无需点保存：回车或失焦即视为输入完成
        PhoneBox.LostFocus += (_, _) => SaveAccountIfChanged();
        PhoneBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveAccountIfChanged();
            }
        };
        PasswordHidden.LostFocus += (_, _) => SaveAccountIfChanged();
        PasswordShown.LostFocus += (_, _) => SaveAccountIfChanged();
        PasswordHidden.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveAccountIfChanged();
            }
        };
        PasswordShown.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveAccountIfChanged();
            }
        };
        ServerBox.LostFocus += (_, _) => SaveServerIfChanged();
        ServerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SaveServerIfChanged();
            }
        };
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
    }

    private void ApplyPalette()
    {
        // 先切主题再上色：RequestedTheme 会重算模板资源，后设颜色才不会被覆盖
        Root.RequestedTheme = Host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        Card.Background = Brush(Palette.Card);
        Card.BorderBrush = Brush(Palette.Hairline);

        foreach (var text in new[] { Intro, Step1, Step2, Step3, Step4, Step5, Step6, Step7, PhoneLabel, PasswordLabel })
        {
            text.Foreground = Brush(Palette.Ink);
            text.FontSize = 15;
        }
        // 链接行的前缀是 13px 灰字（与 mac 壳 linkRow 一致），不随正文字号走
        foreach (var link in new[] { LinkMobile, LinkAppStore, LinkWindows, LinkRegister })
        {
            link.FontSize = 13;
        }
        // 问号按钮的悬停底与描边随主题
        SymbolHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        StrategyHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ServerHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        TokenHelpButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        foreach (var hint in new[] { SymbolHint, StrategyHint })
        {
            hint.Foreground = Brush(Palette.Dim);
        }
        UpdateAccountHint();

        PhoneBox.Background = Brush(Palette.InputBg);
        PhoneBox.BorderBrush = Brush(Palette.Hairline);
        ServerBox.Background = Brush(Palette.InputBg);
        ServerBox.BorderBrush = Brush(Palette.Hairline);
        PasswordFrame.Background = Brush(Palette.InputBg);
        PasswordFrame.BorderBrush = Brush(Palette.Hairline);

        foreach (var help in new[] { SymbolHelpButton, StrategyHelpButton, ServerHelpButton, TokenHelpButton })
        {
            help.Foreground = Brush(Palette.Primary);
            help.BorderBrush = Brush(Palette.Hairline);
            help.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        BuildLinks();
        UpdateEyeButton();
        UpdatePasswordMask();
        UpdateServerTestResult();

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
        TestButton.Content = _testing ? "测试中..." : "测试连接";
        TestButton.IsEnabled = !_testing;
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

    private void UpdateServerTestResult()
    {
        ServerTestResult.Visibility =
            ServerTestResult.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 与设置页 testServer 同款状态色（固定值，不随主题）
        ServerTestResult.Foreground = Brush(_serverTestSuccess switch
        {
            true => Hex(0x34C759),
            false => Hex(0xE3342F),
            null => Hex(0xE8A33D),
        });
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
            SetServerTestResult("请先输入服务器地址", success: false);
            return;
        }
        _serverTesting = true;
        RefreshTransientState();
        SetServerTestResult("正在测试连接...", success: null);
        var message = await State.TestServerAsync(url);
        _serverTesting = false;
        RefreshTransientState();
        SetServerTestResult(message, message.Contains("成功"));
    }

    private void SetServerTestResult(string message, bool? success)
    {
        _serverTestSuccess = success;
        ServerTestResult.Text = message;
        UpdateServerTestResult();
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
            "客户端连接服务器需要有效的 Token。请在「Token 管理」页提交申请，等待服务器审批通过后即可使用。");
    }

    private void OnTokenPageClick(object sender, RoutedEventArgs e) => Host.NavigateToTag("token");

    // -------- 引导文案与外链 --------

    private void BuildLinks()
    {
        Intro.Text = "在开始实盘交易之前，您应该通过 1-3 个月的模拟交易建立信心。\n\n我们支持快期的模拟交易，您需要：";
        SetLink(LinkMobile, "手机端：", UrlMobile);
        // App Store 是手机端 iOS 渠道的子项，保留 Qt 版的 4 空格缩进
        SetLink(LinkAppStore, "    App Store：", UrlAppStore);
        SetLink(LinkWindows, "Windows：", UrlWindows);
        SetLink(LinkRegister, "天勤官网：", UrlRegister);
    }

    /// <summary>链接行：13px 灰前缀 + 主题蓝下划线链接（点击走系统浏览器）</summary>
    private void SetLink(TextBlock block, string prefix, string url)
    {
        block.Inlines.Clear();
        block.FontSize = 13;
        block.Inlines.Add(new Run { Text = prefix, Foreground = Brush(Palette.Dim) });
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
        block.Inlines.Add(link);
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

    private async void OnStrategyHelpClick(object sender, RoutedEventArgs e)
    {
        var note = "提示：策略由服务器计算交易信号，客户端按信号执行开仓 / 平仓。";
        if (State.ServerStrategies.Count > 0)
        {
            note += $"\n共 {State.ServerStrategies.Count} 个策略，选择一个，选中即生效";
        }
        await ShowInfoAsync("选择交易策略", note);
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

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var account = PhoneBox.Text.Trim();
        if (account.Length == 0)
        {
            await ShowInfoAsync("输入错误", "手机号不能为空");
            return;
        }
        if (CurrentPassword.Length == 0 && !_hasStoredPassword)
        {
            await ShowInfoAsync("输入错误", "密码不能为空");
            return;
        }

        _testing = true;
        RefreshTransientState();
        var sim = State.Accounts.FirstOrDefault(a => a.Kind == "sim");
        // 密码留空时由后端沿用已存密码
        var message = await State.TestTqAuthAsync(account, CurrentPassword, sim?.Id);
        _testing = false;
        RefreshTransientState();
        await ShowInfoAsync("测试连接", message);
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
