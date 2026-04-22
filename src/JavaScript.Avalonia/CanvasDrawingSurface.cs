using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace JavaScript.Avalonia;



internal sealed class CanvasDrawingSurface : Control
{
    
    private readonly List<CanvasDrawCommand> _commands = new();
    private CanvasRenderingContext2D? _context;
    private CanvasWebGlFrame? _webGlFrame;

    internal IReadOnlyList<CanvasDrawCommand> Commands => _commands;
    internal string LastWebGlRenderBackend { get; private set; } = "not rendered";

    public CanvasDrawingSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    internal CanvasRenderingContext2D Context => _context ??= new CanvasRenderingContext2D(this);


    internal void AddCommand(CanvasDrawCommand command)
    {
        if (command is null)
        {
            return;
        }

        _commands.Add(command);
        InvalidateVisual();
    }

    internal void ClearAll()
    {
        if (_commands.Count == 0)
        {
            return;
        }

        _commands.Clear();
        InvalidateVisual();
    }

    internal void ClearRect(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var canvasBounds = new Rect(Bounds.Size);
        if (rect.Contains(canvasBounds))
        {
            ClearAll();
            return;
        }

        var removed = _commands.RemoveAll(cmd =>
        {
            var bounds = cmd.GetBounds();
            return bounds.HasValue && rect.Contains(bounds.Value);
        });

        if (removed > 0)
        {
            InvalidateVisual();
        }
    }

    internal void SetWebGlFrame(CanvasWebGlFrame? frame)
    {
        _webGlFrame = frame;
        InvalidateVisual();
    }

    internal void SetWebGlRenderBackend(string backend)
    {
        LastWebGlRenderBackend = backend;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // AdornerLayer clipping follows explicit clip ancestors; the canvas must
        // also clip its own Render output so oversized draws cannot cover siblings.
        using (context.PushClip(bounds))
        {
            RenderCommands(context);

            if (_webGlFrame is not null)
            {
                context.Custom(new CanvasWebGlDrawOperation(this, bounds, _webGlFrame));
            }
        }
    }

    internal void RenderCommands(DrawingContext context)
    {
        foreach (var command in _commands)
        {
            command.Render(context);
        }
    }

    internal FormattedText CreateFormattedText(string text, CanvasStateSnapshot state)
    {
        return new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            state.Typeface,
            state.FontSize,
            state.FillBrush);
    }
}

internal abstract class CanvasDrawCommand
{
    protected CanvasDrawCommand(CanvasStateSnapshot state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    protected CanvasStateSnapshot State { get; }

    internal CanvasStateSnapshot Snapshot => State;

    public void Render(DrawingContext context)
    {
        IDisposable? transform = null;
        IDisposable? clip = null;
        IDisposable? opacity = null;

        try
        {
            if (!State.Transform.IsIdentity)
            {
                transform = context.PushTransform(State.Transform);
            }

            if (State.ClipGeometry is not null)
            {
                clip = context.PushGeometryClip(State.ClipGeometry);
            }

            if (State.GlobalAlpha < 1.0)
            {
                opacity = context.PushOpacity(State.GlobalAlpha);
            }

            RenderCore(context);
        }
        finally
        {
            opacity?.Dispose();
            clip?.Dispose();
            transform?.Dispose();
        }
    }

    protected abstract void RenderCore(DrawingContext context);

    public virtual Rect? GetBounds() => null;
}

internal sealed class FillRectCommand : CanvasDrawCommand
{
    private readonly Rect _rect;

    public FillRectCommand(Rect rect, CanvasStateSnapshot state)
        : base(state)
    {
        _rect = rect;
    }

    protected override void RenderCore(DrawingContext context)
    {
        if (State.FillBrush is not null && _rect.Width > 0 && _rect.Height > 0)
        {
            context.FillRectangle(State.FillBrush, _rect);
        }
    }

    public override Rect? GetBounds() => _rect;
}

internal sealed class StrokeRectCommand : CanvasDrawCommand
{
    private readonly Rect _rect;

    public StrokeRectCommand(Rect rect, CanvasStateSnapshot state)
        : base(state)
    {
        _rect = rect;
    }

    protected override void RenderCore(DrawingContext context)
    {
        if (_rect.Width <= 0 || _rect.Height <= 0)
        {
            return;
        }

        var pen = State.CreatePen();
        if (pen is null)
        {
            return;
        }

        context.DrawRectangle(null, pen, _rect);
    }

    public override Rect? GetBounds()
    {
        var pen = State.CreatePen();
        return pen is null ? _rect : _rect.Inflate(pen.Thickness / 2);
    }
}

internal sealed class FillPathCommand : CanvasDrawCommand
{
    private readonly StreamGeometry _geometry;

    public FillPathCommand(StreamGeometry geometry, CanvasStateSnapshot state)
        : base(state)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
    }

    protected override void RenderCore(DrawingContext context)
    {
        if (State.FillBrush is not null)
        {
            context.DrawGeometry(State.FillBrush, null, _geometry);
        }
    }

    public override Rect? GetBounds() => _geometry.Bounds;
}

internal sealed class StrokePathCommand : CanvasDrawCommand
{
    private readonly StreamGeometry _geometry;

    public StrokePathCommand(StreamGeometry geometry, CanvasStateSnapshot state)
        : base(state)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
    }

    protected override void RenderCore(DrawingContext context)
    {
        var pen = State.CreatePen();
        if (pen is null)
        {
            return;
        }

        context.DrawGeometry(null, pen, _geometry);
    }

    public override Rect? GetBounds()
    {
        var pen = State.CreatePen();
        if (pen is null)
        {
            return _geometry.Bounds;
        }

        return _geometry.GetRenderBounds(pen);
    }
}

internal sealed class FillTextCommand : CanvasDrawCommand
{
    private readonly string _text;
    private readonly Point _origin;

    public FillTextCommand(string text, Point origin, CanvasStateSnapshot state)
        : base(state)
    {
        _text = text ?? string.Empty;
        _origin = origin;
    }

    protected override void RenderCore(DrawingContext context)
    {
        if (string.IsNullOrEmpty(_text) || State.FillBrush is null)
        {
            return;
        }

        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            State.Typeface,
            State.FontSize,
            State.FillBrush);

        formatted.SetForegroundBrush(State.FillBrush);
        var origin = AdjustOrigin(formatted);
        context.DrawText(formatted, origin);
    }

    private Point AdjustOrigin(FormattedText formatted)
    {
        var x = _origin.X;
        var y = _origin.Y;

        switch (State.TextAlign)
        {
            case CanvasTextAlign.Center:
                x -= formatted.Width / 2;
                break;
            case CanvasTextAlign.Right:
            case CanvasTextAlign.End:
                x -= formatted.Width;
                break;
            case CanvasTextAlign.Left:
            case CanvasTextAlign.Start:
            default:
                break;
        }

        switch (State.TextBaseline)
        {
            case CanvasTextBaseline.Top:
                break;
            case CanvasTextBaseline.Hanging:
                y += formatted.Baseline * 0.2;
                break;
            case CanvasTextBaseline.Middle:
                y -= formatted.Height / 2;
                break;
            case CanvasTextBaseline.Alphabetic:
                y -= formatted.Baseline;
                break;
            case CanvasTextBaseline.Bottom:
                y -= formatted.Height;
                break;
            case CanvasTextBaseline.Ideographic:
                y -= formatted.Height * 0.9;
                break;
        }

        return new Point(x, y);
    }

    public override Rect? GetBounds()
    {
        if (string.IsNullOrEmpty(_text))
        {
            return null;
        }

        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            State.Typeface,
            State.FontSize,
            State.FillBrush);

        var origin = AdjustOrigin(formatted);
        return new Rect(origin, new Size(formatted.Width, formatted.Height));
    }
}

internal sealed class StrokeTextCommand : CanvasDrawCommand
{
    private readonly string _text;
    private readonly Point _origin;

    public StrokeTextCommand(string text, Point origin, CanvasStateSnapshot state)
        : base(state)
    {
        _text = text ?? string.Empty;
        _origin = origin;
    }

