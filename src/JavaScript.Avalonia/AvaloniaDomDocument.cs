using System;
using System.Collections.Generic;
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
using Avalonia.Styling;
using Avalonia.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace JavaScript.Avalonia;

public class AvaloniaDomDocument
{
    private readonly Func<string, Control?>? _elementFactory;
    private readonly ConditionalWeakTable<Control, AvaloniaDomElement> _elementWrappers = new();
    private readonly Dictionary<string, List<DocumentEventSubscription>> _documentEventHandlers = new(StringComparer.OrdinalIgnoreCase);
    private bool _readyStateScheduled;
    private string _readyState = "loading";

    protected JintAvaloniaHost Host { get; }

    public AvaloniaDomDocument(JintAvaloniaHost host, Func<string, Control?>? elementFactory = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _elementFactory = elementFactory;
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
            return root is null ? null : WrapControl(root);
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
        var subscription = new DocumentEventSubscription(handler, listenerOptions);
        if (!_documentEventHandlers.TryGetValue(normalized, out var list))
        {
            list = new List<DocumentEventSubscription>();
            _documentEventHandlers[normalized] = list;
        }

        list.Add(subscription);
    }

    public virtual void removeEventListener(string type, JsValue handler)
    {
        var normalized = DocumentNormalizeEventName(type);
        if (string.IsNullOrEmpty(normalized) || !_documentEventHandlers.TryGetValue(normalized, out var list))
        {
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Callback.Equals(handler))
            {
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
        {
            _documentEventHandlers.Remove(normalized);
        }
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
            DispatchDocumentEvent("readystatechange");
            SetReadyState("complete");
            DispatchDocumentEvent("readystatechange");
            DispatchDocumentEvent("DOMContentLoaded");
        }, DispatcherPriority.Background);
    }

    private void SetReadyState(string state)
    {
        _readyState = state;
    }

    private void DispatchDocumentEvent(string type)
    {
        if (!_documentEventHandlers.TryGetValue(type, out var listeners) || listeners.Count == 0)
        {
            return;
        }

        var snapshot = listeners.ToArray();
        foreach (var listener in snapshot)
        {
            listener.Invoke(Host.Engine);
            if (listener.Options.Once)
            {
                listeners.Remove(listener);
            }
        }

        if (listeners.Count == 0)
        {
            _documentEventHandlers.Remove(type);
        }
    }
}

internal sealed class DocumentEventSubscription
{
    public DocumentEventSubscription(JsValue callback, EventListenerOptions options)
    {
        Callback = callback;
        Options = options;
    }

    public JsValue Callback { get; }

    public EventListenerOptions Options { get; }

    public void Invoke(Engine engine)
    {
        try
        {
            engine.Invoke(Callback, Array.Empty<object>());
        }
        catch
        {
        }
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

        return TypeConverter.ToBoolean(value);
    }
}


public class AvaloniaDomElement
{
    private readonly Dictionary<string, List<EventSubscription>> _eventHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _dataAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _styleValues = new(StringComparer.OrdinalIgnoreCase);
    private DomStringMap? _dataset;
    private CssStyleDeclaration? _style;
    private DomTokenList? _classList;
    private double _scrollLeft;
    private double _scrollTop;

    protected JintAvaloniaHost Host { get; }

    public Control Control { get; }

    public AvaloniaDomElement(JintAvaloniaHost host, Control control)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public DomTokenList classList => _classList ??= new DomTokenList(this);

    public CssStyleDeclaration style => _style ??= new CssStyleDeclaration(this);

    public DomStringMap dataset => _dataset ??= new DomStringMap(this);

    internal IReadOnlyDictionary<string, string?> DataAttributes => _dataAttributes;

    internal IReadOnlyDictionary<string, string?> StyleValues => _styleValues;

    public AvaloniaDomElement? parentElement => Control.Parent is Control parent ? Host.Document.WrapControl(parent) : null;

    public AvaloniaDomElement? firstElementChild => GetChildElements().FirstOrDefault();

    public AvaloniaDomElement? lastElementChild => GetChildElements().LastOrDefault();

    public AvaloniaDomElement? previousElementSibling => GetSibling(-1);

