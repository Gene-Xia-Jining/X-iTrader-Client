using XTrader.Shell.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 设置页：服务器连接 + 代理（对应 mac 壳的 SettingsView / Qt SettingsPage）。
/// 软件更新已合并到顶栏版本胶囊（点击即检查更新）。
/// </summary>
public sealed partial class SettingsPage : Page
{

    // -------- 页面公共逻辑（状态 / 宿主窗口 / 主题画刷）--------

    private readonly ThemedPage _theme;

    private AppState State => _theme.State;
    private MainWindow Host => _theme.Host;
    private Palette Palette => _theme.Palette;

    private SolidColorBrush Brush(Windows.UI.Color color) => _theme.Brush(color);

    private static Windows.UI.Color LogLevelColor(string level, bool dark) =>
        ThemedPage.LogLevelColor(level, dark);
    private bool _filling;

    // 表单当前展示值对应的已落库值：既用于「未改动才回填」，也用于避免重复提交
    private string _savedServerUrl = "";
    private string _savedProxy = "";

    public SettingsPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);

        // 设置页没有保存按钮：各字段输入完成（回车或失焦）/ 切换选项即落库
        ServerBox.LostFocus += (_, _) => SaveServerIfChanged();
        ServerBox.KeyDown += (_, e) => OnCommitKey(e, SaveServerIfChanged);
        ProxyBox.LostFocus += (_, _) => SaveProxyIfChanged();
        ProxyBox.KeyDown += (_, e) => OnCommitKey(e, SaveProxyIfChanged);
        ProxyDirect.Checked += (_, _) => OnProxyModeChecked(ProxyMode.Direct);
        ProxySystem.Checked += (_, _) => OnProxyModeChecked(ProxyMode.System);
        ProxyCustom.Checked += (_, _) => OnProxyModeChecked(ProxyMode.Custom);
    }

    private enum ProxyMode { Direct, System, Custom }

    private static void OnCommitKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e, Action commit)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            commit();
        }
    }

    private void ApplyPalette()
    {
        Card.Background = Brush(Palette.Card);
        Card.BorderBrush = Brush(Palette.Hairline);

        foreach (var label in new[] { ServerLabel, ProxyLabel })
        {
            label.Foreground = Brush(Palette.Ink);
        }
        foreach (var hint in new[] { ServerHint, ProxyHint })
        {
            hint.Foreground = Brush(Palette.Dim);
            hint.FontSize = 13;
        }
        foreach (var divider in new[] { Divider1 })
        {
            divider.Fill = Brush(Palette.Divider);
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
        FillFromConfig();
    }

    private void OnStateChanged()
    {
        if (!_filling)
        {
            // 配置事件（config_changed）引起的外部变更回填表单
            FillFromConfig();
        }
    }

    // -------- 表单回填（对应 Qt set_config）--------

    /// <summary>
    /// 用当前配置回填表单：正在编辑（与已落库值不同）的字段不回填，
    /// 免得 config_changed 把输入到一半的内容覆盖掉。
    /// </summary>
    private void FillFromConfig()
    {
        _filling = true;
        var config = State.Config;

        var trimmedServer = ServerBox.Text.Trim();
        if (trimmedServer.Length == 0 || trimmedServer == _savedServerUrl)
        {
            ServerBox.Text = config.ServerUrl;
        }
        _savedServerUrl = config.ServerUrl;

        var proxy = config.ProxyUrl.Trim();
        var trimmedProxy = ProxyBox.Text.Trim();
        // 自定义模式下输入框为空：用户刚切过来还没填，别把单选跳回直连
        var awaitingCustomInput = ProxyCustom.IsChecked == true && trimmedProxy.Length == 0;
        var editingProxy = trimmedProxy.Length > 0 && trimmedProxy != _savedProxy;
        if (!awaitingCustomInput && !editingProxy)
        {
            switch (proxy)
            {
                case "system":
                    ProxySystem.IsChecked = true;
                    ProxyBox.Text = "";
                    break;
                case "":
                    ProxyDirect.IsChecked = true;
                    ProxyBox.Text = "";
                    break;
                default:
                    ProxyCustom.IsChecked = true;
                    ProxyBox.Text = proxy;
                    break;
            }
        }
        _savedProxy = proxy;
        ProxyBox.IsEnabled = ProxyCustom.IsChecked == true;
        _filling = false;
    }

    // -------- 落库（无保存按钮：输入完成 / 切换即生效）--------

    /// <summary>服务器地址输入完成（回车或失焦）即保存；空值视为放弃修改，回退到仍在生效的地址。</summary>
    private void SaveServerIfChanged()
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0)
        {
            ServerBox.Text = _savedServerUrl.Length > 0 ? _savedServerUrl : State.Config.ServerUrl;
            return;
        }
        if (ServerBox.Text != url)
        {
            ServerBox.Text = url;
        }
        if (url == _savedServerUrl)
        {
            return;
        }
        // 只提交服务器地址，其余字段由后端沿用
        _savedServerUrl = url;
        State.SaveConfig(url, null);
    }

    /// <summary>代理模式切换即保存；自定义模式的地址还没填时不落库，等输入完成。</summary>
    private void OnProxyModeChecked(ProxyMode mode)
    {
        if (_filling)
        {
            return;
        }
        ProxyBox.IsEnabled = mode == ProxyMode.Custom;
        switch (mode)
        {
            case ProxyMode.Direct:
                ProxyBox.Text = "";
                CommitProxy("");
                break;
            case ProxyMode.System:
                ProxyBox.Text = "";
                CommitProxy("system");
                break;
            case ProxyMode.Custom:
                // 直连/系统代理没有可回填的地址，输入框留空等用户填写
                ProxyBox.Text = _savedProxy == "system" ? "" : _savedProxy;
                break;
        }
    }

    /// <summary>代理地址输入完成（回车或失焦）即保存；只在自定义模式下生效，空值视为放弃修改。</summary>
    private void SaveProxyIfChanged()
    {
        if (ProxyCustom.IsChecked != true)
        {
            return;
        }
        var value = ProxyBox.Text.Trim();
        if (value.Length == 0)
        {
            ProxyBox.Text = _savedProxy == "system" ? "" : _savedProxy;
            return;
        }
        if (ProxyBox.Text != value)
        {
            ProxyBox.Text = value;
        }
        CommitProxy(value);
    }

    private void CommitProxy(string value)
    {
        if (value == _savedProxy)
        {
            return;
        }
        _savedProxy = value;
        State.SaveConfig(null, value);
    }

    // -------- 动作 --------

    private async void OnTestServerClick(object sender, RoutedEventArgs e)
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0)
        {
            await ShowInfoAsync("输入错误", "服务器地址不能为空");
            return;
        }
        var message = await State.TestServerAsync(url);
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
}
