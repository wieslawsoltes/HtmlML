using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Metadata;

namespace HtmlML;

public class em : Span
{
    public static readonly StyledProperty<string?> classProperty =
        HtmlElementBase.classProperty.AddOwner<em>();

    public static readonly StyledProperty<string?> styleProperty =
        HtmlElementBase.styleProperty.AddOwner<em>();

    static em()
    {
        classProperty.Changed.AddClassHandler<em>((o, e) => HtmlElementBase.ApplyClasses(o, e.NewValue as string));
        styleProperty.Changed.AddClassHandler<em>((o, e) => HtmlElementBase.ApplyStyles(o, e.NewValue as string));
    }

    public em()
    {
        FontStyle = Avalonia.Media.FontStyle.Italic;
    }

    [Content]
    public InlineCollection content => Inlines;

    public string? @class
    {
        get => GetValue(classProperty);
        set => SetValue(classProperty, value);
    }

    public string? style
    {
        get => GetValue(styleProperty);
        set => SetValue(styleProperty, value);
    }
}
