using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace FloatNote;

/// <summary>
/// 原图查看窗口。
///
/// 刻意不使用 ScrollViewer：它的模板里带一个"滚动条交汇角"矩形，
/// 用系统白底绘制，在深色背景上就是一个刺眼的白方块，
/// 而该角落在模板内部，改样式很难彻底去掉。
/// 这里改成自绘：Image + Scale/Translate 变换，Ctrl+滚轮缩放、左键拖动平移。
/// </summary>
public sealed class ImageViewer : Window
{
    private readonly Image _img = new();
    private readonly BitmapSource _source;

    private readonly ScaleTransform _scaleTx = new(1, 1);
    private readonly TranslateTransform _moveTx = new(0, 0);

    private Point _dragStart;
    private bool _dragging;

    private static readonly Brush BarBg = new SolidColorBrush(Color.FromRgb(32, 35, 42));
    private static readonly Brush BarFg = new SolidColorBrush(Color.FromRgb(226, 230, 238));
    private static readonly Brush CanvasBg = new SolidColorBrush(Color.FromRgb(24, 26, 32));

    private ImageViewer(BitmapSource bmp, string caption)
    {
        _source = bmp;

        Title = caption;
        WindowStyle = WindowStyle.None;
        // 允许调整窗口大小：
        // - 拖窗口边缘/角落改变视图大小
        // - 双击标题栏最大化 / 还原
        // - Ctrl+滚轮仍然是"内容缩放"（与窗口大小独立）
        ResizeMode = ResizeMode.CanResize;
        // 最小尺寸按 DIP 设定；200% 缩放下实际像素会是 2 倍
        MinWidth = 260;
        MinHeight = 180;
        Topmost = true;
        ShowInTaskbar = false;
        Background = CanvasBg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;

        var chrome = new WindowChrome
        {
            CaptionHeight = 34,
            // 用 0 而不是 6：非 0 时系统会在窗口四周画边框，
            // 在深色背景上表现为一圈浅色细线（实测顶部 1~2px）。
            // 缩放改由内容层自己贴热区实现，见 SetupResizeGrips。
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, chrome);

        // ---- 窗口尺寸：按图片比例，留出标题栏与提示行 ----
        var wa = SystemParameters.WorkArea;
        double maxW = wa.Width * 0.85;
        double maxH = wa.Height * 0.85 - 80;
        double ratio = (double)bmp.PixelWidth / bmp.PixelHeight;

        double imgW = Math.Min(bmp.PixelWidth * 1.0, maxW);
        double imgH = imgW / ratio;
        if (imgH > maxH) { imgH = maxH; imgW = imgH * ratio; }

        Width = Math.Max(440, imgW + 8);
        Height = Math.Max(320, imgH + 34 + 42);

        // ---- 自绘标题栏 ----
        var titleText = new TextBlock
        {
            Text = caption,
            FontSize = 12.5,
            Foreground = BarFg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var closeBtn = new Button
        {
            Content = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 44,
            Height = 34,
            Foreground = BarFg,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "关闭 (Esc)",
            Template = FlatButtonTemplate(),
        };
        WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
        closeBtn.Click += (_, _) => Close();

        var bar = new Grid { Background = BarBg, Height = 34 };
        bar.Children.Add(titleText);
        bar.Children.Add(closeBtn);
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
        };

        // ---- 图片画布（自绘，无滚动条）----
        _img.Source = bmp;
        _img.Stretch = Stretch.Uniform;
        _img.HorizontalAlignment = HorizontalAlignment.Center;
        _img.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);

        var group = new TransformGroup();
        group.Children.Add(_scaleTx);
        group.Children.Add(_moveTx);
        _img.RenderTransform = group;
        _img.RenderTransformOrigin = new Point(0.5, 0.5);

        var canvas = new Border
        {
            Background = CanvasBg,
            ClipToBounds = true,
            Child = _img,
        };

