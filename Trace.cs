using System;
using System.IO;
using System.Text;

namespace FloatNote;

/// <summary>
/// 轻量追踪日志。写到 %APPDATA%\FloatNote\trace.log。
/// 用于定位"点击后白屏卡退"这类没有异常栈的硬崩溃：
/// 靠"最后一条日志是哪一步"就能判断崩在哪里。
/// </summary>
internal static class Trace
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FloatNote", "trace.log");

    public static void Log(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                // 超过 200KB 就清空，避免无限增长
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 200 * 1024) File.Delete(Path);

                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { }
    }

    public static void Clear()
    {
        try { File.Delete(Path); } catch { }
    }
}
