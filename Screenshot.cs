using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatNote;

/// <summary>
/// 框选截图。
///
/// 设计要点（两条都是踩过坑后的结论）：
///
/// 1. **先抓屏，再在静态图上框选。**
///    早期版本直接用一层全屏半透明窗口盖住活的桌面。
///    在 200% 缩放 + 3200×2000 物理分辨率下，那是一个
///    3200×2000 的分层窗口（约 25MB 表面），每次重绘都走软件合成，
///    结果 ShowDialog 的模态消息循环直接卡死（表现为白屏卡退）。
///    现在改为主流截图工具的做法：先用 GDI 抓到一张位图，
///    再把它画在普通窗口里，用户拖着框选。没有分层窗口，没有实时重绘。
///
/// 2. **不用 ShowDialog。**
///    模态嵌套消息循环在这种"弹出即全屏"的场景下很容易死锁。
///    改用 Show() + TaskCompletionSource，纯异步、无线程阻塞。
/// </summary>
public sealed class RegionSnipper : Window
{
    private readonly BitmapSource _screen;        // 已抓好的全屏图（物理像素）
    private readonly Int32Rect _screenRect;       // 全屏图对应的虚拟桌面物理矩形
    private readonly double _dpiX, _dpiY;

    private readonly Canvas _overlay = new();
    private readonly Border _sel;
    private readonly Border _hint;
    private readonly TextBlock _sizeLabel;

    private readonly System.Windows.Shapes.Rectangle _dimTop = new();
    private readonly System.Windows.Shapes.Rectangle _dimBottom = new();
    private readonly System.Windows.Shapes.Rectangle _dimLeft = new();
    private readonly System.Windows.Shapes.Rectangle _dimRight = new();

    private readonly TaskCompletionSource<BitmapSource?> _tcs = new();

    private Point _startPhys;
    private bool _dragging;
    private bool _done;

    /// <summary>等待用户框选完成。取消返回 null。</summary>
    public Task<BitmapSource?> Result => _tcs.Task;

