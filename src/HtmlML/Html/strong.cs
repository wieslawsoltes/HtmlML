using Avalonia;
using Avalonia.Controls.Documents;

namespace HtmlML;

public class strong : Span
{
    public static readonly StyledProperty<string?> classProperty =
        HtmlElementBase.classProperty.AddOwner<strong>();

    public static readonly StyledProperty<string?> styleProperty =
        HtmlElementBase.styleProperty.AddOwner<strong>();

    static strong()
    {
        classProperty.Changed.AddClassHandler<strong>((o, e) => HtmlElementBase.ApplyClasses(o, e.NewValue as string));
        styleProperty.Changed.AddClassHandler<strong>((o, e) => HtmlElementBase.ApplyStyles(o, e.NewValue as string));
    }

    public strong()
    {
        FontWeight = Avalonia.Media.FontWeight.Bold;
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
