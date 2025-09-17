using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Boolean;
using Jint.Native.Object;
using Jint.Runtime;

namespace JavaScript.Avalonia;

public class AvaloniaDomDocument
{
    private readonly Func<string, Control?>? _elementFactory;
    private readonly ConditionalWeakTable<Control, AvaloniaDomElement> _elementWrappers = new();
    private readonly Dictionary<string, List<DomEventRegistration>> _documentEventListeners = new(StringComparer.OrdinalIgnoreCase);
    private bool _readyStateScheduled;
    private string _readyState = "loading";
    private readonly DomHeadElement _head;
    private readonly DomDocumentElement _documentElement;

    protected JintAvaloniaHost Host { get; }

    public AvaloniaDomDocument(JintAvaloniaHost host, Func<string, Control?>? elementFactory = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _elementFactory = elementFactory;
        _head = new DomHeadElement(this);
        _documentElement = new DomDocumentElement(this, _head);
    }

    protected virtual Control? GetDocumentRoot()
        => Host.TopLevel.Content as Control;

    public virtual string readyState => _readyState;

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

    public virtual object? createTextNode(string data)
    {
        var textBlock = CreateTextNodeControl(data ?? string.Empty);
        if (_elementWrappers.TryGetValue(textBlock, out var existing))
        {
            return existing;
        }

        var node = new AvaloniaDomTextNode(Host, textBlock);
        _elementWrappers.Add(textBlock, node);
        return node;
    }

    public virtual object? body
    {
        get
        {
            var root = GetDocumentRoot();
            if (root is null)
            {
                return null;
            }

            var wrapper = WrapControl(root);
            wrapper.SetNodeNameOverride("BODY");
            return wrapper;
        }
    }

    public DomHeadElement head => _head;

    public DomDocumentElement documentElement => _documentElement;

    public virtual string? title
    {
        get => Host.TopLevel is Window window ? window.Title : null;
        set
        {
            if (Host.TopLevel is Window window)
            {
                window.Title = value;
            }
        }
    }

    public virtual object[] forms => GetCollection(IsFormControl);

    public virtual object[] images => GetCollection(ctrl => ctrl is Image);

    public virtual object[] links => GetCollection(IsLinkControl);

    private static bool DocumentIsNullish(JsValue value) => value.IsNull() || value.IsUndefined();

    private static string DocumentNormalizeEventName(string? type)
        => string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim().ToLowerInvariant();

    public virtual void addEventListener(string type, JsValue handler)
        => addEventListener(type, handler, JsValue.Undefined);

