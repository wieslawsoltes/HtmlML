using System;
using Avalonia;
using Avalonia.Controls;

namespace HtmlML;

public class body : DockPanel
{
    protected override Type StyleKeyOverride => typeof(StackPanel);

    public static readonly DirectProperty<body, string?> idProperty =
        NameProperty.AddOwner<body>(o => o.Name, (o, v) => o.Name = v);

    public static readonly StyledProperty<double> widthProperty =
        WidthProperty.AddOwner<body>();

    public static readonly StyledProperty<double> heightProperty =
        HeightProperty.AddOwner<body>();

    public double width
    {
        get { return GetValue(widthProperty); }
        set { SetValue(widthProperty, value); }
    }

    public double height
    {
        get { return GetValue(heightProperty); }
        set { SetValue(heightProperty, value); }
    }
}
