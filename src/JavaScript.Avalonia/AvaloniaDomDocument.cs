using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Jint;
using Jint.Native;

namespace JavaScript.Avalonia;

public class AvaloniaDomDocument
{
    private readonly Func<string, Control?>? _elementFactory;

    protected JintAvaloniaHost Host { get; }

    public AvaloniaDomDocument(JintAvaloniaHost host, Func<string, Control?>? elementFactory = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _elementFactory = elementFactory;
    }

    protected virtual Control? GetDocumentRoot()
        => Host.TopLevel.Content as Control;

    public virtual object? getElementById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var root = GetDocumentRoot();
        if (root is null)
        {
            return null;
        }

        foreach (var control in Traverse(root))
        {
            if (string.Equals((control as StyledElement)?.Name, id, StringComparison.Ordinal))
            {
                return WrapControl(control);
            }
        }

        return null;
    }

    public virtual object? querySelector(string selector)
    {
        var all = querySelectorAll(selector);
        return all is { Length: > 0 } ? all[0] : null;
    }

    public virtual object[] querySelectorAll(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Array.Empty<object>();
        }

        var root = GetDocumentRoot();
        if (root is null)
        {
            return Array.Empty<object>();
        }

        var list = new List<object>();
        foreach (var control in Traverse(root))
        {
            if (MatchesSelector(control, selector))
            {
                list.Add(WrapControl(control));
            }
        }

        return list.ToArray();
    }

    public virtual object? createElement(string tag)
    {
        var control = CreateControl(tag);
        if (control is null)
        {
            return null;
        }

        return WrapControl(control);
    }

    public virtual object? body
    {
        get
        {
            var root = GetDocumentRoot();
            return root is null ? null : WrapControl(root);
        }
    }

    protected virtual object WrapControl(Control control) => new AvaloniaDomElement(Host, control);

    protected virtual Control? CreateControl(string tag)
    {
        if (_elementFactory is not null)
        {
            return _elementFactory(tag);
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var typeName = ToTypeName(tag.Trim());
        if (typeName is null)
        {
            return null;
        }

        var assembly = typeof(Control).Assembly;
        var qualified = typeName.Contains('.') ? typeName : $"Avalonia.Controls.{typeName}";
        var type = assembly.GetType(qualified, throwOnError: false, ignoreCase: true)
                   ?? Type.GetType(qualified, throwOnError: false, ignoreCase: true);
        if (type is null)
        {
            return null;
        }

        if (!typeof(Control).IsAssignableFrom(type))
        {
            return null;
        }

        try
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        catch
        {
            return null;
        }
    }

    protected static IEnumerable<Control> Traverse(Control root)
    {
        yield return root;

        switch (root)
        {
            case Panel panel:
                foreach (var child in panel.Children.OfType<Control>())
                {
                    foreach (var x in Traverse(child))
                    {
                        yield return x;
                    }
                }
                break;
            case ContentControl cc when cc.Content is Control content:
                foreach (var x in Traverse(content))
                {
                    yield return x;
                }
                break;
            case Decorator decorator when decorator.Child is Control child:
                foreach (var x in Traverse(child))
                {
                    yield return x;
                }
                break;
        }
    }

    protected virtual bool MatchesSelector(Control control, string selector)
    {
        selector = selector.Trim();
        if (selector.StartsWith("#", StringComparison.Ordinal))
        {
            var id = selector.Substring(1);
            return string.Equals((control as StyledElement)?.Name, id, StringComparison.Ordinal);
        }

        if (selector.StartsWith(".", StringComparison.Ordinal))
        {
            var cls = selector.Substring(1);
            return (control as StyledElement)?.Classes.Contains(cls) == true;
        }

        var typeName = control.GetType().Name;
        return string.Equals(typeName, selector, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ToTypeName(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        if (tag.IndexOfAny(new[] { '-', '_' }) >= 0)
        {
            var parts = tag.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Select(Capitalize));
        }

        if (char.IsLower(tag[0]))
        {
            return char.ToUpper(tag[0], CultureInfo.InvariantCulture) + tag.Substring(1);
        }

        return tag;
    }

    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length == 1)
        {
            return value.ToUpper(CultureInfo.InvariantCulture);
        }

        return char.ToUpper(value[0], CultureInfo.InvariantCulture) + value.Substring(1);
    }
}

public class AvaloniaDomElement
{
    private readonly Dictionary<string, List<EventSubscription>> _eventHandlers = new(StringComparer.OrdinalIgnoreCase);

    protected JintAvaloniaHost Host { get; }

    public Control Control { get; }

