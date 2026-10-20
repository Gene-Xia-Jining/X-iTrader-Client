using XTrader.Shell.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 设置页：代理服务器配置（对应 mac 壳的 SettingsView / Qt SettingsPage）。
/// 服务器地址与测试连接已合并到顶栏服务器弹窗（点击服务器状态胶囊）；
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
    private string _savedProxy = "";

    public SettingsPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);

        // 设置页没有保存按钮：各字段输入完成（回车或失焦）/ 切换选项即落库
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

        ProxyLabel.Foreground = Brush(Palette.Ink);
        ProxyHint.Foreground = Brush(Palette.Dim);
        ProxyHint.FontSize = 13;
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
}
