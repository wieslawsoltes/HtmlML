using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class DomIdentityAttributeTests
{
    [AvaloniaFact]
    public void IdAndNameRemainMutableAfterBodyAndElementAreStyled()
    {
        var root = new CssLayoutPanel { Width = 240, Height = 80 };
        var window = new Window { Width = 240, Height = 80, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var body = HostTestUtilities.GetElement(document.body);
        var element = HostTestUtilities.GetElement(document.createElement("input"));
        element.id = "before-style";
        element.name = "before-name";
        body.appendChild(element);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(element, document.getElementById("before-style"));

        var exception = Record.Exception(() =>
        {
            body.id = "body";
            body.name = "frame-body";
            element.id = "after-style";
            element.name = "after-name";
        });

        Assert.Null(exception);
        Assert.Equal("body", body.getAttribute("id"));
        Assert.Equal("frame-body", body.getAttribute("name"));
        Assert.Equal("after-style", element.id);
        Assert.Equal("after-name", element.name);
        Assert.Null(document.getElementById("before-style"));
        Assert.Same(element, document.getElementById("after-style"));
        Assert.Same(element, document.querySelector("#after-style"));
        Assert.Same(element, document.querySelector("[name='after-name']"));
        Assert.True(element.matches("input#after-style[name='after-name']"));
        Assert.Null((element.Control as StyledElement)?.Name);

        element.removeAttribute("id");
        element.removeAttribute("name");
        Assert.Equal(string.Empty, element.id);
        Assert.Equal(string.Empty, element.name);
        Assert.Null(element.getAttribute("id"));
        Assert.Null(element.getAttribute("name"));
        Assert.Null(document.getElementById("after-style"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NativeControlNameIsCapturedAsInitialIdWithoutBecomingHtmlName()
    {
        var native = new Border { Name = "native-id" };
        var root = new CssLayoutPanel { Width = 240, Height = 80 };
        root.Children.Add(native);
        var window = new Window { Width = 240, Height = 80, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var element = document.WrapControl(native);

        Assert.Equal("native-id", element.id);
        Assert.Equal(string.Empty, element.name);
        Assert.Same(element, document.getElementById("native-id"));

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        element.id = "dom-id";
        element.name = "form-name";

        Assert.Equal("native-id", native.Name);
        Assert.Equal("dom-id", element.id);
        Assert.Equal("form-name", element.name);
        Assert.Null(document.getElementById("native-id"));
        Assert.Same(element, document.getElementById("dom-id"));

        element.removeAttribute("id");
        Assert.Equal(string.Empty, element.id);
        Assert.Equal("native-id", native.Name);
        Assert.Null(document.getElementById("native-id"));
        Assert.Null(document.getElementById("dom-id"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