    protected override void RenderCore(DrawingContext context)
    {
        if (string.IsNullOrEmpty(_text) || State.StrokeBrush is null)
        {
            return;
        }

        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            State.Typeface,
            State.FontSize,
            State.StrokeBrush);

        formatted.SetForegroundBrush(State.StrokeBrush);
        context.DrawText(formatted, AdjustOrigin(formatted));
    }

    private Point AdjustOrigin(FormattedText formatted)
    {
        var x = _origin.X;
        var y = _origin.Y;

        switch (State.TextAlign)
        {
            case CanvasTextAlign.Center:
                x -= formatted.Width / 2;
                break;
            case CanvasTextAlign.Right:
            case CanvasTextAlign.End:
                x -= formatted.Width;
                break;
            case CanvasTextAlign.Left:
            case CanvasTextAlign.Start:
            default:
                break;
        }

        switch (State.TextBaseline)
        {
            case CanvasTextBaseline.Top:
                break;
            case CanvasTextBaseline.Hanging:
                y += formatted.Baseline * 0.2;
                break;
            case CanvasTextBaseline.Middle:
                y -= formatted.Height / 2;
                break;
            case CanvasTextBaseline.Alphabetic:
                y -= formatted.Baseline;
                break;
            case CanvasTextBaseline.Bottom:
                y -= formatted.Height;
                break;
            case CanvasTextBaseline.Ideographic:
                y -= formatted.Height * 0.9;
                break;
        }

        return new Point(x, y);
    }

    public override Rect? GetBounds()
    {
        if (string.IsNullOrEmpty(_text))
        {
            return null;
        }

        var formatted = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            State.Typeface,
            State.FontSize,
            State.StrokeBrush);

        return new Rect(AdjustOrigin(formatted), new Size(formatted.Width, formatted.Height));
    }
}

internal sealed class DrawImageCommand : CanvasDrawCommand
{
    private readonly IImage _image;
    private readonly Rect _sourceRect;
    private readonly Rect _destinationRect;

    public DrawImageCommand(IImage image, Rect sourceRect, Rect destinationRect, CanvasStateSnapshot state)
        : base(state)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _sourceRect = sourceRect;
        _destinationRect = destinationRect;
    }

    protected override void RenderCore(DrawingContext context)
    {
        context.DrawImage(_image, _sourceRect, _destinationRect);
    }

    public override Rect? GetBounds() => _destinationRect;
}

internal sealed class DrawCanvasSurfaceCommand : CanvasDrawCommand
{
    private readonly IReadOnlyList<CanvasDrawCommand> _commands;
    private readonly Rect _sourceRect;
    private readonly Rect _destinationRect;

    public DrawCanvasSurfaceCommand(CanvasDrawingSurface surface, Rect sourceRect, Rect destinationRect, CanvasStateSnapshot state)
        : base(state)
    {
        _commands = new List<CanvasDrawCommand>((surface ?? throw new ArgumentNullException(nameof(surface))).Commands);
        _sourceRect = sourceRect;
        _destinationRect = destinationRect;
    }

    protected override void RenderCore(DrawingContext context)
    {
        if (_sourceRect.Width <= 0 || _sourceRect.Height <= 0 || _destinationRect.Width <= 0 || _destinationRect.Height <= 0)
        {
            return;
        }

        var scaleX = _destinationRect.Width / _sourceRect.Width;
        var scaleY = _destinationRect.Height / _sourceRect.Height;
        var transform = new Matrix(
            scaleX,
            0,
            0,
            scaleY,
            _destinationRect.X - (_sourceRect.X * scaleX),
            _destinationRect.Y - (_sourceRect.Y * scaleY));

        using (context.PushClip(_destinationRect))
        using (context.PushTransform(transform))
        {
            foreach (var command in _commands)
            {
                command.Render(context);
            }
        }
    }

    public override Rect? GetBounds() => _destinationRect;
}

internal sealed class CanvasTextMetrics
{
    public CanvasTextMetrics(double width)
    {
        this.width = width;
    }

    public double width { get; }
}

internal sealed class CanvasDomMatrix
{
    public CanvasDomMatrix(Matrix matrix)
    {
        a = matrix.M11;
        b = matrix.M12;
        c = matrix.M21;
        d = matrix.M22;
        e = matrix.M31;
        f = matrix.M32;
        m11 = a;
        m12 = b;
        m21 = c;
        m22 = d;
        m41 = e;
        m42 = f;
        is2D = true;
        isIdentity = matrix.IsIdentity;
    }

    public double a { get; }

    public double b { get; }

    public double c { get; }

    public double d { get; }

    public double e { get; }

    public double f { get; }

    public double m11 { get; }

    public double m12 { get; }

    public double m21 { get; }

    public double m22 { get; }

    public double m41 { get; }

    public double m42 { get; }

    public bool is2D { get; }

    public bool isIdentity { get; }

    public double[] toFloat32Array() => new[] { a, b, 0, 0, c, d, 0, 0, 0, 0, 1, 0, e, f, 0, 1 };

    public double[] toFloat64Array() => toFloat32Array();
}

internal sealed class CanvasImageData
{
    public CanvasImageData(int width, int height)
        : this(width, height, new byte[Math.Max(0, width) * Math.Max(0, height) * 4])
    {
    }

    public CanvasImageData(int width, int height, byte[] data)
    {
        this.width = Math.Max(0, width);
        this.height = Math.Max(0, height);
        this.data = data ?? Array.Empty<byte>();
    }

    public int width { get; }

    public int height { get; }

    public byte[] data { get; }
}

internal sealed class CanvasRenderingContext2D
{
    private readonly CanvasDrawingSurface _owner;
    private readonly CanvasPathBuilder _path = new();
    private CanvasState _state = CanvasState.CreateDefault();
    private readonly Stack<CanvasState> _stateStack = new();
    private byte[]? _rgbaPixels;
    private int _rgbaWidth;
    private int _rgbaHeight;

