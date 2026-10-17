using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>
/// 页面基类：统一持有 AppState、宿主窗口引用与主题画刷。
///
/// 页面导航由 MainWindow 传入 <see cref="PageContext"/>（经 Frame 的导航参数），
/// 基类在 OnNavigatedTo 里取值；主题深浅由宿主窗口的变化事件驱动
/// <see cref="ApplyPalette"/> 重取颜色。
/// </summary>
public abstract class ThemedPageBase : Page
{
    protected AppState State { get; private set; } = null!;
    protected MainWindow Host { get; private set; } = null!;
    protected Palette Palette { get; private set; } = ThemeTokens.For(false);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PageContext context)
        {
            return;
        }

        State = context.State;
        Host = context.Window;
        Palette = ThemeTokens.For(Host.IsDark);
        Host.ThemeChanged += OnHostThemeChanged;
        State.PropertyChanged += OnStatePropertyChanged;

        ApplyPalette();
        OnStateChanged();

        Unloaded += (_, _) =>
        {
            Host.ThemeChanged -= OnHostThemeChanged;
            State.PropertyChanged -= OnStatePropertyChanged;
        };
    }

    /// <summary>由子类覆写：按当前 Palette 重新着色本页元素。</summary>
    protected virtual void ApplyPalette() { }

    /// <summary>由子类覆写：状态变化时刷新数据展示。</summary>
    protected virtual void OnStateChanged() { }

    private void OnHostThemeChanged()
    {
        Palette = ThemeTokens.For(Host.IsDark);
        ApplyPalette();
        OnStateChanged();
    }

    private void OnStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 所有状态变更都在 UI 线程发起（AppState.Post 保证），可直接刷新
        OnStateChanged();
    }

    // -------- 画刷工具 --------

    protected SolidColorBrush Brush(Windows.UI.Color color) => new(color);

    /// <summary>日志/等宽文本的配色（对应 Swift 的 logColors）。</summary>
    protected static Windows.UI.Color LogLevelColor(string level, bool dark) => level switch
    {
        "ERROR" => dark ? Hex(0xFF453A) : Hex(0xC5241F),
        "WARNING" => dark ? Hex(0xFFD60A) : Hex(0xB8780A),
        "SUCCESS" => dark ? Hex(0x30D158) : Hex(0x1D9552),
        _ => dark ? Microsoft.UI.Colors.White : Hex(0x1D1D1F),
    };

    protected static Windows.UI.Color Hex(uint value) => Windows.UI.Color.FromArgb(
        0xFF, (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
}
