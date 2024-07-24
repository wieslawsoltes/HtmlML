using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace HtmlML;

public class p : TextBlock
{
    protected override Type StyleKeyOverride => typeof(p);
    
    public p()
    {
        DockPanel.SetDock(this, Dock.Top);
        FontWeight = FontWeight.Normal;
        FontSize = 16;
    }
}