    internal CanvasRenderingContext2D(CanvasDrawingSurface owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public object? canvas { get; set; }

    internal int CommandCount => _owner.Commands.Count;

    public object? fillStyle
    {
        get => _state.FillStyleValue;
        set => _state.SetFillStyle(value);
    }

    public object? strokeStyle
    {
        get => _state.StrokeStyleValue;
        set => _state.SetStrokeStyle(value);
    }

    public double lineWidth
    {
        get => _state.LineWidth;
        set => _state.LineWidth = value > 0 ? value : _state.LineWidth;
    }

    public string lineCap
    {
        get => _state.LineCap.ToString().ToLowerInvariant();
        set => _state.LineCap = CanvasStrokeParser.ParseLineCap(value, _state.LineCap);
    }

    public string lineJoin
    {
        get => _state.LineJoin.ToString().ToLowerInvariant();
        set => _state.LineJoin = CanvasStrokeParser.ParseLineJoin(value, _state.LineJoin);
    }

    public double miterLimit
    {
        get => _state.MiterLimit;
        set => _state.MiterLimit = value > 0 ? value : _state.MiterLimit;
    }

    public double globalAlpha
    {
        get => _state.GlobalAlpha;
        set => _state.GlobalAlpha = Math.Clamp(value, 0.0, 1.0);
    }

    public double lineDashOffset
    {
        get => _state.LineDashOffset;
        set => _state.LineDashOffset = value;
    }

    public string font
    {
        get => _state.Font;
        set => _state.UpdateFont(value);
    }

    public string textAlign
    {
        get => CanvasTextParser.ToString(_state.TextAlign);
        set => _state.TextAlign = CanvasTextParser.ParseAlign(value, _state.TextAlign);
    }

    public string textBaseline
    {
        get => CanvasTextParser.ToString(_state.TextBaseline);
        set => _state.TextBaseline = CanvasTextParser.ParseBaseline(value, _state.TextBaseline);
    }

    public bool imageSmoothingEnabled { get; set; } = true;

    public string globalCompositeOperation { get; set; } = "source-over";

    public string shadowColor { get; set; } = string.Empty;

    public double shadowBlur { get; set; }

    public double shadowOffsetX { get; set; }

    public double shadowOffsetY { get; set; }

    public double[] mozCurrentTransform => ToCanvasMatrix(_state.Transform);

    public double[] mozCurrentTransformInverse => ToCanvasMatrix(InvertOrIdentity(_state.Transform));

    public void save()
    {
        _stateStack.Push(_state.Clone());
    }

    public void restore()
    {
        if (_stateStack.Count > 0)
        {
            _state = _stateStack.Pop();
        }
    }

    public void resetTransform()
    {
        _state.Transform = Matrix.Identity;
    }

    public void setTransform(double a, double b, double c, double d, double e, double f)
    {
        _state.Transform = new Matrix(a, b, c, d, e, f);
    }

    public void transform(double a, double b, double c, double d, double e, double f)
    {
        var m = new Matrix(a, b, c, d, e, f);
        _state.Transform = _state.Transform * m;
    }

    public void translate(double x, double y)
    {
        var translation = Matrix.CreateTranslation(x, y);
        _state.Transform = _state.Transform * translation;
    }

    public void scale(double x, double y)
    {
        var scale = Matrix.CreateScale(x, y);
        _state.Transform = _state.Transform * scale;
    }

    public void rotate(double angle)
    {
        var rotation = Matrix.CreateRotation(angle);
        _state.Transform = _state.Transform * rotation;
    }

    public void beginPath()
    {
        _path.Clear();
    }

    public void closePath()
    {
        _path.ClosePath();
    }

    public void moveTo(double x, double y)
    {
        _path.MoveTo(x, y);
    }

    public void lineTo(double x, double y)
    {
        _path.LineTo(x, y);
    }

    public void bezierCurveTo(double cp1x, double cp1y, double cp2x, double cp2y, double x, double y)
    {
        _path.CubicBezierTo(cp1x, cp1y, cp2x, cp2y, x, y);
    }

    public void quadraticCurveTo(double cpx, double cpy, double x, double y)
    {
        _path.QuadraticBezierTo(cpx, cpy, x, y);
    }

    public void arc(double x, double y, double radius, double startAngle, double endAngle, bool counterclockwise = false)
    {
        _path.Arc(x, y, radius, startAngle, endAngle, counterclockwise);
    }

    public void rect(double x, double y, double width, double height)
    {
        _path.Rect(x, y, width, height);
    }

    public void clip()
    {
        if (_path.IsEmpty)
        {
            return;
        }

        _state.ClipGeometry = _path.BuildGeometry();
    }

    public void clip(string fillRule)
    {
        clip();
    }

    public void clip(object? pathOrFillRule)
    {
        clip();
    }

    public void clip(object? path, object? fillRule)
    {
        clip();
    }

    public void setLineDash(object? segments)
    {
        _state.LineDash = CanvasStrokeParser.ParseLineDash(segments);
    }

    public double[] getLineDash()
    {
        return _state.LineDash.Length == 0 ? Array.Empty<double>() : (double[])_state.LineDash.Clone();
    }

    public CanvasGradient createLinearGradient(double x0, double y0, double x1, double y1)
    {
        return new CanvasLinearGradient(x0, y0, x1, y1);
    }

    public CanvasGradient createRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1)
    {
        return new CanvasRadialGradient(x0, y0, r0, x1, y1, r1);
    }

    public CanvasPattern? createPattern(object image, string repetition)
    {
        if (!TryCreatePatternSource(image, out var source, out var width, out var height))
        {
            return null;
        }

        return new CanvasPattern(source, width, height, repetition);
    }

    public CanvasPattern? createPattern(object image, object? repetition)
        => createPattern(image, Convert.ToString(repetition, CultureInfo.InvariantCulture) ?? "repeat");

    public void stroke()
    {
        if (_path.IsEmpty)
        {
            return;
        }

        var geometry = _path.BuildGeometry();
        _owner.AddCommand(new StrokePathCommand(geometry, _state.CreateSnapshot()));
    }

    public void fill()
    {
        if (_path.IsEmpty)
        {
            return;
        }

        var geometry = _path.BuildGeometry();
        _owner.AddCommand(new FillPathCommand(geometry, _state.CreateSnapshot()));
    }

    public void fill(string fillRule)
    {
        fill();
    }

    public void fill(object? pathOrFillRule)
    {
        fill();
    }

    public void fill(object? path, object? fillRule)
    {
        fill();
    }

    public void fillRect(double x, double y, double width, double height)
    {
        var rect = new Rect(x, y, width, height);
        _owner.AddCommand(new FillRectCommand(rect, _state.CreateSnapshot()));
    }

    public void strokeRect(double x, double y, double width, double height)
    {
        var rect = new Rect(x, y, width, height);
        _owner.AddCommand(new StrokeRectCommand(rect, _state.CreateSnapshot()));
    }

    public void clearRect(double x, double y, double width, double height)
    {
        var rect = new Rect(x, y, width, height);
        _owner.ClearRect(rect);
    }

