namespace HtmlML.Core;

public enum HtmlMlPointerEventKind
{
    Pressed,
    Moved,
    Released,
    Wheel,
    Entered,
    Exited,
    Canceled
}

public enum HtmlMlPointerType
{
    Mouse,
    Touch,
    Pen,
    Unknown
}

public sealed class HtmlMlPointerInputEventArgs : EventArgs
{
    public required HtmlMlPointerEventKind Kind { get; init; }

    public required HtmlMlPointerType PointerType { get; init; }

    public required long PointerId { get; init; }

    public required HtmlMlPoint Position { get; init; }

    public HtmlMlPoint Delta { get; init; }

    public int Button { get; init; }

    public int Buttons { get; init; }

    public bool AltKey { get; init; }

    public bool ControlKey { get; init; }

    public bool MetaKey { get; init; }

    public bool ShiftKey { get; init; }

    public HtmlMlBackendHandle SourceHandle { get; init; }

    public HtmlMlBackendHandle NativeEventHandle { get; init; }

    public bool Handled { get; set; }
}

public sealed class HtmlMlKeyboardInputEventArgs : EventArgs
{
    public required string Type { get; init; }

    public required string Key { get; init; }

    public string? Code { get; init; }

    public bool IsRepeat { get; init; }

    public bool AltKey { get; init; }

    public bool ControlKey { get; init; }

    public bool MetaKey { get; init; }

    public bool ShiftKey { get; init; }

    public HtmlMlBackendHandle SourceHandle { get; init; }

    public HtmlMlBackendHandle NativeEventHandle { get; init; }

    public bool Handled { get; set; }
}

public sealed class HtmlMlTextInputEventArgs : EventArgs
{
    public required string Text { get; init; }

    public HtmlMlBackendHandle SourceHandle { get; init; }

    public HtmlMlBackendHandle NativeEventHandle { get; init; }

    public bool Handled { get; set; }
}

public interface IHtmlMlInputSource
{
    event EventHandler<HtmlMlPointerInputEventArgs>? Pointer;

    event EventHandler<HtmlMlKeyboardInputEventArgs>? Keyboard;

    event EventHandler<HtmlMlTextInputEventArgs>? TextInput;
}
