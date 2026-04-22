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
    public void CreatePattern_UsesTiledImageBrush()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        var pattern = ctx.createPattern(bitmap, "repeat");

        Assert.NotNull(pattern);
        ctx.fillStyle = pattern;
        ctx.fillRect(0, 0, 20, 20);

        var command = Assert.IsType<FillRectCommand>(surface.Commands.Single());
        var brush = Assert.IsAssignableFrom<ITileBrush>(command.Snapshot.FillBrush);
        Assert.Equal(TileMode.Tile, brush.TileMode);
        Assert.Equal(new RelativeRect(new Rect(0, 0, 4, 4), RelativeUnit.Absolute), brush.DestinationRect);
    }

    [AvaloniaFact]
    public void ImageData_RoundTripsPixelsForCanvasInterop()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;
        var imageData = ctx.createImageData(2, 1);
        imageData.data[0] = 10;
        imageData.data[1] = 20;
        imageData.data[2] = 30;
        imageData.data[3] = 255;
        imageData.data[4] = 40;
        imageData.data[5] = 50;
        imageData.data[6] = 60;
        imageData.data[7] = 128;

        ctx.putImageData(imageData, 0, 0);

        var copy = ctx.getImageData(0, 0, 2, 1);
        Assert.Equal(imageData.data, copy.data);
        Assert.True(ctx.TryGetRgbaPixels(out var width, out var height, out var pixels));
        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal(imageData.data, pixels);
    }

    [AvaloniaFact]
    public void PutImageData_RendersPixelData()
    {
        var surface = new CanvasDrawingSurface
        {
            Width = 4,
            Height = 4
        };
        var window = new Window
        {
            Width = 4,
            Height = 4,
            Content = surface
        };
        var ctx = surface.Context;
        var imageData = ctx.createImageData(1, 1);
        imageData.data[0] = 255;
        imageData.data[1] = 0;
        imageData.data[2] = 0;
        imageData.data[3] = 255;

        ctx.putImageData(imageData, 1, 1);
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var rendered = ReadPixel(frame!, 1, 1);
        Assert.True(rendered.R > 200, $"Expected putImageData to render a red pixel, but got {rendered}.");
    }

    [AvaloniaFact]
    public void DrawImage_FromCanvasDoesNotDuplicatePixelAndSurfacePaths()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var sourceCanvas = HostTestUtilities.GetElement(host.Document.createElement("canvas"));
        sourceCanvas.width = 4;
        sourceCanvas.height = 4;
        var sourceContext = Assert.IsType<CanvasRenderingContext2D>(sourceCanvas.getContext("2d"));
        var imageData = sourceContext.createImageData(1, 1);
        imageData.data[0] = 0;
        imageData.data[1] = 255;
        imageData.data[2] = 0;
        imageData.data[3] = 255;
        sourceContext.putImageData(imageData, 0, 0);

        var target = new CanvasDrawingSurface();
        var targetContext = target.Context;
        targetContext.drawImage(sourceCanvas, 0, 0);

        Assert.Single(target.Commands);
        Assert.IsType<DrawCanvasSurfaceCommand>(target.Commands[0]);
    }

    [AvaloniaFact]
    public void CanvasDimensionAssignment_ResetsContextStateAndCommands()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var canvas = HostTestUtilities.GetElement(host.Document.createElement("canvas"));
        canvas.width = 4;
        canvas.height = 4;
        var context = Assert.IsType<CanvasRenderingContext2D>(canvas.getContext("2d"));
        context.fillStyle = "#ff0000";
        context.fillRect(0, 0, 4, 4);

        canvas.width = 4;

        Assert.Equal(0, context.CommandCount);
        context.fillRect(0, 0, 1, 1);
        Assert.True(CanvasContextBridge.TryGetSurface(canvas.Control, out var surface));
        var command = Assert.IsType<FillRectCommand>(surface.Commands.Single());
        Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(command.Snapshot.FillBrush).Color);
    }

    [AvaloniaFact]
    public void MeasureText_ReturnsWidth()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        ctx.font = "18px Segoe UI";

        var metrics = Assert.IsType<CanvasTextMetrics>(ctx.measureText("xterm"));

        Assert.True(metrics.width > 0);
    }

    [AvaloniaFact]
    public void TransformAccessors_ReturnCanvasCompatibleMatrices()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        ctx.setTransform(2, 3, 4, 5, 6, 7);

        Assert.Equal(new[] { 2d, 3d, 4d, 5d, 6d, 7d }, ctx.mozCurrentTransform);
        Assert.Equal(new[] { -2.5d, 1.5d, 2d, -1d, 1d, -2d }, ctx.mozCurrentTransformInverse);

        var transform = Assert.IsType<CanvasDomMatrix>(ctx.getTransform());
        Assert.Equal(2, transform.a);
        Assert.Equal(3, transform.b);
        Assert.Equal(4, transform.c);
        Assert.Equal(5, transform.d);
        Assert.Equal(6, transform.e);
        Assert.Equal(7, transform.f);
    }

    [AvaloniaFact]
    public void StrokeText_RecordsTextCommand()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        ctx.strokeStyle = "#ff0000";
        ctx.strokeText("PDF.js", 12, 24);

        var command = Assert.IsType<StrokeTextCommand>(surface.Commands.Single());
        Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(command.Snapshot.StrokeBrush).Color);
    }

    [AvaloniaFact]
    public void Font_ParsesCssFamilyListAndStyle()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        ctx.font = "italic bold 14px Menlo, Monaco, \"Courier New\", monospace";
        ctx.fillText("x", 0, 0);

        var command = Assert.IsType<FillTextCommand>(surface.Commands.Single());
        Assert.Equal(14, command.Snapshot.FontSize);
        Assert.Equal(FontStyle.Italic, command.Snapshot.Typeface.Style);
        Assert.Equal(FontWeight.Bold, command.Snapshot.Typeface.Weight);
        Assert.Contains("Menlo", command.Snapshot.Typeface.FontFamily.Name, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void Stroke_PreservesLineDashState()
    {
        var surface = new CanvasDrawingSurface();
        var ctx = surface.Context;

        ctx.setLineDash(new[] { 4d, 2d });
        ctx.lineDashOffset = 1.5;
        ctx.beginPath();
        ctx.moveTo(0, 0);
        ctx.lineTo(12, 0);
        ctx.stroke();

        var command = Assert.IsType<StrokePathCommand>(surface.Commands.Single());
        Assert.Equal(new[] { 4d, 2d }, command.Snapshot.LineDash);
        Assert.Equal(1.5, command.Snapshot.LineDashOffset);
    }

    [AvaloniaFact]
    public void Arc_RendersCurvedCircleSegments()
    {
        var surface = new CanvasDrawingSurface
        {
            Width = 100,
            Height = 100
        };
        var window = new Window
        {
            Width = 100,
            Height = 100,
            Content = surface
        };
        var ctx = surface.Context;
        ctx.fillStyle = "#ff0000";
        ctx.beginPath();
        ctx.arc(50, 50, 30, 0, Math.PI * 2);
        ctx.fill();

        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            var diagonalInsideCircle = ReadPixel(frame!, 68, 32);

            Assert.True(
                diagonalInsideCircle.R > 150,
                $"Expected a filled circular arc at the diagonal sample point, but got {diagonalInsideCircle}.");
        }
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

    [AvaloniaFact]
    public void WebGlSurface_UsesSkiaFallbackForRegularControlsByDefault()
    {
        var canvasTarget = new Border
        {
            Width = 80,
            Height = 40,
            Background = Brushes.White
        };
        var window = new Window
        {
            Width = 100,
            Height = 80,
            Content = new VisualLayerManager { Child = canvasTarget }
        };

        window.Show();

        var context = Assert.IsType<CanvasWebGlRenderingContext>(CanvasContextBridge.GetContext(canvasTarget, "webgl"));

        Assert.Null(canvasTarget.Child);
        Assert.Equal("not rendered", context.RenderBackend);
    }

    [AvaloniaFact]
    public void WebGlTexSubImage2D_UsesImageSourceOverloadArguments()
    {
        var context = new CanvasWebGlRenderingContext(new CanvasDrawingSurface());
        var texture = context.createTexture();
        var pixels = new byte[16];

        context.pixelStorei(context.UNPACK_ALIGNMENT, 1);
        context.pixelStorei(context.UNPACK_FLIP_Y_WEBGL, true);
        context.bindTexture(context.TEXTURE_2D, texture);
        context.texSubImage2D(context.TEXTURE_2D, 0, 0, 0, context.RGBA, context.UNSIGNED_BYTE, pixels);

        Assert.Equal(context.RGBA, texture.Format);
        Assert.Equal(context.UNSIGNED_BYTE, texture.Type);
        Assert.Same(pixels, texture.Pixels);
        Assert.Equal(1, texture.UnpackAlignment);
        Assert.True(texture.UnpackFlipY);
        Assert.True(texture.NativeDirty);
    }

    [AvaloniaFact]
    public void WebGlSurface_UsesDedicatedOpenGlTarget()
    {
        var canvasTarget = new CanvasOpenGlDrawingSurface
        {
            Width = 80,
            Height = 40
        };
        var window = new Window
        {
            Width = 100,
            Height = 80,
            Content = new VisualLayerManager { Child = canvasTarget }
        };

        window.Show();

        var context = Assert.IsType<CanvasWebGlRenderingContext>(CanvasContextBridge.GetContext(canvasTarget, "webgl"));

        Assert.Same(context, canvasTarget.Context);
        Assert.NotNull(canvasTarget.GetVisualParent());
        Assert.Empty(window.GetVisualDescendants().OfType<CanvasDrawingSurface>());
    }

    [AvaloniaFact]
    public void NativeWebGlSurface_QueuesDrawsWhileOpenGlInitializes()
    {
        var canvasTarget = new CanvasOpenGlDrawingSurface
        {
            Width = 80,
            Height = 40
        };
        var window = new Window
        {
            Width = 100,
            Height = 80,
            Content = new VisualLayerManager { Child = canvasTarget }
        };

        window.Show();

        var context = Assert.IsType<CanvasWebGlRenderingContext>(CanvasContextBridge.GetContext(canvasTarget, "webgl"));

        context.drawArrays(context.TRIANGLES, 0, 3);

        Assert.Equal(1, context.DrawCallCount);
        Assert.Equal(1, context.TriangleCount);
        Assert.Equal("Queued drawArrays mode 4 with 3 vertices", context.LastDrawStatus);
        Assert.Empty(window.GetVisualDescendants().OfType<CanvasDrawingSurface>());
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
