using System;
using Avalonia.Controls.Documents;

namespace HtmlML;

public class br : LineBreak
{
    protected override Type StyleKeyOverride => typeof(LineBreak);
}
