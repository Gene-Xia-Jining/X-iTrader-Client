using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XTrader.Shell.State;
using XTrader.Shell.Theme;

namespace XTrader.Shell.Views;

/// <summary>
/// 页面公共逻辑的宿主（组合而非继承）。
///
/// 不能把 XAML 页面的基类改成自定义类：XAML 根元素写 <c>Page</c> 时，WinUI 的
/// XAML 编译器会生成 <c>partial class Xxx : Page</c>，而 C# 侧再声明
/// <c>: ThemedPageBase</c> 就触发 CS0263（两个 partial 基类不一致）。因此各页
/// 仍是普通 Page，在构造函数里 <c>new ThemedPage(this)</c> 把状态与主题的胶水
/// 代码挂上来，页面自身只需要实现 <see cref="ApplyPalette"/> / <see cref="OnStateChanged"/>。
/// </summary>
public sealed class ThemedPage
{
    private readonly Page _page;

    public ThemedPage(Page page, Action applyPalette, Action onStateChanged)
    {
        _page = page;
        ApplyPalette = applyPalette;
        OnStateChanged = onStateChanged;
        _page.Unloaded += (_, _) => Detach();
    }

    /// <summary>按当前 Palette 重新着色本页元素。</summary>
    public Action ApplyPalette { get; }

    /// <summary>状态变化时刷新数据展示。</summary>
    public Action OnStateChanged { get; }

    public AppState State { get; private set; } = null!;
    public MainWindow Host { get; private set; } = null!;
    public Palette Palette { get; private set; } = ThemeTokens.For(false);

    /// <summary>由页面在 OnNavigatedTo 里调用：取导航参数、订阅主题与状态。</summary>
    public void Attach(NavigationEventArgs e)
    {
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
    }

    private void Detach()
    {
        if (Host is null || State is null)
        {
            return;
        }
        Host.ThemeChanged -= OnHostThemeChanged;
        State.PropertyChanged -= OnStatePropertyChanged;
    }

    private void OnHostThemeChanged()
    {
        Palette = ThemeTokens.For(Host.IsDark);
        ApplyPalette();
        OnStateChanged();
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 所有状态变更都在 UI 线程发起（AppState.Post 保证），可直接刷新
        OnStateChanged();
    }

    // -------- 画刷工具 --------

    public SolidColorBrush Brush(Windows.UI.Color color) => new(color);

    /// <summary>
    /// 页内分段 tab 的选中态：实心主题蓝 + 白字半粗，未选中透明底 + 发丝描边。
    /// 模拟交易页与实盘交易页的「快速开始 / 交易记录」切换共用。
    /// </summary>
    public void StyleCapsuleTab(Button tab, bool isOn)
    {
        tab.Background = new SolidColorBrush(isOn ? Palette.Primary : Microsoft.UI.Colors.Transparent);
        tab.BorderBrush = new SolidColorBrush(isOn ? Palette.Primary : Palette.Hairline);
        tab.BorderThickness = new Thickness(1);
        tab.Foreground = new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Palette.Ink);
        tab.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>
    /// 带内嵌问号的胶囊 tab（「历史成交」用）：问号用 Border + TextBlock 而不是 Button，
    /// 避免 WinUI 默认按钮在悬停/按下时改成浅灰底，破坏选中态蓝色胶囊的整体观感。
    /// </summary>
    public void StyleCapsuleTabContainer(Border capsule, Button tab, Border helpRing, TextBlock helpIcon, bool isOn)
    {
        capsule.Background = new SolidColorBrush(isOn ? Palette.Primary : Microsoft.UI.Colors.Transparent);
        capsule.BorderBrush = new SolidColorBrush(isOn ? Palette.Primary : Palette.Hairline);
        tab.Foreground = new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Palette.Ink);
        tab.FontWeight = isOn ? FontWeights.SemiBold : FontWeights.Normal;
        helpIcon.Foreground = new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Palette.Primary);
        helpRing.BorderBrush = new SolidColorBrush(
            isOn ? Windows.UI.Color.FromArgb(115, 255, 255, 255) : Palette.Hairline);
    }

    /// <summary>日志/等宽文本的配色（对应 Swift 的 logColors）。</summary>
    public static Windows.UI.Color LogLevelColor(string level, bool dark) => level switch
    {
        "ERROR" => dark ? Hex(0xFF453A) : Hex(0xC5241F),
        "WARNING" => dark ? Hex(0xFFD60A) : Hex(0xB8780A),
        "SUCCESS" => dark ? Hex(0x30D158) : Hex(0x1D9552),
        _ => dark ? Microsoft.UI.Colors.White : Hex(0x1D1D1F),
    };

    public static Windows.UI.Color Hex(uint value) => Windows.UI.Color.FromArgb(
        0xFF, (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
}
