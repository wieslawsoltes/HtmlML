using System;
using System.Collections.Generic;
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);

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

    public void Render(DrawingContext context)
    {
        IDisposable? transform = null;
        IDisposable? opacity = null;

        try
        {
            if (!State.Transform.IsIdentity)
            {
                transform = context.PushTransform(State.Transform);
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

    public string fillStyle
    {
        get => _state.FillBrush.Color.ToString();
        set => _state.FillBrush = CanvasColorParser.Parse(value, _state.FillBrush);
    }

    public string strokeStyle
    {
        get => _state.StrokeBrush.Color.ToString();
        set => _state.StrokeBrush = CanvasColorParser.Parse(value, _state.StrokeBrush);
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
}

internal sealed class CanvasState
{
    public ImmutableSolidColorBrush FillBrush { get; set; } = new ImmutableSolidColorBrush(Colors.Black);
    public ImmutableSolidColorBrush StrokeBrush { get; set; } = new ImmutableSolidColorBrush(Colors.Black);
    public double LineWidth { get; set; } = 1.0;
    public PenLineCap LineCap { get; set; } = PenLineCap.Flat;
    public PenLineJoin LineJoin { get; set; } = PenLineJoin.Miter;
    public double MiterLimit { get; set; } = 10.0;
    public double GlobalAlpha { get; set; } = 1.0;
    public Matrix Transform { get; set; } = Matrix.Identity;
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
        LineWidth = LineWidth,
        LineCap = LineCap,
        LineJoin = LineJoin,
        MiterLimit = MiterLimit,
        GlobalAlpha = GlobalAlpha,
        Transform = Transform,
        Typeface = Typeface,
        FontSize = FontSize,
        TextAlign = TextAlign,
        TextBaseline = TextBaseline,
        Font = Font
    };

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
        Transform = state.Transform;
        Typeface = state.Typeface;
        FontSize = state.FontSize;
        TextAlign = state.TextAlign;
        TextBaseline = state.TextBaseline;
    }

    public ImmutableSolidColorBrush FillBrush { get; }
    public ImmutableSolidColorBrush StrokeBrush { get; }
    public double LineWidth { get; }
    public PenLineCap LineCap { get; }
    public PenLineJoin LineJoin { get; }
    public double MiterLimit { get; }
    public double GlobalAlpha { get; }
    public Matrix Transform { get; }
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
    public static ImmutableSolidColorBrush Parse(string? value, ImmutableSolidColorBrush current)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return current;
        }

        if (string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return new ImmutableSolidColorBrush(Colors.Transparent);
        }

        try
        {
            var brush = Brush.Parse(value);
            if (brush is ISolidColorBrush solid)
            {
                return new ImmutableSolidColorBrush(solid.Color);
            }
        }
        catch
        {
        }

        if (Color.TryParse(value, out var color))
        {
            return new ImmutableSolidColorBrush(color);
        }

        return current;
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

        var tokens = font.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        double size = state.FontSize;
        string family = state.Typeface.FontFamily.Name;
        FontStyle style = state.Typeface.Style;
        FontWeight weight = state.Typeface.Weight;

        foreach (var token in tokens)
        {
            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase) && double.TryParse(token.AsSpan(0, token.Length - 2), NumberStyles.Number, CultureInfo.InvariantCulture, out var px))
            {
                size = px;
            }
            else if (string.Equals(token, "italic", StringComparison.OrdinalIgnoreCase))
            {
                style = FontStyle.Italic;
            }
            else if (string.Equals(token, "bold", StringComparison.OrdinalIgnoreCase))
            {
                weight = FontWeight.Bold;
            }
            else
            {
                family = token;
            }
        }

        state.FontSize = size;
        state.Typeface = new Typeface(family, style, weight);
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

        var sweep = _endAngle - _startAngle;
        if (!_counterClockwise && sweep < 0)
        {
            sweep += Math.PI * 2;
        }
        else if (_counterClockwise && sweep > 0)
        {
            sweep -= Math.PI * 2;
        }

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        var step = sweep / segments;

        for (int i = 0; i <= segments; i++)
        {
            var angle = _startAngle + step * i;
            var point = new Point(
                _center.X + _radius * Math.Cos(angle),
                _center.Y + _radius * Math.Sin(angle));

            if (!figureOpen)
            {
                context.BeginFigure(point, true);
                figureStart = point;
                figureOpen = true;
            }
            else if (i == 0)
            {
                context.LineTo(point);
            }
            else
            {
                context.LineTo(point);
            }

            currentPoint = point;
        }
    }
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