    public virtual void addEventListener(string type, JsValue handler, JsValue options)
    {
        var normalized = DocumentNormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized) || DocumentIsNullish(handler))
        {
            return;
        }

        var listenerOptions = EventListenerOptions.FromJsValue(options);
        var listeners = GetDocumentListeners(normalized, create: true)!;
        if (listeners.Any(l => l.Callback.Equals(handler) && l.Options.Capture == listenerOptions.Capture))
        {
            return;
        }

        listeners.Add(new DomEventRegistration(handler, listenerOptions));
    }

    public virtual void removeEventListener(string type, JsValue handler)
        => removeEventListener(type, handler, JsValue.Undefined);

    public virtual void removeEventListener(string type, JsValue handler, JsValue options)
    {
        var normalized = DocumentNormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (!_documentEventListeners.TryGetValue(normalized, out var list))
        {
            return;
        }

        var capture = EventListenerOptions.FromJsValue(options).Capture;
        for (var i = 0; i < list.Count; i++)
        {
            var listener = list[i];
            if (listener.Callback.Equals(handler) && listener.Options.Capture == capture)
            {
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
        {
            _documentEventListeners.Remove(normalized);
        }
    }

    private List<DomEventRegistration>? GetDocumentListeners(string type, bool create)
    {
        if (_documentEventListeners.TryGetValue(type, out var list))
        {
            return list;
        }

        if (!create)
        {
            return null;
        }

        list = new List<DomEventRegistration>();
        _documentEventListeners[type] = list;
        return list;
    }

    public virtual object[] getElementsByClassName(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return Array.Empty<object>();
        }

        var classes = className.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return QueryAll(ctrl => ctrl is StyledElement se && classes.All(c => se.Classes.Contains(c)));
    }

    public virtual object[] getElementsByTagName(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return Array.Empty<object>();
        }

        return QueryAll(ctrl => string.Equals(ctrl.GetType().Name, tagName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    protected internal virtual AvaloniaDomElement WrapControl(Control control)
    {
        if (_elementWrappers.TryGetValue(control, out var existing))
        {
            return existing;
        }

        var created = new AvaloniaDomElement(Host, control);
        _elementWrappers.Add(control, created);
        return created;
    }

    private object[] GetCollection(Func<Control, bool> predicate) => QueryAll(predicate);

    private object[] QueryAll(Func<Control, bool> predicate)
    {
        var root = GetDocumentRoot();
        if (root is null)
        {
            return Array.Empty<object>();
        }

        var list = new List<object>();
        foreach (var control in Traverse(root))
        {
            if (predicate(control))
            {
                list.Add(WrapControl(control));
            }
        }

        return list.ToArray();
    }

    protected virtual bool IsFormControl(Control control)
    {
        var name = control.GetType().Name;
        return string.Equals(name, "Form", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Form", StringComparison.OrdinalIgnoreCase);
    }

    protected virtual bool IsLinkControl(Control control)
    {
        if (control is HyperlinkButton)
        {
            return true;
        }

        var name = control.GetType().Name;
        return string.Equals(name, "Hyperlink", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Link", StringComparison.OrdinalIgnoreCase);
    }

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

    private static TextBlock CreateTextNodeControl(string text)
        => new() { Text = text };

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

    internal void ScheduleReadyStateCompletion()
    {
        if (_readyStateScheduled)
        {
            return;
        }

        _readyStateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            SetReadyState("interactive");
            DispatchDocumentLifecycleEvent("readystatechange", bubbles: false, cancelable: false);
            SetReadyState("complete");
            DispatchDocumentLifecycleEvent("readystatechange", bubbles: false, cancelable: false);
            DispatchDocumentLifecycleEvent("DOMContentLoaded", bubbles: false, cancelable: false);
            DispatchDocumentLifecycleEvent("load", bubbles: false, cancelable: false);
        }, DispatcherPriority.Background);
    }

    private void SetReadyState(string state)
    {
        _readyState = state;
    }

    private void DispatchDocumentLifecycleEvent(string type, bool bubbles, bool cancelable)
    {
        var evt = new DomEvent(type, bubbles, cancelable, null, Host.GetTimestamp(), isTrusted: true);
        evt.target = this;
        DispatchDocumentEvent(evt);
    }

    internal void RaiseDocumentEvent(string type, bool bubbles, bool cancelable)
        => DispatchDocumentLifecycleEvent(type, bubbles, cancelable);

    private void DispatchDocumentEvent(DomEvent domEvent)
    {
        var normalizedType = DocumentNormalizeEventName(domEvent.type);
        if (string.IsNullOrEmpty(normalizedType))
        {
            return;
        }

        var entry = new DomEventPathEntry(this);
        var path = new List<DomEventPathEntry> { entry };
        if (!HasListeners(normalizedType, path))
        {
            return;
        }

        InvokeListeners(entry, normalizedType, domEvent, capture: true, DomEventPhase.AtTarget);
        if (!domEvent.ImmediatePropagationStopped)
        {
            InvokeListeners(entry, normalizedType, domEvent, capture: false, DomEventPhase.AtTarget);
        }

        domEvent.ResetCurrentTarget();
    }

    public virtual bool dispatchEvent(JsValue eventValue)
    {
        var synthetic = CreateSyntheticEvent(eventValue);
        if (synthetic is null)
        {
            return true;
        }

        synthetic.target = this;
        DispatchDocumentEvent(synthetic);
        synthetic.SyncDefaultPrevented();
        return !synthetic.defaultPrevented;
    }

    internal void DispatchPointerEvent(AvaloniaDomElement target, string type, PointerEventArgs args, bool bubbles, bool cancelable)
    {
        var evt = new DomPointerEvent(type, args, target.Control, Host.GetTimestamp(), bubbles, cancelable);
        DispatchDomEventInternal(target, evt);
    }

    internal void DispatchKeyboardEvent(AvaloniaDomElement target, string type, KeyEventArgs args, bool bubbles, bool cancelable)
    {
        var evt = new DomKeyboardEvent(type, args, Host.GetTimestamp(), bubbles, cancelable);
        DispatchDomEventInternal(target, evt);
    }

    internal void DispatchTextInputEvent(AvaloniaDomElement target, string type, TextInputEventArgs args, bool bubbles, bool cancelable)
    {
        var evt = new DomTextInputEvent(type, args, Host.GetTimestamp(), bubbles, cancelable);
        DispatchDomEventInternal(target, evt);
    }

    internal void DispatchRoutedEvent(AvaloniaDomElement target, string type, RoutedEventArgs args, bool bubbles, bool cancelable)
    {
        var evt = new DomEvent(type, bubbles, cancelable, args, Host.GetTimestamp(), isTrusted: true);
        DispatchDomEventInternal(target, evt);
    }

    internal void DispatchSyntheticEvent(AvaloniaDomElement target, DomSyntheticEvent evt)
        => DispatchDomEventInternal(target, evt);

    internal DomSyntheticEvent? CreateSyntheticEvent(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        var timeStamp = Host.GetTimestamp();

        if (value.IsObject())
        {
            var existing = value.ToObject();
            if (existing is DomSyntheticEvent domSyntheticEvent)
            {
                return domSyntheticEvent;
            }
        }

        if (value.IsString())
        {
            var rawType = value.AsString();
            var trimmedType = rawType?.Trim() ?? string.Empty;
            var normalizedType = DocumentNormalizeEventName(trimmedType);
            if (string.IsNullOrEmpty(normalizedType))
            {
                return null;
            }

            return new DomSyntheticEvent(trimmedType, bubbles: false, cancelable: false, timeStamp, detail: null, accessor: null);
        }

        if (value.IsObject())
        {
            var obj = value.AsObject();
            var typeValue = obj.Get("type");
            var rawType = typeValue.IsString() ? typeValue.AsString() : string.Empty;
            var trimmedType = rawType?.Trim() ?? string.Empty;
            var normalizedType = DocumentNormalizeEventName(trimmedType);
            if (string.IsNullOrEmpty(normalizedType))
            {
                return null;
            }

            var bubbles = ToBoolean(obj.Get("bubbles"));
            var cancelable = ToBoolean(obj.Get("cancelable"));
            var detailValue = obj.Get("detail");
            var detail = detailValue.IsUndefined() ? null : detailValue.ToObject();

            JsValueAccessor accessor = new(value => obj.Set("defaultPrevented", JsValue.FromObject(Host.Engine, value), throwOnError: false));
            obj.Set("defaultPrevented", JsBoolean.False, throwOnError: false);

            var syntheticPath = ExtractSyntheticPath(obj.Get("path"));
            var evt = new DomSyntheticEvent(trimmedType, bubbles, cancelable, timeStamp, detail, accessor)
            {
                SyntheticPath = syntheticPath
            };
            return evt;
        }

        return null;
    }

    internal DomSyntheticEvent CreateEventFromConstructor(JsValue typeValue, JsValue initValue, bool isCustom)
    {
        var type = RequireEventType(typeValue);
        ParseEventInit(initValue, isCustom, out var bubbles, out var cancelable, out var detail, out var syntheticPath);
        var evt = new DomSyntheticEvent(type, bubbles, cancelable, Host.GetTimestamp(), detail, accessor: null)
        {
            SyntheticPath = syntheticPath
        };
        return evt;
    }

    private string RequireEventType(JsValue value)
    {
        if (!value.IsString())
        {
            throw new ArgumentException("Event type must be a non-empty string.");
        }

        var type = value.AsString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(type))
        {
            throw new ArgumentException("Event type must be a non-empty string.");
        }

        return type;
    }

    private void ParseEventInit(JsValue initValue, bool isCustom, out bool bubbles, out bool cancelable, out object? detail, out List<AvaloniaDomElement>? syntheticPath)
    {
        bubbles = false;
        cancelable = false;
        detail = null;
        syntheticPath = null;

        if (initValue.IsNull() || initValue.IsUndefined())
        {
            return;
        }

        if (!initValue.IsObject())
        {
            return;
        }

        var init = initValue.AsObject();
        bubbles = ToBoolean(init.Get("bubbles"));
        cancelable = ToBoolean(init.Get("cancelable"));

        if (isCustom)
        {
            var detailValue = init.Get("detail");
            if (!detailValue.IsUndefined() && !detailValue.IsNull())
            {
                detail = detailValue.ToObject();
            }
        }

        syntheticPath = ExtractSyntheticPath(init.Get("path"));
    }

    private List<AvaloniaDomElement>? ExtractSyntheticPath(JsValue pathValue)
    {
        if (pathValue.IsUndefined() || pathValue.IsNull() || !pathValue.IsObject())
        {
            return null;
        }

        if (!pathValue.IsArray())
        {
            return null;
        }

        var array = pathValue.AsArray();
        var lengthValue = array.Get("length");
        uint length = 0;
        if (lengthValue.IsNumber())
        {
            var number = lengthValue.AsNumber();
            if (number > 0)
            {
                length = (uint)Math.Min(number, uint.MaxValue);
            }
        }
        var list = new List<AvaloniaDomElement>();

        for (uint i = 0; i < length; i++)
        {
            var entry = array.Get(i);
            if (entry.IsUndefined() || entry.IsNull())
            {
                continue;
            }

            var resolved = entry.ToObject();
            if (resolved is AvaloniaDomElement element && !list.Contains(element))
            {
                list.Add(element);
            }
        }

        return list.Count > 0 ? list : null;
    }

    private void DispatchDomEventInternal(AvaloniaDomElement target, DomEvent domEvent)
    {
        domEvent.target = target;
        var normalizedType = DocumentNormalizeEventName(domEvent.type);
        if (string.IsNullOrEmpty(normalizedType))
        {
            return;
        }

        var path = BuildEventPath(target);
        if (domEvent.SyntheticPath is { Count: > 0 })
        {
            var insertIndex = Math.Max(1, path.Count - 1);
            foreach (var synthetic in domEvent.SyntheticPath)
            {
                if (synthetic is null)
                {
                    continue;
                }

                path.Insert(insertIndex, new DomEventPathEntry(synthetic));
                insertIndex++;
            }
        }
        if (!HasListeners(normalizedType, path))
        {
            return;
        }

        for (var i = 0; i < path.Count - 1; i++)
        {
            var entry = path[i];
            InvokeListeners(entry, normalizedType, domEvent, capture: true, DomEventPhase.CapturingPhase);
            if (domEvent.PropagationStopped)
            {
                domEvent.ResetCurrentTarget();
                return;
            }
        }

        var targetEntry = path[^1];
        InvokeListeners(targetEntry, normalizedType, domEvent, capture: true, DomEventPhase.AtTarget);
        if (!domEvent.ImmediatePropagationStopped)
        {
            InvokeListeners(targetEntry, normalizedType, domEvent, capture: false, DomEventPhase.AtTarget);
        }

        if (domEvent.PropagationStopped || !domEvent.bubbles)
        {
            domEvent.ResetCurrentTarget();
            return;
        }

        for (var i = path.Count - 2; i >= 0; i--)
        {
            var entry = path[i];
            InvokeListeners(entry, normalizedType, domEvent, capture: false, DomEventPhase.BubblingPhase);
            if (domEvent.PropagationStopped)
            {
                domEvent.ResetCurrentTarget();
                return;
            }
        }

        domEvent.ResetCurrentTarget();
    }

    private List<DomEventPathEntry> BuildEventPath(AvaloniaDomElement target)
    {
        var path = new List<DomEventPathEntry> { new(this) };
        var stack = new Stack<AvaloniaDomElement>();
        for (var current = target; current is not null; current = current.parentElement)
        {
            stack.Push(current);
        }

        while (stack.Count > 0)
        {
            path.Add(new DomEventPathEntry(stack.Pop()));
        }

        return path;
    }

    private bool HasListeners(string type, List<DomEventPathEntry> path)
    {
        if (_documentEventListeners.TryGetValue(type, out var documentList) && documentList.Count > 0)
        {
            return true;
        }

        foreach (var entry in path)
        {
            if (!entry.IsDocument && entry.Element!.HasListeners(type))
            {
                return true;
            }
        }

        return false;
    }

    private void InvokeListeners(DomEventPathEntry entry, string type, DomEvent domEvent, bool capture, DomEventPhase phase)
    {
        if (entry.IsDocument)
        {
            if (!_documentEventListeners.TryGetValue(type, out var list) || list.Count == 0)
            {
                return;
            }

            InvokeListenersCore(this, type, domEvent, capture, phase, list, listener => RemoveDocumentListener(type, listener));
            return;
        }

        var element = entry.Element!;
        var listeners = element.GetListeners(type);
        if (listeners is null || listeners.Count == 0)
        {
            return;
        }

        InvokeListenersCore(element, type, domEvent, capture, phase, listeners, listener => element.RemoveListener(type, listener));
    }

    private void InvokeListenersCore(object currentTarget, string type, DomEvent domEvent, bool capture, DomEventPhase phase, IReadOnlyList<DomEventRegistration> listeners, Action<DomEventRegistration> remove)
    {
        if (listeners.Count == 0)
        {
            return;
        }

        var snapshot = listeners.ToArray();
        foreach (var listener in snapshot)
        {
            if (listener.Options.Capture != capture)
            {
                continue;
            }

            domEvent.SetCurrentTarget(currentTarget, phase, listener.Options.Passive);
            try
            {
                Host.Engine.Invoke(listener.Callback, domEvent);
            }
            catch
            {
            }

            if (listener.Options.Once)
            {
                remove(listener);
            }

            if (domEvent.ImmediatePropagationStopped)
            {
                break;
            }
        }
    }

    private void RemoveDocumentListener(string type, DomEventRegistration listener)
    {
        if (_documentEventListeners.TryGetValue(type, out var list) && list.Remove(listener) && list.Count == 0)
        {
            _documentEventListeners.Remove(type);
        }
    }

    private static bool ToBoolean(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return false;
        }

        return Jint.Runtime.TypeConverter.ToBoolean(value);
    }

    private readonly struct DomEventPathEntry
    {
        public DomEventPathEntry(AvaloniaDomDocument document)
        {
            Document = document;
            Element = null;
        }

        public DomEventPathEntry(AvaloniaDomElement element)
        {
            Document = null;
            Element = element;
        }

        public AvaloniaDomDocument? Document { get; }

        public AvaloniaDomElement? Element { get; }

        public bool IsDocument => Document is not null;
    }

}