    public void fillText(string text, double x, double y)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var origin = new Point(x, y);
        _owner.AddCommand(new FillTextCommand(text, origin, _state.CreateSnapshot()));
    }

    public void fillText(string text, double x, double y, double maxWidth)
    {
        fillText(text, x, y);
    }

    public void fillText(string text, double x, double y, object? maxWidth)
    {
        fillText(text, x, y);
    }

    public void strokeText(string text, double x, double y)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var origin = new Point(x, y);
        _owner.AddCommand(new StrokeTextCommand(text, origin, _state.CreateSnapshot()));
    }

    public void strokeText(string text, double x, double y, double maxWidth)
    {
        strokeText(text, x, y);
    }

    public void strokeText(string text, double x, double y, object? maxWidth)
    {
        strokeText(text, x, y);
    }

    public object measureText(string text)
    {
        var formatted = _owner.CreateFormattedText(text ?? string.Empty, _state.CreateSnapshot());
        return new CanvasTextMetrics(formatted.Width);
    }

    public object getTransform()
        => new CanvasDomMatrix(_state.Transform);

    public CanvasImageData createImageData(double sw, double sh)
        => new(Math.Max(0, (int)Math.Round(sw)), Math.Max(0, (int)Math.Round(sh)));

    public CanvasImageData createImageData(CanvasImageData imageData)
        => new(imageData?.width ?? 0, imageData?.height ?? 0);

    public CanvasImageData getImageData(double sx, double sy, double sw, double sh)
    {
        var width = Math.Max(0, (int)Math.Round(sw));
        var height = Math.Max(0, (int)Math.Round(sh));
        var data = new byte[width * height * 4];
        if (_rgbaPixels is null || width == 0 || height == 0)
        {
            return new CanvasImageData(width, height, data);
        }

        var sourceX = (int)Math.Round(sx);
        var sourceY = (int)Math.Round(sy);
        for (var y = 0; y < height; y++)
        {
            var srcY = sourceY + y;
            if (srcY < 0 || srcY >= _rgbaHeight)
            {
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                var srcX = sourceX + x;
                if (srcX < 0 || srcX >= _rgbaWidth)
                {
                    continue;
                }

                var source = ((srcY * _rgbaWidth) + srcX) * 4;
                var target = ((y * width) + x) * 4;
                data[target] = _rgbaPixels[source];
                data[target + 1] = _rgbaPixels[source + 1];
                data[target + 2] = _rgbaPixels[source + 2];
                data[target + 3] = _rgbaPixels[source + 3];
            }
        }

        return new CanvasImageData(width, height, data);
    }

    public void putImageData(CanvasImageData imageData, double dx, double dy)
        => PutImageData(imageData, dx, dy);

    public void putImageData(CanvasImageData imageData, double dx, double dy, double dirtyX, double dirtyY, double dirtyWidth, double dirtyHeight)
        => PutImageData(imageData, dx + dirtyX, dy + dirtyY, dirtyX, dirtyY, dirtyWidth, dirtyHeight);

    internal bool TryGetRgbaPixels(out int width, out int height, out byte[] pixels)
    {
        width = _rgbaWidth;
        height = _rgbaHeight;
        pixels = _rgbaPixels is null ? Array.Empty<byte>() : (byte[])_rgbaPixels.Clone();
        return width > 0 && height > 0 && pixels.Length == width * height * 4;
    }

    public void drawImage(object image, double dx, double dy)
    {
        if (TryResolveImagePixels(image, out var pixelWidth, out var pixelHeight, out var pixelData))
        {
            var pixelSourceRect = new Rect(0, 0, pixelWidth, pixelHeight);
            BlitRgbaPixels(pixelData, pixelWidth, pixelHeight, pixelSourceRect, new Rect(dx, dy, pixelSourceRect.Width, pixelSourceRect.Height));
        }

        if (TryResolveCanvasSurface(image, out var surface, out var canvasSourceRect))
        {
            DrawCanvasSurface(surface, canvasSourceRect, new Rect(dx, dy, canvasSourceRect.Width, canvasSourceRect.Height));
            return;
        }

        if (!TryResolveImage(image, out var resolved, out var sourceRect))
        {
            return;
        }

        var destRect = new Rect(dx, dy, sourceRect.Width, sourceRect.Height);
        if (destRect.Width <= 0 || destRect.Height <= 0)
        {
            return;
        }

        _owner.AddCommand(new DrawImageCommand(resolved, sourceRect, destRect, _state.CreateSnapshot()));
    }

    public void drawImage(object image, double dx, double dy, double dWidth, double dHeight)
    {
        if (dWidth <= 0 || dHeight <= 0)
        {
            return;
        }

        if (TryResolveImagePixels(image, out var pixelWidth, out var pixelHeight, out var pixelData))
        {
            BlitRgbaPixels(pixelData, pixelWidth, pixelHeight, new Rect(0, 0, pixelWidth, pixelHeight), new Rect(dx, dy, dWidth, dHeight));
        }

        if (TryResolveCanvasSurface(image, out var surface, out var canvasSourceRect))
        {
            DrawCanvasSurface(surface, canvasSourceRect, new Rect(dx, dy, dWidth, dHeight));
            return;
        }

        if (!TryResolveImage(image, out var resolved, out var sourceRect))
        {
            return;
        }

        var destRect = new Rect(dx, dy, dWidth, dHeight);
        _owner.AddCommand(new DrawImageCommand(resolved, sourceRect, destRect, _state.CreateSnapshot()));
    }

    public void drawImage(object image, double sx, double sy, double sWidth, double sHeight, double dx, double dy, double dWidth, double dHeight)
    {
        if (sWidth <= 0 || sHeight <= 0 || dWidth <= 0 || dHeight <= 0)
        {
            return;
        }

        if (TryResolveImagePixels(image, out var pixelWidth, out var pixelHeight, out var pixelData))
        {
            var pixelSourceRect = new Rect(sx, sy, sWidth, sHeight).Intersect(new Rect(0, 0, pixelWidth, pixelHeight));
            if (pixelSourceRect.Width > 0 && pixelSourceRect.Height > 0)
            {
                BlitRgbaPixels(pixelData, pixelWidth, pixelHeight, pixelSourceRect, new Rect(dx, dy, dWidth, dHeight));
            }
        }

        if (TryResolveCanvasSurface(image, out var surface, out var canvasSourceRect))
        {
            var canvasCrop = new Rect(sx, sy, sWidth, sHeight);
            var canvasBounded = canvasCrop.Intersect(canvasSourceRect);
            if (canvasBounded.Width <= 0 || canvasBounded.Height <= 0)
            {
                return;
            }

            DrawCanvasSurface(surface, canvasBounded, new Rect(dx, dy, dWidth, dHeight));
            return;
        }

        if (!TryResolveImage(image, out var resolved, out var fullSourceRect))
        {
            return;
        }

        var crop = new Rect(sx, sy, sWidth, sHeight);
        var bounded = crop.Intersect(fullSourceRect);
        if (bounded.Width <= 0 || bounded.Height <= 0)
        {
            return;
        }

        var destRect = new Rect(dx, dy, dWidth, dHeight);
        _owner.AddCommand(new DrawImageCommand(resolved, bounded, destRect, _state.CreateSnapshot()));
    }

    private void PutImageData(CanvasImageData? imageData, double dx, double dy)
        => PutImageData(imageData, dx, dy, 0, 0, imageData?.width ?? 0, imageData?.height ?? 0);

    private void PutImageData(CanvasImageData? imageData, double dx, double dy, double sourceX, double sourceY, double sourceWidth, double sourceHeight)
    {
        if (imageData is null || imageData.width <= 0 || imageData.height <= 0 || imageData.data.Length < imageData.width * imageData.height * 4)
        {
            return;
        }

        var sourceRect = new Rect(sourceX, sourceY, sourceWidth, sourceHeight).Intersect(new Rect(0, 0, imageData.width, imageData.height));
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
        {
            return;
        }

        BlitRgbaPixels(imageData.data, imageData.width, imageData.height, sourceRect, new Rect(dx, dy, sourceRect.Width, sourceRect.Height));
    }

    private void BlitRgbaPixels(byte[] sourcePixels, int sourceWidth, int sourceHeight, Rect sourceRect, Rect destinationRect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourcePixels.Length < sourceWidth * sourceHeight * 4 || sourceRect.Width <= 0 || sourceRect.Height <= 0 || destinationRect.Width <= 0 || destinationRect.Height <= 0)
        {
            return;
        }

        var canvasWidth = GetCanvasPixelWidth(destinationRect.Right);
        var canvasHeight = GetCanvasPixelHeight(destinationRect.Bottom);
        EnsureRgbaBuffer(canvasWidth, canvasHeight);

        var startX = Math.Max(0, (int)Math.Floor(destinationRect.X));
        var startY = Math.Max(0, (int)Math.Floor(destinationRect.Y));
        var endX = Math.Min(_rgbaWidth, (int)Math.Ceiling(destinationRect.Right));
        var endY = Math.Min(_rgbaHeight, (int)Math.Ceiling(destinationRect.Bottom));

        for (var y = startY; y < endY; y++)
        {
            var sourceYRatio = (y + 0.5 - destinationRect.Y) / destinationRect.Height;
            var srcY = (int)Math.Floor(sourceRect.Y + (sourceYRatio * sourceRect.Height));
            srcY = Math.Clamp(srcY, 0, sourceHeight - 1);

            for (var x = startX; x < endX; x++)
            {
                var sourceXRatio = (x + 0.5 - destinationRect.X) / destinationRect.Width;
                var srcX = (int)Math.Floor(sourceRect.X + (sourceXRatio * sourceRect.Width));
                srcX = Math.Clamp(srcX, 0, sourceWidth - 1);

                var source = ((srcY * sourceWidth) + srcX) * 4;
                var target = ((y * _rgbaWidth) + x) * 4;
                _rgbaPixels![target] = sourcePixels[source];
                _rgbaPixels[target + 1] = sourcePixels[source + 1];
                _rgbaPixels[target + 2] = sourcePixels[source + 2];
                _rgbaPixels[target + 3] = sourcePixels[source + 3];
            }
        }
    }

    private int GetCanvasPixelWidth(double minimumWidth)
    {
        var width = canvas is AvaloniaDomElement element ? element.width : _owner.Bounds.Width;
        return Math.Max(1, (int)Math.Ceiling(Math.Max(width, minimumWidth)));
    }

    private int GetCanvasPixelHeight(double minimumHeight)
    {
        var height = canvas is AvaloniaDomElement element ? element.height : _owner.Bounds.Height;
        return Math.Max(1, (int)Math.Ceiling(Math.Max(height, minimumHeight)));
    }

    private void EnsureRgbaBuffer(int width, int height)
    {
        if (_rgbaPixels is not null && _rgbaWidth == width && _rgbaHeight == height)
        {
            return;
        }

        var oldPixels = _rgbaPixels;
        var oldWidth = _rgbaWidth;
        var oldHeight = _rgbaHeight;
        _rgbaWidth = width;
        _rgbaHeight = height;
        _rgbaPixels = new byte[width * height * 4];

        if (oldPixels is null)
        {
            return;
        }

        var copyWidth = Math.Min(oldWidth, width);
        var copyHeight = Math.Min(oldHeight, height);
        for (var y = 0; y < copyHeight; y++)
        {
            Array.Copy(oldPixels, y * oldWidth * 4, _rgbaPixels, y * width * 4, copyWidth * 4);
        }
    }

    private void DrawCanvasSurface(CanvasDrawingSurface surface, Rect sourceRect, Rect destinationRect)
    {
        if (ReferenceEquals(surface, _owner) || sourceRect.Width <= 0 || sourceRect.Height <= 0 || destinationRect.Width <= 0 || destinationRect.Height <= 0)
        {
            return;
        }

        _owner.AddCommand(new DrawCanvasSurfaceCommand(surface, sourceRect, destinationRect, _state.CreateSnapshot()));
    }

    private static bool TryResolveImagePixels(object image, out int width, out int height, out byte[] pixels)
    {
        if (image is AvaloniaDomElement element &&
            CanvasContextBridge.TryGetSurface(element.Control, out var canvasSurface) &&
            canvasSurface.Context.TryGetRgbaPixels(out width, out height, out pixels))
        {
            return true;
        }

        if (AvaloniaDomImageElement.TryGetRgbaPixels(image, out width, out height, out pixels))
        {
            return true;
        }

        width = 0;
        height = 0;
        pixels = Array.Empty<byte>();
        return false;
    }

    private static bool TryCreatePatternSource(
        object image,
        [NotNullWhen(true)] out IImageBrushSource? source,
        out double width,
        out double height)
    {
        if (TryResolveImagePixels(image, out var pixelWidth, out var pixelHeight, out var pixelData))
        {
            source = CreateWriteableBitmap(pixelWidth, pixelHeight, pixelData);
            width = pixelWidth;
            height = pixelHeight;
            return true;
        }

        if (TryResolveCanvasSurface(image, out var surface, out var sourceRect))
        {
            source = RasterizeCanvasSurface(surface, sourceRect);
            width = sourceRect.Width;
            height = sourceRect.Height;
            return width > 0 && height > 0;
        }

        if (TryResolveImage(image, out var resolved, out var resolvedRect) &&
            resolved is IImageBrushSource imageBrushSource)
        {
            source = imageBrushSource;
            width = resolvedRect.Width;
            height = resolvedRect.Height;
            return width > 0 && height > 0;
        }

        source = null;
        width = 0;
        height = 0;
        return false;
    }

    private static RenderTargetBitmap RasterizeCanvasSurface(CanvasDrawingSurface surface, Rect sourceRect)
    {
        var width = Math.Max(1, (int)Math.Ceiling(sourceRect.Width));
        var height = Math.Max(1, (int)Math.Ceiling(sourceRect.Height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext(clear: true);

        using (context.PushTransform(Matrix.CreateTranslation(-sourceRect.X, -sourceRect.Y)))
        {
            surface.RenderCommands(context);
        }

        return bitmap;
    }

    private static WriteableBitmap CreateWriteableBitmap(int width, int height, byte[] rgbaPixels)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var framebuffer = bitmap.Lock();
        var bgraRow = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var source = sourceOffset + x * 4;
                var target = x * 4;
                var alpha = rgbaPixels[source + 3];
                bgraRow[target] = Premultiply(rgbaPixels[source + 2], alpha);
                bgraRow[target + 1] = Premultiply(rgbaPixels[source + 1], alpha);
                bgraRow[target + 2] = Premultiply(rgbaPixels[source], alpha);
                bgraRow[target + 3] = alpha;
            }

            Marshal.Copy(bgraRow, 0, IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes), bgraRow.Length);
        }

        return bitmap;
    }

    private static byte Premultiply(byte channel, byte alpha)
    {
        if (alpha >= byte.MaxValue)
        {
            return channel;
        }

        return alpha == 0 ? (byte)0 : (byte)((channel * alpha + 127) / 255);
    }

    private static bool TryResolveCanvasSurface(object image, [NotNullWhen(true)] out CanvasDrawingSurface? surface, out Rect sourceRect)
    {
        if (image is AvaloniaDomElement element && CanvasContextBridge.TryGetSurface(element.Control, out surface))
        {
            var width = GetCanvasDimension(element.width, element.offsetWidth, element.Control.Bounds.Width);
            var height = GetCanvasDimension(element.height, element.offsetHeight, element.Control.Bounds.Height);
            sourceRect = new Rect(0, 0, width, height);
            return sourceRect.Width > 0 && sourceRect.Height > 0;
        }

        surface = null;
        sourceRect = default;
        return false;
    }

    private static double GetCanvasDimension(params double[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (double.IsFinite(candidate) && candidate > 0)
            {
                return candidate;
            }
        }

        return 0;
    }

    private static double[] ToCanvasMatrix(Matrix matrix)
        => new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 };

    private static Matrix InvertOrIdentity(Matrix matrix)
    {
        var a = matrix.M11;
        var b = matrix.M12;
        var c = matrix.M21;
        var d = matrix.M22;
        var e = matrix.M31;
        var f = matrix.M32;
        var determinant = (a * d) - (b * c);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < double.Epsilon)
        {
            return Matrix.Identity;
        }

        return new Matrix(
            d / determinant,
            -b / determinant,
            -c / determinant,
            a / determinant,
            ((c * f) - (d * e)) / determinant,
            ((b * e) - (a * f)) / determinant);
    }

    private static bool TryResolveImage(object image, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IImage? resolved, out Rect sourceRect)
    {
        resolved = image switch
        {
            IImage img => img,
            Image control when control.Source is IImage src => src,
            _ => null
        };

        if (resolved is null)
        {
            sourceRect = default;
            return false;
        }

        var size = resolved.Size;
        sourceRect = new Rect(0, 0, size.Width, size.Height);
        return sourceRect.Width > 0 && sourceRect.Height > 0;
    }
}

