using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace JavaScript.Avalonia;

internal sealed record CanvasWebGlFrame(
    double Width,
    double Height,
    CanvasWebGlColor ClearColor,
    bool HasClear,
    IReadOnlyList<CanvasWebGlTriangle> Triangles);

internal readonly record struct CanvasWebGlColor(double R, double G, double B, double A);

internal readonly record struct CanvasWebGlPoint(double X, double Y, double Z);

internal readonly record struct CanvasWebGlTriangle(
    CanvasWebGlPoint A,
    CanvasWebGlPoint B,
    CanvasWebGlPoint C,
    CanvasWebGlColor Color)
{
    public double Depth => (A.Z + B.Z + C.Z) / 3.0;
}

internal sealed class CanvasWebGlDrawOperation : ICustomDrawOperation
{
    private readonly CanvasDrawingSurface _owner;
    private readonly CanvasWebGlFrame _frame;

    public CanvasWebGlDrawOperation(CanvasDrawingSurface owner, Rect bounds, CanvasWebGlFrame frame)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Bounds = bounds;
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public Rect Bounds { get; }

    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

    public bool HitTest(Point p) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
        {
            _owner.SetWebGlRenderBackend("Avalonia DrawingContext fallback");
            return;
        }

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        if (canvas is null)
        {
            _owner.SetWebGlRenderBackend("Skia unavailable");
            return;
        }

        _owner.SetWebGlRenderBackend(lease.GrContext is not null ? "Skia GRContext" : "Skia CPU");

        canvas.Save();
        try
        {
            var clip = new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom);
            canvas.ClipRect(clip, SKClipOperation.Intersect, antialias: false);
            canvas.Translate((float)Bounds.X, (float)Bounds.Y);
            if (_frame.Width > 0 && _frame.Height > 0)
            {
                float scaleX = (float)(Bounds.Width / _frame.Width);
                float scaleY = (float)(Bounds.Height / _frame.Height);
                canvas.Scale(scaleX, scaleY);
            }

            if (_frame.HasClear)
            {
                using var clearPaint = new SKPaint
                {
                    Color = ToSkColor(_frame.ClearColor),
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };
                canvas.DrawRect(0, 0, (float)_frame.Width, (float)_frame.Height, clearPaint);
            }

            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            foreach (var triangle in _frame.Triangles)
            {
                fillPaint.Color = ToSkColor(triangle.Color);
                using var path = new SKPath();
                path.MoveTo((float)triangle.A.X, (float)triangle.A.Y);
                path.LineTo((float)triangle.B.X, (float)triangle.B.Y);
                path.LineTo((float)triangle.C.X, (float)triangle.C.Y);
                path.Close();
                canvas.DrawPath(path, fillPaint);
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    public void Dispose()
    {
    }

    private static SKColor ToSkColor(CanvasWebGlColor color)
    {
        var r = ToByte(color.R);
        var g = ToByte(color.G);
        var b = ToByte(color.B);
        var a = ToByte(color.A);
        return new SKColor(r, g, b, a);
    }

    private static byte ToByte(double value)
        => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
