using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace HtmlML;

public class code : Span
{
    public static readonly StyledProperty<string?> classProperty =
        HtmlElementBase.classProperty.AddOwner<code>();

    public static readonly StyledProperty<string?> styleProperty =
        HtmlElementBase.styleProperty.AddOwner<code>();

    static code()
    {
        classProperty.Changed.AddClassHandler<code>((o, e) => HtmlElementBase.ApplyClasses(o, e.NewValue as string));
        styleProperty.Changed.AddClassHandler<code>((o, e) => HtmlElementBase.ApplyStyles(o, e.NewValue as string));
    }

    public code()
    {
        FontFamily = new FontFamily("monospace");
    }

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
