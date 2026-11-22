using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatNote;

internal static class Native
{
    // ---------- 全局热键 ----------
    public const int WM_HOTKEY = 0x0312;
    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_WIN = 0x0008;
    public const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- 窗口样式 ----------
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---------- 分层窗口透明 ----------
    private const int WS_EX_LAYERED = 0x00080000;
    private const int GWL_EXSTYLE_ = -20;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 设置窗口整体不透明度，**真正与桌面做 alpha 混合**。
    ///
    /// 为什么不能用 WPF 的 Window.Opacity：
    /// 当 AllowsTransparency=false（我们用 WindowChrome 保留原生圆角与硬件加速，必须为 false）时，
    /// WPF 的 Opacity 只是把窗口内容按比例调暗再画到一张不透明表面上，
    /// 底层 DWM 不做 alpha 合成。实测 65% 时卡片色 255 变成 166（=255×0.65），
    /// 背后桌面（RGB 18）一个像素都没混进来 —— 即"变暗"而非"变透明"。
    ///
    /// 正确做法：给窗口加 WS_EX_LAYERED，再用 SetLayeredWindowAttributes 设置整窗 alpha。
    /// 这是 DWM 原生支持的合成路径，仍然走 GPU。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// 真正让窗口半透明。
    ///
    /// 踩坑记录（很重要）：
    /// 1) WPF 的 Window.Opacity 在 AllowsTransparency=false 时**只压暗**，
    ///    实测 65% 时卡片 255 → 166（=255×0.65），背后桌面一个像素都没混入。
    /// 2) 试图用 WS_EX_LAYERED + SetLayeredWindowAttributes 绕过，
    ///    SetWindowLongPtr 返回成功（err=0），但随后 GetWindowLongPtr 又读回旧值
    ///    —— WPF 的 HwndSource 会重置扩展样式，这条路在 WPF 窗口上不可靠。
    ///
    /// 结论：必须用 WPF 自己的分层窗口（AllowsTransparency=true）+ 窗口级 Opacity，
    /// 这才是真正与桌面做 alpha 混合的路径。
    /// 是否启用由 NoteWindow 在创建时按当前不透明度决定（见 RebuildForTransparency）。
    /// </summary>
    public static void ApplyLayeredOpacity(Window w, double opacity)
    {
        // AllowsTransparency=true 时，WPF 的 Opacity 走 DWM alpha 合成
        if (!w.AllowsTransparency) return;
        w.Opacity = Math.Clamp(opacity, 0.15, 1.0);
    }

    // ---------- 自绘窗口的拖动/缩放 ----------
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>
    /// 用系统拖动循环移动窗口。
    ///
    /// 分层窗口不能使用 WindowChrome（两者互斥：AllowsTransparency=true 时
    /// 系统不提供非客户区，WindowChrome 会把窗口高度压成只剩标题栏）。
    /// 所以拖动改由这里发起 —— 仍走 Windows 自己的拖动循环，
    /// 手感、贴边、多屏处理与原生完全一致，不是自己算鼠标位移那种"飘"的实现。
    /// </summary>
    public static void BeginDragMove(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    // ---------- DWM ----------
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>
    /// 恢复 DWM 原生圆角 + 投影。
    ///
    /// 背景：WindowChrome 的 GlassFrameThickness=0 会通知 DWM 不要绘制窗口边框，
    /// 结果圆角和投影一起消失，窗口变成一块生硬的直角方块。
    /// 这里用 DwmExtendFrameIntoClientArea 把边框向内扩 1px，
    /// 让 DWM 重新接管合成——投影和圆角都由 GPU 完成，不额外消耗 CPU。
    /// </summary>
    public static void EnableNativeFrame(Window w, bool withShadow = true)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Win11：显式要求圆角
        int pref = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { /* Win10 及更早不支持，忽略 */ }

        // Win10/11：扩 1px 边框，换回投影。
        // 注意：这 1px 会画出系统边框色，在深色窗口上会显示成一条白线，
        // 所以深色窗口用 withShadow:false 只取圆角。
        try
        {
            var m = withShadow
                ? new MARGINS { Left = 1, Top = 1, Right = 1, Bottom = 1 }
                : new MARGINS { Left = 0, Top = 0, Right = 0, Bottom = 0 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
        catch { }
    }

    /// <summary>隐藏 Alt+Tab 中的条目（便签属于工具窗，不该污染任务切换列表）。</summary>
    public static void HideFromAltTab(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((long)ex | WS_EX_TOOLWINDOW));
    }

    // ---------- 屏幕信息 ----------
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    private const int LOGPIXELSX = 88;

    /// <summary>主显示器 DPI 缩放（1.0 = 100%）。截图坐标必须换算，否则高缩放下截偏。</summary>
    public static double PrimaryDpiScale()
    {
        try
        {
            var dc = GetDC(IntPtr.Zero);
            int dpi = GetDeviceCaps(dc, LOGPIXELSX);
            ReleaseDC(IntPtr.Zero, dc);
            return dpi / 96.0;
        }
        catch { return 1.0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// 把已存在的窗口恢复并拉到前台。
    ///
    /// Windows 有前台锁：后台进程直接调 SetForegroundWindow 往往被静默忽略。
    /// 常见有效做法是先把本线程挂到当前前台窗口的输入队列上，
    /// 取得前台权限后再激活目标窗口，最后解挂。
    /// </summary>
    public static void ShowAndActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
        else ShowWindow(hwnd, 5);                  // SW_SHOW

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (fgThread != 0 && fgThread != myThread)
                attached = AttachThreadInput(myThread, fgThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// 整个虚拟桌面的**物理像素**边界（多显示器合并，可能含负坐标）。
    /// 用 GetSystemMetrics 而非 SystemParameters：前者返回真实像素，
    /// 后者返回 DIP，在 200% 缩放下会差一倍，导致截屏只截到左上角。
    /// </summary>
    public static Int32Rect VirtualScreenPixels() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));
}

/// <summary>全局热键注册器。事件驱动，空闲时 CPU 为 0（不轮询）。</summary>
internal sealed class HotkeyHost : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _map = new();

    public HotkeyHost(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle)
                  ?? throw new InvalidOperationException("window handle not ready");
        _source.AddHook(WndProc);
    }

    public bool TryRegister(int id, int mods, int vk, Action action)
    {
        if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk))
            return false;
        _map[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _map.TryGetValue(wParam.ToInt32(), out var a))
        {
            a();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _map.Keys)
            Native.UnregisterHotKey(_source.Handle, id);
        _map.Clear();
        _source.RemoveHook(WndProc);
    }
}
