using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FloatNote;

public enum NoteItemKind { Text, Image }

/// <summary>便签里的一条内容。图片只存文件名，位图按需解码，避免常驻内存。</summary>
public sealed class NoteItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NoteItemKind Kind { get; set; }
    public string Text { get; set; } = "";

    /// <summary>媒体目录内的相对文件名；为空表示无图。</summary>
    public string? MediaFile { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    private BitmapSource? _thumb;
    [JsonIgnore]
    public BitmapSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    [JsonIgnore] public string TimeLabel => CreatedAt.ToString("HH:mm");
    [JsonIgnore] public bool IsImage => Kind == NoteItemKind.Image;
    [JsonIgnore] public bool IsText => Kind == NoteItemKind.Text;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>一个独立悬浮窗的状态。</summary>
public sealed class NoteWindowState : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "便签";
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 260;
    public bool Collapsed { get; set; }
    public double Opacity { get; set; } = 0.97;
    public string Accent { get; set; } = "#2F6FEB";
    public List<NoteItem> Items { get; set; } = new();

    [JsonIgnore] public int Count => Items.Count;

    /// <summary>强调色画刷。XAML 直接绑定它，改色只需 Raise 一次。</summary>
    [JsonIgnore]
    public System.Windows.Media.Brush AccentBrush
    {
        get
        {
            try
            {
                var c = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(Accent);
                var b = new System.Windows.Media.SolidColorBrush(c);
                b.Freeze();          // 冻结后可跨线程共享，渲染更省
                return b;
            }
            catch { return System.Windows.Media.Brushes.SteelBlue; }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class AppState
{
    public List<NoteWindowState> Windows { get; set; } = new();
    public bool StartWithWindows { get; set; }
}

/// <summary>
/// 磁盘持久化。数据放在 %APPDATA%\FloatNote：
///   state.json  —— 只存文字与图片文件名，几 KB
///   media\      —— PNG 截图，不进内存就不占内存
/// </summary>
public static class Store
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatNote");

    public static string MediaDir { get; } = Path.Combine(Root, "media");
    private static string StatePath => Path.Combine(Root, "state.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static System.Timers.Timer? _debounce;

    public static void Init()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MediaDir);
    }

    public static AppState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new AppState();
            var txt = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<AppState>(txt, JsonOpts) ?? new AppState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[FloatNote] load failed: " + ex.Message);
            // 坏档不静默丢弃：改名留证据
            try { File.Move(StatePath, StatePath + ".bak", true); } catch { }
            return new AppState();
        }
    }

    /// <summary>合并写入，400ms 防抖——拖拽窗口时不会反复落盘。</summary>
    public static void SaveDebounced(AppState state)
    {
        _debounce ??= new System.Timers.Timer(400) { AutoReset = false };
        _debounce.Elapsed -= OnTick;
        _debounce.Elapsed += OnTick;
        _stateRef = state;
        _debounce.Stop();
        _debounce.Start();
    }

    private static AppState? _stateRef;

    private static void OnTick(object? s, System.Timers.ElapsedEventArgs e)
        => SaveNow(_stateRef ?? new AppState());

    public static void SaveNow(AppState state)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmp, StatePath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[FloatNote] save failed: " + ex.Message);
        }
    }

    /// <summary>把位图存成 PNG，返回文件名。</summary>
    public static string SaveImage(BitmapSource bmp)
    {
        Directory.CreateDirectory(MediaDir);
        var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.png";
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(MediaDir, name));
        enc.Save(fs);
        return name;
    }

    /// <summary>
    /// 按需解码：DecodePixelWidth 让 WPF 直接解出缩略尺寸。
    ///
    /// 默认 640 而不是 1400：便签缩略图显示宽度约 300 DIP，
    /// 200% 缩放下也只需 600 物理像素。按 1400 解码会多占 4~5 倍内存
    /// （实测 10 张图时 Private Bytes 明显偏高）。
    /// 点开看大图时会用 decodeWidth=0 重新解原图，不影响清晰度。
    /// </summary>
    public static BitmapSource? LoadImage(string? file, int decodeWidth = 640)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var path = Path.Combine(MediaDir, file);
        if (!File.Exists(path)) return null;

        try
        {
            using var fs = File.OpenRead(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读入并释放文件句柄
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[FloatNote] image load failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>媒体文件的绝对路径；文件名非法或为空时返回 null。</summary>
    public static string? MediaPath(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        // 防御路径穿越：只接受纯文件名
        if (file.Contains(Path.DirectorySeparatorChar) || file.Contains('/') || file.Contains(".."))
            return null;
        return Path.Combine(MediaDir, file);
    }

    public static void DeleteMedia(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        try
        {
            var p = Path.Combine(MediaDir, file);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }

    /// <summary>清理不再被任何窗口引用的图片。</summary>
    public static int GcMedia(AppState state)
    {
        try
        {
            var used = state.Windows.SelectMany(w => w.Items)
                                    .Select(i => i.MediaFile)
                                    .Where(f => !string.IsNullOrEmpty(f))
                                    .Select(f => f!)
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int n = 0;
            foreach (var f in Directory.EnumerateFiles(MediaDir, "*.png"))
            {
                if (!used.Contains(Path.GetFileName(f)))
                {
                    try { File.Delete(f); n++; } catch { }
                }
            }
            return n;
        }
        catch { return 0; }
    }
}