        // Ctrl+滚轮缩放（以中心为基准）
        canvas.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            double next = Math.Clamp(_scaleTx.ScaleX * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.1, 10);
            _scaleTx.ScaleX = _scaleTx.ScaleY = next;
            e.Handled = true;
        };

        // 左键拖动平移（放大后用来查看局部）
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _dragStart = e.GetPosition(canvas);
            canvas.CaptureMouse();
            canvas.Cursor = Cursors.SizeAll;
        };
        canvas.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetPosition(canvas);
            _moveTx.X += p.X - _dragStart.X;
            _moveTx.Y += p.Y - _dragStart.Y;
            _dragStart = p;
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            canvas.ReleaseMouseCapture();
            canvas.Cursor = Cursors.Arrow;
        };

        var hint = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(148, 226, 230, 238)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 6, 8, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        // 提示按窗口宽度分档，窗口缩小时不靠省略号硬切，
        // 而是换成更短的整句，保证每条信息都完整可读。
        void UpdateHint()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            hint.Text = w >= 620
                ? "拖边缘改大小 · Ctrl+滚轮缩放 · 拖动平移 · Ctrl+S 另存 · Esc 关闭"
                : w >= 440
                    ? "拖边缘改大小 · Ctrl+滚轮缩放 · Esc 关闭"
                    : "拖边缘改大小 · Esc 关闭";
        }
        SizeChanged += (_, _) => UpdateHint();
        UpdateHint();

        var root = new DockPanel { Background = CanvasBg };
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(hint);
        root.Children.Add(canvas);
        var shell = new Grid();
        shell.Children.Add(root);
        AddResizeGrips(shell);
        Content = shell;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SaveAs();
                e.Handled = true;
            }
            else if (e.Key == Key.D0 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _scaleTx.ScaleX = _scaleTx.ScaleY = 1;
                _moveTx.X = _moveTx.Y = 0;
                e.Handled = true;
            }
        };

        // withShadow:false —— 深色窗口如果扩 1px DWM 边框，
        // 那条边框会用系统浅色绘制，在顶部显出一条白线。
        Loaded += (_, _) => Native.EnableNativeFrame(this, withShadow: false);
    }

    // ==================== 边缘缩放热区 ====================

    private bool _resizing;
    private string _dir = "";
    private Point _dragOrigin;
    private double _w0, _h0, _l0, _t0;

    /// <summary>
    /// 在窗口四周贴透明热区，拖动即可改尺寸。
    /// 之所以不用 WindowChrome 的 ResizeBorderThickness：
    /// 那会让系统在深色窗口外圈画一条浅色边框线。
    /// </summary>
    private void AddResizeGrips(Grid host)
    {
        foreach (var d in new[] { "L", "R", "T", "B", "TL", "TR", "BL", "BR" })
        {
            var g = new Border
            {
                Background = Brushes.Transparent,
                Tag = d,
                Cursor = d switch
                {
                    "L" or "R" => Cursors.SizeWE,
                    "T" or "B" => Cursors.SizeNS,
                    "TL" or "BR" => Cursors.SizeNWSE,
                    _ => Cursors.SizeNESW,
                },
            };

            switch (d)
            {
                case "L": g.Width = 6; g.HorizontalAlignment = HorizontalAlignment.Left;
                    g.VerticalAlignment = VerticalAlignment.Stretch; break;
                case "R": g.Width = 6; g.HorizontalAlignment = HorizontalAlignment.Right;
                    g.VerticalAlignment = VerticalAlignment.Stretch; break;
                case "T": g.Height = 6; g.VerticalAlignment = VerticalAlignment.Top;
                    g.HorizontalAlignment = HorizontalAlignment.Stretch; break;
                case "B": g.Height = 6; g.VerticalAlignment = VerticalAlignment.Bottom;
                    g.HorizontalAlignment = HorizontalAlignment.Stretch; break;
                case "TL": g.Width = 10; g.Height = 10;
                    g.HorizontalAlignment = HorizontalAlignment.Left;
                    g.VerticalAlignment = VerticalAlignment.Top; break;
                case "TR": g.Width = 10; g.Height = 10;
                    g.HorizontalAlignment = HorizontalAlignment.Right;
                    g.VerticalAlignment = VerticalAlignment.Top; break;
                case "BL": g.Width = 10; g.Height = 10;
                    g.HorizontalAlignment = HorizontalAlignment.Left;
                    g.VerticalAlignment = VerticalAlignment.Bottom; break;
                case "BR": g.Width = 10; g.Height = 10;
                    g.HorizontalAlignment = HorizontalAlignment.Right;
                    g.VerticalAlignment = VerticalAlignment.Bottom; break;
            }

            g.MouseLeftButtonDown += GripDown;
            g.MouseMove += GripMove;
            g.MouseLeftButtonUp += GripUp;
            host.Children.Add(g);
        }
    }

    private void GripDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border g || g.Tag is not string d) return;
        _resizing = true;
        _dir = d;
        _dragOrigin = PointToScreen(e.GetPosition(this));
        _w0 = ActualWidth; _h0 = ActualHeight; _l0 = Left; _t0 = Top;
        g.CaptureMouse();
        e.Handled = true;
    }

    private void GripMove(object sender, MouseEventArgs e)
    {
        if (!_resizing) return;
        var p = PointToScreen(e.GetPosition(this));
        double dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;

        double w = _w0, h = _h0, l = _l0, t = _t0;
        if (_dir.Contains('R')) w = Math.Max(MinWidth, _w0 + dx);
        if (_dir.Contains('L')) { w = Math.Max(MinWidth, _w0 - dx); l = _l0 + (_w0 - w); }
        if (_dir.Contains('B')) h = Math.Max(MinHeight, _h0 + dy);
        if (_dir.Contains('T')) { h = Math.Max(MinHeight, _h0 - dy); t = _t0 + (_h0 - h); }

        Width = w; Height = h; Left = l; Top = t;
    }

    private void GripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        Mouse.Capture(null);
    }

    private static ControlTemplate FlatButtonTemplate()
    {
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);

        var t = new ControlTemplate(typeof(Button)) { VisualTree = bd };

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Button.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(196, 43, 28))));
        t.Triggers.Add(hover);

        return t;
    }

    private void SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存图片",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            FileName = $"FloatNote_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            BitmapEncoder enc = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".jpg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_source));
            using var fs = System.IO.File.Create(dlg.FileName);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, "FloatNote",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void Show(Window? owner, BitmapSource bmp)
    {
        var v = new ImageViewer(bmp, "查看图片");
        if (owner != null) v.Owner = owner;
        v.Show();
        v.Activate();
    }
}