internal readonly struct EventListenerOptions
{
    public EventListenerOptions(bool capture, bool once, bool passive)
    {
        Capture = capture;
        Once = once;
        Passive = passive;
    }

    public bool Capture { get; }

    public bool Once { get; }

    public bool Passive { get; }

    public static EventListenerOptions FromJsValue(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return default;
        }

        if (value.IsBoolean())
        {
            var flag = value.AsBoolean();
            return new EventListenerOptions(flag, false, false);
        }

        if (value.IsObject())
        {
            var obj = value.AsObject();
            var capture = ToBoolean(obj.Get("capture"));
            var once = ToBoolean(obj.Get("once"));
            var passive = ToBoolean(obj.Get("passive"));
            return new EventListenerOptions(capture, once, passive);
        }

        return default;
    }

    private static bool ToBoolean(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return false;
        }

        return Jint.Runtime.TypeConverter.ToBoolean(value);
    }
}

internal sealed class DomEventRegistration
{
    public DomEventRegistration(JsValue callback, EventListenerOptions options)
    {
        Callback = callback;
        Options = options;
    }

    public JsValue Callback { get; }

    public EventListenerOptions Options { get; }
}


public class AvaloniaDomElement
{
    private readonly Dictionary<string, List<DomEventRegistration>> _eventListeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _dataAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _styleValues = new(StringComparer.OrdinalIgnoreCase);
    private DomStringMap? _dataset;
    private CssStyleDeclaration? _style;
    private DomTokenList? _classList;
    private double _scrollLeft;
    private double _scrollTop;
    private bool _pointerHandlersAttached;
    private bool _pointerOverHandlersAttached;
    private bool _keyboardHandlersAttached;
    private bool _textInputHandlersAttached;
    private bool _clickHandlersAttached;
    private string? _nodeNameOverride;