internal sealed class CanvasState
{
    public IImmutableBrush? FillBrush { get; set; } = new ImmutableSolidColorBrush(Colors.Black);
    public IImmutableBrush? StrokeBrush { get; set; } = new ImmutableSolidColorBrush(Colors.Black);
    public object? FillStyleValue { get; set; } = "#000000";
    public object? StrokeStyleValue { get; set; } = "#000000";
    public double LineWidth { get; set; } = 1.0;
    public PenLineCap LineCap { get; set; } = PenLineCap.Flat;
    public PenLineJoin LineJoin { get; set; } = PenLineJoin.Miter;
    public double MiterLimit { get; set; } = 10.0;
    public double GlobalAlpha { get; set; } = 1.0;
    public double[] LineDash { get; set; } = Array.Empty<double>();
    public double LineDashOffset { get; set; }
    public Matrix Transform { get; set; } = Matrix.Identity;
    public Geometry? ClipGeometry { get; set; }
    public Typeface Typeface { get; set; } = new Typeface("Segoe UI");
    public double FontSize { get; set; } = 16.0;
    public CanvasTextAlign TextAlign { get; set; } = CanvasTextAlign.Start;
    public CanvasTextBaseline TextBaseline { get; set; } = CanvasTextBaseline.Alphabetic;
    public string Font { get; set; } = "16px Segoe UI";

    public static CanvasState CreateDefault() => new();

    public CanvasState Clone() => new()
    {
        FillBrush = FillBrush,
        StrokeBrush = StrokeBrush,
        FillStyleValue = FillStyleValue,
        StrokeStyleValue = StrokeStyleValue,
        LineWidth = LineWidth,
        LineCap = LineCap,
        LineJoin = LineJoin,
        MiterLimit = MiterLimit,
        GlobalAlpha = GlobalAlpha,
        LineDash = LineDash.Length == 0 ? Array.Empty<double>() : (double[])LineDash.Clone(),
        LineDashOffset = LineDashOffset,
        Transform = Transform,
        ClipGeometry = ClipGeometry,
        Typeface = Typeface,
        FontSize = FontSize,
        TextAlign = TextAlign,
        TextBaseline = TextBaseline,
        Font = Font
    };

    public void SetFillStyle(object? value)
    {
        if (CanvasPaintParser.TryCreateBrush(value, FillBrush, out var brush, out var stored) && brush is not null)
        {
            FillBrush = brush;
            FillStyleValue = stored;
        }
    }

