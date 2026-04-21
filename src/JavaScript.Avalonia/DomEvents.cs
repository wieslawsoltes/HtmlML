using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JavaScript.Avalonia;

public enum DomEventPhase
{
    None = 0,
    CapturingPhase = 1,
    AtTarget = 2,
    BubblingPhase = 3
}

public class DomEvent
{
    private readonly RoutedEventArgs? _args;
    private bool _propagationStopped;
    private bool _immediatePropagationStopped;
    private bool _defaultPrevented;
    private bool _handledFlag;
    private bool _currentListenerPassive;

    internal DomEvent(string type, bool bubbles, bool cancelable, RoutedEventArgs? args, double timeStamp, bool isTrusted)
    {
        this.type = type;
        this.bubbles = bubbles;
        this.cancelable = cancelable;
        _args = args;
        this.timeStamp = timeStamp;
        this.isTrusted = isTrusted;
        _handledFlag = args?.Handled ?? false;
    }

    public string type { get; }

    public object? target { get; internal set; }

    public object? currentTarget { get; internal set; }

    public DomEventPhase eventPhase { get; internal set; } = DomEventPhase.None;

    public bool bubbles { get; }

    public bool cancelable { get; }

    public double timeStamp { get; }

    public bool defaultPrevented => _defaultPrevented;

    public bool isTrusted { get; }

    internal List<AvaloniaDomElement>? SyntheticPath { get; set; }

    public bool handled
    {
        get => _handledFlag;
        set
        {
            _handledFlag = value;
            if (_args is not null)
            {
                _args.Handled = value;
            }
        }
    }

    internal bool PropagationStopped => _propagationStopped;

    internal bool ImmediatePropagationStopped => _immediatePropagationStopped;

    internal void SetCurrentTarget(object? currentTarget, DomEventPhase phase, bool passive)
    {
        this.currentTarget = currentTarget;
        eventPhase = phase;
        _currentListenerPassive = passive;
    }

    internal void ResetCurrentTarget()
    {
        currentTarget = null;
        eventPhase = DomEventPhase.None;
        _currentListenerPassive = false;
    }

    public void stopPropagation()
    {
        _propagationStopped = true;
        handled = true;
    }

    public void stopImmediatePropagation()
    {
        _propagationStopped = true;
        _immediatePropagationStopped = true;
        handled = true;
    }

    public void preventDefault()
    {
        if (!cancelable)
        {
            return;
        }

        if (_currentListenerPassive)
        {
            return;
        }

        _defaultPrevented = true;
        handled = true;
    }
}

public sealed class DomPointerEvent : DomEvent
{
    private readonly PointerEventArgs _args;
    private readonly Control _relativeTo;
    private readonly Control _viewport;

    internal DomPointerEvent(string type, PointerEventArgs args, Control relativeTo, Control? viewport, double timeStamp, bool bubbles = true, bool cancelable = true)
        : base(type, bubbles, cancelable, args, timeStamp, isTrusted: true)
    {
        _args = args;
        _relativeTo = relativeTo;
        _viewport = viewport ?? relativeTo;
    }

    public int pointerId => _args.Pointer?.Id ?? 0;

    public string pointerType => _args.Pointer?.Type.ToString().ToLowerInvariant() ?? "mouse";

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

    public double deltaX => _args is PointerWheelEventArgs wheel ? wheel.Delta.X : 0;

    public double deltaY => _args is PointerWheelEventArgs wheel ? -wheel.Delta.Y : 0;

    public double deltaZ => 0;

    public int deltaMode => 0;

    public int buttons
    {
        get
        {
            try
            {
                var point = _args.GetCurrentPoint(_relativeTo);
                return ToButtons(point.Properties);
            }
            catch
            {
                return 0;
            }
        }
    }

    public int button
    {
        get
        {
            try
            {
                var point = _args.GetCurrentPoint(_relativeTo);
                return ToButton(point.Properties.PointerUpdateKind);
            }
            catch
            {
                return -1;
            }
        }
    }

    public bool altKey => (_args.KeyModifiers & KeyModifiers.Alt) != 0;

    public bool ctrlKey => (_args.KeyModifiers & KeyModifiers.Control) != 0;

    public bool shiftKey => (_args.KeyModifiers & KeyModifiers.Shift) != 0;

    public bool metaKey => (_args.KeyModifiers & KeyModifiers.Meta) != 0;

    public bool isPrimary => _args.Pointer?.IsPrimary ?? false;

    private Point GetClientPosition()
    {
        var offset = GetOffsetPosition();
        var origin = _relativeTo.TranslatePoint(new Point(0, 0), _viewport);
        return origin.HasValue
            ? new Point(origin.Value.X + offset.X, origin.Value.Y + offset.Y)
            : offset;
    }

    private Point GetOffsetPosition() => _args.GetPosition(_relativeTo);

    private static int ToButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 0,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 1,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => 3,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => 4,
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
}

public sealed class DomKeyboardEvent : DomEvent
{
    private readonly KeyEventArgs _args;

    internal DomKeyboardEvent(string type, KeyEventArgs args, double timeStamp, bool bubbles = true, bool cancelable = true)
        : base(type, bubbles, cancelable, args, timeStamp, isTrusted: true)
    {
        _args = args;
    }

    public string? key => MapKey(_args.Key, _args.KeyModifiers);

    public string? code => MapCode(_args.Key);

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
}

public sealed class DomTextInputEvent : DomEvent
{
    private readonly TextInputEventArgs _args;

    internal DomTextInputEvent(string type, TextInputEventArgs args, double timeStamp, bool bubbles = true, bool cancelable = true)
        : base(type, bubbles, cancelable, args, timeStamp, isTrusted: true)
    {
        _args = args;
    }

    public string? data => _args.Text;
}

public sealed class DomSyntheticEvent : DomEvent
{
    internal DomSyntheticEvent(string type, bool bubbles, bool cancelable, double timeStamp, object? detail, JsValueAccessor? accessor)
        : base(type, bubbles, cancelable, args: null, timeStamp, isTrusted: false)
    {
        this.detail = detail;
        _accessor = accessor;
    }

    public object? detail { get; }

    private readonly JsValueAccessor? _accessor;

    internal void SyncDefaultPrevented()
    {
        _accessor?.SetDefaultPrevented(defaultPrevented);
    }
}

public sealed class JsValueAccessor
{
    private readonly Action<bool> _setDefaultPrevented;

    public JsValueAccessor(Action<bool> setDefaultPrevented)
    {
        _setDefaultPrevented = setDefaultPrevented;
    }

    public void SetDefaultPrevented(bool value) => _setDefaultPrevented(value);
}
