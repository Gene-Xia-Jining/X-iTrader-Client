using Microsoft.UI.Xaml;
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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!SingleInstance.TryAcquire())
        {
            // 已有实例：SingleInstance 内部已唤出对方窗口，本进程退出
            Exit();
            return;
        }

        State = new AppState(DispatcherQueue);
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
