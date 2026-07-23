using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssGeneratedPseudoElementStackingTests
{
    [AvaloniaFact]
    public void PositionedBeforeUsesItsAuthoredZIndexAgainstRealChildren()
    {
        var root = new CssLayoutPanel { Width = 180, Height = 120, Background = Brushes.White };
        var window = new Window
        {
            Width = 180,
            Height = 120,
            Content = root,
            Background = Brushes.White,
            SystemDecorations = SystemDecorations.None
        };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { height: 120px; margin: 0; width: 180px; }
            #stage { background: red; height: 120px; position: relative; width: 180px; }
            #stage::before {
                background: rgb(0, 192, 0);
                border-radius: 12px;
                content: "";
                height: 50px;
                left: 30px;
                position: absolute;
                top: 20px;
                width: 90px;
                z-index: 2;
            }
            #occluder {
                background: rgb(0, 0, 192);
                height: 50px;
                left: 40px;
                position: absolute;
                top: 30px;
                width: 90px;
                z-index: 1;
            }
            """;
        document.head.appendChild(style);
        var body = HostTestUtilities.GetElement(document.body);
        body.innerHTML = "<div id='stage'><div id='occluder'></div></div>";

        window.Show();
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        document.FlushPendingLayout();

        var stage = Assert.IsType<CssLayoutPanel>(
            HostTestUtilities.GetElement(body.querySelector("#stage")).Control);
        var pseudo = Assert.Single(stage.Children.OfType<DomGeneratedBackgroundControl>());
        Assert.Equal(2, pseudo.GetValue(Canvas.ZIndexProperty));
        using var frame = Assert.IsAssignableFrom<Bitmap>(window.CaptureRenderedFrame());
        Assert.Equal(Color.FromRgb(0, 192, 0), ReadPixel(frame, 50, 40));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static Color ReadPixel(Bitmap bitmap, int x, int y)
    {
        var bytes = new byte[4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(x, y, 1, 1),
                handle.AddrOfPinnedObject(),
                bytes.Length,
                4);
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