    public AvaloniaDomElement(JintAvaloniaHost host, Control control)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public virtual void addEventListener(string type, JsValue handler)
    {
        var normalized = NormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized) || IsNullish(handler))
        {
            return;
        }

        var subscription = EventSubscription.Create(this, normalized, handler);
        if (subscription is null)
        {
            return;
        }

        if (!_eventHandlers.TryGetValue(normalized, out var list))
        {
            list = new List<EventSubscription>();
            _eventHandlers[normalized] = list;
        }

        list.Add(subscription);
    }

    public virtual void removeEventListener(string type, JsValue handler)
    {
        var normalized = NormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized) || !_eventHandlers.TryGetValue(normalized, out var list))
        {
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var subscription = list[i];
            if (subscription.Callback.Equals(handler))
            {
                subscription.Dispose();
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
        {
            _eventHandlers.Remove(normalized);
        }
    }

    public virtual AvaloniaDomElement? appendChild(AvaloniaDomElement child)
    {
        if (child is null)
        {
            return null;
        }

        if (TryGetControlsCollection(Control, out var list))
        {
            list.Add(child.Control);
            return child;
        }

        if (Control is ContentControl cc)
        {
            cc.Content = child.Control;
            return child;
        }

        return null;
    }

    public virtual void remove()
    {
        var parent = Control.Parent;
        if (parent is Panel panel)
        {
            panel.Children.Remove(Control);
        }
        else if (parent is Decorator decorator)
        {
            if (decorator.Child == Control)
            {
                decorator.Child = null;
            }
        }
        else if (parent is ContentControl cc)
        {
            if (Equals(cc.Content, Control))
            {
                cc.Content = null;
            }
        }
    }

    public virtual string? getAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.ToLowerInvariant();
        if (TryGetAttribute(name, out var value))
        {
            return value;
        }

        var prop = Control.GetType().GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
        return prop?.GetValue(Control)?.ToString();
    }

    public virtual void setAttribute(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.ToLowerInvariant();
        switch (name)
        {
            case "id":
                if (Control is StyledElement se)
                {
                    se.Name = value;
                }
                return;
            case "class":
                if (Control is StyledElement se2)
                {
                    se2.Classes.Clear();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        foreach (var cls in value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            se2.Classes.Add(cls);
                        }
                    }
                }
                return;
            case "title":
                ToolTip.SetTip(Control, value);
                return;
        }

        if (TrySetAttribute(name, value))
        {
            return;
        }

        var type = Control.GetType();
        var prop = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                   ?? type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
        if (prop is null || !prop.CanWrite)
        {
            return;
        }

        try
        {
            object? converted = value;
            if (prop.PropertyType == typeof(double) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            {
                converted = d;
            }
            else if (prop.PropertyType == typeof(int) && int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var i))
            {
                converted = i;
            }
            prop.SetValue(Control, converted);
        }
        catch
        {
        }
    }

    public virtual void classListAdd(string cls)
    {
        if (Control is StyledElement se && !string.IsNullOrWhiteSpace(cls))
        {
            se.Classes.Add(cls);
        }
    }

    public virtual void classListRemove(string cls)
    {
        if (Control is StyledElement se && !string.IsNullOrWhiteSpace(cls))
        {
            se.Classes.Remove(cls);
        }
    }

    public virtual void classListToggle(string cls)
    {
        if (Control is StyledElement se && !string.IsNullOrWhiteSpace(cls))
        {
            if (se.Classes.Contains(cls))
            {
                se.Classes.Remove(cls);
            }
            else
            {
                se.Classes.Add(cls);
            }
        }
    }

    public virtual string? textContent
    {
        get => Control is TextBlock tb ? tb.Text : null;
        set
        {
            if (Control is TextBlock tb)
            {
                tb.Text = value;
            }
        }
    }

    private static bool IsNullish(JsValue value) => value.IsNull() || value.IsUndefined();

    private static string NormalizeEventName(string? type)
        => string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim().ToLowerInvariant();

    private void HandlePointerEvent(JsValue callback, PointerEventArgs args)
    {
        var data = new PointerEventInfo
        {
            x = args.GetPosition(Control).X,
            y = args.GetPosition(Control).Y,
            button = GetPointerButton(args, Control),
            handled = args.Handled
        };

        try
        {
            Host.Engine.Invoke(callback, data);
        }
        catch
        {
        }

        if (data.handled)
        {
            args.Handled = true;
        }
    }

    private void HandleKeyEvent(JsValue callback, KeyEventArgs args)
    {
        var data = new KeyEventInfo
        {
            key = args.Key.ToString(),
            handled = args.Handled
        };

        try
        {
            Host.Engine.Invoke(callback, data);
        }
        catch
        {
        }

        if (data.handled)
        {
            args.Handled = true;
        }
    }

    private void HandleTextInputEvent(JsValue callback, TextInputEventArgs args)
    {
        var data = new TextInputEventInfo
        {
            text = args.Text,
            handled = args.Handled
        };

        try
        {
            Host.Engine.Invoke(callback, data);
        }
        catch
        {
        }

        if (data.handled)
        {
            args.Handled = true;
        }
    }

    private static string? GetPointerButton(PointerEventArgs args, Control control)
    {
        try
        {
            return args.GetCurrentPoint(control).Properties.PointerUpdateKind.ToString();
        }
        catch
        {
            return null;
        }
    }

    private sealed class EventSubscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        private EventSubscription(JsValue callback, Action unsubscribe)
        {
            Callback = callback;
            _unsubscribe = unsubscribe;
        }

        public JsValue Callback { get; }

        public static EventSubscription? Create(AvaloniaDomElement element, string eventName, JsValue callback)
        {
            var control = element.Control;
            switch (eventName)
            {
                case "pointerdown":
                case "mousedown":
                    EventHandler<PointerPressedEventArgs> down = (s, e) => element.HandlePointerEvent(callback, e);
                    control.PointerPressed += down;
                    return new EventSubscription(callback, () => control.PointerPressed -= down);
                case "pointermove":
                case "mousemove":
                    EventHandler<PointerEventArgs> move = (s, e) => element.HandlePointerEvent(callback, e);
                    control.PointerMoved += move;
                    return new EventSubscription(callback, () => control.PointerMoved -= move);
                case "pointerup":
                case "mouseup":
                    EventHandler<PointerReleasedEventArgs> up = (s, e) => element.HandlePointerEvent(callback, e);
                    control.PointerReleased += up;
                    return new EventSubscription(callback, () => control.PointerReleased -= up);
                case "pointerenter":
                case "mouseenter":
                    EventHandler<PointerEventArgs> enter = (s, e) => element.HandlePointerEvent(callback, e);
                    control.PointerEntered += enter;
                    return new EventSubscription(callback, () => control.PointerEntered -= enter);
                case "pointerleave":
                case "mouseleave":
                    EventHandler<PointerEventArgs> leave = (s, e) => element.HandlePointerEvent(callback, e);
                    control.PointerExited += leave;
                    return new EventSubscription(callback, () => control.PointerExited -= leave);
                case "click":
                    if (control is Button button)
                    {
                        EventHandler<RoutedEventArgs> handler = (s, e) =>
                        {
                            try
                            {
                                element.Host.Engine.Invoke(callback);
                            }
                            catch
                            {
                            }
                        };
                        button.Click += handler;
                        return new EventSubscription(callback, () => button.Click -= handler);
                    }
                    else
                    {
                        EventHandler<PointerReleasedEventArgs> click = (s, e) => element.HandlePointerEvent(callback, e);
                        control.PointerReleased += click;
                        return new EventSubscription(callback, () => control.PointerReleased -= click);
                    }
                case "keydown":
                    EventHandler<KeyEventArgs> keyDown = (s, e) => element.HandleKeyEvent(callback, e);
                    control.KeyDown += keyDown;
                    return new EventSubscription(callback, () => control.KeyDown -= keyDown);
                case "keyup":
                    EventHandler<KeyEventArgs> keyUp = (s, e) => element.HandleKeyEvent(callback, e);
                    control.KeyUp += keyUp;
                    return new EventSubscription(callback, () => control.KeyUp -= keyUp);
                case "textinput":
                case "input":
                    EventHandler<TextInputEventArgs> textInput = (s, e) => element.HandleTextInputEvent(callback, e);
                    control.TextInput += textInput;
                    return new EventSubscription(callback, () => control.TextInput -= textInput);
                default:
                    return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _unsubscribe();
            _disposed = true;
        }
    }

    public sealed class PointerEventInfo
    {
        public double x { get; set; }
        public double y { get; set; }
        public string? button { get; set; }
        public bool handled { get; set; }
    }

    public sealed class KeyEventInfo
    {
        public string? key { get; set; }
        public bool handled { get; set; }
    }

    public sealed class TextInputEventInfo
    {
        public string? text { get; set; }
        public bool handled { get; set; }
    }

    protected virtual bool TryGetAttribute(string name, out string? value)
    {
        value = null;
        switch (name)
        {
            case "id":
                value = (Control as StyledElement)?.Name;
                return true;
            case "class":
                value = string.Join(' ', (Control as StyledElement)?.Classes ?? new Classes());
                return true;
            case "title":
                value = ToolTip.GetTip(Control)?.ToString();
                return true;
            case "style":
                value = GetStyleString(Control);
                return true;
        }

        return false;
    }

    protected virtual bool TrySetAttribute(string name, string? value)
    {
        if (name == "style")
        {
            return true;
        }

        return false;
    }

    protected virtual string GetStyleString(Control control) => string.Empty;

    protected static bool TryGetControlsCollection(Control parent, out Controls controls)
    {
        var prop = parent.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(p => p.PropertyType == typeof(Controls) && string.Equals(p.Name, "content", StringComparison.OrdinalIgnoreCase));
        if (prop is not null)
        {
            controls = (Controls)prop.GetValue(parent)!;
            return true;
        }

        if (parent is Panel panel)
        {
            controls = panel.Children;
            return true;
        }

        controls = null!;
        return false;
    }
}
