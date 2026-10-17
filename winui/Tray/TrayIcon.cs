using System.Runtime.InteropServices;

namespace XTrader.Shell.Tray;

/// <summary>
/// 系统托盘图标（对应 mac 壳的 NSStatusItem）。
///
/// 用 Shell_NotifyIcon + CreatePopupMenu 直接 P/Invoke，不引入第三方托盘库
/// （WinForms 的 NotifyIcon 会把 WinForms 整套依赖带进来）。
///
/// 交互：左键单击唤回主窗口；右键弹出菜单「显示窗口 / 退出后端 / 退出」。
/// 需要一个隐藏窗口接收托盘回调消息，用 <see cref="MessageWindow"/> 注册一个
/// 仅消息窗口（HWND_MESSAGE）承载。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    private const int IDI_APPLICATION = 32512;
    private const uint NIM_ADD = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RETURNCMD = 0x100;

    private const int CmdShow = 1;
    private const int CmdQuitBackend = 2;
    private const int CmdQuit = 3;

    private readonly MessageWindow _window;
    private readonly Action _onShow;
    private readonly Action _onQuitBackend;
    private readonly Action _onQuit;
    private bool _added;

    internal TrayIcon(Action onShow, Action onQuitBackend, Action onQuit)
    {
        _onShow = onShow;
        _onQuitBackend = onQuitBackend;
        _onQuit = onQuit;
        _window = new MessageWindow(WindowTitle, OnMessage);
    }

    private const string WindowTitle = "iTrader 智能交易系统";
    private const string Tooltip = "iTrader 智能交易系统";

    internal void Add()
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        data.szTip = Tooltip;
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
    };

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_TRAYICON:
                switch ((int)lParam)
                {
                    case WM_LBUTTONUP:
                        _onShow();
                        break;
                    case WM_RBUTTONUP:
                        ShowMenu();
                        break;
                }
                break;
            case WM_COMMAND:
                switch ((int)wParam)
                {
                    case CmdShow: _onShow(); break;
                    case CmdQuitBackend: _onQuitBackend(); break;
                    case CmdQuit: _onQuit(); break;
                }
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, CmdShow, "显示窗口");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CmdQuitBackend, "退出后端");
        AppendMenuW(menu, MF_STRING, CmdQuit, "退出");

        GetCursorPos(out var pt);
        // 前置窗口，否则菜单不会因点击别处而消失（TrackPopupMenu 的既有要求）
        SetForegroundWindow(_window.Handle);
        var cmd = TrackPopupMenu(
            menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _window.Handle, IntPtr.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdShow: _onShow(); break;
            case CmdQuitBackend: _onQuitBackend(); break;
            case CmdQuit: _onQuit(); break;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        _window.Destroy();
    }

    // -------- P/Invoke --------

    /// <summary>
    /// NOTIFYICONDATAW 必须按完整 V4 布局声明：Shell_NotifyIcon 用 cbSize 判定
    /// 结构版本，cbSize 不等于任何已知尺寸（V1/V2/V3/V4）时会直接返回 FALSE，
    /// 表现为"托盘图标不出现"。x64 下完整尺寸为 976 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        // uTimeout / uVersion 的 union：两者都是 uint，按 4 字节占位即可
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
