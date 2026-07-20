using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JavaScript.Avalonia;

/// <summary>
/// DOM FocusEvent payload shared by native and virtual focus transitions.
/// </summary>
public sealed class DomFocusEvent : DomEvent
{
    internal DomFocusEvent(
        string type,
        bool bubbles,
        double timeStamp,
        object? relatedTarget)
        : base(
            type,
            bubbles,
            cancelable: false,
            initiallyHandled: false,
            timeStamp,
            isTrusted: true)
    {
        this.relatedTarget = relatedTarget;
    }

    public object? relatedTarget { get; }
}

public sealed class DomPointerEvent : DomEvent
{
    // Avalonia's macOS backend converts precise AppKit scrollingDelta values
    // to control scroll units by dividing by 50. DOM WheelEvent with
    // deltaMode=DOM_DELTA_PIXEL must expose pixel-scale values instead.
    internal const double WheelPixelScale = 50d;
    private readonly PointerEventArgs _args;
    private readonly Control _relativeTo;
    private readonly Control _viewport;
    private readonly object? _view;
    private readonly Vector _movement;
    private readonly int _detail;
    private readonly object? _relatedTarget;
    private readonly string _pointerType;
    private bool _positionCached;
    private Point _offsetPosition;
    private Point _clientPosition;
    private bool _pointPropertiesCached;
    private PointerPointProperties _pointProperties;

    internal DomPointerEvent(string type, PointerEventArgs args, Control relativeTo, Control? viewport, object? view, double timeStamp, bool bubbles = true, bool cancelable = true, Vector movement = default, int detail = 0, object? relatedTarget = null)
        : base(type, bubbles, cancelable, args.Handled, timeStamp, isTrusted: true)
    {
        _args = args;
        _relativeTo = relativeTo;
        _viewport = viewport ?? relativeTo;
        _view = view;
        _movement = movement;
        _detail = detail;
        _relatedTarget = relatedTarget;
        _pointerType = args.Pointer?.Type.ToString().ToLowerInvariant() ?? "mouse";
    }

    public int pointerId => _args.Pointer?.Id ?? 0;

    public string pointerType => _pointerType;

    public double clientX => GetClientPosition().X;

    public double clientY => GetClientPosition().Y;

    public double x => clientX;

    public double y => clientY;

    public double screenX => clientX;

    public double screenY => clientY;

    public double pageX => clientX;

    public double pageY => clientY;

    public double offsetX => GetOffsetPosition().X;

    public double offsetY => GetOffsetPosition().Y;

    public double layerX => offsetX;

    public double layerY => offsetY;

    public double deltaX => _args is PointerWheelEventArgs wheel ? wheel.Delta.X * WheelPixelScale : 0;

    public double deltaY => _args is PointerWheelEventArgs wheel ? -wheel.Delta.Y * WheelPixelScale : 0;

    public double deltaZ => 0;

    public int deltaMode => 0;

    public object? view => _view;

    public object? relatedTarget => _relatedTarget;

    public int detail => _detail;

    public double movementX => _movement.X;

    public double movementY => _movement.Y;

    public int which
    {
        get
        {
            var pressed = buttons;
            if ((pressed & 1) != 0) return 1;
            if ((pressed & 4) != 0) return 2;
            if ((pressed & 2) != 0) return 3;
            return type is "click" or "mousedown" or "mouseup" or "mousemove" or "wheel"
                ? button switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 3,
                    _ => 0
                }
                : 0;
        }
    }

    public double width => 1;

    public double height => 1;

    public double pressure => buttons == 0 ? 0 : 0.5;

    public double tangentialPressure => 0;

    public int tiltX => 0;

    public int tiltY => 0;

    public int twist => 0;

    public int buttons
    {
        get
        {
            return ToButtons(GetPointProperties());
        }
    }

    public int button
    {
        get
        {
            return ToButton(GetPointProperties().PointerUpdateKind, type);
        }
    }

    public bool altKey => (_args.KeyModifiers & KeyModifiers.Alt) != 0;

    public bool ctrlKey => (_args.KeyModifiers & KeyModifiers.Control) != 0;

    public bool shiftKey => (_args.KeyModifiers & KeyModifiers.Shift) != 0;

    public bool metaKey => (_args.KeyModifiers & KeyModifiers.Meta) != 0;

    public bool isPrimary => _args.Pointer?.IsPrimary ?? false;

    public bool getModifierState(string key)
        => key?.Trim().ToLowerInvariant() switch
        {
            "alt" => altKey,
            "control" or "ctrl" => ctrlKey,
            "shift" => shiftKey,
            "meta" => metaKey,
            _ => false
        };

    private Point GetClientPosition()
    {
        EnsurePositionCache();
        return _clientPosition;
    }

    private Point GetOffsetPosition()
    {
        EnsurePositionCache();
        return _offsetPosition;
    }

    private void EnsurePositionCache()
    {
        if (_positionCached)
        {
            return;
        }

        _offsetPosition = _args.GetPosition(_relativeTo);
        var origin = _relativeTo.TranslatePoint(new Point(0, 0), _viewport);
        _clientPosition = origin.HasValue
            ? new Point(origin.Value.X + _offsetPosition.X, origin.Value.Y + _offsetPosition.Y)
            : _offsetPosition;
        _positionCached = true;
    }

    private PointerPointProperties GetPointProperties()
    {
        if (_pointPropertiesCached)
        {
            return _pointProperties;
        }

        try
        {
            _pointProperties = _args.GetCurrentPoint(_relativeTo).Properties;
        }
        catch
        {
            _pointProperties = default;
        }

        _pointPropertiesCached = true;
        return _pointProperties;
    }

    private static int ToButton(PointerUpdateKind kind, string eventType) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 0,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 1,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => 3,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => 4,
        _ when eventType.StartsWith("mouse", StringComparison.OrdinalIgnoreCase)
               || string.Equals(eventType, "wheel", StringComparison.OrdinalIgnoreCase) => 0,
        _ => -1
    };

    private static int ToButtons(PointerPointProperties properties)
    {
        var result = 0;
        if (properties.IsLeftButtonPressed)
        {
            result |= 1;
        }

        if (properties.IsRightButtonPressed)
        {
            result |= 2;
        }

        if (properties.IsMiddleButtonPressed)
        {
            result |= 4;
        }

        if (properties.IsXButton1Pressed)
        {
            result |= 8;
        }

        if (properties.IsXButton2Pressed)
        {
            result |= 16;
        }

        return result;
    }

    protected override void OnHandledChanged(bool value) => _args.Handled = value;
}

