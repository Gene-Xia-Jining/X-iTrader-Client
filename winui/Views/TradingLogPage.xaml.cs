using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using XTrader.Shell.State;

namespace XTrader.Shell.Views;

/// <summary>
/// 交易日志页：内存会话日志（对应 mac 壳的 TradingLogView / Qt LogPage）。
///
/// 直接订阅 AppState.Logs 的集合变更增量追加 TextBlock，而不是每次全量重建：
/// 日志可达 500 行，全量重建在滚动时会明显掉帧。
/// </summary>
public sealed partial class TradingLogPage : ThemedPageBase
{
    public TradingLogPage()
    {
        InitializeComponent();
    }

    protected override void ApplyPalette()
    {
        PageTitle.Foreground = Brush(Palette.Ink);
        Subtitle.Foreground = Brush(Palette.Dim);
        Hint.Foreground = Brush(Palette.Dim);
        LogScroll.Background = Brush(Palette.LogBg);
        LogScroll.BorderBrush = Brush(Palette.Hairline);
        RebuildLogs();
    }

    protected override void OnStateChanged()
    {
        // 日志增量由 CollectionChanged 处理；此处仅确保已挂载后的首帧渲染
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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
                    LogPanel.Children.Add(BuildLine(item));
                }
                // 超过上限时 AppState 会移除最旧的若干条
                while (LogPanel.Children.Count > State.Logs.Count)
                {
                    LogPanel.Children.RemoveAt(0);
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
        LogPanel.Children.Clear();
        foreach (var line in State.Logs)
        {
            LogPanel.Children.Add(BuildLine(line));
        }
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
    }

    private TextBlock BuildLine(LogLine line) => new()
    {
        Text = $"[{line.Time:HH:mm:ss}] [{line.Level}] {line.Message}",
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        Foreground = Brush(LogLevelColor(line.Level, Host.IsDark)),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private void OnClearClick(object sender, RoutedEventArgs e) => State.Logs.Clear();
}
