using XTrader.Shell.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 设置页：服务器连接 + 代理 + 软件更新（对应 mac 壳的 SettingsView / Qt SettingsPage）。
/// </summary>
public sealed partial class SettingsPage : Page
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
    private bool _filling;

    public SettingsPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
    }

    private void ApplyPalette()
    {
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);
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
        foreach (var divider in new[] { Divider1, Divider2, Divider3 })
        {
            divider.Fill = Brush(Palette.Divider);
        }
        VersionText.Foreground = Brush(Palette.Ink);
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
        VersionText.Text = $"当前版本 v{State.BackendVersion}";
        if (!_filling)
        {
            // 配置事件（config_changed）引起的外部变更回填表单
            FillFromConfig();
        }
        RefreshUpdateArea();
    }

    // -------- 表单回填（对应 Qt set_config）--------

    private void FillFromConfig()
    {
        _filling = true;
        var config = State.Config;
        if (ServerBox.Text != config.ServerUrl)
        {
            ServerBox.Text = config.ServerUrl;
        }

        var proxy = config.ProxyUrl.Trim();
        if (proxy == "system")
        {
            ProxySystem.IsChecked = true;
            ProxyBox.Text = "";
        }
        else if (proxy.Length > 0)
        {
            ProxyCustom.IsChecked = true;
            ProxyBox.Text = proxy;
        }
        else
        {
            ProxyDirect.IsChecked = true;
            ProxyBox.Text = "";
        }
        ProxyBox.IsEnabled = ProxyCustom.IsChecked == true;

        AutoCheckBox.IsChecked = config.AutoCheckUpdate;
        _filling = false;
    }

    // -------- 动作 --------

    private async void OnTestServerClick(object sender, RoutedEventArgs e)
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0)
        {
            ShowTestResult("请先输入服务器地址", success: false);
            return;
        }
        ShowTestResult("正在测试连接...", success: null);
        var message = await State.TestServerAsync(url);
        ShowTestResult(message, message.Contains("成功"));
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var url = ServerBox.Text.Trim();
        if (url.Length == 0)
        {
            _ = ShowInfoAsync("服务器地址不能为空");
            return;
        }

        // 对应 Qt _current_proxy_value：系统代理存 "system"，直连存空串
        string proxyValue;
        if (ProxyDirect.IsChecked == true)
        {
            proxyValue = "";
        }
        else if (ProxySystem.IsChecked == true)
        {
            proxyValue = "system";
        }
        else
        {
            proxyValue = ProxyBox.Text.Trim();
        }

        State.SaveConfig(url, proxyValue, AutoCheckBox.IsChecked == true);
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) => State.CheckUpdate();

    private void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        switch (State.UpdatePhase)
        {
            case UpdatePhase.Available:
                State.DownloadUpdate();
                break;
            case UpdatePhase.Ready:
                State.ApplyUpdate();
                break;
        }
    }

    private void RefreshUpdateArea()
    {
        var phase = State.UpdatePhase;
        ActionButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Visibility = Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = phase is not (UpdatePhase.Checking or UpdatePhase.Downloading);

        switch (phase)
        {
            case UpdatePhase.Available available:
                ActionButton.Content = $"下载 v{available.Version}{(available.HasPatch ? "（差量）" : "")}";
                ActionButton.Visibility = Visibility.Visible;
                break;
            case UpdatePhase.Ready ready:
                ActionButton.Content = $"安装并重启后端（{ready.Kind}）";
                ActionButton.Visibility = Visibility.Visible;
                break;
            case UpdatePhase.Checking:
                SetStatus("正在检查更新...", UpdateStatusKind.Pending);
                break;
            case UpdatePhase.Downloading:
                SetStatus("正在下载更新...", UpdateStatusKind.Pending);
                break;
            case UpdatePhase.Latest latest:
                SetStatus(latest.Message, UpdateStatusKind.Success);
                break;
            case UpdatePhase.Failed failed:
                SetStatus(failed.Message, UpdateStatusKind.Danger);
                break;
        }
    }

    private enum UpdateStatusKind { Pending, Success, Danger }

    private void SetStatus(string message, UpdateStatusKind kind)
    {
        UpdateStatus.Text = message;
        // Qt set_update_status：进行中 WARNING / 成功 SUCCESS / 失败 DANGER（固定色，不随主题）
        UpdateStatus.Foreground = Brush(kind switch
        {
            UpdateStatusKind.Success => Hex(0x34C759),
            UpdateStatusKind.Danger => Hex(0xE3342F),
            _ => Hex(0xE8A33D),
        });
        UpdateStatus.Visibility = Visibility.Visible;
    }

    private void ShowTestResult(string message, bool? success)
    {
        ServerTestResult.Text = message;
        ServerTestResult.Foreground = Brush(success switch
        {
            true => Hex(0x34C759),
            false => Hex(0xE3342F),
            null => Hex(0xE8A33D),
        });
        ServerTestResult.Visibility = Visibility.Visible;
    }

    private async Task ShowInfoAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "提示",
            Content = message,
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }
}
