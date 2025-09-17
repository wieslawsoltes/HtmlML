using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
}
