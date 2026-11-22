using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FloatNote;

/// <summary>
/// 纯 P/Invoke 托盘图标（Shell_NotifyIcon）。
/// 相比引入 System.Windows.Forms 只为用 NotifyIcon，可省约 10MB 常驻内存。
///
/// 复用 App 里已有的隐藏窗口作为消息接收者，不额外注册窗口类——
/// 单文件发布时 Marshal.GetHINSTANCE 会失效，自建窗口类容易踩坑。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1;
    private const int NIF_ICON = 2;
    private const int NIF_TIP = 4;
    private const int NIF_INFO = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string exe, int index);

    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInst, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private readonly HwndSource _source;
    private NOTIFYICONDATA _data;
    private readonly IntPtr _hIcon;
    private bool _disposed;

    public event Action? LeftClick;
    public event Action? RightClick;

    public TrayIcon(HwndSource source, string tip)
    {
        _source = source;
        _hIcon = LoadAppIcon();

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
        };

        if (!Shell_NotifyIcon(NIM_ADD, ref _data))
            Debug.WriteLine("[FloatNote] Shell_NotifyIcon ADD failed");

        _source.AddHook(WndProcHook);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            switch (ev)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    LeftClick?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    RightClick?.Invoke();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            var d = _data;
            d.uFlags = NIF_INFO;
            d.szInfoTitle = title.Length > 60 ? title[..60] : title;
            d.szInfo = text.Length > 250 ? text[..250] : text;
            d.dwInfoFlags = 0x1;   // NIIF_INFO
            d.uTimeoutOrVersion = 4000;
            Shell_NotifyIcon(NIM_MODIFY, ref d);
            _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        }
        catch { }
    }

    private static IntPtr LoadAppIcon()
    {
        // 优先用 exe 内嵌图标（TaskbarIcon 显示更贴合应用）
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = ExtractIcon(IntPtr.Zero, exe, 0);
                if (icon != IntPtr.Zero && icon != (IntPtr)1) return icon;
            }
        }
        catch { }

        return LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION 兜底
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Shell_NotifyIcon(NIM_DELETE, ref _data); } catch { }
        try { _source.RemoveHook(WndProcHook); } catch { }
        try { if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); } catch { }
    }
}
