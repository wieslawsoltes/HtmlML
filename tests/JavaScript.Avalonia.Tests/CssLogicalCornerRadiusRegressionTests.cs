using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssLogicalCornerRadiusRegressionTests
{
    [AvaloniaFact]
    public void LogicalCornerRadiiReachComputedNativeAndPortablePresentation()
    {
        var root = new CssLayoutPanel { Width = 120, Height = 80 };
        var window = new Window { Width = 120, Height = 80, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { height: 80px; margin: 0; width: 120px; }
            .toolbar {
                background: #2e2e2e;
                border-start-start-radius: 1px;
                border-start-end-radius: 4px;
                border-end-start-radius: 2px;
                border-end-end-radius: 3px;
                height: 40px;
                width: 52px;
            }
            """;
        document.head.appendChild(style);
        var toolbar = HostTestUtilities.GetElement(document.createElement("div"));
        toolbar.className = "toolbar";
        HostTestUtilities.GetElement(document.body).appendChild(toolbar);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        AssertRadii(document, toolbar);
        var panel = Assert.IsType<CssLayoutPanel>(toolbar.Control);
        var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(120, 80));
        Assert.Equal(52, portable.GetBox(panel).BorderBox.Width);

        CssLayout.SetNativeLayoutHotPath(panel, true);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();

        AssertRadii(document, toolbar);
        Assert.Equal(52, toolbar.getBoundingClientRect().width);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertRadii(AvaloniaDomDocument document, AvaloniaDomElement toolbar)
    {
        var computed = document.getComputedStyle(toolbar);
        var panel = Assert.IsType<CssLayoutPanel>(toolbar.Control);
        Assert.Equal("1px", computed.getPropertyValue("border-top-left-radius"));
        Assert.Equal("4px", computed.getPropertyValue("border-top-right-radius"));
        Assert.Equal("2px", computed.getPropertyValue("border-bottom-left-radius"));
        Assert.Equal("3px", computed.getPropertyValue("border-bottom-right-radius"));
        Assert.Equal(new CornerRadius(1, 4, 3, 2), panel.CornerRadius);
    }
}
