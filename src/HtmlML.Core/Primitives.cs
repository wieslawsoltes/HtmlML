using System.Runtime.CompilerServices;

namespace HtmlML.Core;

public readonly record struct HtmlMlPoint(double X, double Y);

public readonly record struct HtmlMlSize(double Width, double Height)
{
    public static HtmlMlSize Empty { get; } = new(0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct HtmlMlRect(double X, double Y, double Width, double Height)
{
    public static HtmlMlRect Empty { get; } = new(0, 0, 0, 0);

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public HtmlMlSize Size => new(Width, Height);

    public bool Contains(HtmlMlPoint point)
        => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}

public readonly record struct HtmlMlColor(byte A, byte R, byte G, byte B)
{
    public static HtmlMlColor Transparent { get; } = new(0, 0, 0, 0);

    public static HtmlMlColor FromRgb(byte red, byte green, byte blue)
        => new(byte.MaxValue, red, green, blue);
}

public readonly record struct HtmlMlNodeId(long Value)
{
    public bool IsEmpty => Value == 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Carries a backend-owned native object without making that object's framework type
/// part of the portable DOM or runtime contract. Equality is reference identity.
/// </summary>
public readonly struct HtmlMlBackendHandle : IEquatable<HtmlMlBackendHandle>
{
    private readonly object? _value;

    private HtmlMlBackendHandle(object value) => _value = value;

    public bool IsEmpty => _value is null;

    public Type? NativeType => _value?.GetType();

    public static HtmlMlBackendHandle Create(object value)
        => new(value ?? throw new ArgumentNullException(nameof(value)));

    public bool TryGet<T>(out T? value) where T : class
    {
        value = _value as T;
        return value is not null;
    }

    public T GetRequired<T>() where T : class
        => _value as T
           ?? throw new InvalidOperationException(
               $"Backend handle contains '{_value?.GetType().FullName ?? "<empty>"}', not '{typeof(T).FullName}'.");

    public bool Equals(HtmlMlBackendHandle other) => ReferenceEquals(_value, other._value);

    public override bool Equals(object? obj) => obj is HtmlMlBackendHandle other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : RuntimeHelpers.GetHashCode(_value);

    public static bool operator ==(HtmlMlBackendHandle left, HtmlMlBackendHandle right) => left.Equals(right);

    public static bool operator !=(HtmlMlBackendHandle left, HtmlMlBackendHandle right) => !left.Equals(right);
}

public readonly record struct HtmlMlBackendNode(HtmlMlNodeId Id, HtmlMlBackendHandle Handle)
{
    public bool IsEmpty => Id.IsEmpty || Handle.IsEmpty;
}
