using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssIndefiniteHeightRegressionTests
{
    [AvaloniaFact]
    public void BorderBoxMinimumHeightIncludesVerticalPaddingExactlyOnceInNativeAndPortableLayout()
    {
        using var fixture = CreateFixture("""
            .host { width: 200px; }
            .subject {
                box-sizing: border-box;
                display: flex;
                flex-direction: column;
                min-height: 100px;
                padding-top: 10px;
                padding-bottom: 15px;
            }
            """);

        AssertGeometry(fixture, expectedHostHeight: 100, expectedSubjectHeight: 100);
        AssertPortableGeometry(fixture, expectedHostHeight: 100, expectedSubjectHeight: 100);

        EnableNativeLayout(fixture);
        AssertGeometry(fixture, expectedHostHeight: 100, expectedSubjectHeight: 100);
    }

    [AvaloniaFact]
    public void PercentageHeightInAutoHeightContainingBlockUsesIntrinsicHeightWithoutOverflowInNativeAndPortableLayout()
    {
        using var fixture = CreateFixture("""
            .host { width: 200px; }
            .spacer { height: 50px; }
            .subject { height: 100%; min-height: 100px; }
            """, includeSpacer: true);

        AssertGeometry(fixture, expectedHostHeight: 150, expectedSubjectHeight: 100);
        AssertPortableGeometry(fixture, expectedHostHeight: 150, expectedSubjectHeight: 100);

        EnableNativeLayout(fixture);
        AssertGeometry(fixture, expectedHostHeight: 150, expectedSubjectHeight: 100);
    }

    [AvaloniaFact]
    public void PercentageHeightInStretchedFlexItemStillResolvesAgainstDefiniteUsedHeight()
    {
        using var fixture = CreateFixture("""
            .host { align-items: stretch; display: flex; height: 120px; width: 200px; }
            .subject { width: 100px; }
            .inner { height: 100%; min-height: 20px; }
            """, includeInner: true);

        Assert.Equal(120, fixture.Subject.getBoundingClientRect().height);
        Assert.Equal(120, fixture.Inner!.getBoundingClientRect().height);
        var portable = AvaloniaCssLayoutProjection.Capture(
            Assert.IsType<CssLayoutPanel>(fixture.HostElement.Control),
            new Size(200, 120));
        Assert.Equal(120, portable.GetBox(fixture.Inner.Control).BorderBox.Height);

        EnableNativeLayout(fixture);
        Assert.Equal(120, fixture.Subject.getBoundingClientRect().height);
        Assert.Equal(120, fixture.Inner.getBoundingClientRect().height);
    }

    private static Fixture CreateFixture(string css, bool includeSpacer = false, bool includeInner = false)
    {
        var root = new CssLayoutPanel { Width = 300, Height = 300 };
        var window = new Window { Width = 300, Height = 300, Content = root };
        var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = css;
        document.head.appendChild(style);

        var container = HostTestUtilities.GetElement(document.createElement("div"));
        container.className = "host";
        AvaloniaDomElement? spacer = null;
        if (includeSpacer)
        {
            spacer = HostTestUtilities.GetElement(document.createElement("div"));
            spacer.className = "spacer";
            container.appendChild(spacer);
        }
        var subject = HostTestUtilities.GetElement(document.createElement("div"));
        subject.className = "subject";
        AvaloniaDomElement? inner = null;
        if (includeInner)
        {
            inner = HostTestUtilities.GetElement(document.createElement("div"));
            inner.className = "inner";
            subject.appendChild(inner);
        }
        container.appendChild(subject);
        HostTestUtilities.GetElement(document.body).appendChild(container);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        return new Fixture(window, host, container, spacer, subject, inner);
    }

    private static void AssertGeometry(Fixture fixture, double expectedHostHeight, double expectedSubjectHeight)
    {
        var host = fixture.HostElement.getBoundingClientRect();
        var subject = fixture.Subject.getBoundingClientRect();
        Assert.Equal(expectedHostHeight, host.height);
        Assert.Equal(expectedSubjectHeight, subject.height);
        Assert.Equal(host.bottom, subject.bottom);
    }

    private static void AssertPortableGeometry(Fixture fixture, double expectedHostHeight, double expectedSubjectHeight)
    {
        var hostRect = fixture.HostElement.getBoundingClientRect();
        var snapshot = AvaloniaCssLayoutProjection.Capture(
            Assert.IsType<CssLayoutPanel>(fixture.HostElement.Control),
            new Size(hostRect.width, hostRect.height));
        Assert.Equal(expectedSubjectHeight, snapshot.GetBox(fixture.Subject.Control).BorderBox.Height);
        Assert.Equal(expectedHostHeight, snapshot.GetBox(fixture.Subject.Control).BorderBox.Bottom);
    }

    private static void EnableNativeLayout(Fixture fixture)
    {
        foreach (var panel in new[]
                 {
                     Assert.IsType<CssLayoutPanel>(fixture.HostElement.Control),
                     fixture.Spacer?.Control as CssLayoutPanel,
                     Assert.IsType<CssLayoutPanel>(fixture.Subject.Control),
                     fixture.Inner?.Control as CssLayoutPanel
                 }.OfType<CssLayoutPanel>())
        {
            CssLayout.SetNativeLayoutHotPath(panel, true);
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
        var content = Assert.IsAssignableFrom<Control>(fixture.Window.Content);
        content.InvalidateMeasure();
        content.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture(
        Window window,
        AvaloniaBrowserHost browserHost,
        AvaloniaDomElement hostElement,
        AvaloniaDomElement? spacer,
        AvaloniaDomElement subject,
        AvaloniaDomElement? inner) : IDisposable
    {
        public Window Window { get; } = window;
        public AvaloniaDomElement HostElement { get; } = hostElement;
        public AvaloniaDomElement? Spacer { get; } = spacer;
        public AvaloniaDomElement Subject { get; } = subject;
        public AvaloniaDomElement? Inner { get; } = inner;

        public void Dispose()
        {
            browserHost.Dispose();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
