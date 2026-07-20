using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

/// <remarks>
/// Maps WPT css/CSS2/normal-flow/min-height-percentage-002.xht and the modern
/// check-layout test unresolvable-min-height.html. The definite counter-cases
/// map min-height-percentage-001.xht and -003.xht.
/// </remarks>
public sealed class CssDocumentViewportPercentageMinHeightTests
{
    [AvaloniaFact]
    public void PercentageMinimumHeightInAutoHeightDocumentBodyUsesIntrinsicHeightInNativeAndPortableLayout()
    {
        var root = new CssLayoutPanel { Width = 200, Height = 300 };
        var window = new Window { Width = 200, Height = 300, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;

        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            body.definite { height: 100%; }
            .fixed { height: 120px; }
            .subject {
                box-sizing: border-box;
                min-height: 100%;
                padding: 10px;
            }
            .content { height: 60px; }
            """;
        document.head.appendChild(style);

        var subject = HostTestUtilities.GetElement(document.createElement("main"));
        subject.className = "subject";
        var content = HostTestUtilities.GetElement(document.createElement("div"));
        content.className = "content";
        subject.appendChild(content);
        HostTestUtilities.GetElement(document.body).appendChild(subject);

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            CssLayout.SetNativeLayoutHotPath(Assert.IsType<CssLayoutPanel>(subject.Control), true);
            Dispatcher.UIThread.RunJobs();

            // The body's used viewport height does not make its auto CSS height
            // a definite percentage basis. The percentage minimum is therefore
            // auto and this child keeps its 60px content + 20px padding height.
            var bodyElement = HostTestUtilities.GetElement(document.body);
            var body = Assert.IsType<CssLayoutPanel>(bodyElement.Control);
            Assert.Equal("auto", bodyElement.ComputedStyleValues["height"]);
            Assert.True(CssLayout.GetCssHeightIsAuto(body));
            Assert.True(CssLayout.GetHeight(body) is { IsAuto: true });
            Assert.False(CssLayout.GetDocumentViewportRoot(body));
            Assert.False(body.Parent is CssLayoutPanel);
            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(200, 300));
            Assert.Equal(
                (Native: 80d, Portable: 80d),
                (Native: subject.getBoundingClientRect().height,
                    Portable: portable.GetBox(subject.Control).BorderBox.Height));

            // Once CSS explicitly supplies the root height, the same
            // percentage minimum has a definite 300px basis in both lanes.
            bodyElement.className = "definite";
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("100%", bodyElement.ComputedStyleValues["height"]);
            Assert.False(CssLayout.GetCssHeightIsAuto(body));
            portable = AvaloniaCssLayoutProjection.Capture(body, new Size(200, 300));
            Assert.Equal(
                (Native: 300d, Portable: 300d),
                (Native: subject.getBoundingClientRect().height,
                    Portable: portable.GetBox(subject.Control).BorderBox.Height));

            // An ordinary authored pixel height is likewise definite.
            var fixedHost = HostTestUtilities.GetElement(document.createElement("div"));
            fixedHost.className = "fixed";
            bodyElement.appendChild(fixedHost);
            fixedHost.appendChild(subject);
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();
            Assert.False(CssLayout.GetCssHeightIsAuto(fixedHost.Control));
            portable = AvaloniaCssLayoutProjection.Capture(
                Assert.IsType<CssLayoutPanel>(fixedHost.Control),
                new Size(200, 120));
            Assert.Equal(
                (Native: 120d, Portable: 120d),
                (Native: subject.getBoundingClientRect().height,
                    Portable: portable.GetBox(subject.Control).BorderBox.Height));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ManuallyHostedPanelWithoutDomMetadataRetainsDefinitePercentageBasis()
    {
        var root = new CssLayoutPanel { Width = 200, Height = 300 };
        var subject = new CssLayoutPanel();
        root.Children.Add(subject);
        CssLayout.SetMinHeight(subject, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetNativeLayoutHotPath(root, true);
        var window = new Window { Width = 200, Height = 300, Content = root };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(CssLayout.GetCssHeightIsAuto(root));
            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(200, 300));
            Assert.Equal(
                (Native: 300d, Portable: 300d),
                (Native: subject.Bounds.Height,
                    Portable: portable.GetBox(subject).BorderBox.Height));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void VirtualIframeDocumentViewportRootRetainsDefinitePercentageBasis()
    {
        var hostRoot = new CssLayoutPanel { Width = 200, Height = 300 };
        var root = new CssLayoutPanel();
        var subject = new CssLayoutPanel();
        hostRoot.Children.Add(root);
        root.Children.Add(subject);
        CssLayout.SetWidth(root, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetHeight(root, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetDocumentViewportRoot(root, true);
        CssLayout.SetCssHeightIsAuto(root, true);
        CssLayout.SetMinHeight(subject, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetNativeLayoutHotPath(hostRoot, true);
        var window = new Window { Width = 200, Height = 300, Content = hostRoot };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(200, 300));
            Assert.Equal(
                (Native: 300d, Portable: 300d),
                (Native: subject.Bounds.Height,
                    Portable: portable.GetBox(subject).BorderBox.Height));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void NativeBorderHostedDocumentSurfaceRetainsDefinitePercentageBasis()
    {
        var root = new CssLayoutPanel();
        var subject = new CssLayoutPanel();
        root.Children.Add(subject);
        CssLayout.SetCssHeightIsAuto(root, true);
        CssLayout.SetMinHeight(subject, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetNativeLayoutHotPath(root, true);
        var viewportHost = new Border
        {
            Width = 200,
            Height = 300,
            Child = root
        };
        var window = new Window { Width = 200, Height = 300, Content = viewportHost };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(200, 300));
            Assert.Equal(
                (Native: 300d, Portable: 300d),
                (Native: subject.Bounds.Height,
                    Portable: portable.GetBox(subject).BorderBox.Height));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void NativeGridHostedDocumentSurfaceRetainsDefinitePercentageBasis()
    {
        var root = new CssLayoutPanel();
        var subject = new CssLayoutPanel();
        root.Children.Add(subject);
        CssLayout.SetCssHeightIsAuto(root, true);
        CssLayout.SetMinHeight(subject, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetNativeLayoutHotPath(root, true);
        var nativeGrid = new Grid();
        nativeGrid.Children.Add(root);
        var viewportHost = new Border
        {
            Width = 200,
            Height = 300,
            Child = nativeGrid
        };
        var window = new Window { Width = 200, Height = 300, Content = viewportHost };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var portable = AvaloniaCssLayoutProjection.Capture(root, new Size(200, 300));
            Assert.Equal(
                (Native: 300d, Portable: 300d),
                (Native: subject.Bounds.Height,
                    Portable: portable.GetBox(subject).BorderBox.Height));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void HostProjectedNonAutoHeightRemainsDefiniteWhenCssHeightMetadataIsAuto()
    {
        var hostRoot = new CssLayoutPanel { Width = 200, Height = 300 };
        var projected = new CssLayoutPanel();
        var subject = new CssLayoutPanel();
        hostRoot.Children.Add(projected);
        projected.Children.Add(subject);
        CssLayout.SetHeight(projected, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetCssHeightIsAuto(projected, true);
        CssLayout.SetMinHeight(subject, new CssLength(100, CssLengthUnit.Percent));
        CssLayout.SetNativeLayoutHotPath(hostRoot, true);
        var window = new Window { Width = 200, Height = 300, Content = hostRoot };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var portable = AvaloniaCssLayoutProjection.Capture(projected, new Size(200, 300));
            Assert.Equal(
                (Native: 300d, Portable: 300d),
                (Native: subject.Bounds.Height,
                    Portable: portable.GetBox(subject).BorderBox.Height));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
