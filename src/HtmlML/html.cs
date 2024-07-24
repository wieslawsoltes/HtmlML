using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Metadata;

namespace HtmlML;

public class html : Window
{
    protected override Type StyleKeyOverride => typeof(Window);

    public static readonly StyledProperty<content> contentProperty =
        AvaloniaProperty.Register<title, content>(nameof(content));
    
    public static readonly DirectProperty<html, string?> nameProperty =
        Window.NameProperty.AddOwner<html>(o => o.Name, (o, v) => o.Name = v);

    public static readonly StyledProperty<double> widthProperty =
        Window.WidthProperty.AddOwner<html>();

    public static readonly StyledProperty<double> heightProperty =
        Window.HeightProperty.AddOwner<html>();

    public static readonly StyledProperty<string?> titleProperty =
        Window.TitleProperty.AddOwner<html>();
    
    [Content]
    public content content { get; } = new content();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        Content = content[1];

        Title = ((content[0] as head).content[0] as title).Text;
    }

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
    
    public string? title
    {
        get { return GetValue(titleProperty); }
        set { SetValue(titleProperty, value); }
    }
}
