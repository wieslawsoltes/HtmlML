using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssVariableSideBorderRegressionTests
{
    [AvaloniaFact]
    public void LogicalInlineEndVariableBorderCreatesSidebarSeparatorInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 600, Height = 100 };
        var window = new Window { Width = 600, Height = 100, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            :root { --divider-base: #4a4a4a; --divider: var(--divider-base); }
            html, body { height: 100px; margin: 0; width: 600px; }
            .split { display: flex; height: 100px; width: 600px; }
            .sidebar {
                border-inline-end: 1px solid var(--divider);
                flex: none;
                width: 200px;
            }
            .main { flex: 1 1 auto; min-width: 0; }
            """;
        document.head.appendChild(style);
        var split = Append(document, "split");
        var sidebar = Append(document, "sidebar", split);
        var main = Append(document, "main", split);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        AssertSeparator(document, sidebar, main);
        var snapshot = AvaloniaCssLayoutProjection.Capture(
            Assert.IsType<CssLayoutPanel>(split.Control),
            new Size(600, 100));
        Assert.Equal(201, snapshot.GetBox(sidebar.Control).BorderBox.Width);
        Assert.Equal(399, snapshot.GetBox(main.Control).BorderBox.Width);

        foreach (var panel in new[] { split.Control, sidebar.Control, main.Control }.OfType<CssLayoutPanel>())
        {
            CssLayout.SetNativeLayoutHotPath(panel, true);
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
        Dispatcher.UIThread.RunJobs();
        AssertSeparator(document, sidebar, main);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NestedInheritedVariableSideBorderHasNativeAndPortableGeometry()
    {
        var root = new CssLayoutPanel { Width = 300, Height = 300 };
        var window = new Window { Width = 300, Height = 300, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            :root { --divider-base: #4a4a4a; }
            [data-theme=dark] { --divider: var(--divider-base); }
            .footer {
                border-top: 1px solid var(--divider);
                display: flex;
                padding: 16px 20px;
            }
            .button { height: 34px; width: 80px; }
            """;
        document.head.appendChild(style);
        var themed = HostTestUtilities.GetElement(document.createElement("section"));
        themed.setAttribute("data-theme", "dark");
        HostTestUtilities.GetElement(document.body).appendChild(themed);
        var footer = HostTestUtilities.GetElement(document.createElement("div"));
        footer.className = "footer";
        themed.appendChild(footer);
        var button = HostTestUtilities.GetElement(document.createElement("div"));
        button.className = "button";
        footer.appendChild(button);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        var computed = document.getComputedStyle(footer);
        var panel = Assert.IsType<CssLayoutPanel>(footer.Control);
        Assert.Equal("#4a4a4a", computed.getPropertyValue("--divider-base"));
        Assert.Equal("var(--divider-base)", computed.getPropertyValue("--divider"));
        Assert.Equal("1px", computed.getPropertyValue("border-top-width"));
        Assert.Equal("solid", computed.getPropertyValue("border-top-style"));
        Assert.Equal("rgb(74, 74, 74)", computed.getPropertyValue("border-top-color"));
        Assert.Equal(1, panel.BorderThickness.Top);
        Assert.Equal(67, footer.getBoundingClientRect().height);
        Assert.Equal(
            67,
            AvaloniaCssLayoutProjection.Measure(
                panel,
                new Size(200, double.PositiveInfinity)).Height);

        CssLayout.SetNativeLayoutHotPath(panel, true);
        CssLayout.SetNativeLayoutHotPath(Assert.IsType<CssLayoutPanel>(button.Control), true);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(67, footer.getBoundingClientRect().height);

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

    private static void AssertSeparator(
        AvaloniaDomDocument document,
        AvaloniaDomElement sidebar,
        AvaloniaDomElement main)
    {
        var computed = document.getComputedStyle(sidebar);
        var sidebarRect = sidebar.getBoundingClientRect();
        var mainRect = main.getBoundingClientRect();
        Assert.Equal("1px", computed.getPropertyValue("border-right-width"));
        Assert.Equal("solid", computed.getPropertyValue("border-right-style"));
        Assert.Equal("rgb(74, 74, 74)", computed.getPropertyValue("border-right-color"));
        Assert.Equal(1, Assert.IsType<CssLayoutPanel>(sidebar.Control).BorderThickness.Right);
        Assert.Equal(201, sidebarRect.width);
        Assert.Equal(399, mainRect.width);
        Assert.Equal(sidebarRect.right, mainRect.left);
    }
}
