using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using JavaScript.Avalonia;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public class CanvasRenderingContext2DTests
{
    [AvaloniaFact]
    public void FillRect_UsesLinearGradientBrush()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        var gradient = ctx.createLinearGradient(0, 0, 100, 0);
        gradient.addColorStop(0, "#ff0000");
        gradient.addColorStop(1, "#0000ff");
        ctx.fillStyle = gradient;

        ctx.fillRect(0, 0, 50, 10);

        var command = Assert.IsType<FillRectCommand>(surface.Commands.Single());
        var brush = Assert.IsType<ImmutableLinearGradientBrush>(command.Snapshot.FillBrush);
        Assert.Equal(new RelativePoint(new Point(0, 0), RelativeUnit.Absolute), brush.StartPoint);
        Assert.Equal(new RelativePoint(new Point(100, 0), RelativeUnit.Absolute), brush.EndPoint);
        Assert.Equal(2, brush.GradientStops.Count);
        Assert.Equal(0, brush.GradientStops[0].Offset);
        Assert.Equal(Colors.Red, brush.GradientStops[0].Color);
        Assert.Equal(1, brush.GradientStops[1].Offset);
        Assert.Equal(Colors.Blue, brush.GradientStops[1].Color);
    }

    [AvaloniaFact]
    public void DrawImage_RecordsDestinationRect()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(12, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        ctx.drawImage(bitmap, 5, 6, 30, 40);

        var command = Assert.IsType<DrawImageCommand>(surface.Commands.Single());
        var bounds = command.GetBounds();
        Assert.True(bounds.HasValue);
        Assert.Equal(new Rect(5, 6, 30, 40), bounds.Value);
    }

    [AvaloniaFact]
    public void CanvasAdorner_DoesNotDrawOverSiblingControls()
    {
        var canvasTarget = new Border
        {
            Width = 80,
            Height = 40,
            Background = Brushes.White
        };
        var sibling = new Border
        {
            Width = 80,
            Height = 40,
            Background = Brushes.Lime
        };
        var root = new StackPanel
        {
            Children =
            {
                canvasTarget,
                sibling
            }
        };
        var window = new Window
        {
            Width = 100,
            Height = 100,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();

        var ctx = Assert.IsType<CanvasRenderingContext2D>(CanvasContextBridge.GetContext(canvasTarget, "2d"));
        ctx.fillStyle = "#ff0000";
        ctx.fillRect(0, 0, 80, 120);

        Assert.Single(window.GetVisualDescendants().OfType<CanvasDrawingSurface>());

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            var belowTarget = ReadPixel(frame!, 10, 50);

            Assert.True(belowTarget.G > 180, $"Expected sibling to remain green, but got {belowTarget}.");
            Assert.True(belowTarget.R < 80, $"Expected canvas not to draw red over sibling, but got {belowTarget}.");
        }
    }

    private static Color ReadPixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), buffer.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        var format = bitmap.Format ?? PixelFormat.Bgra8888;
        if (format == PixelFormat.Bgra8888 || format == PixelFormat.Rgb32)
        {
            return Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
        }

        if (format == PixelFormat.Rgba8888)
        {
            return Color.FromArgb(buffer[3], buffer[0], buffer[1], buffer[2]);
        }

        throw new NotSupportedException($"Unsupported screenshot pixel format: {format}.");
    }
}
