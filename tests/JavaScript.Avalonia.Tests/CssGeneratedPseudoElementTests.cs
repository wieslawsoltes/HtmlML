using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using JavaScript.Avalonia;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssGeneratedPseudoElementTests
{
    [AvaloniaFact]
    public void HiddenAbsolutePseudoBackgroundIsNotMaterializedAndCanBecomeVisible()
    {
        var button = new CssLayoutPanel
        {
            Width = 28,
            Height = 24,
            Background = Brushes.Transparent
        };
        var values = new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["position"] = "absolute",
            ["visibility"] = "hidden",
            ["inset"] = "1px 0",
            ["background-color"] = "#f2f2f2"
        };

        button.SetGeneratedPseudoElement("after", values);
        Assert.Empty(button.Children.OfType<DomGeneratedBackgroundControl>());

        values["visibility"] = "visible";
        button.SetGeneratedPseudoElement("after", values);
        var overlay = Assert.Single(button.Children.OfType<DomGeneratedBackgroundControl>());
        Assert.Equal(
            Color.Parse("#f2f2f2"),
            Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Background).Color);

        values["visibility"] = "hidden";
        button.SetGeneratedPseudoElement("after", values);
        Assert.Empty(button.Children.OfType<DomGeneratedBackgroundControl>());
    }

    [AvaloniaFact]
    public void EightDigitCssHexPaintsHoverOverlayAsRrggbbaaRatherThanAvaloniaAarrggbb()
    {
        var button = new CssLayoutPanel
        {
            Width = 20,
            Height = 20,
            Background = Brushes.Black
        };
        button.SetGeneratedPseudoElement("after", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["position"] = "absolute",
            ["inset"] = "0",
            ["background-color"] = "#b8b8b833"
        });
        button.RefreshGeneratedPseudoElements(new Size(20, 20));

        var overlay = Assert.Single(button.Children.OfType<DomGeneratedBackgroundControl>());
        Assert.Equal(
            Color.FromArgb(0x33, 0xb8, 0xb8, 0xb8),
            Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Background).Color);

        button.Measure(new Size(20, 20));
        button.Arrange(new Rect(0, 0, 20, 20));
        using var frame = new RenderTargetBitmap(new PixelSize(20, 20), new Vector(96, 96));
        frame.Render(button);
        Assert.Equal(Color.FromRgb(37, 37, 37), ReadPixel(frame, 10, 10));
    }

    [AvaloniaFact]
    public void FlowBlockBeforePaintsAndSeparatesAdjacentToolbarGroupInNativeAndPortableLayout()
    {
        var group = new CssLayoutPanel
        {
            Width = 100,
            Background = Brushes.Transparent
        };
        CssLayout.SetNativeLayoutHotPath(group, true);
        var firstTool = new Border();
        CssLayout.SetHeight(firstTool, new CssLength(10, CssLengthUnit.Pixel));
        group.Children.Add(firstTool);

        group.SetGeneratedPseudoElement("before", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["display"] = "block",
            ["height"] = "1px",
            ["margin-top"] = "0px",
            ["margin-right"] = "8px",
            ["margin-bottom"] = "6px",
            ["margin-left"] = "8px",
            ["background-color"] = "#363a45"
        });

        group.Measure(new Size(100, double.PositiveInfinity));
        Assert.Equal(17, group.DesiredSize.Height);
        group.Arrange(new Rect(0, 0, 100, 17));

        Assert.Equal(new Rect(0, 7, 100, 10), firstTool.Bounds);
        var brush = Assert.IsType<DrawingBrush>(group.Background);
        var drawing = Assert.IsType<DrawingGroup>(brush.Drawing);
        var separator = Assert.IsType<GeometryDrawing>(drawing.Children[1]);
        var geometry = Assert.IsType<RectangleGeometry>(separator.Geometry);
        Assert.Equal(new Rect(8, 0, 84, 1), geometry.Rect);
        Assert.Equal(Color.Parse("#363a45"), Assert.IsAssignableFrom<ISolidColorBrush>(separator.Brush).Color);

        var portable = AvaloniaCssLayoutProjection.Capture(group, new Size(100, 17));
        var portableTool = portable.GetBox(firstTool).BorderBox;
        Assert.Equal((0d, 7d, 100d, 10d),
            (portableTool.X, portableTool.Y, portableTool.Width, portableTool.Height));
    }

    [AvaloniaFact]
    public void FlowPseudosAreOrderedFlexItemsAndTheirCrossStrutProducesThirtyTwoPixelMenuRows()
    {
        var row = new CssLayoutPanel { Background = Brushes.Transparent };
        CssLayout.SetNativeLayoutHotPath(row, true);
        CssLayout.SetDisplay(row, CssDisplay.Flex);
        CssLayout.SetAlignItems(row, "flex-start");
        CssLayout.SetPaddingTop(row, new CssLength(2, CssLengthUnit.Pixel));
        CssLayout.SetPaddingBottom(row, new CssLength(2, CssLengthUnit.Pixel));

        var label = new Border();
        CssLayout.SetWidth(label, new CssLength(10, CssLengthUnit.Pixel));
        CssLayout.SetHeight(label, new CssLength(12, CssLengthUnit.Pixel));
        row.Children.Add(label);

        row.SetGeneratedPseudoElement("before", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["display"] = "block",
            ["width"] = "4px",
            ["height"] = "28px",
            ["background-color"] = "#ff0000"
        });
        row.SetGeneratedPseudoElement("after", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["display"] = "block",
            ["align-self"] = "center",
            ["width"] = "6px",
            ["height"] = "20px",
            ["background-color"] = "#0000ff"
        });

        row.Measure(new Size(100, double.PositiveInfinity));
        Assert.Equal(new Size(20, 32), row.DesiredSize);
        Assert.Equal(new Size(20, 32), AvaloniaCssLayoutProjection.Measure(row, new Size(100, double.PositiveInfinity)));

        row.Arrange(new Rect(0, 0, 20, 32));
        Assert.Equal(new Rect(4, 2, 10, 12), label.Bounds);

        var nativeBrush = Assert.IsType<DrawingBrush>(row.Background);
        var nativeDrawing = Assert.IsType<DrawingGroup>(nativeBrush.Drawing);
        Assert.Equal(
            new Rect(0, 2, 4, 28),
            Assert.IsType<RectangleGeometry>(Assert.IsType<GeometryDrawing>(nativeDrawing.Children[1]).Geometry).Rect);
        Assert.Equal(
            new Rect(14, 6, 6, 20),
            Assert.IsType<RectangleGeometry>(Assert.IsType<GeometryDrawing>(nativeDrawing.Children[2]).Geometry).Rect);

        var portable = AvaloniaCssLayoutProjection.Capture(row, new Size(20, 32));
        Assert.True(portable.TryGetPseudoBox(row, before: true, out var portableBefore));
        Assert.True(portable.TryGetPseudoBox(row, before: false, out var portableAfter));
        Assert.Equal((0d, 2d, 4d, 28d),
            (portableBefore.BorderBox.X, portableBefore.BorderBox.Y, portableBefore.BorderBox.Width, portableBefore.BorderBox.Height));
        Assert.Equal((4d, 2d, 10d, 12d),
            (portable.GetBox(label).BorderBox.X, portable.GetBox(label).BorderBox.Y,
                portable.GetBox(label).BorderBox.Width, portable.GetBox(label).BorderBox.Height));
        Assert.Equal((14d, 6d, 6d, 20d),
            (portableAfter.BorderBox.X, portableAfter.BorderBox.Y, portableAfter.BorderBox.Width, portableAfter.BorderBox.Height));
    }

    [AvaloniaFact]
    public void AbsolutePseudoHonorsOwnSizeAndPercentageTranslationForCenteredRadioDot()
    {
        var marker = new CssLayoutPanel
        {
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent
        };
        marker.SetGeneratedPseudoElement("before", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["position"] = "absolute",
            ["background-color"] = "#202020",
            ["border-radius"] = "50%",
            ["left"] = "50%",
            ["top"] = "50%",
            ["width"] = "6px",
            ["height"] = "6px",
            ["transform"] = "translate(-50%, -50%)"
        });

        marker.Measure(new Size(18, 18));
        marker.Arrange(new Rect(0, 0, 18, 18));

        var dot = Assert.Single(marker.Children.OfType<DomGeneratedBackgroundControl>());
        Assert.Equal(new Rect(6, 6, 6, 6), dot.Bounds);
        Assert.Equal(new CornerRadius(3), dot.CornerRadius);
    }

    [AvaloniaFact]
    public void BorderlessPositionedHostKeepsAbsolutePseudoContainingBlockAtBorderBox()
    {
        var (native, portable) = ArrangeCurrentDayUnderline(borderWidth: 0);

        Assert.Equal(new Rect(6, 28, 22, 2), native);
        Assert.Equal((6d, 28d, 22d, 2d), portable);
    }

    [AvaloniaFact]
    public void BorderedPositionedHostUsesPaddingBoxForAbsolutePseudoInNativeAndPortableLayout()
    {
        var (native, portable) = ArrangeCurrentDayUnderline(borderWidth: 1);

        // Chrome's 34px day button has a 1px border, so its
        // absolute containing block is the 32px padding box at local (1, 1).
        Assert.Equal(new Rect(7, 27, 20, 2), native);
        Assert.Equal((7d, 27d, 20d, 2d), portable);
    }

    private static (Rect Native, (double X, double Y, double Width, double Height) Portable)
        ArrangeCurrentDayUnderline(double borderWidth)
    {
        var day = new CssLayoutPanel
        {
            Width = 34,
            Height = 34,
            BorderThickness = new Thickness(borderWidth),
            Background = Brushes.Transparent
        };
        CssLayout.SetPosition(day, CssPosition.Relative);
        day.SetGeneratedPseudoElement("before", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["position"] = "absolute",
            ["background-color"] = "#000000",
            ["left"] = "6px",
            ["right"] = "6px",
            ["top"] = "100%",
            ["height"] = "2px",
            ["margin-top"] = "-6px"
        });

        day.Measure(new Size(34, 34));
        day.Arrange(new Rect(0, 0, 34, 34));

        var underline = Assert.Single(day.Children.OfType<DomGeneratedBackgroundControl>());
        var native = underline.Bounds;

        var snapshot = AvaloniaCssLayoutProjection.Capture(day, new Size(34, 34));
        Assert.True(snapshot.TryGetPseudoBox(day, before: true, out var pseudo));
        var box = pseudo.BorderBox;
        return (native, (box.X, box.Y, box.Width, box.Height));
    }

    private static Color ReadPixel(Bitmap bitmap, int x, int y)
    {
        var bytes = new byte[4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), bytes.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        return bitmap.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    [AvaloniaFact]
    public void AbsolutePseudoPositionsDeclaredSizeFromRightAndBottomWhenStartEdgesAreAuto()
    {
        var tab = new CssLayoutPanel
        {
            Width = 100,
            Height = 48,
            Background = Brushes.Transparent
        };
        tab.SetGeneratedPseudoElement("after", new Dictionary<string, string>
        {
            ["content"] = "\"\"",
            ["position"] = "absolute",
            ["background-color"] = "#d1d4dc",
            ["right"] = "2px",
            ["bottom"] = "0px",
            ["width"] = "20px",
            ["height"] = "4px"
        });

        tab.Measure(new Size(100, 48));
        tab.Arrange(new Rect(0, 0, 100, 48));

        var rail = Assert.Single(tab.Children.OfType<DomGeneratedBackgroundControl>());
        Assert.Equal(new Rect(78, 44, 20, 4), rail.Bounds);
    }
}
