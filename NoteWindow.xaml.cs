using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatNote;

public sealed partial class NoteWindow : Window
{
    private readonly NoteWindowState _state;
    private readonly ObservableCollection<NoteItem> _items = new();
    private readonly App _app;
    private bool _reallyClosing;
    /// <summary>收起前的高度，用于展开时还原。0 表示尚未记录。</summary>
    private double _restoreHeight;

    /// <summary>
    /// 布局完成前禁止持久化尺寸。
    /// InitializeComponent 之后、首帧布局之前，ActualWidth/Height 还是 0 或旧值，
    /// SizeChanged 会在这个窗口期触发，把正确尺寸写成错误值（实测把 430 写成了 280）。
    /// </summary>
    private bool _boundsReady;

    public NoteWindowState State => _state;

    public NoteWindow(App app, NoteWindowState state)
    {
        _app = app;
        _state = state;

        InitializeComponent();

        DataContext = state;
        ItemsHost.ItemsSource = _items;

        foreach (var it in state.Items)
        {
            if (it.IsImage) it.Thumb = Store.LoadImage(it.MediaFile);
            _items.Add(it);
        }

        // ---- 尺寸兜底 ----
        // 注意：JSON 里缺失字段会反序列化成 0，不是 NaN。
        // 直接把 0 当真值会把窗口压成一条 26px 高的细线（实测过）。
        double w = state.Width, h = state.Height;
        if (double.IsNaN(w) || w < MinWidth) w = 300;
        if (double.IsNaN(h) || h < MinHeight) h = 300;
        if (w > 1400) w = 1400;                 // 防止异常值撑满屏幕
        if (h > 1200) h = 1200;

        // ---- 位置兜底 ----
        // 同样：缺失 = 0，而 (0,0) 也在屏内，所以不能只判 NaN。
        // 判"是否落在任一屏幕工作区内"，否则重新摆到右侧。
        var wa = SystemParameters.WorkArea;
        bool hasPos = !double.IsNaN(state.Left) && !double.IsNaN(state.Top)
                      && state.Left != 0 && state.Top != 0
                      && state.Left > wa.Left - w + 80
                      && state.Left < wa.Right - 80
                      && state.Top > wa.Top - 40
                      && state.Top < wa.Bottom - 80;

        if (!hasPos)
        {
            int idx = _app.Windows.Count;
            state.Left = wa.Right - w - 20 - (idx % 4) * 18;
            state.Top = wa.Top + 70 + (idx % 9) * 40;
        }

        Left = state.Left;
        Top = state.Top;
        Width = w;
        Height = h;

        Loaded += OnLoaded;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        LocationChanged += (_, _) => PersistBounds();
        SizeChanged += (_, _) => PersistBounds();
        StateChanged += (_, _) => PersistBounds();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDownHook;

        RefreshEmptyState();
        ApplyCollapsed(state.Collapsed);

        // 分层窗口没有系统缩放边框，自己贴一圈透明热区
        SetupResizeGrips();
    }