public sealed class DomKeyboardEvent : DomEvent
{
    private readonly KeyEventArgs _args;

    internal DomKeyboardEvent(string type, KeyEventArgs args, double timeStamp, bool bubbles = true, bool cancelable = true)
        : base(type, bubbles, cancelable, args.Handled, timeStamp, isTrusted: true)
    {
        _args = args;
    }

    public string? key => MapKey(_args.Key, _args.KeyModifiers);

    public string? code => MapCode(_args.Key);

    public int keyCode => MapLegacyKeyCode(_args.Key);

    public int which => keyCode;

    public int charCode => 0;

    public bool repeat => false;

    public bool altKey => (_args.KeyModifiers & KeyModifiers.Alt) != 0;

    public bool ctrlKey => (_args.KeyModifiers & KeyModifiers.Control) != 0;

    public bool shiftKey => (_args.KeyModifiers & KeyModifiers.Shift) != 0;

    public bool metaKey => (_args.KeyModifiers & KeyModifiers.Meta) != 0;

    private static string MapKey(Key key, KeyModifiers modifiers)
    {
        return key switch
        {
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Escape => "Escape",
            Key.Space => " ",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            _ => MapCharacterKey(key, modifiers)
        };
    }

    private static string MapCode(Key key)
    {
        var name = key.ToString();
        if (name.Length == 1 && char.IsLetter(name[0]))
        {
            return "Key" + char.ToUpperInvariant(name[0]);
        }

        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            return "Digit" + name;
        }

        return key switch
        {
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Escape => "Escape",
            Key.Space => "Space",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            _ => name
        };
    }

    private static string MapCharacterKey(Key key, KeyModifiers modifiers)
    {
        var name = key.ToString();
        if (name.Length != 1 || !char.IsLetterOrDigit(name[0]))
        {
            return name;
        }

        return (char.IsLetter(name[0]) && (modifiers & KeyModifiers.Shift) == 0)
            ? name.ToLowerInvariant()
            : name;
    }

    private static int MapLegacyKeyCode(Key key)
    {
        var code = MapCode(key);
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
        {
            return code[3];
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
        {
            return code[5];
        }

        if (code.Length is 2 or 3
            && code[0] == 'F'
            && int.TryParse(code.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return 111 + functionKey;
        }

        return key switch
        {
            Key.Back => 8,
            Key.Tab => 9,
            Key.Enter => 13,
            Key.Escape => 27,
            Key.Space => 32,
            Key.PageUp => 33,
            Key.PageDown => 34,
            Key.End => 35,
            Key.Home => 36,
            Key.Left => 37,
            Key.Up => 38,
            Key.Right => 39,
            Key.Down => 40,
            Key.Insert => 45,
            Key.Delete => 46,
            _ => 0
        };
    }

    protected override void OnHandledChanged(bool value) => _args.Handled = value;
}

public sealed class DomTextInputEvent : DomEvent
{
    private readonly TextInputEventArgs _args;

    internal DomTextInputEvent(string type, TextInputEventArgs args, double timeStamp, bool bubbles = true, bool cancelable = true)
        : base(type, bubbles, cancelable, args.Handled, timeStamp, isTrusted: true)
    {
        _args = args;
    }

    public string? data => _args.Text;

    protected override void OnHandledChanged(bool value) => _args.Handled = value;
}

internal sealed class AvaloniaDomRoutedEvent : DomEvent
{
    private readonly RoutedEventArgs _args;

    internal AvaloniaDomRoutedEvent(
        string type,
        bool bubbles,
        bool cancelable,
        RoutedEventArgs args,
        double timeStamp,
        bool isTrusted)
        : base(type, bubbles, cancelable, args.Handled, timeStamp, isTrusted)
    {
        _args = args;
    }

    protected override void OnHandledChanged(bool value) => _args.Handled = value;
}
