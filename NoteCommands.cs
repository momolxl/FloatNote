using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatNote;

/// <summary>
/// 条目命令。
/// 独立 ResourceDictionary 里无法解析 code-behind 事件处理器，
/// 所以模板里必须用路由命令。
///
/// 关键点：命令绑定注册在 <see cref="NoteWindow"/> 上，而不是 App。
/// 路由命令沿可视树向上冒泡查找 CommandBinding，App 不在可视树上，
/// 注册到 App 会导致 OnOpen / OnDelete 永远不执行（命令"点了没反应"）。
/// </summary>
public static class NoteCommands
{
    public static readonly RoutedCommand DeleteItem = new(nameof(DeleteItem), typeof(NoteCommands));
    public static readonly RoutedCommand OpenImage = new(nameof(OpenImage), typeof(NoteCommands));

    static NoteCommands()
    {
        CommandManager.RegisterClassCommandBinding(
            typeof(NoteWindow),
            new CommandBinding(DeleteItem, OnDelete, OnCanDelete));

        CommandManager.RegisterClassCommandBinding(
            typeof(NoteWindow),
            new CommandBinding(OpenImage, OnOpen, OnCanOpen));
    }

    private static void OnCanDelete(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = e.Parameter is NoteItem;

    private static void OnDelete(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not NoteItem item) return;
        FindWindow(sender)?.RemoveItem(item);
    }

    private static void OnCanOpen(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = e.Parameter is NoteItem { IsImage: true };

    /// <summary>
    /// 在应用内放大查看。
    /// 不走系统照片程序：它冷启动慢、依赖文件关联，体验不可控。
    /// 应用内打开是即时的，且支持 Ctrl+滚轮缩放与另存。
    /// </summary>
    private static void OnOpen(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not NoteItem item || string.IsNullOrEmpty(item.MediaFile)) return;

        var bitmap = Store.LoadImage(item.MediaFile, 0);   // 0 = 原始分辨率
        if (bitmap == null) return;

        ImageViewer.Show(FindWindow(sender), bitmap);
    }

    private static NoteWindow? FindWindow(object sender)
    {
        if (sender is NoteWindow nw) return nw;
        if (sender is DependencyObject d)
            return Window.GetWindow(d) as NoteWindow ?? WalkUp(d);
        return null;
    }

    /// <summary>VisualTreeHelper 没有 GetAncestor，手动向上找。</summary>
    private static NoteWindow? WalkUp(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is NoteWindow nw) return nw;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
