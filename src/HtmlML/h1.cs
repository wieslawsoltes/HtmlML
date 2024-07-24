using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace HtmlML;

public class h1 : TextBlock
{
    protected override Type StyleKeyOverride => typeof(h1);

    public h1()
    {
        FontWeight = FontWeight.Bold;
        FontSize = 32;
    }
}
