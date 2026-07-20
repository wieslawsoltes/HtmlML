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

public sealed class CssBorderOverlayRasterTests
{
    [AvaloniaFact]
    public void RoundedBorderOverlayStaysOwnerLocalAndTracksLiveStyleChangesInNativeLayout()
        => AssertBorderContract(nativeLayout: true);

    [AvaloniaFact]
    public void RoundedBorderOverlayStaysOwnerLocalAndTracksLiveStyleChangesInPortableLayout()
        => AssertBorderContract(nativeLayout: false);

    private static void AssertBorderContract(bool nativeLayout)
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
            .card {
                box-sizing: border-box;
                width: 80px;
                height: 50px;
                background: #ffffff;
                border: 1px solid #cbd5e1;
                border-radius: 12px;
            }
            .changed { width: 100px; border-color: #b91c1c; }
            .none { border-style: none; }
            """;
        document.head.appendChild(style);
        var card = HostTestUtilities.GetElement(document.createElement("section"));
        card.className = "card";
        HostTestUtilities.GetElement(document.body).appendChild(card);

        window.Show();
        document.EnsureStylesCurrent();
        var panel = Assert.IsType<CssLayoutPanel>(card.Control);
        CssLayout.SetNativeLayoutHotPath(panel, nativeLayout);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();

        Assert.Equal(new Thickness(1), panel.BorderThickness);
        Assert.Equal(new CornerRadius(12), panel.CornerRadius);
        Assert.Equal(Color.Parse("#cbd5e1"), Assert.IsAssignableFrom<ISolidColorBrush>(panel.BorderTopBrush).Color);
        AssertOverlay(panel, width: 80, height: 50);
        AssertRaster(root, edge: Color.Parse("#cbd5e1"));

        card.className = "card changed";
        Flush(document, panel);
        Assert.Equal(100, card.getBoundingClientRect().width);
        Assert.Equal(Color.Parse("#b91c1c"), Assert.IsAssignableFrom<ISolidColorBrush>(panel.BorderTopBrush).Color);
        AssertOverlay(panel, width: 100, height: 50);
        AssertRaster(root, edge: Color.Parse("#b91c1c"));

        card.className = "card changed none";
        Flush(document, panel);
        Assert.Equal(default, panel.BorderThickness);
        Assert.DoesNotContain(panel.Children, static child => child is DomBorderOverlayControl);
        AssertRaster(root, edge: Colors.White);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Flush(AvaloniaDomDocument document, CssLayoutPanel panel)
    {
        document.EnsureStylesCurrent();
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();
    }

    private static void AssertOverlay(CssLayoutPanel panel, double width, double height)
    {
        var overlay = Assert.Single(panel.Children.OfType<DomBorderOverlayControl>());
        Assert.Equal(new Rect(0, 0, width, height), overlay.Bounds);
    }

    private static void AssertRaster(CssLayoutPanel root, Color edge)
    {
        using var frame = new RenderTargetBitmap(new PixelSize(140, 90), new Vector(96, 96));
        frame.Render(root);
        Assert.Equal(Color.Parse("#eef4fb"), ReadPixel(frame, 20, 20));
        Assert.Equal(edge, ReadPixel(frame, 20, 45));
        Assert.Equal(Colors.White, ReadPixel(frame, 23, 45));
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
