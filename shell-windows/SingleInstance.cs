using System.Runtime.InteropServices;

namespace XTrader.Shell;

/// <summary>
/// 单实例保护：协议要求同机只有一个后端实例，多开壳会导致重复 spawn。
///
/// 做法：命名 Mutex 判定是否已有实例；若已有，则把自己（第二个进程）退出，
/// 并把已运行实例的主窗口唤到前台（FindWindow 按标题 + SW_RESTORE）。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = "X-iTrader-Client-Shell-Singleton";
    private const string WindowTitle = "iTrader 智能交易系统";

    private static Mutex? _mutex;
    private static bool _owned;

    /// <summary>获取单实例所有权。false 表示已有实例在运行（调用方应退出）。</summary>
    internal static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _owned = createdNew;
        if (!createdNew)
        {
            // 已有实例：唤出其窗口后本进程退出
            BringExistingToFront();
        }
        return createdNew;
    }

    internal static void Release()
    {
        if (_mutex is null)
        {
            return;
        }
        try
        {
            if (_owned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // 退出路径的释放失败无需上报
        }
        _mutex.Dispose();
        _mutex = null;
        _owned = false;
    }

    private static void BringExistingToFront()
    {
        var hwnd = FindWindowW(null, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        // 若窗口最小化（或被托盘隐藏）先还原，再置前
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? className, string windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