    protected JintAvaloniaHost Host { get; }

    public Control Control { get; }

    public AvaloniaDomElement(JintAvaloniaHost host, Control control)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Control = control ?? throw new ArgumentNullException(nameof(control));
        EnsureEventBridges();
    }

    public virtual int nodeType => 1;

    public virtual string nodeName
    {
        get
        {
            var name = _nodeNameOverride ?? Control.GetType().Name;
            return string.IsNullOrEmpty(name) ? string.Empty : name.ToUpperInvariant();
        }
    }

    public virtual string? nodeValue
    {
        get => null;
        set { }
    }

    public AvaloniaDomDocument ownerDocument => Host.Document;

    public AvaloniaDomElement? parentNode => parentElement;

    public DomTokenList classList => _classList ??= new DomTokenList(this);

    public CssStyleDeclaration style => _style ??= new CssStyleDeclaration(this);

    public DomStringMap dataset => _dataset ??= new DomStringMap(this);

    internal IReadOnlyDictionary<string, string?> DataAttributes => _dataAttributes;

    internal IReadOnlyDictionary<string, string?> StyleValues => _styleValues;

    public AvaloniaDomElement? parentElement => Control.Parent is Control parent ? Host.Document.WrapControl(parent) : null;

    public AvaloniaDomElement? firstChild => GetChildElements().FirstOrDefault();

    public AvaloniaDomElement? lastChild => GetChildElements().LastOrDefault();

    public AvaloniaDomElement? previousSibling => GetSibling(-1);

    public AvaloniaDomElement? nextSibling => GetSibling(1);

    public AvaloniaDomElement? firstElementChild => firstChild;

    public AvaloniaDomElement? lastElementChild => lastChild;

    public AvaloniaDomElement? previousElementSibling => previousSibling;

    public AvaloniaDomElement? nextElementSibling => nextSibling;

    public object[] childNodes => GetChildElements().Cast<object>().ToArray();

    public object[] children => childNodes;

    public int childElementCount => GetChildElements().Count();

    public bool hasChildNodes => GetChildElements().Any();

    public string tagName => nodeName;

    public virtual DomRect getBoundingClientRect() => new(Control.Bounds);

    public virtual double offsetWidth => Control.Bounds.Width;

    public virtual double offsetHeight => Control.Bounds.Height;

    public virtual double offsetTop => Control.Bounds.Top;

    public virtual double offsetLeft => Control.Bounds.Left;

    public virtual double scrollWidth => Control.Bounds.Width;

    public virtual double scrollHeight => Control.Bounds.Height;

    public virtual double scrollTop
    {
        get => _scrollTop;
        set => _scrollTop = value;
    }

    public virtual double scrollLeft
    {
        get => _scrollLeft;
        set => _scrollLeft = value;
    }

    public virtual void addEventListener(string type, JsValue handler)
        => addEventListener(type, handler, JsValue.Undefined);

    public virtual void addEventListener(string type, JsValue handler, JsValue optionsValue)
    {
        var normalized = NormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized) || IsNullish(handler))
        {
            return;
        }

        var options = EventListenerOptions.FromJsValue(optionsValue);
        var listeners = GetOrCreateEventListeners(normalized);
        if (listeners.Any(l => l.Callback.Equals(handler) && l.Options.Capture == options.Capture))
        {
            return;
        }

        listeners.Add(new DomEventRegistration(handler, options));
    }

    public virtual void removeEventListener(string type, JsValue handler)
        => removeEventListener(type, handler, JsValue.Undefined);

    public virtual void removeEventListener(string type, JsValue handler, JsValue optionsValue)
    {
        var normalized = NormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (!_eventListeners.TryGetValue(normalized, out var list))
        {
            return;
        }

        var capture = EventListenerOptions.FromJsValue(optionsValue).Capture;
        for (var i = 0; i < list.Count; i++)
        {
            var listener = list[i];
            if (listener.Callback.Equals(handler) && listener.Options.Capture == capture)
            {
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
        {
            _eventListeners.Remove(normalized);
        }
    }

    internal bool HasListeners(string type)
        => _eventListeners.TryGetValue(type, out var list) && list.Count > 0;

    internal List<DomEventRegistration>? GetListeners(string type)
        => _eventListeners.TryGetValue(type, out var list) ? list : null;

    internal void RemoveListener(string type, DomEventRegistration listener)
    {
        if (_eventListeners.TryGetValue(type, out var list) && list.Remove(listener) && list.Count == 0)
        {
            _eventListeners.Remove(type);
        }
    }

    private List<DomEventRegistration> GetOrCreateEventListeners(string eventName)
    {
        if (!_eventListeners.TryGetValue(eventName, out var list))
        {
            list = new List<DomEventRegistration>();
            _eventListeners[eventName] = list;
        }

        return list;
    }

    internal void SetNodeNameOverride(string value)
    {
        _nodeNameOverride = string.IsNullOrEmpty(value) ? null : value.ToUpperInvariant();
    }

    private void EnsureEventBridges()
    {
        if (!_pointerHandlersAttached)
        {
            AttachPointerHandlers();
        }

        if (!_pointerOverHandlersAttached)
        {
            AttachPointerOverHandlers();
        }

        if (!_keyboardHandlersAttached)
        {
            AttachKeyboardHandlers();
        }

        if (!_textInputHandlersAttached)
        {
            AttachTextInputHandlers();
        }

        if (!_clickHandlersAttached)
        {
            AttachClickHandlers();
        }
    }

    private void AttachPointerHandlers()
    {
        _pointerHandlersAttached = true;
        Control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        Control.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        Control.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void AttachPointerOverHandlers()
    {
        _pointerOverHandlersAttached = true;
        Control.PointerEntered += OnPointerEntered;
        Control.PointerExited += OnPointerExited;
    }

    private void AttachKeyboardHandlers()
    {
        _keyboardHandlersAttached = true;
        Control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Control.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void AttachTextInputHandlers()
    {
        _textInputHandlersAttached = true;
        Control.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void AttachClickHandlers()
    {
        _clickHandlersAttached = true;
        if (Control is Button button)
        {
            button.Click += OnButtonClick;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Control))
        {
            return;
        }

        Host.Document.DispatchPointerEvent(this, "pointerdown", e, bubbles: true, cancelable: true);
        Host.Document.DispatchPointerEvent(this, "mousedown", e, bubbles: true, cancelable: true);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Control))
        {
            return;
        }

        Host.Document.DispatchPointerEvent(this, "pointermove", e, bubbles: true, cancelable: false);
        Host.Document.DispatchPointerEvent(this, "mousemove", e, bubbles: true, cancelable: false);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Control))
        {
            return;
        }

        Host.Document.DispatchPointerEvent(this, "pointerup", e, bubbles: true, cancelable: true);
        Host.Document.DispatchPointerEvent(this, "mouseup", e, bubbles: true, cancelable: true);

        if (Control is not Button && e.InitialPressMouseButton == MouseButton.Left)
        {
            Host.Document.DispatchPointerEvent(this, "click", e, bubbles: true, cancelable: true);
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        Host.Document.DispatchPointerEvent(this, "pointerenter", e, bubbles: false, cancelable: false);
        Host.Document.DispatchPointerEvent(this, "mouseenter", e, bubbles: false, cancelable: false);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        Host.Document.DispatchPointerEvent(this, "pointerleave", e, bubbles: false, cancelable: false);
        Host.Document.DispatchPointerEvent(this, "mouseleave", e, bubbles: false, cancelable: false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Control))
        {
            return;
        }

        Host.Document.DispatchKeyboardEvent(this, "keydown", e, bubbles: true, cancelable: true);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Control))
        {
            return;
        }

        Host.Document.DispatchKeyboardEvent(this, "keyup", e, bubbles: true, cancelable: false);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Control))
        {
            return;
        }

        Host.Document.DispatchTextInputEvent(this, "textinput", e, bubbles: true, cancelable: false);
        Host.Document.DispatchTextInputEvent(this, "input", e, bubbles: true, cancelable: false);
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        Host.Document.DispatchRoutedEvent(this, "click", e, bubbles: true, cancelable: true);
    }

    public bool dispatchEvent(JsValue eventValue)
    {
        var synthetic = Host.Document.CreateSyntheticEvent(eventValue);
        if (synthetic is null)
        {
            return true;
        }

        Host.Document.DispatchSyntheticEvent(this, synthetic);
        synthetic.SyncDefaultPrevented();
        return !synthetic.defaultPrevented;
    }

    public virtual AvaloniaDomElement? appendChild(AvaloniaDomElement child)
        => InsertChild(child, reference: null, placeBefore: false);

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

    public virtual AvaloniaDomElement? insertBefore(AvaloniaDomElement newChild, AvaloniaDomElement? referenceChild)
    {
        if (referenceChild is null)
        {
            return appendChild(newChild);
        }

        return InsertChild(newChild, referenceChild, placeBefore: true);
    }

    public virtual AvaloniaDomElement? removeChild(AvaloniaDomElement child)
    {
        if (child is null)
        {
            return null;
        }

        if (TryGetControlsCollection(Control, out var list))
        {
            return list.Remove(child.Control) ? child : null;
        }

        if (Control is ContentControl cc)
        {
            if (ReferenceEquals(cc.Content, child.Control))
            {
                cc.Content = null;
                return child;
            }

            return null;
        }

        if (Control is Decorator decorator)
        {
            if (ReferenceEquals(decorator.Child, child.Control))
            {
                decorator.Child = null;
                return child;
            }

            return null;
        }

        return null;
    }

    public virtual AvaloniaDomElement? replaceChild(AvaloniaDomElement newChild, AvaloniaDomElement oldChild)
    {
        if (newChild is null || oldChild is null)
        {
            return null;
        }

        if (ReferenceEquals(newChild.Control, oldChild.Control))
        {
            return oldChild;
        }

        if (TryGetControlsCollection(Control, out var list))
        {
            var index = list.IndexOf(oldChild.Control);
            if (index < 0)
            {
                return null;
            }

            DetachFromParent(newChild.Control);
            list.RemoveAt(index);
            list.Insert(index, newChild.Control);
            return oldChild;
        }

        if (Control is ContentControl cc)
        {
            if (!ReferenceEquals(cc.Content, oldChild.Control))
            {
                return null;
            }

            DetachFromParent(newChild.Control);
            cc.Content = newChild.Control;
            return oldChild;
        }

        if (Control is Decorator decorator)
        {
            if (!ReferenceEquals(decorator.Child, oldChild.Control))
            {
                return null;
            }

            DetachFromParent(newChild.Control);
            decorator.Child = newChild.Control;
            return oldChild;
        }

        return null;
    }

    public virtual string? getAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.ToLowerInvariant();
        if (_dataAttributes.TryGetValue(name, out var dataValue))
        {
            return dataValue;
        }

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
                if (Control is StyledElement styled)
                {
                    styled.Name = value;
                }
                return;
            case "class":
                classList.SetFromString(value);
                return;
            case "title":
                ToolTip.SetTip(Control, value);
                return;
        }

        if (name.StartsWith("data-", StringComparison.Ordinal))
        {
            SetDataAttribute(name, value);
            return;
        }

        if (TrySetAttribute(name, value))
        {
            return;
        }

        SetControlProperty(name, value);
    }

    public virtual void classListAdd(string cls)
    {
        classList.add(cls);
    }

    public virtual void classListRemove(string cls)
    {
        classList.remove(cls);
    }

    public virtual void classListToggle(string cls)
    {
        classList.toggle(cls);
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

    internal bool TryGetDataAttribute(string attributeName, out string? value)
        => _dataAttributes.TryGetValue(attributeName.ToLowerInvariant(), out value);

    internal void SetDataAttribute(string attributeName, string? value)
    {
        attributeName = attributeName.ToLowerInvariant();
        if (value is null)
        {
            _dataAttributes.Remove(attributeName);
        }
        else
        {
            _dataAttributes[attributeName] = value;
        }
    }

    internal bool RemoveDataAttribute(string attributeName)
    {
        attributeName = attributeName.ToLowerInvariant();
        return _dataAttributes.Remove(attributeName);
    }

    internal IEnumerable<KeyValuePair<string, string?>> EnumerateDataset()
    {
        foreach (var pair in _dataAttributes)
        {
            yield return new KeyValuePair<string, string?>(DomStringMap.ToDatasetKey(pair.Key), pair.Value);
        }
    }

    internal void SetStyleProperty(string propertyName, string? value)
    {
        var normalized = CssStyleDeclaration.NormalizePropertyName(propertyName);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (value is null)
        {
            if (_styleValues.Remove(normalized))
            {
                ApplyStyleToControl(normalized, null);
            }
        }
        else
        {
            _styleValues[normalized] = value;
            ApplyStyleToControl(normalized, value);
        }
    }

    private void ApplyStyleToControl(string cssName, string? value)
    {
        var propertyName = CssStyleDeclaration.ToPropertyName(cssName);
        if (string.IsNullOrEmpty(propertyName))
        {
            return;
        }

        SetControlProperty(propertyName, value);
    }

    private void ApplyStyleAttribute(string? value)
    {
        var keys = _styleValues.Keys.ToList();
        foreach (var key in keys)
        {
            ApplyStyleToControl(key, null);
        }

        _styleValues.Clear();

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var declarations = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var declaration in declarations)
        {
            var parts = declaration.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var property = parts[0].Trim();
            var propertyValue = parts[1].Trim();
            if (string.IsNullOrEmpty(property))
            {
                continue;
            }

            SetStyleProperty(property, propertyValue);
        }
    }

    private void SetControlProperty(string propertyName, string? value)
    {
        if (TrySetAvaloniaProperty(propertyName, value))
        {
            return;
        }

        var type = Control.GetType();
        var prop = type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                   ?? type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
        if (prop is null || !prop.CanWrite)
        {
            return;
        }

        try
        {
            var converted = ConvertToPropertyValue(prop.PropertyType, value);
            prop.SetValue(Control, converted);
        }
        catch
        {
        }
    }

    private bool TrySetAvaloniaProperty(string propertyName, string? value)
    {
        var type = Control.GetType();
        var registered = FindAvaloniaProperty(type, propertyName);
        if (registered is null)
        {
            return false;
        }

        if (value is null)
        {
            Control.ClearValue(registered);
            return true;
        }

        var converted = ConvertToPropertyValue(registered.PropertyType, value);

        try
        {
            Control.SetValue(registered, converted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? ConvertToPropertyValue(Type propertyType, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (propertyType == typeof(string))
        {
            return value;
        }

        if (TryConvertFromString(propertyType, value, out var convertedFromString))
        {
            return convertedFromString;
        }

        if (propertyType == typeof(Thickness) || propertyType == typeof(Thickness?))
        {
            if (TryParseThickness(value, out var thickness))
            {
                return thickness;
            }
        }

        if (propertyType == typeof(Color) || propertyType == typeof(Color?))
        {
            if (Color.TryParse(value, out var color))
            {
                return color;
            }
        }

        if (typeof(IBrush).IsAssignableFrom(propertyType))
        {
            try
            {
                return Brush.Parse(value);
            }
            catch
            {
            }
        }

        if (propertyType == typeof(double) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        if (propertyType == typeof(int) && int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var i))
        {
            return i;
        }

        if (propertyType == typeof(bool) && bool.TryParse(value, out var b))
        {
            return b;
        }

        return value;
    }

    private static bool TryConvertFromString(Type propertyType, string value, out object? converted)
    {
        var converter = TypeDescriptor.GetConverter(propertyType);
        if (converter is not null && converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                converted = converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                return true;
            }
            catch
            {
            }
        }

        converted = null;
        return false;
    }

    private static bool TryParseThickness(string value, out Thickness thickness)
    {
        thickness = default;
        var separators = new[] { ',', ' ' };
        var parts = value.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        static bool TryParseDouble(string token, out double result)
            => double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out result);

        switch (parts.Length)
        {
            case 1 when TryParseDouble(parts[0], out var uniform):
                thickness = new Thickness(uniform);
                return true;
            case 2 when TryParseDouble(parts[0], out var horizontal) && TryParseDouble(parts[1], out var vertical):
                thickness = new Thickness(horizontal, vertical, horizontal, vertical);
                return true;
            case 4 when TryParseDouble(parts[0], out var left)
                        && TryParseDouble(parts[1], out var top)
                        && TryParseDouble(parts[2], out var right)
                        && TryParseDouble(parts[3], out var bottom):
                thickness = new Thickness(left, top, right, bottom);
                return true;
            default:
                return false;
        }
    }

    private static AvaloniaProperty? FindAvaloniaProperty(Type controlType, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName) || controlType is null)
        {
            return null;
        }

        var registry = AvaloniaPropertyRegistry.Instance;

        for (var type = controlType; type is not null && typeof(AvaloniaObject).IsAssignableFrom(type); type = type.BaseType)
        {
            var property = registry.FindRegistered(type, propertyName);
            if (property is not null)
            {
                return property;
            }

            PropertyInfo? info = null;
            try
            {
                info = type.GetProperty(propertyName, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            }
            catch (AmbiguousMatchException)
            {
                // Prefer explicit lookup below using registered property names
            }
            if (info is not null)
            {
                property = registry.FindRegistered(type, info.Name);
                if (property is not null)
                {
                    return property;
                }
            }

            var pascal = CssStyleDeclaration.ToPropertyName(propertyName);
            if (!string.Equals(pascal, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = registry.FindRegistered(type, pascal);
                if (property is not null)
                {
                    return property;
                }
            }
        }

        return null;
    }

    private IEnumerable<AvaloniaDomElement> GetChildElements()
    {
        if (TryGetControlsCollection(Control, out var controls))
        {
            foreach (var child in controls.OfType<Control>())
            {
                yield return Host.Document.WrapControl(child);
            }
            yield break;
        }

        if (Control is ContentControl cc && cc.Content is Control content)
        {
            yield return Host.Document.WrapControl(content);
        }
        else if (Control is Decorator decorator && decorator.Child is Control child)
        {
            yield return Host.Document.WrapControl(child);
        }
    }

    private AvaloniaDomElement? GetSibling(int offset)
    {
        if (Control.Parent is Panel panel)
        {
            var list = panel.Children.OfType<Control>().ToList();
            var index = list.IndexOf(Control);
            if (index >= 0)
            {
                var target = index + offset;
                if (target >= 0 && target < list.Count)
                {
                    return Host.Document.WrapControl(list[target]);
                }
            }
        }

        return null;
    }

    private AvaloniaDomElement? InsertChild(AvaloniaDomElement? child, AvaloniaDomElement? reference, bool placeBefore)
    {
        if (child is null)
        {
            return null;
        }

        if (TryGetControlsCollection(Control, out var list))
        {
            if (reference is null)
            {
                DetachFromParent(child.Control);
                list.Add(child.Control);
                return child;
            }

            var index = list.IndexOf(reference.Control);
            if (index < 0)
            {
                return null;
            }

            DetachFromParent(child.Control);
            var targetIndex = placeBefore ? index : Math.Min(index + 1, list.Count);
            list.Insert(targetIndex, child.Control);
            return child;
        }

        if (Control is ContentControl cc)
        {
            if (reference is not null && !ReferenceEquals(cc.Content, reference.Control))
            {
                return null;
            }

            DetachFromParent(child.Control);
            cc.Content = child.Control;
            return child;
        }

        if (Control is Decorator decorator)
        {
            if (reference is not null && !ReferenceEquals(decorator.Child, reference.Control))
            {
                return null;
            }

            DetachFromParent(child.Control);
            decorator.Child = child.Control;
            return child;
        }

        return null;
    }

    private static void DetachFromParent(Control control)
    {
        var parent = control.Parent;
        switch (parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator when decorator.Child == control:
                decorator.Child = null;
                break;
            case ContentControl cc when ReferenceEquals(cc.Content, control):
                cc.Content = null;
                break;
        }
    }

    private static bool IsNullish(JsValue value) => value.IsNull() || value.IsUndefined();

    private static string NormalizeEventName(string? type)
        => string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim().ToLowerInvariant();

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
            ApplyStyleAttribute(value);
            return true;
        }

        return false;
    }

    protected virtual string GetStyleString(Control control)
    {
        if (_styleValues.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", _styleValues.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

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

public sealed class AvaloniaDomTextNode : AvaloniaDomElement
{
    public AvaloniaDomTextNode(JintAvaloniaHost host, TextBlock control)
        : base(host, control)
    {
    }

    private TextBlock TextBlock => (TextBlock)Control;

    public override int nodeType => 3;

    public override string nodeName => "#TEXT";

    public override string? nodeValue
    {
        get => data;
        set => data = value ?? string.Empty;
    }

    public string data
    {
        get => TextBlock.Text ?? string.Empty;
        set => TextBlock.Text = value ?? string.Empty;
    }

    public override string? textContent
    {
        get => data;
        set => data = value ?? string.Empty;
    }
}

public sealed class DomHeadElement
{
    private readonly AvaloniaDomDocument _document;
    private DomDocumentElement? _parent;
    private readonly List<object> _children = new();

    internal DomHeadElement(AvaloniaDomDocument document)
    {
        _document = document;
    }

    internal void SetParent(DomDocumentElement parent)
    {
        _parent = parent;
    }

    public int nodeType => 1;

    public string nodeName => "HEAD";

    public AvaloniaDomDocument ownerDocument => _document;

    public DomDocumentElement? parentElement => _parent;

    public object[] childNodes => _children.ToArray();

    public object[] children => childNodes;

    public object? firstChild => _children.Count > 0 ? _children[0] : null;

    public object? lastChild => _children.Count > 0 ? _children[^1] : null;

    public bool hasChildNodes => _children.Count > 0;

    public object appendChild(object node)
    {
        if (node is null)
        {
            return node!;
        }

        _children.Remove(node);
        _children.Add(node);
        return node;
    }

    public object insertBefore(object node, object? referenceNode)
    {
        if (referenceNode is null)
        {
            return appendChild(node);
        }

        var index = _children.IndexOf(referenceNode);
        if (index < 0)
        {
            return appendChild(node);
        }

        _children.Remove(node);
        _children.Insert(index, node);
        return node;
    }

    public object? removeChild(object node)
    {
        if (_children.Remove(node))
        {
            return node;
        }

        return null;
    }

    public object? replaceChild(object newChild, object oldChild)
    {
        var index = _children.IndexOf(oldChild);
        if (index < 0)
        {
            return null;
        }

        _children[index] = newChild;
        return oldChild;
    }
}

public sealed class DomDocumentElement
{
    private readonly AvaloniaDomDocument _document;
    private readonly DomHeadElement _head;

    internal DomDocumentElement(AvaloniaDomDocument document, DomHeadElement head)
    {
        _document = document;
        _head = head;
        _head.SetParent(this);
    }

    public int nodeType => 1;

    public string nodeName => "HTML";

    public AvaloniaDomDocument ownerDocument => _document;

    public DomDocumentElement? parentElement => null;

    public DomHeadElement head => _head;

    public AvaloniaDomElement? body
    {
        get => _document.body as AvaloniaDomElement;
    }

    public object[] childNodes
    {
        get
        {
            var bodyElement = body;
            return bodyElement is null ? new object[] { _head } : new object[] { _head, bodyElement };
        }
    }

    public object[] children => childNodes;

    public object? firstChild => childNodes.FirstOrDefault();

    public object? lastChild => childNodes.LastOrDefault();

    public bool hasChildNodes => childNodes.Length > 0;

    public bool contains(object? node)
    {
        if (node is null)
        {
            return false;
        }

        if (ReferenceEquals(node, this) || ReferenceEquals(node, _head))
        {
            return true;
        }

        var bodyElement = body;
        return bodyElement is not null && ReferenceEquals(node, bodyElement);
    }

    public object appendChild(object node)
    {
        if (node is DomHeadElement)
        {
            return node;
        }

        if (node is AvaloniaDomElement element)
        {
            body?.appendChild(element);
            return element;
        }

        return node;
    }
}

public sealed class DomTokenList
{
    private readonly AvaloniaDomElement _element;

    public DomTokenList(AvaloniaDomElement element)
    {
        _element = element;
    }

    private StyledElement? Styled => _element.Control as StyledElement;

    public string value => Styled is { } styled ? string.Join(' ', styled.Classes) : string.Empty;

    public void add(params string[] tokens)
    {
        var styled = Styled;
        if (styled is null)
        {
            return;
        }

        foreach (var token in SplitTokens(tokens))
        {
            styled.Classes.Add(token);
        }
    }

    public void remove(params string[] tokens)
    {
        var styled = Styled;
        if (styled is null)
        {
            return;
        }

        foreach (var token in SplitTokens(tokens))
        {
            styled.Classes.Remove(token);
        }
    }

    public bool toggle(string token)
        => toggle(token, null);

    public bool toggle(string token, bool? force)
    {
        var styled = Styled;
        if (styled is null || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var contains = styled.Classes.Contains(token);
        var shouldAdd = force ?? !contains;

        if (shouldAdd)
        {
            if (!contains)
            {
                styled.Classes.Add(token);
            }

            return true;
        }

        styled.Classes.Remove(token);
        return false;
    }

    public bool contains(string token)
    {
        return Styled?.Classes.Contains(token) == true;
    }

    public void SetFromString(string? value)
    {
        var styled = Styled;
        if (styled is null)
        {
            return;
        }

        styled.Classes.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var token in SplitTokens(new[] { value }))
        {
            styled.Classes.Add(token);
        }
    }


    private static IEnumerable<string> SplitTokens(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            foreach (var part in token.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return part;
            }
        }
    }
}

public sealed class DomStringMap : DynamicObject
{
    private readonly AvaloniaDomElement _element;

    public DomStringMap(AvaloniaDomElement element)
    {
        _element = element;
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _element.SetDataAttribute(ToAttributeName(binder.Name), value?.ToString());
        return true;
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        if (_element.TryGetDataAttribute(ToAttributeName(binder.Name), out var value))
        {
            result = value;
            return true;
        }

        result = null;
        return true;
    }

    public override bool TryDeleteMember(DeleteMemberBinder binder)
    {
        return _element.RemoveDataAttribute(ToAttributeName(binder.Name));
    }

    public void set(string name, string? value) => _element.SetDataAttribute(ToAttributeName(name), value);

    public string? get(string name)
    {
        return _element.TryGetDataAttribute(ToAttributeName(name), out var value) ? value : null;
    }

    public bool delete(string name) => _element.RemoveDataAttribute(ToAttributeName(name));

    internal static string ToAttributeName(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsUpper(c))
            {
                builder.Append('-');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return "data-" + builder.ToString();
    }

    internal static string ToDatasetKey(string attributeName)
    {
        if (!attributeName.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
        {
            return attributeName;
        }

        var span = attributeName.Substring(5);
        var builder = new StringBuilder();
        var upper = false;
        foreach (var c in span)
        {
            if (c == '-')
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return builder.ToString();
    }
}

public sealed class CssStyleDeclaration : DynamicObject
{
    private readonly AvaloniaDomElement _element;

    public CssStyleDeclaration(AvaloniaDomElement element)
    {
        _element = element;
    }

    public string cssText => string.Join("; ", _element.StyleValues.Select(kv => $"{kv.Key}: {kv.Value}"));

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _element.SetStyleProperty(binder.Name, value?.ToString());
        return true;
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        var normalized = NormalizePropertyName(binder.Name);
        if (_element.StyleValues.TryGetValue(normalized, out var value))
        {
            result = value;
            return true;
        }

        result = null;
        return true;
    }

    public void setProperty(string propertyName, string? value)
    {
        _element.SetStyleProperty(propertyName, value);
    }

    public void removeProperty(string propertyName)
    {
        _element.SetStyleProperty(propertyName, null);
    }

    public string? getPropertyValue(string propertyName)
    {
        var normalized = NormalizePropertyName(propertyName);
        return _element.StyleValues.TryGetValue(normalized, out var value) ? value : null;
    }

    internal static string NormalizePropertyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        name = name.Trim();
        if (name.Contains('-'))
        {
            return name.ToLowerInvariant();
        }

        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    internal static string ToPropertyName(string cssName)
    {
        if (string.IsNullOrWhiteSpace(cssName))
        {
            return string.Empty;
        }

        var parts = cssName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            builder.Append(char.ToUpper(part[0], CultureInfo.InvariantCulture));
            if (part.Length > 1)
            {
                builder.Append(part.Substring(1));
            }
        }

        return builder.ToString();
    }

    private static string ToCamelCase(string cssName)
    {
        var property = ToPropertyName(cssName);
        if (string.IsNullOrEmpty(property))
        {
            return property;
        }

        if (property.Length == 1)
        {
            return property.ToLowerInvariant();
        }

        return char.ToLowerInvariant(property[0]) + property.Substring(1);
    }
}

public sealed class DomRect
{
    public DomRect(Rect rect)
    {
        x = rect.X;
        y = rect.Y;
        width = rect.Width;
        height = rect.Height;
    }

    public double x { get; }

    public double y { get; }

    public double width { get; }

    public double height { get; }
}
