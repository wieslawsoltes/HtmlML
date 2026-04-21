using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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

internal sealed class CanvasRenderingContext2D
{
    private readonly CanvasDrawingSurface _owner;
    private readonly CanvasPathBuilder _path = new();
    private CanvasState _state = CanvasState.CreateDefault();
    private readonly Stack<CanvasState> _stateStack = new();

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

    public object measureText(string text)
    {
        var formatted = _owner.CreateFormattedText(text ?? string.Empty, _state.CreateSnapshot());
        return new CanvasTextMetrics(formatted.Width);
    }

    public void drawImage(object image, double dx, double dy)
    {
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

    private void DrawCanvasSurface(CanvasDrawingSurface surface, Rect sourceRect, Rect destinationRect)
    {
        if (ReferenceEquals(surface, _owner) || sourceRect.Width <= 0 || sourceRect.Height <= 0 || destinationRect.Width <= 0 || destinationRect.Height <= 0)
        {
            return;
        }

        _owner.AddCommand(new DrawCanvasSurfaceCommand(surface, sourceRect, destinationRect, _state.CreateSnapshot()));
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
