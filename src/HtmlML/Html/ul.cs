using Avalonia;
using Avalonia.Controls;

namespace HtmlML;

public class ul : StackPanel
{
    protected override System.Type StyleKeyOverride => typeof(StackPanel);

    public static readonly DirectProperty<ul, string?> idProperty =
        NameProperty.AddOwner<ul>(o => o.Name, (o, v) => o.Name = v);

    public static readonly StyledProperty<string?> classProperty =
        HtmlElementBase.classProperty.AddOwner<ul>();

    public static readonly StyledProperty<string?> styleProperty =
        HtmlElementBase.styleProperty.AddOwner<ul>();

    public static readonly StyledProperty<bool> disabledProperty =
        HtmlElementBase.disabledProperty.AddOwner<ul>();

    public static readonly StyledProperty<string?> titleProperty =
        HtmlElementBase.titleProperty.AddOwner<ul>();

    static ul()
    {
        classProperty.Changed.AddClassHandler<ul>((o, e) => HtmlElementBase.ApplyClasses(o, e.NewValue as string));
        styleProperty.Changed.AddClassHandler<ul>((o, e) => HtmlElementBase.ApplyStyles(o, e.NewValue as string));
        disabledProperty.Changed.AddClassHandler<ul>((o, e) => HtmlElementBase.ApplyDisabled(o, e.NewValue is bool b && b));
        titleProperty.Changed.AddClassHandler<ul>((o, e) => HtmlElementBase.ApplyTitle(o, e.NewValue as string));
    }

    public ul()
    {
        Orientation = Avalonia.Layout.Orientation.Vertical;
        Margin = new Thickness(16, 0, 0, 0);
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

    public bool disabled
    {
        get => GetValue(disabledProperty);
        set => SetValue(disabledProperty, value);
    }

    public string? title
    {
        get => GetValue(titleProperty);
        set => SetValue(titleProperty, value);
    }

    public string? id
    {
        get => Name;
        set => Name = value;
    }
}
