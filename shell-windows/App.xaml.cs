using Microsoft.UI.Xaml;
using XTrader.Shell.Core;
using XTrader.Shell.State;

namespace XTrader.Shell;

/// <summary>
/// 应用主体。持有唯一的 <see cref="AppState"/> 与主窗口。
///
/// 单实例判定放在 OnLaunched：协议要求同机只有一个后端实例，多开壳会重复
/// spawn 后端。第二个实例在此把已运行实例的窗口唤到前台，然后自行退出。
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    internal static AppState? State { get; private set; }

    public App()
    {
        InitializeComponent();

        // UI 线程上的未处理异常默认会让 WinUI 直接 fail-fast 杀进程：没有弹窗、
        // 没有日志，用户只看到窗口消失。这里至少留痕，并拦下默认的终止。
        // 注意这挡不住 XAML 布局循环（那是原生 fail-fast，不走托管异常通道）。
        UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(AppPaths.DataRoot, "data", "logs", "shell-crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {e.Message}\n{e.Exception}\n\n");
            }
            catch
            {
                // 日志写不进去也要让 e.Handled 生效，不能再抛
            }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!SingleInstance.TryAcquire())
        {
            // 已有实例：SingleInstance 内部已唤出对方窗口，本进程退出
            Exit();
            return;
        }

        // 废弃偏好键：交易记录页不再有模拟 / 实盘分段（模拟账户的记录已并入
        // 模拟交易页的「账户信息」tab），老版本留下的选择已无意义
        ShellPrefs.Remove("tradeRecords.kind");

        State = new AppState(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        State.Start();

        _window = new MainWindow(State);
        _window.Activate();
    }

    /// <summary>壳正常退出前收尾：停止后端、释放单实例。</summary>
    internal void OnExiting()
    {
        State?.Shutdown();
        SingleInstance.Release();
    }
}
