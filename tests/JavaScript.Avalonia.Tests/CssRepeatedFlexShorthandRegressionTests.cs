using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssRepeatedFlexShorthandRegressionTests
{
    [AvaloniaFact]
    public void LaterFlexNoneKeepsFixedSidebarWidthInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 600, Height = 200 };
        var window = new Window { Width = 600, Height = 200, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { height: 200px; margin: 0; width: 600px; }
            .wrapper { display: flex; height: 200px; width: 600px; }
            .sidebar { display: flex; flex: 1 1 auto; flex: none; width: 200px; }
            .main { flex: 1 1 auto; min-width: 0; }
            """;
        document.head.appendChild(style);
        var wrapper = Append(document, "wrapper");
        var sidebar = Append(document, "sidebar", wrapper);
        var main = Append(document, "main", wrapper);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        AssertComputedAndGeometry(document, sidebar, main);

        var snapshot = AvaloniaCssLayoutProjection.Capture(
            Assert.IsType<CssLayoutPanel>(wrapper.Control),
            new Size(600, 200));
        Assert.Equal(200, snapshot.GetBox(sidebar.Control).BorderBox.Width);
        Assert.Equal(400, snapshot.GetBox(main.Control).BorderBox.Width);

        foreach (var panel in new[] { wrapper.Control, sidebar.Control, main.Control }.OfType<CssLayoutPanel>())
        {
            CssLayout.SetNativeLayoutHotPath(panel, true);
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
        Dispatcher.UIThread.RunJobs();
        AssertComputedAndGeometry(document, sidebar, main);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static AvaloniaDomElement Append(
        AvaloniaDomDocument document,
        string className,
        AvaloniaDomElement? parent = null)
    {
        var element = HostTestUtilities.GetElement(document.createElement("div"));
        element.className = className;
        (parent ?? HostTestUtilities.GetElement(document.body)).appendChild(element);
        return element;
    }

    private static void AssertComputedAndGeometry(
        AvaloniaDomDocument document,
        AvaloniaDomElement sidebar,
        AvaloniaDomElement main)
    {
        var computed = document.getComputedStyle(sidebar);
        Assert.Equal("0", computed.getPropertyValue("flex-grow"));
        Assert.Equal("0", computed.getPropertyValue("flex-shrink"));
        Assert.Equal("auto", computed.getPropertyValue("flex-basis"));
        Assert.Equal(200, sidebar.getBoundingClientRect().width);
        Assert.Equal(400, main.getBoundingClientRect().width);
        Assert.Equal(sidebar.getBoundingClientRect().right, main.getBoundingClientRect().left);
    }
}
