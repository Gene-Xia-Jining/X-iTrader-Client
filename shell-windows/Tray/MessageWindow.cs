using System.Runtime.InteropServices;

namespace XTrader.Shell.Tray;

/// <summary>
/// 仅消息窗口（HWND_MESSAGE）：不显示、不参与布局，只用于接收托盘图标
/// 的回调消息（Shell_NotifyIcon 需要一个 HWND 承载 WM_ 消息）。
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly string _className;
    private readonly WndProc _wndProc;  // 保引用，防止委托被 GC 回收

    internal IntPtr Handle { get; private set; }

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal MessageWindow(string title, Action<uint, IntPtr, IntPtr> onMessage)
    {
        _className = $"XTraderMessageWindow_{Guid.NewGuid():N}";
        _wndProc = (hWnd, msg, wParam, lParam) =>
        {
            onMessage(msg, wParam, lParam);
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        };

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };
        _ = RegisterClassW(ref wc);

        // HWND_MESSAGE (-3)：仅消息窗口
        Handle = CreateWindowExW(
            0, _className, title, 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    internal void Destroy()
    {
        if (Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        _ = UnregisterClassW(_className, GetModuleHandleW(null));
    }

    public void Dispose() => Destroy();

    // -------- P/Invoke --------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
