using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FloatNote;

/// <summary>按条目类型挑模板：文字用紧凑气泡，图片用可点击缩略图。</summary>
public sealed class NoteItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is NoteItem n && n.Kind == NoteItemKind.Image ? ImageTemplate : TextTemplate;
}