    public AvaloniaDomElement? nextElementSibling => GetSibling(1);

    public object[] children => GetChildElements().Cast<object>().ToArray();

    public int childElementCount => GetChildElements().Count();

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
        var subscription = EventSubscription.Create(this, normalized, handler, options);
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
                list.RemoveAt(i);
                subscription.Dispose();
                break;
            }
        }

        if (list.Count == 0)
        {
            _eventHandlers.Remove(normalized);
        }
    }

    internal void RemoveSubscription(EventSubscription subscription)
    {
        if (_eventHandlers.TryGetValue(subscription.EventName, out var list) && list.Remove(subscription))
        {
            subscription.Dispose();
            if (list.Count == 0)
            {
                _eventHandlers.Remove(subscription.EventName);
            }
        }
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

    private static object? ConvertToPropertyValue(Type propertyType, string? value)
    {
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

    private PointerEventInfo CreatePointerEventInfo(PointerEventArgs args) => new(args, Control);

    private KeyEventInfo CreateKeyEventInfo(KeyEventArgs args) => new(args);

    private TextInputEventInfo CreateTextInputEventInfo(TextInputEventArgs args) => new(args);

    private static bool IsNullish(JsValue value) => value.IsNull() || value.IsUndefined();

    private static string NormalizeEventName(string? type)
        => string.IsNullOrWhiteSpace(type) ? string.Empty : type.Trim().ToLowerInvariant();

    private void HandlePointerEvent(EventSubscription subscription, PointerEventArgs args)
    {
        var data = CreatePointerEventInfo(args);
        subscription.Invoke(data);
    }

    private void HandleKeyEvent(EventSubscription subscription, KeyEventArgs args)
    {
        var data = CreateKeyEventInfo(args);
        subscription.Invoke(data);
    }

    private void HandleTextInputEvent(EventSubscription subscription, TextInputEventArgs args)
    {
        var data = CreateTextInputEventInfo(args);
        subscription.Invoke(data);
    }

    internal static string? GetPointerButton(PointerEventArgs args, Control control)
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

    internal sealed class EventSubscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        private EventSubscription(AvaloniaDomElement element, string eventName, JsValue callback, EventListenerOptions options, Action unsubscribe)
        {
            Element = element;
            EventName = eventName;
            Callback = callback;
            Options = options;
            _unsubscribe = unsubscribe;
        }

        public AvaloniaDomElement Element { get; }

        public string EventName { get; }

        public JsValue Callback { get; }

        public EventListenerOptions Options { get; }

        public static EventSubscription? Create(AvaloniaDomElement element, string eventName, JsValue callback, EventListenerOptions options)
        {
            var control = element.Control;
            EventSubscription? subscription = null;

            switch (eventName)
            {
                case "pointerdown":
                case "mousedown":
                    EventHandler<PointerPressedEventArgs>? down = null;
                    down = (s, e) => subscription?.Invoke(element.CreatePointerEventInfo(e));
                    control.PointerPressed += down;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.PointerPressed -= down);
                    return subscription;
                case "pointermove":
                case "mousemove":
                    EventHandler<PointerEventArgs>? move = null;
                    move = (s, e) => subscription?.Invoke(element.CreatePointerEventInfo(e));
                    control.PointerMoved += move;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.PointerMoved -= move);
                    return subscription;
                case "pointerup":
                case "mouseup":
                    EventHandler<PointerReleasedEventArgs>? up = null;
                    up = (s, e) => subscription?.Invoke(element.CreatePointerEventInfo(e));
                    control.PointerReleased += up;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.PointerReleased -= up);
                    return subscription;
                case "pointerenter":
                case "mouseenter":
                    EventHandler<PointerEventArgs>? enter = null;
                    enter = (s, e) => subscription?.Invoke(element.CreatePointerEventInfo(e));
                    control.PointerEntered += enter;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.PointerEntered -= enter);
                    return subscription;
                case "pointerleave":
                case "mouseleave":
                    EventHandler<PointerEventArgs>? leave = null;
                    leave = (s, e) => subscription?.Invoke(element.CreatePointerEventInfo(e));
                    control.PointerExited += leave;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.PointerExited -= leave);
                    return subscription;
                case "click":
                    if (control is Button button)
                    {
                        EventHandler<RoutedEventArgs>? handler = null;
                        handler = (s, e) => subscription?.Invoke(Array.Empty<object?>());
                        button.Click += handler;
                        subscription = new EventSubscription(element, eventName, callback, options, () => button.Click -= handler);
                        return subscription;
                    }
                    else
                    {
                        EventHandler<PointerReleasedEventArgs>? click = null;
                        click = (s, e) => subscription?.Invoke(element.CreatePointerEventInfo(e));
                        control.PointerReleased += click;
                        subscription = new EventSubscription(element, eventName, callback, options, () => control.PointerReleased -= click);
                        return subscription;
                    }
                case "keydown":
                    EventHandler<KeyEventArgs>? keyDown = null;
                    keyDown = (s, e) => subscription?.Invoke(element.CreateKeyEventInfo(e));
                    control.KeyDown += keyDown;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.KeyDown -= keyDown);
                    return subscription;
                case "keyup":
                    EventHandler<KeyEventArgs>? keyUp = null;
                    keyUp = (s, e) => subscription?.Invoke(element.CreateKeyEventInfo(e));
                    control.KeyUp += keyUp;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.KeyUp -= keyUp);
                    return subscription;
                case "textinput":
                case "input":
                    EventHandler<TextInputEventArgs>? textInput = null;
                    textInput = (s, e) => subscription?.Invoke(element.CreateTextInputEventInfo(e));
                    control.TextInput += textInput;
                    subscription = new EventSubscription(element, eventName, callback, options, () => control.TextInput -= textInput);
                    return subscription;
                default:
                    return null;
            }
        }

        public void Invoke(params object?[] arguments)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Element.Host.Engine.Invoke(Callback, arguments);
            }
            catch
            {
            }

            if (Options.Once)
            {
                Element.RemoveSubscription(this);
            }
        }

        public void Invoke(object argument)
        {
            Invoke(new[] { argument });
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
}

