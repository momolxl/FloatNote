using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace FloatNote;

public sealed partial class App : Application
{
    internal static App Instance { get; private set; } = null!;
    private static NoteWindow? _focused;
    /// <summary>取"当前该投递到的窗口"：优先生效焦点，其次任一可见窗口。</summary>
    internal static NoteWindow? FocusedNote
    {
        get
        {
            if (_focused is { } f && Instance._windows.Contains(f) && f.IsVisible) return f;
            return Instance._windows.FirstOrDefault(w => w.IsVisible);
        }
        private set => _focused = value;
    }

    private readonly List<NoteWindow> _windows = new();
    private AppState _state = new();
    private TrayIcon? _tray;
    private HotkeyHost? _hotkeys;
    private Window? _msgWindow;
    private System.Windows.Interop.HwndSource? _msgSource;

    internal IReadOnlyList<NoteWindow> NoteWindows => _windows;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Instance = this;

        // 未捕获异常一律落盘。
        // 没有这层兜底，任何 UI 线程异常都会让进程直接消失（表现为"白屏后卡退"），
        // 而且拿不到任何线索。
        DispatcherUnhandledException += (_, ex) =>
        {
            CrashLog("Dispatcher", ex.Exception);
            ex.Handled = true;                     // 不让整个程序因为一次操作死掉
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            CrashLog("AppDomain", ex.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            CrashLog("Task", ex.Exception);
            ex.SetObserved();
        };

        // 单实例
        if (!SingleInstance.Acquire())
        {
            // 已有实例：唤醒它，而不是静默退出
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        // 收到外部唤醒请求 -> 在 UI 线程把窗口亮出来
        SingleInstance.ActivateRequested += () => Dispatcher.BeginInvoke(new Action(RevealAll));

        Store.Init();
        Trace.Clear();                 // 每次启动重置追踪日志
        _state = Store.Load();

        // 用一个不可见窗口承载热键消息循环
        _msgWindow = new Window
            {
                Width = 0, Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Opacity = 0,
                Left = -32000, Top = -32000,
            };
        _msgWindow.Show();
        _msgWindow.Hide();

        var helper = new System.Windows.Interop.WindowInteropHelper(_msgWindow);
        _msgSource = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);

        SetupHotkeys();
        SetupTray();

        // 恢复上次的窗口
        foreach (var st in _state.Windows.ToList())
            CreateWindow(st);

        // 首次启动给出明确反馈：窗口会闪三下，托盘再补一条气泡。
        // 悬浮工具"启动了却找不到"是最大的体验坑，必须主动告知。
        bool firstRun = _state.Windows.Count <= 1;

        // 即使 --silent 自启，只要一个窗口都没有也必须建一个，
        // 否则程序完全隐身，用户会以为"没启动"。
        if (_windows.Count == 0)
        {
            var w = NewWindow();
            if (HasFlag(e.Args, "--silent"))
            {
                // 自启后窗口保持可见，只是不抢焦点。
                // 之前这里 Hide()，导致开机后桌面上什么都没有，
                // 用户会以为程序没启动——这最容易被当成"坏了"。
                w.ShowActivated = false;
                w.Topmost = true;
                TrayTip("FloatNote 已启动，便签在屏幕右侧");
            }
        }

        if (firstRun)
            AnnounceFirstRun();
    }