    public void SetStrokeStyle(object? value)
    {
        if (CanvasPaintParser.TryCreateBrush(value, StrokeBrush, out var brush, out var stored) && brush is not null)
        {
            StrokeBrush = brush;
            StrokeStyleValue = stored;
        }
    }

    public CanvasStateSnapshot CreateSnapshot() => new(this);

    public void UpdateFont(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Font = value;
        CanvasFontParser.Apply(this, value);
    }
}

internal sealed class CanvasStateSnapshot
{
    public CanvasStateSnapshot(CanvasState state)
    {
        FillBrush = state.FillBrush;
        StrokeBrush = state.StrokeBrush;
        LineWidth = state.LineWidth;
        LineCap = state.LineCap;
        LineJoin = state.LineJoin;
        MiterLimit = state.MiterLimit;
        GlobalAlpha = state.GlobalAlpha;
        LineDash = state.LineDash.Length == 0 ? Array.Empty<double>() : (double[])state.LineDash.Clone();
        LineDashOffset = state.LineDashOffset;
        Transform = state.Transform;
        ClipGeometry = state.ClipGeometry;
        Typeface = state.Typeface;
        FontSize = state.FontSize;
        TextAlign = state.TextAlign;
        TextBaseline = state.TextBaseline;
    }

    public IImmutableBrush? FillBrush { get; }
    public IImmutableBrush? StrokeBrush { get; }
    public double LineWidth { get; }
    public PenLineCap LineCap { get; }
    public PenLineJoin LineJoin { get; }
    public double MiterLimit { get; }
    public double GlobalAlpha { get; }
    public double[] LineDash { get; }
    public double LineDashOffset { get; }
    public Matrix Transform { get; }
    public Geometry? ClipGeometry { get; }
    public Typeface Typeface { get; }
    public double FontSize { get; }
    public CanvasTextAlign TextAlign { get; }
    public CanvasTextBaseline TextBaseline { get; }

    public Pen? CreatePen()
    {
        if (StrokeBrush is null || LineWidth <= 0)
        {
            return null;
        }

        var pen = new Pen(StrokeBrush, LineWidth)
        {
            LineCap = LineCap,
            LineJoin = LineJoin,
            MiterLimit = MiterLimit
        };

        if (LineDash.Length > 0 || LineDashOffset != 0)
        {
            pen.DashStyle = new ImmutableDashStyle(LineDash, LineDashOffset);
        }

        return pen;
    }
}

internal enum CanvasTextAlign
{
    Start,
    End,
    Left,
    Right,
    Center
}

internal enum CanvasTextBaseline
{
    Alphabetic,
    Top,
    Hanging,
    Middle,
    Ideographic,
    Bottom
}

internal static class CanvasStrokeParser
{
    public static PenLineCap ParseLineCap(string? value, PenLineCap current)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            "butt" => PenLineCap.Flat,
            _ => current
        };
    }

    public static PenLineJoin ParseLineJoin(string? value, PenLineJoin current)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "round" => PenLineJoin.Round,
            "bevel" => PenLineJoin.Bevel,
            "miter" => PenLineJoin.Miter,
            _ => current
        };
    }

    public static double[] ParseLineDash(object? value)
    {
        if (value is null || value is string || value is not IEnumerable enumerable)
        {
            return Array.Empty<double>();
        }

        var result = new List<double>();
        foreach (var item in enumerable)
        {
            if (!TryConvertToDouble(item, out var segment))
            {
                continue;
            }

            if (!double.IsFinite(segment) || segment < 0)
            {
                continue;
            }

            result.Add(segment);
        }

        return result.Count == 0 ? Array.Empty<double>() : result.ToArray();
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double doubleValue:
                result = doubleValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case decimal decimalValue:
                result = (double)decimalValue;
                return true;
            default:
                try
                {
                    result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    result = 0;
                    return false;
                }
        }
    }
}

internal static class CanvasTextParser
{
    public static CanvasTextAlign ParseAlign(string? value, CanvasTextAlign current)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "start" => CanvasTextAlign.Start,
            "end" => CanvasTextAlign.End,
            "left" => CanvasTextAlign.Left,
            "right" => CanvasTextAlign.Right,
            "center" => CanvasTextAlign.Center,
            _ => current
        };
    }

    public static string ToString(CanvasTextAlign align) => align switch
    {
        CanvasTextAlign.Start => "start",
        CanvasTextAlign.End => "end",
        CanvasTextAlign.Left => "left",
        CanvasTextAlign.Right => "right",
        CanvasTextAlign.Center => "center",
        _ => "start"
    };

    public static CanvasTextBaseline ParseBaseline(string? value, CanvasTextBaseline current)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "alphabetic" => CanvasTextBaseline.Alphabetic,
            "top" => CanvasTextBaseline.Top,
            "hanging" => CanvasTextBaseline.Hanging,
            "middle" => CanvasTextBaseline.Middle,
            "ideographic" => CanvasTextBaseline.Ideographic,
            "bottom" => CanvasTextBaseline.Bottom,
            _ => current
        };
    }

    public static string ToString(CanvasTextBaseline baseline) => baseline switch
    {
        CanvasTextBaseline.Alphabetic => "alphabetic",
        CanvasTextBaseline.Top => "top",
        CanvasTextBaseline.Hanging => "hanging",
        CanvasTextBaseline.Middle => "middle",
        CanvasTextBaseline.Ideographic => "ideographic",
        CanvasTextBaseline.Bottom => "bottom",
        _ => "alphabetic"
    };
}

internal static class CanvasColorParser
{
    public static IImmutableBrush Parse(string? value, IImmutableBrush? current)
    {
        var fallback = current is ImmutableSolidColorBrush solid ? solid.Color : Colors.Black;
        var color = ParseColor(value, fallback);
        return new ImmutableSolidColorBrush(color);
    }

    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return Colors.Transparent;
        }

        try
        {
            var brush = Brush.Parse(value);
            if (brush is ISolidColorBrush solid)
            {
                return solid.Color;
            }
        }
        catch
        {
        }

        if (Color.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}

internal static class CanvasPaintParser
{
    public static bool TryCreateBrush(object? value, IImmutableBrush? currentBrush, out IImmutableBrush? brush, out object? stored)
    {
        switch (value)
        {
            case null:
                brush = currentBrush;
                stored = null;
                return false;
            case string s:
                brush = CanvasColorParser.Parse(s, currentBrush);
                stored = s;
                return true;
            case CanvasGradient gradient:
                brush = gradient.ToImmutableBrush();
                stored = gradient;
                return true;
            case CanvasPattern pattern:
                brush = pattern.ToImmutableBrush();
                stored = pattern;
                return brush is not null;
            case IImmutableBrush immutable:
                brush = immutable;
                stored = immutable;
                return true;
            case ISolidColorBrush solidBrush:
                brush = solidBrush.ToImmutable();
                stored = solidBrush;
                return true;
            default:
                var asString = value?.ToString();
                if (!string.IsNullOrWhiteSpace(asString))
                {
                    brush = CanvasColorParser.Parse(asString, currentBrush);
                    stored = asString;
                    return true;
                }

                brush = currentBrush;
                stored = currentBrush;
                return false;
        }
    }
}

public sealed class CanvasPattern
{
    private readonly IImageBrushSource _source;
    private readonly double _width;
    private readonly double _height;
    private readonly string _repetition;
    private Matrix _transform = Matrix.Identity;