public sealed class AvaloniaDomTextNode : AvaloniaDomElement
{
    public AvaloniaDomTextNode(JintAvaloniaHost host, TextBlock control)
        : base(host, control)
    {
    }

    private TextBlock TextBlock => (TextBlock)Control;

    public string data
    {
        get => TextBlock.Text ?? string.Empty;
        set => TextBlock.Text = value ?? string.Empty;
    }

    public string nodeValue
    {
        get => data;
        set => data = value ?? string.Empty;
    }

    public override string? textContent
    {
        get => data;
        set => data = value ?? string.Empty;
    }
}

public abstract class DomEventBase
{
    protected DomEventBase(RoutedEventArgs args)
    {
        RoutedEventArgs = args;
    }

    protected RoutedEventArgs RoutedEventArgs { get; }

    public bool handled
    {
        get => RoutedEventArgs.Handled;
        set => RoutedEventArgs.Handled = value;
    }

    public void stopPropagation() => RoutedEventArgs.Handled = true;

    public void preventDefault() => RoutedEventArgs.Handled = true;
}

public sealed class PointerEventInfo : DomEventBase
{
    private readonly PointerEventArgs _args;
    private readonly Control _relativeTo;

    public PointerEventInfo(PointerEventArgs args, Control relativeTo)
        : base(args)
    {
        _args = args;
        _relativeTo = relativeTo;
    }

    public double x => _args.GetPosition(_relativeTo).X;

    public double y => _args.GetPosition(_relativeTo).Y;

    public string? button => AvaloniaDomElement.GetPointerButton(_args, _relativeTo);
}

public sealed class KeyEventInfo : DomEventBase
{
    private readonly KeyEventArgs _args;

    public KeyEventInfo(KeyEventArgs args)
        : base(args)
    {
        _args = args;
    }

    public string? key => _args.Key.ToString();
}

public sealed class TextInputEventInfo : DomEventBase
{
    private readonly TextInputEventArgs _args;

    public TextInputEventInfo(TextInputEventArgs args)
        : base(args)
    {
        _args = args;
    }

    public string? text => _args.Text;
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
