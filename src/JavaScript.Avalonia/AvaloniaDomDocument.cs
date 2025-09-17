using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    public virtual CssComputedStyle getComputedStyle(object? element)
    {
        if (element is AvaloniaDomElement domElement)
        {
            return domElement.ComputeComputedStyle();
        }

        return CssComputedStyle.Empty;
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
    private readonly Dictionary<string, ClrEventBridge> _clrEventBridges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_builtinEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "click",
        "mousedown",
        "mousemove",
        "mouseup",
        "mouseenter",
        "mouseleave",
        "pointerdown",
        "pointermove",
        "pointerup",
        "pointerenter",
        "pointerleave",
        "keydown",
        "keyup",
        "textinput",
        "input"
    };

    private sealed record ClrEventBridge(EventInfo EventInfo, Delegate Handler, string EventName);

    private readonly Dictionary<string, string?> _dataAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _styleValues = new(StringComparer.OrdinalIgnoreCase);
    private DomStringMap? _dataset;
    private CssStyleDeclaration? _style;
    private DomTokenList? _classList;
    private WeakReference<Control>? _cachedScrollOwner;
    private WeakReference<IScrollable>? _cachedScrollable;
    private double _scrollLeft;
    private double _scrollTop;
    private bool _pointerHandlersAttached;
    private bool _pointerOverHandlersAttached;
    private bool _keyboardHandlersAttached;
    private bool _textInputHandlersAttached;
    private bool _clickHandlersAttached;
    private string? _nodeNameOverride;

    private enum LengthAxis
    {
        Horizontal,
        Vertical,
        Both
    }

    private enum BoxSide
    {
        Top,
        Right,
        Bottom,
        Left
    }

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

    public virtual object? getContext(string type) => CanvasContextBridge.GetContext(Control, type);

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

    public virtual double clientWidth => GetClientSize().Width;

    public virtual double clientHeight => GetClientSize().Height;

    public virtual double offsetWidth => Control.Bounds.Width;

    public virtual double offsetHeight => Control.Bounds.Height;

    public virtual double offsetTop => GetOffsetRelativeToParent().Y;

    public virtual double offsetLeft => GetOffsetRelativeToParent().X;

    public virtual AvaloniaDomElement? offsetParent => FindOffsetParentElement();

    public virtual double scrollWidth => GetScrollSize().Width;

    public virtual double scrollHeight => GetScrollSize().Height;

    public virtual double scrollTop
    {
        get => GetScrollOffset().Y;
        set
        {
            var current = GetScrollOffset();
            SetScrollOffset(current.X, value);
        }
    }

    public virtual double scrollLeft
    {
        get => GetScrollOffset().X;
        set
        {
            var current = GetScrollOffset();
            SetScrollOffset(value, current.Y);
        }
    }

    private Size GetClientSize()
    {
        if (Control is ScrollViewer viewer)
        {
            var viewport = viewer.Viewport;
            if (IsFiniteSize(viewport))
            {
                return viewport;
            }
        }

        if (TryGetScrollInfo(out _, out var scrollable))
        {
            var viewport = scrollable.Viewport;
            if (IsFiniteSize(viewport))
            {
                return viewport;
            }
        }

        return Control.Bounds.Size;
    }

    private Size GetScrollSize()
    {
        if (Control is ScrollViewer viewer)
        {
            var width = Math.Max(viewer.Extent.Width, viewer.Viewport.Width);
            var height = Math.Max(viewer.Extent.Height, viewer.Viewport.Height);
            return new Size(double.IsFinite(width) ? width : 0, double.IsFinite(height) ? height : 0);
        }

        if (TryGetScrollInfo(out _, out var scrollable))
        {
            var viewport = scrollable.Viewport;
            var extent = scrollable.Extent;
            var width = Math.Max(extent.Width, viewport.Width);
            var height = Math.Max(extent.Height, viewport.Height);
            return new Size(double.IsFinite(width) ? width : 0, double.IsFinite(height) ? height : 0);
        }

        return Control.Bounds.Size;
    }

    private Vector GetScrollOffset()
    {
        if (Control is ScrollViewer viewer)
        {
            var offset = viewer.Offset;
            _scrollLeft = offset.X;
            _scrollTop = offset.Y;
            return offset;
        }

        if (TryGetScrollInfo(out _, out var scrollable))
        {
            var offset = scrollable.Offset;
            if (double.IsFinite(offset.X) && double.IsFinite(offset.Y))
            {
                _scrollLeft = offset.X;
                _scrollTop = offset.Y;
                return offset;
            }
        }

        return new Vector(_scrollLeft, _scrollTop);
    }

    private void SetScrollOffset(double left, double top)
    {
        var desired = new Vector(left, top);
        _scrollLeft = left;
        _scrollTop = top;

        if (Control is ScrollViewer viewer)
        {
            var viewerViewport = viewer.Viewport;
            var viewerExtent = viewer.Extent;
            var viewerMaxX = Math.Max(0, viewerExtent.Width - viewerViewport.Width);
            var viewerMaxY = Math.Max(0, viewerExtent.Height - viewerViewport.Height);
            var viewerClampedX = Clamp(desired.X, 0, double.IsFinite(viewerMaxX) ? viewerMaxX : 0);
            var viewerClampedY = Clamp(desired.Y, 0, double.IsFinite(viewerMaxY) ? viewerMaxY : 0);

            var viewerOffset = new Vector(viewerClampedX, viewerClampedY);
            if (!AreClose(viewer.Offset.X, viewerClampedX) || !AreClose(viewer.Offset.Y, viewerClampedY))
            {
                viewer.SetValue(ScrollViewer.OffsetProperty, viewerOffset);
            }
            _scrollLeft = viewerClampedX;
            _scrollTop = viewerClampedY;
            return;
        }

        if (!TryGetScrollInfo(out var owner, out var scrollable))
        {
            return;
        }

        var viewport = scrollable.Viewport;
        var extent = scrollable.Extent;

        var maxX = Math.Max(0, extent.Width - viewport.Width);
        var maxY = Math.Max(0, extent.Height - viewport.Height);

        var clampedX = Clamp(desired.X, 0, double.IsFinite(maxX) ? maxX : 0);
        var clampedY = Clamp(desired.Y, 0, double.IsFinite(maxY) ? maxY : 0);
        _scrollLeft = clampedX;
        _scrollTop = clampedY;
        var current = scrollable.Offset;

        if (AreClose(current.X, clampedX) && AreClose(current.Y, clampedY))
        {
            return;
        }

        ApplyScrollOffset(owner, new Vector(clampedX, clampedY));
    }

    private void ApplyScrollOffset(Control owner, Vector offset)
    {
        switch (owner)
        {
            case ScrollViewer viewer:
                viewer.Offset = offset;
                return;
            default:
            {
                var property = owner.GetType().GetProperty("Offset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanWrite == true)
                {
                    property.SetValue(owner, offset);
                    return;
                }

                var registered = FindAvaloniaProperty(owner.GetType(), "Offset");
                if (registered is not null && registered.PropertyType == typeof(Vector))
                {
                    owner.SetValue(registered, offset);
                }

                break;
            }
        }
    }

    private bool TryGetScrollInfo(out Control owner, out IScrollable scrollable)
    {
        if (_cachedScrollable is not null
            && _cachedScrollable.TryGetTarget(out var cachedScrollable)
            && _cachedScrollOwner is not null
            && _cachedScrollOwner.TryGetTarget(out var cachedOwner)
            && cachedScrollable is not null
            && cachedOwner is not null)
        {
            owner = cachedOwner;
            scrollable = cachedScrollable;
            return true;
        }

        if (Control is IScrollable direct)
        {
            owner = Control;
            scrollable = direct;
            CacheScrollable(owner, scrollable);
            return true;
        }

        foreach (var visual in Control.GetVisualDescendants())
        {
            if (visual is Control candidate && visual is IScrollable candidateScrollable)
            {
                owner = candidate;
                scrollable = candidateScrollable;
                CacheScrollable(owner, scrollable);
                return true;
            }
        }

        owner = null!;
        scrollable = null!;
        return false;
    }

    private void CacheScrollable(Control owner, IScrollable scrollable)
    {
        _cachedScrollOwner = new WeakReference<Control>(owner);
        _cachedScrollable = new WeakReference<IScrollable>(scrollable);
    }

    private static bool IsFiniteSize(Size size)
        => double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width >= 0 && size.Height >= 0;

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static bool AreClose(double a, double b)
        => Math.Abs(a - b) < 0.001;

    private Point GetOffsetRelativeToParent()
    {
        var parent = FindOffsetParentControl();
        if (parent is null)
        {
            return Control.Bounds.Position;
        }

        var translated = Control.TranslatePoint(new Point(0, 0), parent);
        if (translated.HasValue)
        {
            return translated.Value;
        }

        var deltaX = Control.Bounds.Left - parent.Bounds.Left;
        var deltaY = Control.Bounds.Top - parent.Bounds.Top;
        return new Point(deltaX, deltaY);
    }

    private Control? FindOffsetParentControl()
    {
        var parent = Control.Parent as Control;
        while (parent is not null)
        {
            return parent;
        }

        return null;
    }

    private AvaloniaDomElement? FindOffsetParentElement()
    {
        var parentControl = FindOffsetParentControl();
        return parentControl is null ? null : Host.Document.WrapControl(parentControl);
    }

    public virtual void addEventListener(string type, JsValue handler)
        => addEventListener(type, handler, JsValue.Undefined);

    public virtual void addEventListener(string type, JsValue handler, JsValue optionsValue)
    {
        var trimmedType = type?.Trim() ?? string.Empty;
        var normalized = NormalizeEventName(trimmedType);
        if (string.IsNullOrEmpty(normalized) || IsNullish(handler))
        {
            return;
        }

        EnsureClrEventBridge(normalized, trimmedType);

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
            TryDetachClrEvent(normalized);
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
            TryDetachClrEvent(type);
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

    private void EnsureClrEventBridge(string normalizedEventName, string originalEventName)
    {
        if (string.IsNullOrEmpty(originalEventName) || _clrEventBridges.ContainsKey(normalizedEventName) || s_builtinEventNames.Contains(normalizedEventName))
        {
            return;
        }

        var eventInfo = FindClrEvent(originalEventName);
        if (eventInfo is null)
        {
            return;
        }

        if (!TryCreateClrEventDelegate(eventInfo, normalizedEventName, out var handler))
        {
            return;
        }

        try
        {
            eventInfo.AddEventHandler(Control, handler);
            _clrEventBridges[normalizedEventName] = new ClrEventBridge(eventInfo, handler, originalEventName);
        }
        catch
        {
            // Ignore binding failures; event will simply not be bridged.
        }
    }

    private void TryDetachClrEvent(string eventName)
    {
        var normalized = NormalizeEventName(eventName);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (!_clrEventBridges.TryGetValue(normalized, out var bridge))
        {
            return;
        }

        try
        {
            bridge.EventInfo.RemoveEventHandler(Control, bridge.Handler);
        }
        catch
        {
        }

        _clrEventBridges.Remove(normalized);
    }

    private EventInfo? FindClrEvent(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return null;
        }

        var type = Control.GetType();
        while (type is not null)
        {
            var eventInfo = type.GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (eventInfo is not null)
            {
                return eventInfo;
            }

            type = type.BaseType;
        }

        return null;
    }

    private bool TryCreateClrEventDelegate(EventInfo eventInfo, string normalizedEventName, out Delegate handler)
    {
        handler = null!;
        var handlerType = eventInfo.EventHandlerType;
        if (handlerType is null)
        {
            return false;
        }

        var invoke = handlerType.GetMethod("Invoke");
        if (invoke is null)
        {
            return false;
        }

        var parameters = invoke.GetParameters();
        var method = typeof(AvaloniaDomElement).GetMethod(nameof(OnClrEventRaised), BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is null)
        {
            return false;
        }

        if (parameters.Length == 2)
        {
            var senderParam = Expression.Parameter(parameters[0].ParameterType, "sender");
            var argsParam = Expression.Parameter(parameters[1].ParameterType, "args");
            var body = Expression.Call(Expression.Constant(this), method, Expression.Constant(normalizedEventName), Expression.Convert(senderParam, typeof(object)), Expression.Convert(argsParam, typeof(object)));
            handler = Expression.Lambda(handlerType, body, senderParam, argsParam).Compile();
            return true;
        }

        if (parameters.Length == 1)
        {
            var argsParam = Expression.Parameter(parameters[0].ParameterType, "args");
            var body = Expression.Call(Expression.Constant(this), method, Expression.Constant(normalizedEventName), Expression.Constant(null, typeof(object)), Expression.Convert(argsParam, typeof(object)));
            handler = Expression.Lambda(handlerType, body, argsParam).Compile();
            return true;
        }

        return false;
    }

    private void OnClrEventRaised(string eventKey, object? sender, object? args)
    {
        if (!HasListeners(eventKey))
        {
            return;
        }

        var eventName = eventKey;
        if (_clrEventBridges.TryGetValue(eventKey, out var bridge) && !string.IsNullOrEmpty(bridge.EventName))
        {
            eventName = bridge.EventName;
        }

        if (args is RoutedEventArgs routedArgs)
        {
            Host.Document.DispatchRoutedEvent(this, eventName, routedArgs, bubbles: true, cancelable: true);
            return;
        }

        var synthetic = new DomSyntheticEvent(eventName, bubbles: false, cancelable: false, Host.GetTimestamp(), args, accessor: null);
        Host.Document.DispatchSyntheticEvent(this, synthetic);
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
            if (HandleStyleProperty(normalized, null))
            {
                return;
            }

            if (_styleValues.Remove(normalized))
            {
                ApplyStyleToControl(normalized, null);
            }
        }
        else
        {
            if (HandleStyleProperty(normalized, value))
            {
                return;
            }

            _styleValues[normalized] = value;
            ApplyStyleToControl(normalized, value);
        }
    }

    private bool HandleStyleProperty(string normalizedName, string? value)
    {
        switch (normalizedName)
        {
            case "margin":
                return ApplyMarginShorthand(value);
            case "margin-top":
                return ApplyMarginComponent(BoxSide.Top, value);
            case "margin-right":
                return ApplyMarginComponent(BoxSide.Right, value);
            case "margin-bottom":
                return ApplyMarginComponent(BoxSide.Bottom, value);
            case "margin-left":
                return ApplyMarginComponent(BoxSide.Left, value);
            case "padding":
                return ApplyPaddingShorthand(value);
            case "padding-top":
                return ApplyPaddingComponent(BoxSide.Top, value);
            case "padding-right":
                return ApplyPaddingComponent(BoxSide.Right, value);
            case "padding-bottom":
                return ApplyPaddingComponent(BoxSide.Bottom, value);
            case "padding-left":
                return ApplyPaddingComponent(BoxSide.Left, value);
            case "border":
                return ApplyBorderShorthand(value);
            case "border-width":
                return ApplyBorderWidthShorthand(value);
            case "border-color":
                return ApplyBorderColorShorthand(value);
            case "border-style":
                return ApplyBorderStyleShorthand(value);
            case "border-top-width":
                return ApplyBorderSideWidth(BoxSide.Top, value);
            case "border-right-width":
                return ApplyBorderSideWidth(BoxSide.Right, value);
            case "border-bottom-width":
                return ApplyBorderSideWidth(BoxSide.Bottom, value);
            case "border-left-width":
                return ApplyBorderSideWidth(BoxSide.Left, value);
            case "border-top-color":
                return ApplyBorderSideColor(BoxSide.Top, value);
            case "border-right-color":
                return ApplyBorderSideColor(BoxSide.Right, value);
            case "border-bottom-color":
                return ApplyBorderSideColor(BoxSide.Bottom, value);
            case "border-left-color":
                return ApplyBorderSideColor(BoxSide.Left, value);
            case "border-top-style":
                return ApplyBorderSideStyle(BoxSide.Top, value);
            case "border-right-style":
                return ApplyBorderSideStyle(BoxSide.Right, value);
            case "border-bottom-style":
                return ApplyBorderSideStyle(BoxSide.Bottom, value);
            case "border-left-style":
                return ApplyBorderSideStyle(BoxSide.Left, value);
            default:
                return false;
        }
    }

    private bool ApplyMarginShorthand(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            ClearAvaloniaProperty("Margin");
            RemoveThicknessStyleValues("margin");
            return true;
        }

        if (!TryParseThickness(value, allowNegative: true, allowAuto: true, out var thickness))
        {
            return false;
        }

        thickness = NormalizeThickness(thickness);
        SetAvaloniaProperty("Margin", thickness);
        UpdateThicknessStyleValues("margin", thickness);
        return true;
    }

    private bool ApplyMarginComponent(BoxSide side, string? value)
    {
        var margin = GetThicknessProperty("Margin");

        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            margin = SetThicknessSide(margin, side, 0);
        }
        else
        {
            if (!TryParseLength(value, GetAxisForSide(side), allowNegative: true, allowAuto: true, out var component))
            {
                return false;
            }

            if (double.IsNaN(component))
            {
                component = 0;
            }

            margin = SetThicknessSide(margin, side, component);
        }

        SetAvaloniaProperty("Margin", margin);
        UpdateThicknessStyleValues("margin", margin);
        return true;
    }

    private bool ApplyPaddingShorthand(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            ClearAvaloniaProperty("Padding");
            RemoveThicknessStyleValues("padding");
            return true;
        }

        if (!TryParseThickness(value, allowNegative: false, allowAuto: false, out var thickness))
        {
            return false;
        }

        thickness = NormalizeThickness(thickness);
        SetAvaloniaProperty("Padding", thickness);
        UpdateThicknessStyleValues("padding", thickness);
        return true;
    }

    private bool ApplyPaddingComponent(BoxSide side, string? value)
    {
        var padding = GetThicknessProperty("Padding");

        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            padding = SetThicknessSide(padding, side, 0);
        }
        else
        {
            if (!TryParseLength(value, GetAxisForSide(side), allowNegative: false, allowAuto: false, out var component))
            {
                return false;
            }

            padding = SetThicknessSide(padding, side, component);
        }

        SetAvaloniaProperty("Padding", padding);
        UpdateThicknessStyleValues("padding", padding);
        return true;
    }

    private bool ApplyBorderShorthand(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            ClearAvaloniaProperty("BorderThickness");
            ClearAvaloniaProperty("BorderBrush");
            RemoveThicknessStyleValues("border", "width");
            RemoveBorderColorStyleValues();
            RemoveBorderStyleEntries();
            _styleValues.Remove("border");
            return true;
        }

        var tokens = SplitCssTokens(value);
        if (tokens.Count == 0)
        {
            return false;
        }

        var widthTokens = tokens.Where(IsLengthToken).ToArray();
        if (widthTokens.Length > 0)
        {
            ApplyBorderWidthShorthand(string.Join(" ", widthTokens));
        }

        var styleToken = tokens.FirstOrDefault(IsBorderStyleToken);
        if (styleToken is not null)
        {
            ApplyBorderStyleShorthand(styleToken);
        }

        var colorTokens = tokens.Except(widthTokens).Where(token => !IsBorderStyleToken(token)).ToArray();
        if (colorTokens.Length > 0)
        {
            ApplyBorderColorShorthand(string.Join(" ", colorTokens));
        }

        _styleValues["border"] = value;
        return true;
    }

    private bool ApplyBorderWidthShorthand(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            ClearAvaloniaProperty("BorderThickness");
            RemoveThicknessStyleValues("border", "width");
            _styleValues.Remove("border-width");
            return true;
        }

        if (!TryParseThickness(value, allowNegative: false, allowAuto: false, out var thickness))
        {
            return false;
        }

        thickness = NormalizeThickness(thickness);
        SetAvaloniaProperty("BorderThickness", thickness);
        UpdateThicknessStyleValues("border", thickness, "width");
        return true;
    }

    private bool ApplyBorderColorShorthand(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            ClearAvaloniaProperty("BorderBrush");
            RemoveBorderColorStyleValues();
            return true;
        }

        if (!TryParseBrush(value, out var brush))
        {
            return false;
        }

        SetAvaloniaProperty("BorderBrush", brush);
        UpdateBorderColorStyleValues(value);
        RefreshAggregateBorderColor();
        return true;
    }

    private bool ApplyBorderStyleShorthand(string? value)
    {
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            RemoveBorderStyleEntries();
            return true;
        }

        _styleValues["border-style"] = value;
        _styleValues["border-top-style"] = value;
        _styleValues["border-right-style"] = value;
        _styleValues["border-bottom-style"] = value;
        _styleValues["border-left-style"] = value;
        return true;
    }

    private bool ApplyBorderSideWidth(BoxSide side, string? value)
    {
        var thickness = GetThicknessProperty("BorderThickness");

        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            thickness = SetThicknessSide(thickness, side, 0);
        }
        else
        {
            if (!TryParseLength(value, GetAxisForSide(side), allowNegative: false, allowAuto: false, out var component))
            {
                return false;
            }

            thickness = SetThicknessSide(thickness, side, component);
        }

        SetAvaloniaProperty("BorderThickness", thickness);
        UpdateThicknessStyleValues("border", thickness, "width");
        return true;
    }

    private bool ApplyBorderSideColor(BoxSide side, string? value)
    {
        var key = $"border-{SideToString(side)}-color";
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            _styleValues.Remove(key);
            RefreshAggregateBorderColor();
            return true;
        }

        if (!TryParseBrush(value, out var brush))
        {
            return false;
        }

        SetAvaloniaProperty("BorderBrush", brush);
        _styleValues[key] = value;
        RefreshAggregateBorderColor();
        return true;
    }

    private bool ApplyBorderSideStyle(BoxSide side, string? value)
    {
        var key = $"border-{SideToString(side)}-style";
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            _styleValues.Remove(key);
        }
        else
        {
            _styleValues[key] = value;
        }

        RefreshAggregateBorderStyle();
        return true;
    }

    private static string SideToString(BoxSide side)
        => side switch
        {
            BoxSide.Top => "top",
            BoxSide.Right => "right",
            BoxSide.Bottom => "bottom",
            BoxSide.Left => "left",
            _ => "top"
        };

    private void UpdateThicknessStyleValues(string baseName, Thickness thickness, string? suffix = null)
    {
        var aggregateKey = suffix is null ? baseName : $"{baseName}-{suffix}";
        _styleValues[aggregateKey] = FormatThicknessString(thickness);

        SetSide(BoxSide.Top, thickness.Top);
        SetSide(BoxSide.Right, thickness.Right);
        SetSide(BoxSide.Bottom, thickness.Bottom);
        SetSide(BoxSide.Left, thickness.Left);

        void SetSide(BoxSide side, double value)
        {
            var key = $"{baseName}-{SideToString(side)}";
            if (suffix is not null)
            {
                key = $"{key}-{suffix}";
            }

            _styleValues[key] = FormatCssLength(value);
        }
    }

    private void RemoveThicknessStyleValues(string baseName, string? suffix = null)
    {
        var aggregateKey = suffix is null ? baseName : $"{baseName}-{suffix}";
        _styleValues.Remove(aggregateKey);
        foreach (BoxSide side in Enum.GetValues(typeof(BoxSide)))
        {
            var key = $"{baseName}-{SideToString(side)}";
            if (suffix is not null)
            {
                key = $"{key}-{suffix}";
            }

            _styleValues.Remove(key);
        }
    }

    private void UpdateBorderColorStyleValues(string value)
    {
        _styleValues["border-color"] = value;
        _styleValues["border-top-color"] = value;
        _styleValues["border-right-color"] = value;
        _styleValues["border-bottom-color"] = value;
        _styleValues["border-left-color"] = value;
    }

    private void RemoveBorderColorStyleValues()
    {
        _styleValues.Remove("border-color");
        _styleValues.Remove("border-top-color");
        _styleValues.Remove("border-right-color");
        _styleValues.Remove("border-bottom-color");
        _styleValues.Remove("border-left-color");
    }

    private void RemoveBorderStyleEntries()
    {
        _styleValues.Remove("border-style");
        _styleValues.Remove("border-top-style");
        _styleValues.Remove("border-right-style");
        _styleValues.Remove("border-bottom-style");
        _styleValues.Remove("border-left-style");
    }

    private Thickness GetThicknessProperty(string propertyName)
    {
        var property = FindAvaloniaProperty(Control.GetType(), propertyName);
        if (property is null)
        {
            return default;
        }

        var raw = Control.GetValue(property);
        return raw is Thickness thickness ? thickness : default;
    }

    private static Thickness SetThicknessSide(Thickness thickness, BoxSide side, double value)
        => side switch
        {
            BoxSide.Top => new Thickness(thickness.Left, value, thickness.Right, thickness.Bottom),
            BoxSide.Right => new Thickness(thickness.Left, thickness.Top, value, thickness.Bottom),
            BoxSide.Bottom => new Thickness(thickness.Left, thickness.Top, thickness.Right, value),
            BoxSide.Left => new Thickness(value, thickness.Top, thickness.Right, thickness.Bottom),
            _ => thickness
        };

    private static LengthAxis GetAxisForSide(BoxSide side)
        => side is BoxSide.Top or BoxSide.Bottom ? LengthAxis.Vertical : LengthAxis.Horizontal;

    private static Thickness NormalizeThickness(Thickness thickness)
        => new(
            NormalizeThicknessComponent(thickness.Left),
            NormalizeThicknessComponent(thickness.Top),
            NormalizeThicknessComponent(thickness.Right),
            NormalizeThicknessComponent(thickness.Bottom));

    private static double NormalizeThicknessComponent(double value)
        => double.IsNaN(value) ? 0 : value;

    private string FormatThicknessString(Thickness thickness)
        => string.Join(" ", new[]
        {
            FormatCssLength(thickness.Top),
            FormatCssLength(thickness.Right),
            FormatCssLength(thickness.Bottom),
            FormatCssLength(thickness.Left)
        });

    private static string FormatCssLength(double value)
    {
        if (double.IsNaN(value))
        {
            return "auto";
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture) + "px";
    }

    private void SetAvaloniaProperty<T>(string propertyName, T value)
    {
        var property = FindAvaloniaProperty(Control.GetType(), propertyName);
        if (property is null)
        {
            return;
        }

        if (property is AvaloniaProperty<T> typed)
        {
            Control.SetValue(typed, value);
            return;
        }

        Control.SetValue(property, value);
    }

    private void ClearAvaloniaProperty(string propertyName)
    {
        var property = FindAvaloniaProperty(Control.GetType(), propertyName);
        if (property is not null)
        {
            Control.ClearValue(property);
        }
    }

    private bool TryParseBrush(string value, out IBrush? brush)
    {
        brush = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            brush = Brush.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshAggregateBorderColor()
    {
        var top = _styleValues.TryGetValue("border-top-color", out var t) ? t : null;
        var right = _styleValues.TryGetValue("border-right-color", out var r) ? r : null;
        var bottom = _styleValues.TryGetValue("border-bottom-color", out var b) ? b : null;
        var left = _styleValues.TryGetValue("border-left-color", out var l) ? l : null;

        if (top is null && right is null && bottom is null && left is null)
        {
            _styleValues.Remove("border-color");
            return;
        }

        if (top is not null && top == right && top == bottom && top == left)
        {
            _styleValues["border-color"] = top;
            return;
        }

        var parts = new[] { top, right, bottom, left }
            .Select(part => part ?? "currentcolor");
        _styleValues["border-color"] = string.Join(" ", parts);
    }

    private void RefreshAggregateBorderStyle()
    {
        var top = _styleValues.TryGetValue("border-top-style", out var t) ? t : null;
        var right = _styleValues.TryGetValue("border-right-style", out var r) ? r : null;
        var bottom = _styleValues.TryGetValue("border-bottom-style", out var b) ? b : null;
        var left = _styleValues.TryGetValue("border-left-style", out var l) ? l : null;

        if (top is null && right is null && bottom is null && left is null)
        {
            _styleValues.Remove("border-style");
            return;
        }

        if (top is not null && top == right && top == bottom && top == left)
        {
            _styleValues["border-style"] = top;
            return;
        }

        var parts = new[] { top, right, bottom, left }
            .Select(part => part ?? "none");
        _styleValues["border-style"] = string.Join(" ", parts);
    }

    private bool TryParseThickness(string value, bool allowNegative, bool allowAuto, out Thickness thickness)
    {
        thickness = default;
        var tokens = SplitCssTokens(value);
        if (tokens.Count == 0)
        {
            return false;
        }

        double top;
        double right;
        double bottom;
        double left;

        if (tokens.Count == 1)
        {
            if (!TryParseLength(tokens[0], LengthAxis.Both, allowNegative, allowAuto, out var uniform))
            {
                return false;
            }

            top = right = bottom = left = uniform;
        }
        else if (tokens.Count == 2)
        {
            if (!TryParseLength(tokens[0], LengthAxis.Vertical, allowNegative, allowAuto, out var vertical)
                || !TryParseLength(tokens[1], LengthAxis.Horizontal, allowNegative, allowAuto, out var horizontal))
            {
                return false;
            }

            top = bottom = vertical;
            left = right = horizontal;
        }
        else if (tokens.Count == 3)
        {
            if (!TryParseLength(tokens[0], LengthAxis.Vertical, allowNegative, allowAuto, out top)
                || !TryParseLength(tokens[1], LengthAxis.Horizontal, allowNegative, allowAuto, out var horizontal)
                || !TryParseLength(tokens[2], LengthAxis.Vertical, allowNegative, allowAuto, out bottom))
            {
                return false;
            }

            left = right = horizontal;
        }
        else
        {
            if (!TryParseLength(tokens[0], LengthAxis.Vertical, allowNegative, allowAuto, out top)
                || !TryParseLength(tokens[1], LengthAxis.Horizontal, allowNegative, allowAuto, out right)
                || !TryParseLength(tokens[2], LengthAxis.Vertical, allowNegative, allowAuto, out bottom)
                || !TryParseLength(tokens[3], LengthAxis.Horizontal, allowNegative, allowAuto, out left))
            {
                return false;
            }
        }

        top = NormalizeThicknessComponent(top);
        right = NormalizeThicknessComponent(right);
        bottom = NormalizeThicknessComponent(bottom);
        left = NormalizeThicknessComponent(left);

        thickness = new Thickness(left, top, right, bottom);
        return true;
    }

    private bool TryParseLength(string value, LengthAxis axis, bool allowNegative, bool allowAuto, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (allowAuto && string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            result = double.NaN;
            return true;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "thin":
                result = 1;
                return true;
            case "medium":
                result = 3;
                return true;
            case "thick":
                result = 5;
                return true;
        }

        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }
        else if (trimmed.EndsWith("%", StringComparison.OrdinalIgnoreCase))
        {
            var numberPart = trimmed[..^1];
            if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return false;
            }

            var reference = GetReferenceSize(axis);
            result = reference * (percent / 100.0);
            return true;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return false;
        }

        if (!allowNegative && numeric < 0)
        {
            numeric = 0;
        }

        result = numeric;
        return true;
    }

    private double GetReferenceSize(LengthAxis axis)
    {
        var parent = Control.Parent as Control;
        var size = parent?.Bounds.Size ?? default;
        if (size.Width <= 0 && size.Height <= 0)
        {
            var topLevelSize = Host.TopLevel?.ClientSize ?? default;
            if (topLevelSize.Width > 0 || topLevelSize.Height > 0)
            {
                size = topLevelSize;
            }
            else
            {
                size = Control.Bounds.Size;
            }
        }

        return axis switch
        {
            LengthAxis.Vertical => size.Height,
            LengthAxis.Horizontal => size.Width,
            _ => Math.Max(size.Width, size.Height)
        };
    }

    private static bool IsLengthToken(string token)
    {
        token = token.Trim();
        return token.EndsWith("px", StringComparison.OrdinalIgnoreCase)
           || token.EndsWith("%", StringComparison.OrdinalIgnoreCase)
           || token.Equals("thin", StringComparison.OrdinalIgnoreCase)
           || token.Equals("medium", StringComparison.OrdinalIgnoreCase)
           || token.Equals("thick", StringComparison.OrdinalIgnoreCase)
           || double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsBorderStyleToken(string token)
        => token is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset";

    private static List<string> SplitCssTokens(string value)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        var depth = 0;
        foreach (var ch in value)
        {
            if ((char.IsWhiteSpace(ch) || ch == ',') && depth == 0)
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString().Trim());
                    builder.Clear();
                }

                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
            }

            builder.Append(ch);
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString().Trim());
        }

        return tokens;
    }

    private static bool AllowsNegativeLengths(string propertyName)
        => propertyName.Contains("Margin", StringComparison.OrdinalIgnoreCase);

    private static bool AllowsAutoLength(string propertyName)
        => propertyName.Contains("Width", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("Height", StringComparison.OrdinalIgnoreCase)
           || propertyName.Contains("Margin", StringComparison.OrdinalIgnoreCase);

    private static LengthAxis DetermineAxis(string propertyName)
        => propertyName.Contains("Height", StringComparison.OrdinalIgnoreCase) ? LengthAxis.Vertical : LengthAxis.Horizontal;

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
            var converted = ConvertToPropertyValue(propertyName, prop.PropertyType, value);
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

        var converted = ConvertToPropertyValue(propertyName, registered.PropertyType, value);

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

    private object? ConvertToPropertyValue(string propertyName, Type propertyType, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (propertyType == typeof(string))
        {
            return value;
        }

        if (propertyType == typeof(double) || propertyType == typeof(double?))
        {
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                && propertyName.Contains("Max", StringComparison.OrdinalIgnoreCase))
            {
                return double.PositiveInfinity;
            }

            var axis = DetermineAxis(propertyName);
            var allowNegative = AllowsNegativeLengths(propertyName);
            var allowAuto = AllowsAutoLength(propertyName);
            if (TryParseLength(value, axis, allowNegative, allowAuto, out var length))
            {
                return length;
            }
        }

        if (propertyType == typeof(Thickness) || propertyType == typeof(Thickness?))
        {
            var allowNegative = string.Equals(propertyName, "Margin", StringComparison.OrdinalIgnoreCase);
            var allowAuto = allowNegative;
            if (TryParseThickness(value, allowNegative, allowAuto, out var thickness))
            {
                return thickness;
            }
        }

        if (TryConvertFromString(propertyType, value, out var convertedFromString))
        {
            return convertedFromString;
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

    internal CssComputedStyle ComputeComputedStyle()
        => ComputedStyleBuilder.Build(this);

    private sealed class ComputedStyleBuilder
    {
        private readonly AvaloniaDomElement _element;
        private readonly Control _control;
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        private ComputedStyleBuilder(AvaloniaDomElement element)
        {
            _element = element;
            _control = element.Control;
        }

        public static CssComputedStyle Build(AvaloniaDomElement element)
        {
            var builder = new ComputedStyleBuilder(element);
            builder.Populate();
            return new CssComputedStyle(builder._values);
        }

        private void Populate()
        {
            AddValue("box-sizing", "border-box");
            AddValue("position", "static");

            AddDimension("width", _control.Bounds.Width);
            AddDimension("height", _control.Bounds.Height);
            AddMinDimension("min-width", _control.MinWidth);
            AddMinDimension("min-height", _control.MinHeight);
            AddMaxDimension("max-width", _control.MaxWidth);
            AddMaxDimension("max-height", _control.MaxHeight);

            AddThicknessSet("margin", _control.Margin);

            var padding = ResolveThickness("Padding", default);
            AddThicknessSet("padding", padding);

            var borderThickness = ResolveThickness("BorderThickness", (_control as Border)?.BorderThickness ?? default);
            AddBorderThickness(borderThickness);
            AddBorderStyles(borderThickness);

            var borderBrush = ResolveBrush("BorderBrush", (_control as Border)?.BorderBrush);
            AddBorderColors(borderBrush);

            var cornerRadius = ResolveCornerRadius("CornerRadius", (_control as Border)?.CornerRadius ?? default);
            AddCornerRadius(cornerRadius);

            var background = ResolveBrush("Background", null);
            AddBackground(background);

            var foreground = ResolveBrush("Foreground", null);
            AddForeground(foreground);

            AddFontProperties();
            AddOpacity();
            AddVisibility();
            AddDisplay();
            AddOverflow();

            AddValue("line-height", "normal");

            foreach (var pair in _element.StyleValues)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                var normalized = CssStyleDeclaration.NormalizePropertyName(pair.Key);
                if (!_values.ContainsKey(normalized))
                {
                    _values[normalized] = pair.Value!;
                }
            }
        }

        private void AddValue(string propertyName, string value)
            => _values[propertyName] = value;

        private void AddDimension(string propertyName, double value)
            => _values[propertyName] = FormatDimension(value);

        private void AddMinDimension(string propertyName, double value)
            => _values[propertyName] = FormatMinDimension(value);

        private void AddMaxDimension(string propertyName, double value)
            => _values[propertyName] = FormatMaxDimension(value);

        private void AddThicknessSet(string prefix, Thickness thickness)
        {
            var top = FormatFixedLength(thickness.Top);
            var right = FormatFixedLength(thickness.Right);
            var bottom = FormatFixedLength(thickness.Bottom);
            var left = FormatFixedLength(thickness.Left);

            _values[$"{prefix}-top"] = top;
            _values[$"{prefix}-right"] = right;
            _values[$"{prefix}-bottom"] = bottom;
            _values[$"{prefix}-left"] = left;
            _values[prefix] = string.Join(" ", new[] { top, right, bottom, left });
        }

        private void AddBorderThickness(Thickness thickness)
        {
            var top = FormatFixedLength(thickness.Top);
            var right = FormatFixedLength(thickness.Right);
            var bottom = FormatFixedLength(thickness.Bottom);
            var left = FormatFixedLength(thickness.Left);

            _values["border-top-width"] = top;
            _values["border-right-width"] = right;
            _values["border-bottom-width"] = bottom;
            _values["border-left-width"] = left;
            _values["border-width"] = string.Join(" ", new[] { top, right, bottom, left });
        }

        private void AddBorderStyles(Thickness thickness)
        {
            var top = thickness.Top > 0 ? "solid" : "none";
            var right = thickness.Right > 0 ? "solid" : "none";
            var bottom = thickness.Bottom > 0 ? "solid" : "none";
            var left = thickness.Left > 0 ? "solid" : "none";

            _values["border-top-style"] = top;
            _values["border-right-style"] = right;
            _values["border-bottom-style"] = bottom;
            _values["border-left-style"] = left;
            _values["border-style"] = string.Join(" ", new[] { top, right, bottom, left });
        }

        private void AddBorderColors(IBrush? brush)
        {
            var color = FormatColor(brush);
            _values["border-top-color"] = color;
            _values["border-right-color"] = color;
            _values["border-bottom-color"] = color;
            _values["border-left-color"] = color;
            _values["border-color"] = string.Join(" ", new[] { color, color, color, color });
        }

        private void AddCornerRadius(CornerRadius cornerRadius)
        {
            var topLeft = FormatFixedLength(cornerRadius.TopLeft);
            var topRight = FormatFixedLength(cornerRadius.TopRight);
            var bottomRight = FormatFixedLength(cornerRadius.BottomRight);
            var bottomLeft = FormatFixedLength(cornerRadius.BottomLeft);

            _values["border-top-left-radius"] = topLeft;
            _values["border-top-right-radius"] = topRight;
            _values["border-bottom-right-radius"] = bottomRight;
            _values["border-bottom-left-radius"] = bottomLeft;
            _values["border-radius"] = string.Join(" ", new[] { topLeft, topRight, bottomRight, bottomLeft });
        }

        private void AddBackground(IBrush? brush)
        {
            _values["background-color"] = FormatColor(brush);
            _values["background-image"] = "none";
        }

        private void AddForeground(IBrush? brush)
        {
            var color = brush is null ? "rgba(0, 0, 0, 1)" : FormatColor(brush);
            _values["color"] = color;
        }

        private void AddFontProperties()
        {
            TryGetPropertyValue<FontFamily>("FontFamily", out var fontFamily);
            var family = fontFamily?.ToString() ?? "Segoe UI";

            if (!TryGetPropertyValue<double>("FontSize", out var fontSize) || fontSize <= 0)
            {
                fontSize = 12d;
            }

            if (!TryGetPropertyValue<FontWeight>("FontWeight", out var fontWeight))
            {
                fontWeight = FontWeight.Normal;
            }

            if (!TryGetPropertyValue<FontStyle>("FontStyle", out var fontStyle))
            {
                fontStyle = FontStyle.Normal;
            }

            AddValue("font-family", family);
            AddValue("font-size", FormatFixedLength(fontSize));
            AddValue("font-weight", FormatFontWeight(fontWeight));
            AddValue("font-style", fontStyle.ToString().ToLowerInvariant());
        }

        private void AddOpacity()
            => AddValue("opacity", FormatNumber(_control.Opacity));

        private void AddVisibility()
        {
            var visible = _control.IsVisible ? "visible" : "hidden";
            AddValue("visibility", visible);
        }

        private void AddDisplay()
        {
            var display = _control switch
            {
                TextBlock => "inline",
                _ => "block"
            };

            AddValue("display", display);
        }

        private void AddOverflow()
        {
            string overflowX;
            string overflowY;

            if (_control is ScrollViewer viewer)
            {
                overflowX = MapOverflow(viewer.HorizontalScrollBarVisibility);
                overflowY = MapOverflow(viewer.VerticalScrollBarVisibility);
            }
            else
            {
                overflowX = "visible";
                overflowY = "visible";
            }

            AddValue("overflow-x", overflowX);
            AddValue("overflow-y", overflowY);
            AddValue("overflow", overflowX == overflowY ? overflowX : $"{overflowX} {overflowY}");
        }

        private Thickness ResolveThickness(string propertyName, Thickness fallback)
        {
            if (TryGetPropertyValue(propertyName, out Thickness thickness))
            {
                return thickness;
            }

            return fallback;
        }

        private CornerRadius ResolveCornerRadius(string propertyName, CornerRadius fallback)
        {
            if (TryGetPropertyValue(propertyName, out CornerRadius radius))
            {
                return radius;
            }

            return fallback;
        }

        private IBrush? ResolveBrush(string propertyName, IBrush? fallback)
        {
            if (TryGetPropertyValue(propertyName, out IBrush brush) && brush is not null)
            {
                return brush;
            }

            return fallback;
        }

        private bool TryGetPropertyValue<T>(string propertyName, out T value)
        {
            var property = FindAvaloniaProperty(_control.GetType(), propertyName);
            if (property is null)
            {
                value = default!;
                return false;
            }

            var raw = _control.GetValue(property);
            if (raw is T typed)
            {
                value = typed;
                return true;
            }

            if (raw is null)
            {
                value = default!;
                return false;
            }

            try
            {
                value = (T)raw;
                return true;
            }
            catch
            {
                value = default!;
                return false;
            }
        }

        private static string FormatDimension(double value)
        {
            if (!double.IsFinite(value))
            {
                return "auto";
            }

            return FormatFixedLength(value);
        }

        private static string FormatMinDimension(double value)
        {
            if (!double.IsFinite(value))
            {
                return "0px";
            }

            return FormatFixedLength(value);
        }

        private static string FormatMaxDimension(double value)
        {
            if (!double.IsFinite(value) || value == double.PositiveInfinity)
            {
                return "none";
            }

            return FormatFixedLength(value);
        }

        private static string FormatFixedLength(double value)
        {
            var normalized = Normalize(value);
            return $"{normalized.ToString("0.##", CultureInfo.InvariantCulture)}px";
        }

        private static double Normalize(double value)
        {
            if (!double.IsFinite(value))
            {
                return 0;
            }

            return Math.Abs(value) < 0.0005 ? 0 : value;
        }

        private static string FormatFontWeight(FontWeight fontWeight)
        {
            var text = fontWeight.ToString();
            return string.IsNullOrEmpty(text) ? "normal" : text.ToLowerInvariant();
        }

        private static string FormatNumber(double value)
        {
            if (!double.IsFinite(value))
            {
                return "1";
            }

            var normalized = Normalize(value);
            return normalized.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatColor(IBrush? brush)
        {
            if (brush is ISolidColorBrush solid)
            {
                var color = solid.Color;
                var alpha = Math.Round(color.A / 255d, 3);
                return $"rgba({color.R}, {color.G}, {color.B}, {alpha.ToString(CultureInfo.InvariantCulture)})";
            }

            return brush?.ToString() ?? "rgba(0, 0, 0, 0)";
        }

        private static string MapOverflow(ScrollBarVisibility visibility)
            => visibility switch
            {
                ScrollBarVisibility.Auto => "auto",
                ScrollBarVisibility.Hidden => "hidden",
                ScrollBarVisibility.Disabled => "hidden",
                ScrollBarVisibility.Visible => "scroll",
                _ => "auto"
            };
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

    internal static string ToCamelCase(string cssName)
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

public sealed class CssComputedStyle : DynamicObject
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, string> _camelCaseValues;

    internal static CssComputedStyle Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    internal CssComputedStyle(Dictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        _camelCaseValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _values)
        {
            var camel = CssStyleDeclaration.ToCamelCase(pair.Key);
            _camelCaseValues[camel] = pair.Value;
        }
    }

    public int length => _values.Count;

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        if (_camelCaseValues.TryGetValue(binder.Name, out var camelValue))
        {
            result = camelValue;
            return true;
        }

        var normalized = CssStyleDeclaration.NormalizePropertyName(binder.Name);
        if (_values.TryGetValue(normalized, out var value))
        {
            result = value;
            return true;
        }

        result = string.Empty;
        return true;
    }

    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        if (indexes.Length == 1 && indexes[0] is string name)
        {
            result = getPropertyValue(name);
            return true;
        }

        result = null;
        return false;
    }

    public string? getPropertyValue(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return string.Empty;
        }

        var normalized = CssStyleDeclaration.NormalizePropertyName(propertyName);
        return _values.TryGetValue(normalized, out var value) ? value : string.Empty;
    }

    public string item(int index)
    {
        if (index < 0 || index >= _values.Count)
        {
            return string.Empty;
        }

        return _values.ElementAt(index).Key;
    }

    public override IEnumerable<string> GetDynamicMemberNames()
        => _camelCaseValues.Keys;
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