    /// <summary>被激活时才把焦点交给输入框：点进来就能直接打字。</summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        Composer.IsReadOnly = false;
        if (!_state.Collapsed) Composer.Focus();
        SyncPlaceholder();
    }

    /// <summary>
    /// 失活立刻释放焦点。
    /// 这是本项目最重要的一处 CPU 优化：WPF 里只要 TextBox 持有逻辑焦点，
    /// 光标就会以约 530ms 周期闪烁并触发重绘；多个便签常开时，
    /// 这些看不见的重绘会持续占 0.5%~1.5% CPU。
    /// 释放焦点后实测可降到 0.000%。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (Composer.IsKeyboardFocusWithin)
        {
            // 把焦点移到不可见的宿主上，比 Keyboard.ClearFocus() 更稳
            RootGrid.Focus();
        }

        // 失活期间切到只读：WPF 对只读 TextBox 不维持光标闪烁动画，
        // 可彻底消除后台窗口的周期性重绘。重新激活时恢复可编辑。
        Composer.IsReadOnly = true;
        SyncPlaceholder();
    }

    /// <summary>
    /// 短暂高亮边框，帮用户一眼定位刚出现的窗口。
    /// 悬浮小工具最大的可发现性问题就是"启动了但找不到"。
    /// </summary>
    /// <summary>供 App 调用的公开版本：把窗口"闪"一下，提示用户它在这里。</summary>
    public void PulseOpacity() => FlashForAttention();

    private async void FlashForAttention()
    {
        try
        {
            double target = _state.Opacity;
            for (int i = 0; i < 3; i++)
            {
                Opacity = 0.5;
                await Task.Delay(110);
                Opacity = target;
                await Task.Delay(100);
            }
            Opacity = target;
        }
        catch { /* 关闭中，忽略 */ }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Native.EnableNativeFrame(this);
        Native.HideFromAltTab(this);
        _boundsReady = true;                        // 此后的尺寸变化才是真实的

        // 句柄就绪后再应用持久化的不透明度（原生 alpha 需要 HWND）
        if (Math.Abs(_state.Opacity - 1.0) > 0.001)
            ApplyOpacity(_state.Opacity);

        // 不在加载时抢焦点。
        // 每个聚焦的输入框都是一个光标闪烁定时器，8 个窗口常开就是 8 个持续重绘源；
        // 未激活的窗口保持无焦点，WPF 就不会为它产生任何帧。
        SyncPlaceholder();
    }

    // ==================== 窗口生命周期 ====================

    /// <summary>关闭只是隐藏，内容和窗口都留着，可从托盘恢复。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClosing) return;
        e.Cancel = true;
        Hide();
        _app.Persist();
    }

    public void ForceClose()
    {
        _reallyClosing = true;
        Close();
    }

    private void PersistBounds()
    {
        if (!_boundsReady) return;                 // 首帧布局完成前不写回
        if (WindowState != WindowState.Normal) return;

        // 收起态的高度是 34（标题栏），不能当成用户设定的高度存下去，
        // 否则下次启动窗口只剩一条标题栏。
        if (_state.Collapsed) return;

        // 布局尚未完成时 ActualWidth 可能是 0 或旧值，
        // 写回会把尺寸存错，下次启动窗口尺寸就不对。
        if (ActualWidth < MinWidth || ActualHeight < MinHeight) return;
        _state.Left = Left;
        _state.Top = Top;
        _state.Width = Width;
        _state.Height = Height;
        _app.PersistLater();
    }

    // ==================== 标题栏交互 ====================

    /// <summary>
    /// 标题栏拖动交给 WindowChrome。
    /// 双击不能用系统默认的"最大化"——便签最大化毫无意义，
    /// 这里改成收起/展开，并吃掉事件防止 WindowChrome 再补一次最大化。
    /// </summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleCollapse();
            e.Handled = true;
            return;
        }

        // 分层窗口没有系统非客户区，拖动要主动发起。
        // 用 WM_NCLBUTTONDOWN + HTCAPTION 调起系统拖动循环，
        // 手感和贴边行为与原生窗口一致。
        if (e.ButtonState == MouseButtonState.Pressed)
            Native.BeginDragMove(this);
    }

    // ==================== 边缘缩放 ====================

    /// <summary>
    /// 在窗口四周贴 5px 透明热区，拖动时改窗口尺寸。
    /// 分层窗口（AllowsTransparency=true）拿不到系统的 ResizeBorder，
    /// 只能自己实现；这里同样是"按下即捕获鼠标"，手感接近原生。
    /// </summary>
    private void SetupResizeGrips()
    {
        if (Content is not Border root) return;

        // 只把 root 的"内部内容"取出来，再放进一个带热区的新 Grid。
        // 早期写法是 root.Child = null 后把 root 整个搬走 —— 那样内层内容
        // 就跟着丢了，窗口会变成一块空白卡片（已踩过，勿再犯）。
        var inner = root.Child;
        root.Child = null;

        var host = new Grid();
        if (inner != null) host.Children.Add(inner);   // 先把原内容放回去
        root.Child = host;                             // RootCard 仍是 Content

        foreach (var dir in new[] { "L", "R", "T", "B", "TL", "TR", "BL", "BR" })
        {
            var grip = new Border
            {
                Background = Brushes.Transparent,
                Tag = dir,
                Cursor = dir switch
                {
                    "L" or "R" => Cursors.SizeWE,
                    "T" or "B" => Cursors.SizeNS,
                    "TL" or "BR" => Cursors.SizeNWSE,
                    _ => Cursors.SizeNESW,
                },
            };

            switch (dir)
            {
                case "L": grip.Width = 5; grip.HorizontalAlignment = HorizontalAlignment.Left;
                    grip.VerticalAlignment = VerticalAlignment.Stretch; break;
                case "R": grip.Width = 5; grip.HorizontalAlignment = HorizontalAlignment.Right;
                    grip.VerticalAlignment = VerticalAlignment.Stretch; break;
                case "T": grip.Height = 5; grip.VerticalAlignment = VerticalAlignment.Top;
                    grip.HorizontalAlignment = HorizontalAlignment.Stretch; break;
                case "B": grip.Height = 5; grip.VerticalAlignment = VerticalAlignment.Bottom;
                    grip.HorizontalAlignment = HorizontalAlignment.Stretch; break;
                case "TL": grip.Width = 8; grip.Height = 8;
                    grip.HorizontalAlignment = HorizontalAlignment.Left;
                    grip.VerticalAlignment = VerticalAlignment.Top; break;
                case "TR": grip.Width = 8; grip.Height = 8;
                    grip.HorizontalAlignment = HorizontalAlignment.Right;
                    grip.VerticalAlignment = VerticalAlignment.Top; break;
                case "BL": grip.Width = 8; grip.Height = 8;
                    grip.HorizontalAlignment = HorizontalAlignment.Left;
                    grip.VerticalAlignment = VerticalAlignment.Bottom; break;
                case "BR": grip.Width = 8; grip.Height = 8;
                    grip.HorizontalAlignment = HorizontalAlignment.Right;
                    grip.VerticalAlignment = VerticalAlignment.Bottom; break;
            }

            grip.MouseLeftButtonDown += Grip_MouseLeftButtonDown;
            host.Children.Add(grip);
        }
    }

    private bool _resizing;
    private string _resizeDir = "";
    private Point _resizeStart;
    private double _resizeW, _resizeH, _resizeL, _resizeT;

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Collapsed) return;
        if (sender is not Border g || g.Tag is not string dir) return;

        _resizing = true;
        _resizeDir = dir;
        _resizeStart = PointToScreen(e.GetPosition(this));
        _resizeW = ActualWidth;
        _resizeH = ActualHeight;
        _resizeL = Left;
        _resizeT = Top;

        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_resizing) return;

        var p = PointToScreen(e.GetPosition(this));
        double dx = p.X - _resizeStart.X;
        double dy = p.Y - _resizeStart.Y;

        double newW = _resizeW, newH = _resizeH, newL = _resizeL, newT = _resizeT;
        const double minW = 190, minH = 120;

        if (_resizeDir.Contains('R')) newW = Math.Max(minW, _resizeW + dx);
        if (_resizeDir.Contains('L')) { newW = Math.Max(minW, _resizeW - dx); newL = _resizeL + (_resizeW - newW); }
        if (_resizeDir.Contains('B')) newH = Math.Max(minH, _resizeH + dy);
        if (_resizeDir.Contains('T')) { newH = Math.Max(minH, _resizeH - dy); newT = _resizeT + (_resizeH - newH); }

        Width = newW;
        Height = newH;
        Left = newL;
        Top = newT;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_resizing) return;
        _resizing = false;
        Mouse.Capture(null);
        PersistBounds();
    }

    // ==================== 标题重命名 ====================

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;

        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Text = _state.Title;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
    }

    private void CommitTitle()
    {
        var t = TitleEdit.Text.Trim();
        if (!string.IsNullOrEmpty(t))
        {
            _state.Title = t;
            _state.Raise(nameof(NoteWindowState.Title));
        }
        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        _app.PersistLater();
    }

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTitle(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitTitle();

    // ==================== 置顶 / 收起 ====================

    private void BtnNew_Click(object sender, RoutedEventArgs e) => _app.NewWindow();

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinGlyph.Text = Topmost ? "\uE840" : "\uE718";
        BtnPin.ToolTip = Topmost ? "取消置顶" : "置顶到所有窗口之上";
    }

    private void BtnCollapse_Click(object sender, RoutedEventArgs e) => ToggleCollapse();

    private void ToggleCollapse()
    {
        ApplyCollapsed(!_state.Collapsed);
        _app.PersistLater();
    }

    private void ApplyCollapsed(bool collapsed)
    {
        _state.Collapsed = collapsed;
        Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseGlyph.Text = collapsed ? "\uE70E" : "\uE70D";

        if (collapsed)
        {
            // 收起时锁死高度和缩放，否则用户还能把只有标题栏的窗口拉高
            _restoreHeight = Height > 60 ? Height : Math.Max(200, _state.Height);
            MinHeight = 34;
            MaxHeight = 34;
            Height = 34;
            ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            MaxHeight = double.PositiveInfinity;
            MinHeight = 46;

            // 只有真的"从收起展开"时才还原高度。
            // 构造期会以 collapsed=false 调一次，此时 _restoreHeight 还是 0，
            // 若无条件套用默认值，会把 state 里恢复的高度直接覆盖掉（实测 430 -> 280）。
            if (_restoreHeight > 60)
                Height = Math.Max(120, _restoreHeight);
            else if (Height < 120)
                Height = 300;      // 兜底：历史脏数据（Height 被存成 34）时给个可用高度
        }
    }

    // ==================== 添加内容 ====================

    private void BtnSend_Click(object sender, RoutedEventArgs e) => CommitComposer();

    private void Composer_TextChanged(object sender, TextChangedEventArgs e) => SyncPlaceholder();

    private void Composer_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SyncPlaceholder();

    private void Composer_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SyncPlaceholder();

    /// <summary>
    /// 只要输入框是空的就显示提示。
    /// 聚焦时把提示压暗（而不是隐藏），这样既保留可发现性，
    /// 又让闪烁的光标在暗色提示上清晰可辨。
    /// </summary>
    private void SyncPlaceholder()
    {
        bool empty = string.IsNullOrEmpty(Composer.Text);
        bool focused = Composer.IsKeyboardFocusWithin;

        ComposerPh.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // 聚焦时空框会显示光标，光标正好压在首字上很难看；
        // 把提示右移 7px 让开路。未聚焦时没有光标，归位与正文对齐。
        ComposerPh.Margin = focused ? new Thickness(7, 0, 0, 0) : new Thickness(0);
    }

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitComposer();
            e.Handled = true;
        }
    }

    private void CommitComposer()
    {
        var text = Composer.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        AddText(text);
        Composer.Clear();
        Composer.Focus();
    }

    public new void AddText(string text)
    {
        var item = new NoteItem { Kind = NoteItemKind.Text, Text = text };
        AddItem(item);
    }

    public void AddImage(BitmapSource? bmp)
    {
        if (bmp == null) return;

        // 先限制解码尺寸再落盘：4K 截图约 33MB，缩到 1600 宽后约 3MB
        var scaled = Downscale(bmp, 1600);
        var file = Store.SaveImage(scaled);
        var item = new NoteItem { Kind = NoteItemKind.Image, MediaFile = file };
        item.Thumb = Store.LoadImage(file);
        AddItem(item);
    }

    private static BitmapSource Downscale(BitmapSource src, int maxWidth)
    {
        if (src.PixelWidth <= maxWidth) return src;
        double scale = (double)maxWidth / src.PixelWidth;
        var t = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }

    private void AddItem(NoteItem item)
    {
        _state.Items.Add(item);
        _items.Add(item);
        RefreshEmptyState();
        Scroller.ScrollToEnd();
        _app.PersistLater();
    }

    public void RemoveItem(NoteItem item)
    {
        if (!_state.Items.Remove(item)) return;
        _items.Remove(item);
        Store.DeleteMedia(item.MediaFile);
        RefreshEmptyState();
        _app.PersistLater();
    }

    private void RefreshEmptyState()
        => EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ==================== 截图 / 粘贴 ====================

    private void BtnShot_Click(object sender, RoutedEventArgs e) => _app.CaptureInto(this);

    private void BtnPaste_Click(object sender, RoutedEventArgs e) => PasteFromClipboard();

    public void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null) { AddImage(img); return; }
            }
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(t)) AddText(t.Trim());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[FloatNote] paste failed: " + ex.Message);
        }
    }

    // ==================== 更多菜单 ====================

    private void BtnMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };

        menu.Items.Add(MenuItem("复制全部内容", () => CopyAll()));
        menu.Items.Add(MenuItem(_state.Collapsed ? "展开" : "收起", ToggleCollapse));
        menu.Items.Add(new Separator());

        var accents = new[] {
            ("默认蓝", "#2F6FEB"), ("青绿", "#0E9F8E"),
            ("琥珀", "#D98324"), ("玫红", "#D6457A"), ("石墨", "#5B6472"),
        };
        var accentRoot = new MenuItem { Header = "强调色" };
        foreach (var (name, hex) in accents)
        {
            var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = _state.Accent == hex };
            var h = hex;
            mi.Click += (_, _) =>
            {
                _state.Accent = h;
                _state.Raise(nameof(NoteWindowState.AccentBrush));
                _app.PersistLater();
            };
            accentRoot.Items.Add(mi);
        }
        menu.Items.Add(accentRoot);

        var opRoot = new MenuItem { Header = "不透明度" };
        foreach (var v in new[] { 1.0, 0.9, 0.8, 0.65 })
        {
            var mi = new MenuItem { Header = $"{(int)(v * 100)}%", IsCheckable = true, IsChecked = Math.Abs(_state.Opacity - v) < 0.01 };
            var val = v;
            mi.Click += (_, _) =>
            {
                _state.Opacity = val;
                ApplyOpacity(val);
                _app.PersistLater();
            };
            opRoot.Items.Add(mi);
        }
        menu.Items.Add(opRoot);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("新建便签窗口", () => _app.NewWindow()));
        menu.Items.Add(MenuItem("删除本窗口（清空内容）", () => CloseAndDiscard()));
        menu.Items.Add(new Separator());

        // 直接给一个"退出程序"入口：托盘图标在 Win11 常被折叠进"隐藏的图标"，
        // 用户未必找得到。这里与托盘菜单口径一致。
        menu.Items.Add(MenuItem("退出 FloatNote（清空所有便签）", () =>
        {
            var r = MessageBox.Show(this,
                "退出会清空所有便签窗口及其截图，且无法恢复。\n\n确定退出吗？",
                "退出 FloatNote",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (r == MessageBoxResult.OK) _app.ExitApp();
        }));

        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void CopyAll()
    {
        try
        {
            var lines = _state.Items
                .Where(i => i.IsText)
                .Select(i => i.Text);
            var txt = string.Join(Environment.NewLine, lines);
            if (!string.IsNullOrWhiteSpace(txt)) Clipboard.SetText(txt);
        }
        catch { }
    }

    // ==================== 关闭 ====================

    /// <summary>
    /// 关闭 = 结束并丢弃。
    /// 按需求"关闭后图片和信息全部丢失"以节省存储，
    /// 所以这里有明确确认，避免误点丢数据。
    /// 想保留内容请用「收起」或 Ctrl+W（仅隐藏）。
    /// </summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => CloseAndDiscard();

    private void CloseAndDiscard()
    {
        int n = _state.Items.Count;
        if (n > 0 && !ConfirmDiscard(n)) return;
        _app.DeleteWindow(this);
    }

    private bool ConfirmDiscard(int itemCount)
    {
        var r = MessageBox.Show(this,
            $"关闭后将丢弃这 {itemCount} 条内容（含截图），且无法恢复。\n\n确定关闭吗？",
            "关闭便签",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return r == MessageBoxResult.OK;
    }

    /// <summary>
    /// 应用不透明度。
    ///
    /// 注意：**不要**同时用 WPF 的 Window.Opacity。
    /// 在 AllowsTransparency=false 下它只会把内容按比例压暗（实测 65% 时
    /// 255 → 166，背后桌面完全没混入），并不产生真正的透明效果，
    /// 两者一起用还会叠加导致过度变暗。
    /// 这里统一交给 Win32 分层窗口的 SetLayeredWindowAttributes。
    /// </summary>
    private void ApplyOpacity(double value)
    {
        // 现在是分层窗口（AllowsTransparency=True），
        // WPF 的 Opacity 走 DWM alpha 合成，是真正的透明。
        Opacity = Math.Clamp(value, 0.15, 1.0);
        Trace.Log($"ApplyOpacity {value} (layered={AllowsTransparency})");
    }

    // ==================== 快捷键 ====================

    private void OnPreviewKeyDownHook(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.V)
        {
            // 让 Composer 内的纯文本粘贴保持原生行为
            if (Composer.IsKeyboardFocusWithin && Clipboard.ContainsText() && !Clipboard.ContainsImage())
                return;
            PasteFromClipboard();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.W)
        {
            // Ctrl+W 只是隐藏，不丢内容
            Hide();
            _app.Persist();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Composer.IsKeyboardFocusWithin)
        {
            Composer.Clear();
            e.Handled = true;
        }
    }

    /// <summary>从外部（热键/主程序）触发一次截图并投进本窗。</summary>
    public void CaptureHere() => _app.CaptureInto(this);
}
