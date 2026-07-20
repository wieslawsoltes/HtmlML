using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

/// <summary>
/// Browser-authoritative reduction for the CSS initial font metrics consumed by
/// ordinary DOM text and generated list markers. Chrome 150 at DPR 1 computes
/// both to 16px/normal and gives both an 18px natural line box after the
/// layout-only reset used below.
/// </summary>
public sealed class CssInitialFontMetricsAuthorityTests
{
    [AvaloniaFact]
    public void OrdinaryTextAndGeneratedMarkerShareInitialFontAndLineMetrics()
    {
        using var fixture = CreateFixture();

        var portableLive = Capture(fixture);
        var projection = AvaloniaCssLayoutProjection.Capture(
            Assert.IsType<CssLayoutPanel>(fixture.Container.Control),
            new Size(200, 100));
        var portableProjection = new Geometry(
            projection.GetBox(fixture.Plain.Control).BorderBox.Height,
            projection.GetBox(fixture.Item.Control).BorderBox.Height);

        EnableNativeLayout(fixture);
        var nativeLive = Capture(fixture);

        var plainStyle = fixture.Document.getComputedStyle(fixture.Plain);
        var itemStyle = fixture.Document.getComputedStyle(fixture.Item);
        var plainText = Assert.IsType<AvaloniaDomTextNode>(Assert.Single(fixture.Plain.childNodes));
        var textBlock = Assert.IsType<DomTextBlockControl>(plainText.Control);
        var marker = Assert.IsType<CssListMarker>(
            Assert.IsType<CssLayoutPanel>(fixture.Item.Control).ListMarker);

        Assert.Equal("16px", plainStyle.getPropertyValue("font-size"));
        Assert.Equal("normal", plainStyle.getPropertyValue("line-height"));
        Assert.Equal("16px", itemStyle.getPropertyValue("font-size"));
        Assert.Equal("normal", itemStyle.getPropertyValue("line-height"));
        Assert.Equal(16, textBlock.FontSize, 9);
        Assert.Equal(textBlock.FontSize, marker.FontSize, 9);
        Assert.True(
            Math.Abs(portableLive.PlainHeight - portableLive.MarkerHeight) < 0.001
            && Math.Abs(portableProjection.PlainHeight - portableProjection.MarkerHeight) < 0.001
            && Math.Abs(nativeLive.PlainHeight - nativeLive.MarkerHeight) < 0.001,
            $"Chrome natural line boxes: plain=18 marker=18\n"
            + $"portable live: {portableLive}\n"
            + $"portable projection: {portableProjection}\n"
            + $"native live: {nativeLive}\n"
            + $"native text: font={textBlock.FontSize:0.###} desired={textBlock.DesiredSize.Height:0.###}\n"
            + $"generated marker: font={marker.FontSize:0.###} line={marker.LineHeight:0.###} paint={marker.Size.Height:0.###}");
    }

    private static Fixture CreateFixture()
    {
        var root = new CssLayoutPanel
        {
            Width = 200,
            Height = 100,
            Background = Brushes.White
        };
        var window = new Window
        {
            Width = 200,
            Height = 100,
            Background = Brushes.White,
            Content = root
        };
        var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body, #fixture, div, ol, li { margin: 0; padding: 0; border: 0; }
            li { list-style-position: inside; }
            """;
        document.head.appendChild(style);
        var body = HostTestUtilities.GetElement(document.body);
        body.innerHTML = """
            <main id="fixture"><div id="plain">1.</div><ol><li id="marker"></li></ol></main>
            """;

        var container = HostTestUtilities.GetElement(body.querySelector("#fixture"));
        var plain = HostTestUtilities.GetElement(body.querySelector("#plain"));
        var item = HostTestUtilities.GetElement(body.querySelector("#marker"));
        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();
        return new Fixture(window, host, container, plain, item);
    }

    private static Geometry Capture(Fixture fixture)
        => new(
            fixture.Plain.getBoundingClientRect().height,
            fixture.Item.getBoundingClientRect().height);

    private static void EnableNativeLayout(Fixture fixture)
    {
        foreach (var panel in fixture.Elements
                     .Select(static element => element.Control)
                     .OfType<CssLayoutPanel>())
        {
            CssLayout.SetNativeLayoutHotPath(panel, true);
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
        fixture.Container.Control.InvalidateMeasure();
        fixture.Container.Control.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
        fixture.Document.FlushPendingLayout();
    }

    private readonly record struct Geometry(double PlainHeight, double MarkerHeight);

    private sealed class Fixture(
        Window window,
        AvaloniaBrowserHost host,
        AvaloniaDomElement container,
        AvaloniaDomElement plain,
        AvaloniaDomElement item) : IDisposable
    {
        public Window Window { get; } = window;
        public AvaloniaDomDocument Document { get; } = host.Document;
        public AvaloniaDomElement Container { get; } = container;
        public AvaloniaDomElement Plain { get; } = plain;
        public AvaloniaDomElement Item { get; } = item;
        public AvaloniaDomElement[] Elements => [Container, Plain, Item];

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            host.Dispose();
        }
    }
}
