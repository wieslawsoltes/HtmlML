using Avalonia;
using Avalonia.Metadata;

namespace HtmlML;

public class head : AvaloniaObject
{
    [Content]
    public content content { get; } = new content();
}