    internal CanvasPattern(IImageBrushSource source, double width, double height, string? repetition)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _repetition = string.IsNullOrWhiteSpace(repetition) ? "repeat" : repetition.Trim().ToLowerInvariant();
    }

    public void setTransform(object? transform)
    {
        if (TryReadMatrix(transform, out var matrix))
        {
            _transform = matrix;
        }
    }

    internal IImmutableBrush ToImmutableBrush()
    {
        var brush = new ImageBrush(_source)
        {
            Stretch = Stretch.None,
            TileMode = GetTileMode(_repetition),
            SourceRect = new RelativeRect(new Rect(0, 0, 1, 1), RelativeUnit.Relative),
            DestinationRect = new RelativeRect(new Rect(0, 0, _width, _height), RelativeUnit.Absolute)
        };

        if (!_transform.IsIdentity)
        {
            brush.Transform = new MatrixTransform(_transform);
        }

        return brush.ToImmutable();
    }

    private static TileMode GetTileMode(string repetition)
        => repetition switch
        {
            "no-repeat" => TileMode.None,
            "repeat-x" => TileMode.Tile,
            "repeat-y" => TileMode.Tile,
            _ => TileMode.Tile
        };

    private static bool TryReadMatrix(object? value, out Matrix matrix)
    {
        if (value is CanvasDomMatrix domMatrix)
        {
            matrix = new Matrix(domMatrix.a, domMatrix.b, domMatrix.c, domMatrix.d, domMatrix.e, domMatrix.f);
            return true;
        }

        if (value is not null && value is not string && value is IEnumerable enumerable)
        {
            var values = new List<double>(6);
            foreach (var item in enumerable)
            {
                if (TryConvertToDouble(item, out var number))
                {
                    values.Add(number);
                    if (values.Count == 6)
                    {
                        matrix = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
                        return true;
                    }
                }
            }
        }

        if (TryReadProperty(value, "a", out var a) &&
            TryReadProperty(value, "b", out var b) &&
            TryReadProperty(value, "c", out var c) &&
            TryReadProperty(value, "d", out var d) &&
            TryReadProperty(value, "e", out var e) &&
            TryReadProperty(value, "f", out var f))
        {
            matrix = new Matrix(a, b, c, d, e, f);
            return true;
        }

        if (TryReadProperty(value, "m11", out var m11) &&
            TryReadProperty(value, "m12", out var m12) &&
            TryReadProperty(value, "m21", out var m21) &&
            TryReadProperty(value, "m22", out var m22) &&
            TryReadProperty(value, "m41", out var m41) &&
            TryReadProperty(value, "m42", out var m42))
        {
            matrix = new Matrix(m11, m12, m21, m22, m41, m42);
            return true;
        }

        matrix = Matrix.Identity;
        return false;
    }

    private static bool TryReadProperty(object? value, string name, out double number)
    {
        number = 0;
        if (value is null)
        {
            return false;
        }

        if (value is IDictionary dictionary && dictionary.Contains(name))
        {
            return TryConvertToDouble(dictionary[name], out number);
        }

        var property = value.GetType().GetProperty(name);
        return property is not null && TryConvertToDouble(property.GetValue(value), out number);
    }

    private static bool TryConvertToDouble(object? value, out double number)
    {
        switch (value)
        {
            case double doubleValue:
                number = doubleValue;
                return true;
            case float floatValue:
                number = floatValue;
                return true;
            case int intValue:
                number = intValue;
                return true;
            case long longValue:
                number = longValue;
                return true;
            case decimal decimalValue:
                number = (double)decimalValue;
                return true;
            default:
                try
                {
                    number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    number = 0;
                    return false;
                }
        }
    }
}

internal readonly record struct CanvasGradientStop(double Offset, Color Color);

public abstract class CanvasGradient
{
    private readonly List<CanvasGradientStop> _stops = new();

    public void addColorStop(double offset, string color)
    {
        if (double.IsNaN(offset) || double.IsInfinity(offset))
        {
            return;
        }

        var clamped = Math.Clamp(offset, 0.0, 1.0);
        var parsed = CanvasColorParser.ParseColor(color, Colors.Transparent);
        _stops.Add(new CanvasGradientStop(clamped, parsed));
    }

    internal IReadOnlyList<CanvasGradientStop> Stops => _stops;

    protected IEnumerable<GradientStop> EnumerateStops()
    {
        if (_stops.Count == 0)
        {
            yield return new GradientStop(Colors.Transparent, 0);
            yield return new GradientStop(Colors.Transparent, 1);
            yield break;
        }

        foreach (var stop in _stops)
        {
            yield return new GradientStop(stop.Color, stop.Offset);
        }
    }

    internal abstract IImmutableBrush ToImmutableBrush();
}

public sealed class CanvasLinearGradient : CanvasGradient
{
    private readonly double _x0;
    private readonly double _y0;
    private readonly double _x1;
    private readonly double _y1;

    public CanvasLinearGradient(double x0, double y0, double x1, double y1)
    {
        _x0 = x0;
        _y0 = y0;
        _x1 = x1;
        _y1 = y1;
    }

    internal override IImmutableBrush ToImmutableBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(new Point(_x0, _y0), RelativeUnit.Absolute),
            EndPoint = new RelativePoint(new Point(_x1, _y1), RelativeUnit.Absolute),
            SpreadMethod = GradientSpreadMethod.Pad
        };
        brush.GradientStops.Clear();
        foreach (var stop in EnumerateStops())
        {
            brush.GradientStops.Add(stop);
        }
        return new ImmutableLinearGradientBrush(brush);
    }
}

public sealed class CanvasRadialGradient : CanvasGradient
{
    private readonly double _x0;
    private readonly double _y0;
    private readonly double _r0;
    private readonly double _x1;
    private readonly double _y1;
    private readonly double _r1;

    public CanvasRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1)
    {
        _x0 = x0;
        _y0 = y0;
        _r0 = Math.Max(0, r0);
        _x1 = x1;
        _y1 = y1;
        _r1 = Math.Max(0, r1);
    }

    internal override IImmutableBrush ToImmutableBrush()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(new Point(_x0, _y0), RelativeUnit.Absolute),
            Center = new RelativePoint(new Point(_x1, _y1), RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(_r1, RelativeUnit.Absolute),
            RadiusY = new RelativeScalar(_r1, RelativeUnit.Absolute),
            SpreadMethod = GradientSpreadMethod.Pad
        };
        brush.GradientStops.Clear();
        foreach (var stop in EnumerateStops())
        {
            brush.GradientStops.Add(stop);
        }
        return new ImmutableRadialGradientBrush(brush);
    }
}

internal static class CanvasFontParser
{
    public static void Apply(CanvasState state, string font)
    {
        if (state is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(font))
        {
            return;
        }

        var tokens = Tokenize(font);
        double size = state.FontSize;
        string family = state.Typeface.FontFamily.Name;
        FontStyle style = state.Typeface.Style;
        FontWeight weight = state.Typeface.Weight;
        var sizeIndex = -1;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (TryParseSize(token, out var px))
            {
                size = px;
                sizeIndex = index;
                break;
            }
        }

        var descriptorEnd = sizeIndex >= 0 ? sizeIndex : tokens.Count;
        for (var index = 0; index < descriptorEnd; index++)
        {
            var token = tokens[index];
            if (string.Equals(token, "italic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "oblique", StringComparison.OrdinalIgnoreCase))
            {
                style = FontStyle.Italic;
            }
            else if (string.Equals(token, "normal", StringComparison.OrdinalIgnoreCase))
            {
                style = FontStyle.Normal;
                weight = FontWeight.Normal;
            }
            else if (string.Equals(token, "bold", StringComparison.OrdinalIgnoreCase))
            {
                weight = FontWeight.Bold;
            }
            else if (string.Equals(token, "bolder", StringComparison.OrdinalIgnoreCase))
            {
                weight = FontWeight.Bold;
            }
            else if (string.Equals(token, "lighter", StringComparison.OrdinalIgnoreCase))
            {
                weight = FontWeight.Light;
            }
            else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericWeight))
            {
                weight = numericWeight >= 600 ? FontWeight.Bold : FontWeight.Normal;
            }
        }

        if (sizeIndex >= 0 && sizeIndex + 1 < tokens.Count)
        {
            family = NormalizeFamily(JoinTokens(tokens, sizeIndex + 1));
        }

