using XTrader.Shell.Theme;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 系统日志页：内存会话日志（对应 mac 壳的 TradingLogView / Qt LogPage）。
///
/// 整个日志区是一个 RichTextBlock、每行一个 Paragraph：原来每行一个 TextBlock 时，
/// 选中范围被限制在单个控件内，只能复制一行。合并后保留按级别着色，同时可跨行拖选复制。
///
/// 直接订阅 AppState.Logs 的集合变更增量追加，而不是每次全量重建：
/// 日志可达 500 行，全量重建在滚动时会明显掉帧。
/// </summary>
public sealed partial class TradingLogPage : Page
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
    public TradingLogPage()
    {
        InitializeComponent();
        _theme = new ThemedPage(this, ApplyPalette, OnStateChanged);
    }

    private void ApplyPalette()
    {
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);
        Hint.Foreground = Brush(Palette.Dim);
        LogScroll.Background = Brush(Palette.LogBg);
        LogScroll.BorderBrush = Brush(Palette.Hairline);
        RebuildLogs();
    }

    private void OnStateChanged()
    {
        // 日志增量由 CollectionChanged 处理；此处仅确保已挂载后的首帧渲染
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _theme.Attach(e);
        if (State is null)
        {
            return;
        }
        State.Logs.CollectionChanged += OnLogsChanged;
        RebuildLogs();
        Unloaded += (_, _) => State.Logs.CollectionChanged -= OnLogsChanged;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (var item in e.NewItems.OfType<LogLine>())
                {
                    LogText.Blocks.Add(BuildLine(item));
                }
                // 超过上限时 AppState 会移除最旧的若干条，Blocks 比 Logs 多出的就是需要裁掉的量
                while (LogText.Blocks.Count > State.Logs.Count)
                {
                    LogText.Blocks.RemoveAt(0);
                }
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
                break;
            case NotifyCollectionChangedAction.Reset:
                RebuildLogs();
                break;
        }
    }

    private void RebuildLogs()
    {
        LogText.Blocks.Clear();
        foreach (var line in State.Logs)
        {
            LogText.Blocks.Add(BuildLine(line));
        }
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
    }

    /// <summary>一行日志 = 一个 Paragraph，段后留 2px（原来每行 TextBlock 的垂直 padding）</summary>
    private Paragraph BuildLine(LogLine line) => new()
    {
        Margin = new Thickness(0, 0, 0, 2),
        Inlines =
        {
            new Run
            {
                Text = $"[{line.Time:HH:mm:ss}] [{line.Level}] {line.Message}",
                Foreground = Brush(LogLevelColor(line.Level, Host.IsDark)),
            },
        },
    };

    private void OnClearClick(object sender, RoutedEventArgs e) => State.Logs.Clear();
}
