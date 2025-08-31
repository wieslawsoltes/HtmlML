using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HtmlML;

public class li : DockPanel
{
    protected override Type StyleKeyOverride => typeof(DockPanel);

    public static readonly DirectProperty<li, string?> idProperty =
        NameProperty.AddOwner<li>(o => o.Name, (o, v) => o.Name = v);

    public static readonly StyledProperty<string?> classProperty =
        HtmlElementBase.classProperty.AddOwner<li>();

    public static readonly StyledProperty<string?> styleProperty =
        HtmlElementBase.styleProperty.AddOwner<li>();

    public static readonly StyledProperty<bool> disabledProperty =
        HtmlElementBase.disabledProperty.AddOwner<li>();

    public static readonly StyledProperty<string?> titleProperty =
        HtmlElementBase.titleProperty.AddOwner<li>();

    private readonly TextBlock _bullet;

    static li()
    {
        classProperty.Changed.AddClassHandler<li>((o, e) => HtmlElementBase.ApplyClasses(o, e.NewValue as string));
        styleProperty.Changed.AddClassHandler<li>((o, e) => HtmlElementBase.ApplyStyles(o, e.NewValue as string));
        disabledProperty.Changed.AddClassHandler<li>((o, e) => HtmlElementBase.ApplyDisabled(o, e.NewValue is bool b && b));
        titleProperty.Changed.AddClassHandler<li>((o, e) => HtmlElementBase.ApplyTitle(o, e.NewValue as string));
    }

    public li()
    {
        _bullet = new TextBlock
        {
            Text = "•",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };

        SetDock(_bullet, Dock.Left);
        Children.Add(_bullet);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateBullet();
    }

    private void UpdateBullet()
    {
        if (Parent is ol ordered)
        {
            // Determine position among siblings
            var index = 0;
            for (var i = 0; i < ordered.Children.Count; i++)
            {
                if (ReferenceEquals(ordered.Children[i], this))
                {
                    index = i + 1;
                    break;
                }
            }
            _bullet.Text = index.ToString() + ".";
        }
        else
        {
            _bullet.Text = "•";
        }
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
