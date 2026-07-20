using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssOutlineComputedStyleTests
{
    [AvaloniaFact]
    public void OpaqueOutlineShorthandSerializesToCssomRgbLonghands()
    {
        var root = new CssLayoutPanel { Width = 100, Height = 50 };
        var window = new Window { Width = 100, Height = 50, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = ".target { --focus-ring: green solid 5px; outline: var(--focus-ring); }";
        document.head.appendChild(style);
        var target = HostTestUtilities.GetElement(document.createElement("div"));
        target.className = "target";
        HostTestUtilities.GetElement(document.body).appendChild(target);

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        var computed = document.getComputedStyle(target);
        Assert.Equal("rgb(0, 128, 0)", computed.getPropertyValue("outline-color"));
        Assert.Equal("solid", computed.getPropertyValue("outline-style"));
        Assert.Equal("5px", computed.getPropertyValue("outline-width"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void SolidOutlinePaintsOutsideWithoutChangingNativeLayoutGeometry()
        => AssertOutlinePaintContract(nativeLayout: true);

    [AvaloniaFact]
    public void SolidOutlinePaintsOutsideWithoutChangingPortableLayoutGeometry()
        => AssertOutlinePaintContract(nativeLayout: false);

    private static void AssertOutlinePaintContract(bool nativeLayout)
    {
        var root = new CssLayoutPanel
        {
            Width = 140,
            Height = 90,
            Background = Brush.Parse("#eef4fb")
        };
        var window = new Window
        {
            Width = 140,
            Height = 90,
            Background = Brush.Parse("#eef4fb"),
            Content = root
        };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body {
                box-sizing: border-box;
                width: 140px;
                height: 90px;
                margin: 0;
                padding: 0;
                background: #eef4fb;
            }
            body { padding: 20px; }
            .target {
                box-sizing: border-box;
                width: 60px;
                height: 30px;
                background: white;
                border: 2px solid black;
                outline: #00ff00 solid 4px;
                outline-offset: 3px;
            }
            .none { outline-style: none; }
            """;
        document.head.appendChild(style);
        var target = HostTestUtilities.GetElement(document.createElement("div"));
        target.className = "target";
        HostTestUtilities.GetElement(document.body).appendChild(target);

        window.Show();
        document.EnsureStylesCurrent();
        var panel = Assert.IsType<CssLayoutPanel>(target.Control);
        CssLayout.SetNativeLayoutHotPath(panel, nativeLayout);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();

        var borderBox = target.getBoundingClientRect();
        Assert.Equal((20d, 20d, 60d, 30d), (borderBox.left, borderBox.top, borderBox.width, borderBox.height));
        var desiredWithOutline = panel.DesiredSize;
        Assert.Equal(new Size(60, 30), panel.Bounds.Size);
        Assert.Equal(new Thickness(2), panel.BorderThickness);
        Assert.Equal(4, panel.OutlineWidth);
        Assert.Equal(3, panel.OutlineOffset);
        Assert.Equal("solid", panel.OutlineStyle);
        Assert.Equal(Colors.Lime, Assert.IsAssignableFrom<ISolidColorBrush>(panel.OutlineBrush).Color);
        var overlay = Assert.Single(panel.Children.OfType<DomOutlineOverlayControl>());
        Assert.Equal(new Rect(-7, -7, 74, 44), overlay.Bounds);
        Assert.True(panel.IsMeasureValid);
        Assert.True(panel.IsArrangeValid);
        panel.OutlineOffset = 4;
        Assert.True(panel.IsMeasureValid);
        Assert.True(panel.IsArrangeValid);
        panel.OutlineOffset = 3;
        Assert.True(panel.IsMeasureValid);
        Assert.True(panel.IsArrangeValid);
        AssertRaster(root, Colors.Lime);

        target.className = "target none";
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();
        var unchanged = target.getBoundingClientRect();
        Assert.Equal((20d, 20d, 60d, 30d), (unchanged.left, unchanged.top, unchanged.width, unchanged.height));
        Assert.Equal(desiredWithOutline, panel.DesiredSize);
        Assert.Equal(new Thickness(2), panel.BorderThickness);
        Assert.DoesNotContain(panel.Children, static child => child is DomOutlineOverlayControl);
        AssertRaster(root, Color.Parse("#eef4fb"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertRaster(CssLayoutPanel root, Color expectedOutline)
    {
        using var frame = new RenderTargetBitmap(new PixelSize(140, 90), new Vector(96, 96));
        frame.Render(root);
        Assert.Equal(expectedOutline, ReadPixel(frame, 14, 35));
        Assert.Equal(Color.Parse("#eef4fb"), ReadPixel(frame, 18, 35));
        Assert.Equal(Colors.Black, ReadPixel(frame, 20, 35));
        Assert.Equal(Colors.White, ReadPixel(frame, 23, 35));
    }

    private static Color ReadPixel(Bitmap bitmap, int x, int y)
    {
        var bytes = new byte[4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return bitmap.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}