    private RegionSnipper(BitmapSource screen, Int32Rect screenRect)
    {
        _screen = screen;
        _screenRect = screenRect;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;               // 关键：不做分层窗口
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        ResizeMode = ResizeMode.NoResize;

        // 窗口按虚拟桌面的 DIP 尺寸铺满
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _dpiY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (_dpiX <= 0) _dpiX = 1;
        if (_dpiY <= 0) _dpiY = 1;

        // 背景层：抓好的静态桌面图
        var bg = new Image
        {
            Source = screen,
            Stretch = Stretch.Fill,               // 位图是物理像素，窗口是 DIP，用 Fill 精确铺满
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(bg, BitmapScalingMode.NearestNeighbor);

        _sel = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(84, 143, 245)),
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var sizeBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 24, 27, 34)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Child = _sizeLabel,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _sizeBox = sizeBox;

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(232, 24, 27, 34)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 44, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "拖动框选区域　·　Esc 或右键取消",
                Foreground = Brushes.White,
                FontSize = 12.5,
            },
        };

        foreach (var r in new[] { _dimTop, _dimBottom, _dimLeft, _dimRight })
        {
            r.Fill = new SolidColorBrush(Color.FromArgb(105, 10, 12, 18));
            r.IsHitTestVisible = false;
            r.Visibility = Visibility.Collapsed;
            _overlay.Children.Add(r);
        }

        _overlay.Children.Add(_sel);
        _overlay.Children.Add(sizeBox);
        _overlay.Children.Add(_hint);
        _overlay.Background = Brushes.Transparent;

        var root = new Grid { Background = Brushes.Black, ClipToBounds = true };
        root.Children.Add(bg);
        root.Children.Add(_overlay);
        Content = root;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Finish(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };

        Loaded += (_, _) =>
        {
            // 覆盖在截图上的窗口不需要系统边框与投影
            Native.EnableNativeFrame(this, withShadow: false);
            Activate();
            Focus();
        };

        // 用户直接关窗（Alt+F4）也要给出结果，避免调用方永远等下去
        Closed += (_, _) => { if (!_done) { _done = true; _tcs.TrySetResult(null); } };
    }

    private Border _sizeBox;

    // ---------- 坐标换算 ----------

    /// <summary>物理屏幕坐标 → 本窗口 DIP 坐标（用于画选框）。</summary>
    private Point PhysToDip(double px, double py)
        => new(px / _dpiX - Left, py / _dpiY - Top);

    /// <summary>当前光标的物理屏幕坐标。</summary>
    private bool TryCursor(out double px, out double py)
    {
        if (Native.GetCursorPos(out var p)) { px = p.X; py = p.Y; return true; }
        px = py = 0;
        return false;
    }

    // ---------- 交互 ----------

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        if (!TryCursor(out var px, out var py)) return;

        _startPhys = new Point(px, py);
        _dragging = true;

        _hint.Visibility = Visibility.Collapsed;
        _sel.Visibility = Visibility.Visible;
        _sizeBox.Visibility = Visibility.Visible;
        foreach (var r in new[] { _dimTop, _dimBottom, _dimLeft, _dimRight })
            r.Visibility = Visibility.Visible;

        UpdateSelection(px, py);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!TryCursor(out var px, out var py)) return;
        UpdateSelection(px, py);
    }

    /// <summary>重画选框、四周遮罩与尺寸提示。</summary>
    private void UpdateSelection(double curX, double curY)
    {
        // 物理像素矩形（相对虚拟桌面左上角），并夹到屏幕内
        double l = Math.Min(_startPhys.X, curX), r = Math.Max(_startPhys.X, curX);
        double t = Math.Min(_startPhys.Y, curY), b = Math.Max(_startPhys.Y, curY);

        l = Math.Max(l, _screenRect.X);
        t = Math.Max(t, _screenRect.Y);
        r = Math.Min(r, _screenRect.X + _screenRect.Width);
        b = Math.Min(b, _screenRect.Y + _screenRect.Height);

        var p1 = PhysToDip(l, t);
        var p2 = PhysToDip(r, b);

        double w = Math.Max(0, p2.X - p1.X);
        double h = Math.Max(0, p2.Y - p1.Y);

        Canvas.SetLeft(_sel, p1.X);
        Canvas.SetTop(_sel, p1.Y);
        _sel.Width = w;
        _sel.Height = h;

        double fullW = ActualWidth > 0 ? ActualWidth : Width;
        double fullH = ActualHeight > 0 ? ActualHeight : Height;

        // 四周遮罩：只有选区外变暗，选区内保持原样
        SetRect(_dimTop, 0, 0, fullW, Math.Max(0, p1.Y));
        SetRect(_dimBottom, 0, p1.Y + h, fullW, Math.Max(0, fullH - (p1.Y + h)));
        SetRect(_dimLeft, 0, p1.Y, Math.Max(0, p1.X), h);
        SetRect(_dimRight, p1.X + w, p1.Y, Math.Max(0, fullW - (p1.X + w)), h);

        // 尺寸提示跟随选区右下角
        int pw = (int)Math.Round(r - l), ph = (int)Math.Round(b - t);
        _sizeLabel.Text = $"{pw} × {ph}";
        Canvas.SetLeft(_sizeBox, Math.Min(p1.X + w + 8, fullW - 90));
        Canvas.SetTop(_sizeBox, Math.Min(Math.Max(0, p1.Y + h + 8), fullH - 30));
    }

    /// <summary>设置一个遮罩矩形的位置与尺寸（Canvas 绝对布局）。</summary>
    private static void SetRect(System.Windows.Shapes.Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        if (!TryCursor(out var curX, out var curY)) { Finish(null); return; }

        double l = Math.Max(Math.Min(_startPhys.X, curX), _screenRect.X);
        double t = Math.Max(Math.Min(_startPhys.Y, curY), _screenRect.Y);
        double r = Math.Min(Math.Max(_startPhys.X, curX), _screenRect.X + _screenRect.Width);
        double b = Math.Min(Math.Max(_startPhys.Y, curY), _screenRect.Y + _screenRect.Height);

        int x = (int)Math.Round(l) - _screenRect.X;
        int y = (int)Math.Round(t) - _screenRect.Y;
        int w = (int)Math.Round(r) - (int)Math.Round(l);
        int h = (int)Math.Round(b) - (int)Math.Round(t);

        Trace.Log($"框选: 物理 {x},{y} {w}x{h}  (屏幕图 {_screen.PixelWidth}x{_screen.PixelHeight})");

        if (w < 4 || h < 4) { Finish(null); return; }   // 视为误触

        BitmapSource? cropped = null;
        try
        {
            // 已经在手上的图，直接裁切；不再二次抓屏，避免抓到遮罩自己
            x = Math.Clamp(x, 0, Math.Max(0, _screen.PixelWidth - 1));
            y = Math.Clamp(y, 0, Math.Max(0, _screen.PixelHeight - 1));
            w = Math.Clamp(w, 1, _screen.PixelWidth - x);
            h = Math.Clamp(h, 1, _screen.PixelHeight - y);

            var c = new CroppedBitmap(_screen, new Int32Rect(x, y, w, h));
            c.Freeze();
            cropped = c;
            Trace.Log($"裁切完成: {w}x{h}");
        }
        catch (Exception ex)
        {
            Trace.Log("裁切失败: " + ex.Message);
            App.CrashLog("Crop", ex);
        }

        Finish(cropped);
    }

    /// <summary>结束并交出结果（只生效一次）。</summary>
    private void Finish(BitmapSource? result)
    {
        if (_done) return;
        _done = true;

        try { ReleaseMouseCapture(); } catch { }
        _tcs.TrySetResult(result);
        Close();
    }

    // ---------- 入口 ----------

    /// <summary>
    /// 抓屏并让用户框选。返回裁好的位图；取消返回 null。
    /// 全程不阻塞、不嵌套消息循环。
    /// </summary>
    public static async Task<BitmapSource?> PickAsync()
    {
        var rect = Native.VirtualScreenPixels();
        Trace.Log($"PickAsync: 抓屏区域 {rect.X},{rect.Y} {rect.Width}x{rect.Height}");

        var screen = CaptureRegion(rect.X, rect.Y, rect.Width, rect.Height);
        if (screen == null)
        {
            Trace.Log("PickAsync: 抓屏失败");
            return null;
        }
        Trace.Log($"PickAsync: 抓屏成功 {screen.PixelWidth}x{screen.PixelHeight}");

        // 用 Show 而不是 ShowDialog：模态循环在全屏窗口上会死锁
        var snipper = new RegionSnipper(screen, rect);
        snipper.Show();
        return await snipper.Result.ConfigureAwait(true);
    }

    // ---------- GDI 抓屏 ----------

    /// <summary>
    /// 纯 GDI BitBlt 抓屏。
    /// 不走 System.Drawing（省约 10MB 常驻），也不走 VisualBrush 重绘（省一次全屏合成）。
    /// </summary>
    public static BitmapSource? CaptureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            bmp = CreateCompatibleBitmap(screenDc, w, h);
            old = SelectObject(memDc, bmp);

            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT)
                && !BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY))
                return null;

            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch (Exception ex)
        {
            App.CrashLog("BitBlt", ex);
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dd, int x, int y, int w, int h, IntPtr sd, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
