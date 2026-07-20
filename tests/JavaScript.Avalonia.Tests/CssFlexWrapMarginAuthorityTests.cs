using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

/// <summary>
/// Actual-side geometry isolated from the float-built reference in
/// css/css-flexbox/flexbox-flex-wrap-horiz-001.html. The expected border boxes
/// are encoded directly by that unchanged WPT reference and CSS Flexbox's
/// per-line flexible-length and margin rules.
/// </summary>
public sealed class CssFlexWrapMarginAuthorityTests
{
    [AvaloniaFact]
    public void WrappedFlexMarginsAffectLineCollectionWithoutDeflatingTheItemTwice()
    {
        var root = new CssLayoutPanel { Width = 220, Height = 100 };
        CssLayout.SetNativeLayoutHotPath(root, true);
        var window = new Window { Width = 220, Height = 100, Content = root };
        using var browser = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = browser.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            .flexbox {
              display: flex;
              flex-wrap: wrap;
              border: 1px dashed black;
              width: 100px;
              height: 12px;
              margin-bottom: 2px;
            }
            .item { width: 48px; border: 1px solid blue; background: lightblue; }
            """;
        document.head.appendChild(style);

        var shrink = CreateContainer(document, "margin-left: 80px", itemCount: 1);
        var inflexible = CreateContainer(document, "margin-left: 80px; flex: none", itemCount: 1);
        var firstMargin = CreateContainer(document, "margin-right: 1px", itemCount: 2);
        var secondMargin = CreateContainer(document, string.Empty, itemCount: 2);
        secondMargin.Items[1].style.cssText = "margin-right: 1px";

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            AssertItem(shrink, 0, x: 81, y: 1, width: 20, height: 12);
            AssertItem(inflexible, 0, x: 81, y: 1, width: 50, height: 12);
            AssertItem(firstMargin, 0, x: 1, y: 1, width: 50, height: 6);
            AssertItem(firstMargin, 1, x: 1, y: 7, width: 50, height: 6);
            AssertItem(secondMargin, 0, x: 1, y: 1, width: 50, height: 6);
            AssertItem(secondMargin, 1, x: 1, y: 7, width: 50, height: 6);

            foreach (var fixture in new[] { shrink, inflexible, firstMargin, secondMargin })
            {
                var hostRect = fixture.Host.getBoundingClientRect();
                var portable = AvaloniaCssLayoutProjection.Capture(
                    Assert.IsType<CssLayoutPanel>(fixture.Host.Control),
                    new Size(hostRect.width, hostRect.height));
                foreach (var item in fixture.Items)
                {
                    var native = item.getBoundingClientRect();
                    var box = portable.GetBox(item.Control).BorderBox;
                    Assert.Equal(native.left - hostRect.left, box.X, 9);
                    Assert.Equal(native.top - hostRect.top, box.Y, 9);
                    Assert.Equal(native.width, box.Width, 9);
                    Assert.Equal(native.height, box.Height, 9);
                }
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Fixture CreateContainer(
        AvaloniaDomDocument document,
        string firstStyle,
        int itemCount)
    {
        var host = HostTestUtilities.GetElement(document.createElement("div"));
        host.className = "flexbox";
        var items = new AvaloniaDomElement[itemCount];
        for (var index = 0; index < itemCount; index++)
        {
            var item = HostTestUtilities.GetElement(document.createElement("div"));
            item.className = "item";
            if (index == 0)
            {
                item.style.cssText = firstStyle;
            }
            host.appendChild(item);
            items[index] = item;
        }
        HostTestUtilities.GetElement(document.body).appendChild(host);
        return new Fixture(host, items);
    }

    private static void AssertItem(
        Fixture fixture,
        int index,
        double x,
        double y,
        double width,
        double height)
    {
        var host = fixture.Host.getBoundingClientRect();
        var item = fixture.Items[index].getBoundingClientRect();
        Assert.Equal(x, item.left - host.left, 9);
        Assert.Equal(y, item.top - host.top, 9);
        Assert.Equal(width, item.width, 9);
        Assert.Equal(height, item.height, 9);
    }

    private sealed record Fixture(AvaloniaDomElement Host, AvaloniaDomElement[] Items);
}
