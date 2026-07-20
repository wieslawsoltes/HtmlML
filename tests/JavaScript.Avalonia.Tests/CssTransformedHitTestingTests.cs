using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssTransformedHitTestingTests
{
    [AvaloniaFact]
    public void ScaledBorderBoxExpandsBoundingRectAndHitRegionInNativeLayout()
        => AssertScaledHitContract(nativeLayout: true);

    [AvaloniaFact]
    public void ScaledBorderBoxExpandsBoundingRectAndHitRegionInPortableLayout()
        => AssertScaledHitContract(nativeLayout: false);

    [AvaloniaFact]
    public void RotatedBorderBoxUsesTransformedCornersAndInverseHitContainment()
    {
        foreach (var nativeLayout in new[] { true, false })
        {
            var root = new CssLayoutPanel { Width = 120, Height = 120 };
            CssLayout.SetNativeLayoutHotPath(root, nativeLayout);
            var window = new Window { Width = 120, Height = 120, Content = root };
            using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                html, body { height: 120px; margin: 0; width: 120px; }
                #rotated {
                  height: 20px;
                  left: 50px;
                  position: absolute;
                  top: 50px;
                  transform: rotate(45deg);
                  transform-origin: 50% 50%;
                  width: 20px;
                }
                """;
            document.head.appendChild(style);
            var rotated = Append(document, "rotated");

            try
            {
                window.Show();
                document.EnsureStylesCurrent();
                Dispatcher.UIThread.RunJobs();

                var rect = rotated.getBoundingClientRect();
                var edge = 10 * Math.Sqrt(2);
                Assert.Equal(60 - edge, rect.left, 6);
                Assert.Equal(60 - edge, rect.top, 6);
                Assert.Equal(2 * edge, rect.width, 6);
                Assert.Equal(2 * edge, rect.height, 6);
                Assert.Same(rotated, document.elementFromPoint(60, 60));

                // The top-left of the axis-aligned bounding rectangle is not
                // part of the rotated border box. An AABB-only hit test would
                // incorrectly return the element here.
                Assert.NotSame(rotated, document.elementFromPoint(rect.left + 1, rect.top + 1));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    [AvaloniaFact]
    public void NestedScaleAndRotationComposeForBoundingRectAndHitTesting()
    {
        foreach (var nativeLayout in new[] { true, false })
        {
            var root = new CssLayoutPanel { Width = 160, Height = 160 };
            CssLayout.SetNativeLayoutHotPath(root, nativeLayout);
            var window = new Window { Width = 160, Height = 160, Content = root };
            using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                html, body { height: 160px; margin: 0; width: 160px; }
                #parent {
                  height: 40px;
                  left: 20px;
                  position: absolute;
                  top: 20px;
                  transform: scale(2);
                  transform-origin: 0 0;
                  width: 40px;
                }
                #child {
                  height: 10px;
                  left: 10px;
                  position: absolute;
                  top: 5px;
                  transform: rotate(90deg);
                  transform-origin: 0 0;
                  width: 10px;
                }
                """;
            document.head.appendChild(style);
            var parent = Append(document, "parent");
            var child = HostTestUtilities.GetElement(document.createElement("div"));
            child.id = "child";
            parent.appendChild(child);

            try
            {
                window.Show();
                document.EnsureStylesCurrent();
                Dispatcher.UIThread.RunJobs();

                var rect = child.getBoundingClientRect();
                Assert.Equal(20, rect.left, 6);
                Assert.Equal(30, rect.top, 6);
                Assert.Equal(20, rect.width, 6);
                Assert.Equal(20, rect.height, 6);
                Assert.Same(child, document.elementFromPoint(30, 40));
                // Rotation maps the child's excluded local right edge to the
                // bottom edge of this viewport-space bounding rectangle. Step
                // past it to avoid trig round-off at the mathematical edge;
                // exact half-open edges are covered by the scale authority.
                Assert.NotSame(child, document.elementFromPoint(30, 50.001));
                Assert.Same(parent, document.elementFromPoint(70, 70));
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    private static void AssertScaledHitContract(bool nativeLayout)
    {
        var root = new CssLayoutPanel { Width = 120, Height = 120 };
        CssLayout.SetNativeLayoutHotPath(root, nativeLayout);
        var window = new Window { Width = 120, Height = 120, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { height: 120px; margin: 0; width: 120px; }
            #normal { height: 10px; left: 0; position: absolute; top: 0; width: 100px; }
            #scaled {
              height: 1px;
              left: 0;
              position: absolute;
              top: 10px;
              transform: scaleX(100) scaleY(100);
              transform-origin: 0 0;
              width: 1px;
              z-index: 1;
            }
            """;
        document.head.appendChild(style);
        var normal = Append(document, "normal");
        var scaled = Append(document, "scaled");

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        var rect = scaled.getBoundingClientRect();
        Assert.Equal((0d, 10d, 100d, 100d), (rect.left, rect.top, rect.width, rect.height));
        Assert.Same(normal, document.elementFromPoint(50, 9));
        Assert.Same(scaled, document.elementFromPoint(50, 10));
        Assert.Same(scaled, document.elementFromPoint(99, 109));
        Assert.NotSame(scaled, document.elementFromPoint(100, 10));
        Assert.NotSame(scaled, document.elementFromPoint(50, 110));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static AvaloniaDomElement Append(AvaloniaDomDocument document, string id)
    {
        var element = HostTestUtilities.GetElement(document.createElement("div"));
        element.id = id;
        HostTestUtilities.GetElement(document.body).appendChild(element);
        return element;
    }
}
