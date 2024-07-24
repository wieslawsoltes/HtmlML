using System;
using Avalonia.Controls;
using Avalonia.Layout;

namespace HtmlML;

public class div : DockPanel
{
    protected override Type StyleKeyOverride => typeof(StackPanel);

    public div()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(this, Dock.Top);
    }
}
