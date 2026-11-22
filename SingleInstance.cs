using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace FloatNote;

/// <summary>
/// 单实例。第二次启动不再静默退出——那会让用户以为"双击没反应"，
/// 改为通过命名管道唤醒已有实例，让它把窗口亮出来。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\FloatNote.SingleInstance.9A21F3";
    private const string PipeName = "FloatNote.Activate.9A21F3";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _cts;

    public static bool Acquire()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        if (created)
        {
            StartServer();
            return true;
        }
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>已有实例在跑：通知它显示窗口，然后本进程退出。</summary>
    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(600);                       // 最多等 0.6 秒
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.Write("show");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[FloatNote] signal existing failed: " + ex.Message);

            // 兜底：管道不通时，直接把已有窗口拉到前台
            BringExistingToFront();
        }
    }

    /// <summary>寻找同名的已有进程窗口并激活（管道失败时的兜底）。</summary>
    private static void BringExistingToFront()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("FloatNote"))
            {
                if (p.Id == Environment.ProcessId) continue;
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    Native.ShowAndActivate(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch { }
    }

    /// <summary>后台监听唤醒请求，收到就回调 onActivate（在 UI 线程上执行）。</summary>
    private static void StartServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var r = new StreamReader(server);
                    var cmd = await r.ReadToEndAsync().ConfigureAwait(false);

                    if (cmd.Contains("show"))
                        ActivateRequested?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine("[FloatNote] pipe server: " + ex.Message);
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <summary>外部请求"显示窗口"时触发。App 里挂到 UI 线程。</summary>
    public static event Action? ActivateRequested;

    public static void Release()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;

        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        _mutex = null;
    }
}

/// <summary>开机自启：写 HKCU Run 项，无需管理员权限。</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FloatNote";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>返回当前注册表里登记的命令行，便于诊断"为什么没自启"。</summary>
    public static string? CurrentCommand()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                k.SetValue(ValueName, $"\"{exe}\" --silent");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[FloatNote] startup toggle failed: " + ex.Message);
        }
    }
}