    /// <summary>首次启动时把窗口"亮一下"，并在托盘提示怎么找到它。</summary>
    private async void AnnounceFirstRun()
    {
        try
        {
            await Task.Delay(700);           // 等窗口完成首帧
            _windows.FirstOrDefault()?.PulseOpacity();
            TrayTip("已在屏幕右侧显示便签\n左键托盘图标可随时显示/隐藏");
        }
        catch { }
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>把异常写到 %APPDATA%\FloatNote\crash.log，方便定位问题。</summary>
    internal static void CrashLog(string source, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatNote");
            Directory.CreateDirectory(dir);
            var line = $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====
                {ex}
                """;
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch { /* 记日志本身失败就算了，不能因此再抛 */ }
    }

    // ==================== 窗口管理 ====================

    internal NoteWindow NewWindow()
    {
        var st = new NoteWindowState();
        _state.Windows.Add(st);
        var w = CreateWindow(st);
        w.Show();
        w.Activate();
        Persist();
        return w;
    }

    private NoteWindow CreateWindow(NoteWindowState st)
    {
        var w = new NoteWindow(this, st);
        // 不用 w.Opacity（它只压暗，不做真透明），
        // 统一交给原生分层窗口 alpha；窗口 Loaded 后生效。
        // 见 NoteWindow.ApplyOpacity 的说明。
        w.Activated += (_, _) => FocusedNote = w;
        w.IsVisibleChanged += (_, _) => { if (w.IsVisible) FocusedNote = w; };
        _windows.Add(w);
        w.Show();
        return w;
    }

    internal void DeleteWindow(NoteWindow w)
    {
        foreach (var item in w.State.Items.ToList())
            Store.DeleteMedia(item.MediaFile);

        _state.Windows.Remove(w.State);
        _windows.Remove(w);
        if (ReferenceEquals(_focused, w)) _focused = null;
        w.ForceClose();
        Persist();

        if (_windows.Count == 0) TrayTip("便签已全部关闭，可从托盘新建");
    }

    /// <summary>
    /// 无条件下把界面呈现给用户。
    /// 用于：双击 exe 第二次启动、登录后自启、托盘误操作后找不到窗口。
    /// </summary>
    internal void RevealAll()
    {
        if (_windows.Count == 0) { NewWindow(); return; }

        foreach (var w in _windows)
        {
            if (!w.IsVisible) w.Show();
            w.Topmost = true;
            w.Activate();
        }

        // 确保至少有一个真的到前台
        var top = _windows.FirstOrDefault(w => w.IsVisible);
        if (top != null)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(top).Handle;
            Native.ShowAndActivate(h);
        }
    }

    internal void ShowAll()
    {
        if (_windows.Count == 0) { NewWindow(); return; }
        foreach (var w in _windows) w.Show();
    }

    internal void HideAll()
    {
        foreach (var w in _windows) w.Hide();
        Persist();
    }

    // ==================== 截图 ====================

    /// <summary>框选截图并投进指定窗口；没有窗口就新建一个。</summary>
    /// <remarks>
    /// 改成 async void + await：旧版用同步 ShowDialog，在全屏遮罩上会死锁
    /// （表现为点截图后白屏、程序卡死）。
    /// 现在窗口用 Show() 异步弹出，这里 await 结果，不阻塞 UI 线程。
    /// </remarks>
    internal async void CaptureInto(NoteWindow? target)
    {
        Trace.Log($"CaptureInto 进入, target={(target == null ? "null" : target.State.Title)}");

        if (_capturing) { Trace.Log("已在截图中，忽略重复触发"); return; }
        _capturing = true;

        // 截图前把自己全部隐藏，否则会截到自己的窗口
        var visible = _windows.Where(w => w.IsVisible).ToList();
        Trace.Log($"隐藏 {visible.Count} 个窗口");
        foreach (var w in visible) w.Hide();

        BitmapSource? shot = null;
        try
        {
            // 给 DWM 一帧时间完成隐藏，再抓屏，避免抓到自己的残影
            await Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(60);

            Trace.Log("即将抓屏并打开框选");
            shot = await RegionSnipper.PickAsync();
            Trace.Log($"框选返回: {(shot == null ? "null(取消)" : shot.PixelWidth + "x" + shot.PixelHeight)}");
        }
        catch (Exception ex)
        {
            Trace.Log("截图异常: " + ex);
            CrashLog("CaptureInto", ex);
        }
        finally
        {
            // 无论截成功与否都要把窗口放回来，
            // 否则截一张图会让其他便签全部消失。
            foreach (var w in visible) w.Show();
            _capturing = false;
        }

        if (shot == null) return;

        try
        {
            var dest = target ?? _windows.FirstOrDefault() ?? NewWindow();
            dest.Show();
            dest.AddImage(shot);
            dest.Activate();
        }
        catch (Exception ex)
        {
            Trace.Log("投递截图异常: " + ex);
            CrashLog("CaptureInto.Deliver", ex);
        }
    }

    /// <summary>防止连点导致多个遮罩叠加。</summary>
    private bool _capturing;

    /// <summary>截全屏（不框选），热键 Ctrl+Shift+A。</summary>
    internal void CaptureFullScreen()
    {
        var visible = _windows.Where(w => w.IsVisible).ToList();
        foreach (var w in visible) w.Hide();

        BitmapSource? shot = null;
        try
        {
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var r = Native.VirtualScreenPixels();
            shot = RegionSnipper.CaptureRegion(r.X, r.Y, r.Width, r.Height);
        }
        catch { }

        // 全屏抓完立刻还原，避免用户以为窗口丢了
        foreach (var w in visible) w.Show();

        if (shot == null) return;

        var dest = FocusedNote ?? _windows.FirstOrDefault() ?? NewWindow();
        dest.Show();
        dest.AddImage(shot);
        dest.Activate();
    }

    // ==================== 热键 ====================

    private void SetupHotkeys()
    {
        if (_msgWindow == null) return;
        try { _hotkeys = new HotkeyHost(_msgWindow); }
        catch (Exception ex) { Debug.WriteLine("[FloatNote] hotkey host failed: " + ex.Message); return; }

        const int MOD_CS = Native.MOD_CONTROL | Native.MOD_SHIFT;

        // Ctrl+Shift+S：框选截图 -> 当前窗口
        bool ok1 = _hotkeys.TryRegister(1, MOD_CS, 0x53, () => CaptureInto(FocusedNote));

        // Ctrl+Shift+A：全屏截图 -> 当前窗口
        bool ok2 = _hotkeys.TryRegister(2, MOD_CS, 0x41, CaptureFullScreen);

        // Ctrl+Shift+N：新建便签
        bool ok3 = _hotkeys.TryRegister(3, MOD_CS, 0x4E, () => NewWindow());

        // Ctrl+Shift+H：显示/隐藏全部
        bool ok4 = _hotkeys.TryRegister(4, MOD_CS, 0x48, () =>
        {
            if (_windows.Any(w => w.IsVisible)) HideAll(); else ShowAll();
        });

        _hotkeyStatus = (ok1, ok2, ok3, ok4);
    }

    private (bool, bool, bool, bool) _hotkeyStatus;

    internal string HotkeySummary()
    {
        var (a, b, c, d) = _hotkeyStatus;
        var parts = new List<string>();
        parts.Add(a ? "Ctrl+Shift+S 框选截图" : "Ctrl+Shift+S 注册失败(被占用)");
        parts.Add(b ? "Ctrl+Shift+A 全屏截图" : "Ctrl+Shift+A 注册失败");
        parts.Add(c ? "Ctrl+Shift+N 新建便签" : "Ctrl+Shift+N 注册失败");
        parts.Add(d ? "Ctrl+Shift+H 显示/隐藏" : "Ctrl+Shift+H 注册失败");
        return string.Join("\n", parts);
    }

    // ==================== 托盘 ====================

    private void SetupTray()
    {
        if (_msgSource == null) return;
        _tray = new TrayIcon(_msgSource, "FloatNote");
        _tray.LeftClick += () =>
        {
            if (_windows.Any(w => w.IsVisible)) HideAll(); else ShowAll();
        };
        _tray.RightClick += ShowTrayMenu;
    }

    private ContextMenu? _trayMenu;

    private void ShowTrayMenu()
    {
        // 必须先关掉上一个，否则连续右键会叠出多个菜单
        if (_trayMenu != null) { _trayMenu.IsOpen = false; _trayMenu = null; }

        var menu = new ContextMenu { StaysOpen = false };
        _trayMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };

        menu.Items.Add(TrayItem("新建便签窗口  (Ctrl+Shift+N)", () => NewWindow()));
        menu.Items.Add(TrayItem("显示全部  (Ctrl+Shift+H)", () => ShowAll()));
        menu.Items.Add(TrayItem("隐藏全部", () => HideAll()));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem("框选截图  (Ctrl+Shift+S)", () => CaptureInto(FocusedNote)));
        menu.Items.Add(TrayItem("全屏截图  (Ctrl+Shift+A)", CaptureFullScreen));
        menu.Items.Add(new Separator());

        if (_windows.Count > 0)
        {
            var listRoot = new MenuItem { Header = $"窗口列表 ({_windows.Count})" };
            foreach (var w in _windows.ToList())
            {
                var mi = new MenuItem { Header = string.IsNullOrWhiteSpace(w.State.Title) ? "便签" : w.State.Title };
                var target = w;
                mi.Click += (_, _) => { target.Show(); target.Activate(); };
                listRoot.Items.Add(mi);
            }
            menu.Items.Add(listRoot);
            menu.Items.Add(new Separator());
        }

        var startup = new MenuItem
        {
            Header = "开机自动启动",
            IsCheckable = true,
            IsChecked = StartupManager.IsEnabled(),
        };
        startup.Click += (_, _) =>
        {
            bool want = !StartupManager.IsEnabled();
            StartupManager.Set(want);
            _state.StartWithWindows = want;
            Persist();
        };
        menu.Items.Add(startup);

        menu.Items.Add(TrayItem("清理未引用的图片", () =>
        {
            int n = Store.GcMedia(_state);
            TrayTip(n > 0 ? $"已清理 {n} 张无引用图片" : "没有需要清理的图片");
        }));

        menu.Items.Add(TrayItem("快捷键说明", () => TrayTip(HotkeySummary())));
        menu.Items.Add(TrayItem("打开数据目录", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatNote"),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { TrayTip("打开失败：" + ex.Message); }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem("退出 FloatNote", ExitApp));

        menu.IsOpen = true;
    }

    private static MenuItem TrayItem(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void TrayTip(string msg) => _tray?.ShowBalloon("FloatNote", msg);

    // ==================== 持久化 ====================

    internal void Persist() => Store.SaveNow(_state);

    internal void PersistLater() => Store.SaveDebounced(_state);

    // ==================== 退出 ====================

    internal void ExitApp()
    {
        try { Persist(); } catch { }
        _hotkeys?.Dispose();
        _tray?.Dispose();
        foreach (var w in _windows.ToList()) w.ForceClose();
        SingleInstance.Release();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Persist();
        base.OnSessionEnding(e);
    }
}