        state.FontSize = size;
        state.Typeface = new Typeface(family, style, weight);
    }

    private static bool TryParseSize(string token, out double size)
    {
        size = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var slash = token.IndexOf('/');
        var sizeToken = slash >= 0 ? token[..slash] : token;
        if (!sizeToken.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return double.TryParse(
            sizeToken.AsSpan(0, sizeToken.Length - 2),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out size);
    }

    private static List<string> Tokenize(string value)
    {
        var result = new List<string>();
        var start = -1;
        var quote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                if (start < 0)
                {
                    start = index;
                }

                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (start >= 0)
                {
                    result.Add(value[start..index]);
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = index;
            }
        }

        if (start >= 0)
        {
            result.Add(value[start..]);
        }

        return result;
    }

    private static string JoinTokens(IReadOnlyList<string> tokens, int start)
    {
        if (start >= tokens.Count)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(tokens[start]);
        for (var index = start + 1; index < tokens.Count; index++)
        {
            builder.Append(' ');
            builder.Append(tokens[index]);
        }

        return builder.ToString();
    }

    private static string NormalizeFamily(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var families = new List<string>();
        var start = 0;
        var quote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == ',')
            {
                AddFamily(value[start..index], families);
                start = index + 1;
            }
        }

        AddFamily(value[start..], families);
        return families.Count == 0 ? value.Trim() : string.Join(", ", families);
    }

    private static void AddFamily(string value, ICollection<string> families)
    {
        var family = value.Trim();
        if (family.Length >= 2 &&
            ((family[0] == '"' && family[^1] == '"') || (family[0] == '\'' && family[^1] == '\'')))
        {
            family = family[1..^1].Trim();
        }

        if (family.Length > 0)
        {
            families.Add(family);
        }
    }
}

internal sealed class CanvasPathBuilder
{
    private readonly List<ICanvasPathSegment> _segments = new();

    public bool IsEmpty => _segments.Count == 0;

    public void Clear() => _segments.Clear();

    public void ClosePath() => _segments.Add(new ClosePathSegment());

    public void MoveTo(double x, double y) => _segments.Add(new MoveToSegment(new Point(x, y)));

    public void LineTo(double x, double y) => _segments.Add(new LineToSegment(new Point(x, y)));

    public void CubicBezierTo(double cp1x, double cp1y, double cp2x, double cp2y, double x, double y)
        => _segments.Add(new CubicBezierSegment(new Point(cp1x, cp1y), new Point(cp2x, cp2y), new Point(x, y)));

    public void QuadraticBezierTo(double cpx, double cpy, double x, double y)
        => _segments.Add(new QuadraticBezierSegment(new Point(cpx, cpy), new Point(x, y)));

    public void Arc(double x, double y, double radius, double startAngle, double endAngle, bool counterClockwise)
        => _segments.Add(new ArcSegment(new Point(x, y), radius, startAngle, endAngle, counterClockwise));

    public void Rect(double x, double y, double width, double height)
        => _segments.Add(new RectSegment(new Rect(x, y, width, height)));

    public StreamGeometry BuildGeometry()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var current = new Point();
            var figureStart = new Point();
            var figureOpen = false;

            foreach (var segment in _segments)
            {
                segment.Apply(ctx, ref current, ref figureStart, ref figureOpen);
            }

            if (figureOpen)
            {
                ctx.EndFigure(false);
            }
        }        return geometry;
    }
}

internal interface ICanvasPathSegment
{
    void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen);
}

internal sealed class MoveToSegment : ICanvasPathSegment
{
    private readonly Point _point;

    public MoveToSegment(Point point)
    {
        _point = point;
    }

    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        if (figureOpen)
        {
            context.EndFigure(false);
        }

        context.BeginFigure(_point, true);
        figureOpen = true;
        figureStart = _point;
        currentPoint = _point;
    }
}

internal sealed class LineToSegment : ICanvasPathSegment
{
    private readonly Point _point;

    public LineToSegment(Point point)
    {
        _point = point;
    }

    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        EnsureFigure(context, ref currentPoint, ref figureStart, ref figureOpen);
        context.LineTo(_point);
        currentPoint = _point;
    }

    internal static void EnsureFigure(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        if (!figureOpen)
        {
            context.BeginFigure(currentPoint, true);
            figureStart = currentPoint;
            figureOpen = true;
        }
    }
}

internal sealed class CubicBezierSegment : ICanvasPathSegment
{
    private readonly Point _control1;
    private readonly Point _control2;
    private readonly Point _point;

    public CubicBezierSegment(Point control1, Point control2, Point point)
    {
        _control1 = control1;
        _control2 = control2;
        _point = point;
    }

    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        LineToSegment.EnsureFigure(context, ref currentPoint, ref figureStart, ref figureOpen);
        context.CubicBezierTo(_control1, _control2, _point);
        currentPoint = _point;
    }
}

internal sealed class QuadraticBezierSegment : ICanvasPathSegment
{
    private readonly Point _control;
    private readonly Point _point;

    public QuadraticBezierSegment(Point control, Point point)
    {
        _control = control;
        _point = point;
    }

    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        LineToSegment.EnsureFigure(context, ref currentPoint, ref figureStart, ref figureOpen);
        context.QuadraticBezierTo(_control, _point);
        currentPoint = _point;
    }
}

internal sealed class ArcSegment : ICanvasPathSegment
{
    private readonly Point _center;
    private readonly double _radius;
    private readonly double _startAngle;
    private readonly double _endAngle;
    private readonly bool _counterClockwise;

    public ArcSegment(Point center, double radius, double startAngle, double endAngle, bool counterClockwise)
    {
        _center = center;
        _radius = Math.Abs(radius);
        _startAngle = startAngle;
        _endAngle = endAngle;
        _counterClockwise = counterClockwise;
    }

    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        if (_radius <= 0)
        {
            return;
        }

        const double twoPi = Math.PI * 2;
        var sweep = _endAngle - _startAngle;
        if (!_counterClockwise && sweep >= twoPi)
        {
            sweep = twoPi;
        }
        else if (_counterClockwise && sweep <= -twoPi)
        {
            sweep = -twoPi;
        }
        else if (!_counterClockwise && sweep < 0)
        {
            sweep += twoPi;
        }
        else if (_counterClockwise && sweep > 0)
        {
            sweep -= twoPi;
        }

        if (Math.Abs(sweep) <= double.Epsilon)
        {
            return;
        }

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        var step = sweep / segments;
        var angle = _startAngle;
        var startPoint = GetPoint(angle);

        if (!figureOpen)
        {
            context.BeginFigure(startPoint, true);
            figureStart = startPoint;
            figureOpen = true;
        }
        else if (!AreClose(currentPoint, startPoint))
        {
            context.LineTo(startPoint);
        }

        currentPoint = startPoint;

        for (int i = 0; i < segments; i++)
        {
            var nextAngle = angle + step;
            var endPoint = GetPoint(nextAngle);
            var k = 4.0 / 3.0 * Math.Tan(step / 4.0);
            var control1 = new Point(
                currentPoint.X - _radius * k * Math.Sin(angle),
                currentPoint.Y + _radius * k * Math.Cos(angle));
            var control2 = new Point(
                endPoint.X + _radius * k * Math.Sin(nextAngle),
                endPoint.Y - _radius * k * Math.Cos(nextAngle));

            context.CubicBezierTo(control1, control2, endPoint);
            currentPoint = endPoint;
            angle = nextAngle;
        }
    }

    private Point GetPoint(double angle)
        => new(
            _center.X + _radius * Math.Cos(angle),
            _center.Y + _radius * Math.Sin(angle));

    private static bool AreClose(Point a, Point b)
        => Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
}

internal sealed class RectSegment : ICanvasPathSegment
{
    private readonly Rect _rect;

    public RectSegment(Rect rect)
    {
        _rect = rect;
    }

    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        if (_rect.Width <= 0 || _rect.Height <= 0)
        {
            return;
        }

        var p1 = _rect.TopLeft;
        var p2 = _rect.TopRight;
        var p3 = _rect.BottomRight;
        var p4 = _rect.BottomLeft;

        context.BeginFigure(p1, true);
        context.LineTo(p2);
        context.LineTo(p3);
        context.LineTo(p4);
        context.EndFigure(true);

        figureOpen = false;
        currentPoint = p1;
        figureStart = p1;
    }
}

internal sealed class ClosePathSegment : ICanvasPathSegment
{
    public void Apply(StreamGeometryContext context, ref Point currentPoint, ref Point figureStart, ref bool figureOpen)
    {
        if (figureOpen)
        {
            context.EndFigure(true);
            figureOpen = false;
            currentPoint = figureStart;
        }
    }
}
