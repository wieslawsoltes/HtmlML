using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Jint;
using JavaScript.Avalonia;
using Jint.Native;
using Jint.Runtime;

namespace HtmlML;

public class JintHost : JintAvaloniaHost
{
    private readonly html _window;
    private readonly Dictionary<string, CanvasJsElement> _canvasById = new();

    public JintHost(html window) : base(window)
    {
        _window = window;

        var document = new DocumentJs(this);
        Engine.SetValue("document", document);
        try
        {
            Engine.Execute("if (typeof window !== 'undefined') { window.document = document; }");
        }
        catch
        {
            // Ignore configuration errors
        }
    }

    public void RegisterCanvas(canvas c)
    {
        var id = c.Name;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        _canvasById[id] = new CanvasJsElement(this, c);
    }

    public void DispatchPointer(string id, string type, double x, double y)
    {
        if (id is null)
        {
            return;
        }

        if (_canvasById.TryGetValue(id, out var el))
        {
            if (el.Dispatch(type, x, y))
            {
                return;
            }
        }

        try
        {
            Engine.Invoke("onPointerEvent", id, type, x, y);
        }
        catch
        {
            // Ignore missing handler
        }
    }

    public sealed class DocumentJs
    {
        private readonly JintHost _host;

        public DocumentJs(JintHost host)
        {
            _host = host;
        }

        public object? getElementById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (_host._canvasById.TryGetValue(id, out var cel))
            {
                return cel;
            }

            var root = _host._window.Content as Control;
            if (root is null)
            {
                return null;
            }

            foreach (var c in Traverse(root))
            {
                if (c.Name == id)
                {
                    if (c is canvas can)
                    {
                        if (_host._canvasById.TryGetValue(id, out var existing))
                        {
                            return existing;
                        }

                        var wrapper = new CanvasJsElement(_host, can);
                        _host._canvasById[id] = wrapper;
                        return wrapper;
                    }

                    return new DomElement(_host, c);
                }
            }

            return null;
        }

        public object? querySelector(string selector)
        {
            var all = querySelectorAll(selector) as object[];
            return all is { Length: > 0 } ? all![0] : null;
        }

        public object[] querySelectorAll(string selector)
        {
            var root = _host._window.Content as Control;
            if (root is null)
            {
                return Array.Empty<object>();
            }

            var list = new List<object>();
            foreach (var c in Traverse(root))
            {
                if (MatchesSelector(c, selector))
                {
                    list.Add(new DomElement(_host, c));
                }
            }

            return list.ToArray();
        }

        public DomElement? createElement(string tag)
        {
            var ctrl = CreateControl(tag);
            return ctrl is null ? null : new DomElement(_host, ctrl);
        }

        public DomElement? body
        {
            get
            {
                var root = _host._window.Content as Control;
                if (root is null)
                {
                    return null;
                }

                foreach (var c in Traverse(root))
                {
                    if (c is body)
                    {
                        return new DomElement(_host, c);
                    }
                }

                return null;
            }
        }

        private static bool MatchesSelector(Control c, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return false;
            }

            selector = selector.Trim();
            if (selector.StartsWith("#"))
            {
                return string.Equals((c as StyledElement)?.Name, selector.Substring(1), StringComparison.Ordinal);
            }

            if (selector.StartsWith("."))
            {
                return (c as StyledElement)?.Classes.Contains(selector.Substring(1)) == true;
            }

            var n = c.GetType().Name.ToLowerInvariant();
            return n == selector.ToLowerInvariant();
        }

        private static IEnumerable<Control> Traverse(Control root)
        {
            yield return root;

            if (root is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Control c)
                    {
                        foreach (var x in Traverse(c))
                        {
                            yield return x;
                        }
                    }
                }
            }
            else if (root is ContentControl cc && cc.Content is Control qc)
            {
                foreach (var x in Traverse(qc))
                {
                    yield return x;
                }
            }
            else if (root is Decorator dec && dec.Child is Control child)
            {
                foreach (var x in Traverse(child))
                {
                    yield return x;
                }
            }
        }

        private static Control? CreateControl(string tag)
        {
            switch (tag.ToLowerInvariant())
            {
                case "div": return new div();
                case "p": return new p();
                case "h1": return new h1();
                case "h2": return new h2();
                case "h3": return new h3();
                case "h4": return new h4();
                case "h5": return new h5();
                case "h6": return new h6();
                case "span": return new TextBlock();
                case "strong": return new TextBlock { FontWeight = Avalonia.Media.FontWeight.Bold };
                case "em": return new TextBlock { FontStyle = Avalonia.Media.FontStyle.Italic };
                case "code": return new TextBlock { FontFamily = new Avalonia.Media.FontFamily("monospace") };
                case "a": return new TextBlock();
                case "ul": return new ul();
                case "ol": return new ol();
                case "li": return new li();
                case "img": return new img();
                case "hr": return new hr();
                case "section": return new section();
                case "header": return new header();
                case "footer": return new footer();
                case "nav": return new nav();
                case "article": return new article();
                case "aside": return new aside();
                case "canvas": return new canvas();
                default: return new div();
            }
        }
    }

    public sealed class DomElement
    {
        private readonly JintHost _host;
        public Control Control { get; }

        public DomElement(JintHost host, Control control)
        {
            _host = host;
            Control = control;
        }

        public DomElement? appendChild(DomElement child)
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

        public void remove()
        {
            var parent = Control.Parent;
            if (parent is Panel panel)
            {
                panel.Children.Remove(Control);
            }
            else if (parent is Decorator dec)
            {
                if (dec.Child == Control)
                {
                    dec.Child = null;
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

        public string? getAttribute(string name)
        {
            name = name.ToLowerInvariant();
            switch (name)
            {
                case "id": return (Control as StyledElement)?.Name;
                case "class": return string.Join(' ', (Control as StyledElement)?.Classes ?? new Classes());
                case "style": return GetStyleString(Control);
                case "title": return Avalonia.Controls.ToolTip.GetTip(Control)?.ToString();
            }

            var prop = Control.GetType().GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
            return prop?.GetValue(Control)?.ToString();
        }

        public void setAttribute(string name, string? value)
        {
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
                case "style":
                    HtmlElementBase.ApplyStyles(Control, value);
                    return;
                case "title":
                    Avalonia.Controls.ToolTip.SetTip(Control, value);
                    return;
            }

            var type = Control.GetType();
            var prop = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                       ?? type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
            if (prop is not null && prop.CanWrite)
            {
                try
                {
                    object? converted = value;
                    if (prop.PropertyType == typeof(double) && double.TryParse(value, out var d))
                    {
                        converted = d;
                    }

                    prop.SetValue(Control, converted);
                }
                catch
                {
                    // Ignore conversion errors
                }
            }
        }

        public void classListAdd(string cls)
        {
            if (Control is StyledElement se && !string.IsNullOrWhiteSpace(cls))
            {
                se.Classes.Add(cls);
            }
        }

        public void classListRemove(string cls)
        {
            if (Control is StyledElement se && !string.IsNullOrWhiteSpace(cls))
            {
                se.Classes.Remove(cls);
            }
        }

        public void classListToggle(string cls)
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

        public string? textContent
        {
            get
            {
                if (Control is TextBlock tb)
                {
                    return tb.Text;
                }

                return null;
            }
            set
            {
                if (Control is TextBlock tb)
                {
                    tb.Text = value;
                }
            }
        }

        private static bool TryGetControlsCollection(Control parent, out Controls controls)
        {
            var prop = parent.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(p => p.PropertyType == typeof(Controls) && string.Equals(p.Name, "content", StringComparison.OrdinalIgnoreCase));
            if (prop != null)
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

        private static string GetStyleString(Control c) => string.Empty;
    }

    public sealed class CanvasJsElement
    {
        private readonly JintHost _host;
        private readonly canvas _canvas;

        private readonly List<JsValue> _down = new();
        private readonly List<JsValue> _move = new();
        private readonly List<JsValue> _up = new();

        public CanvasJsElement(JintHost host, canvas c)
        {
            _host = host;
            _canvas = c;
        }

        public canvas.Canvas2DContext getContext(string type)
        {
            return _canvas.GetContext(type);
        }

        public void addEventListener(string type, JsValue handler)
        {
            if (IsNullish(handler))
            {
                return;
            }

            switch (type)
            {
                case "pointerdown":
                    _down.Add(handler);
                    break;
                case "pointermove":
                    _move.Add(handler);
                    break;
                case "pointerup":
                    _up.Add(handler);
                    break;
            }
        }

        public void removeEventListener(string type, JsValue handler)
        {
            if (IsNullish(handler))
            {
                return;
            }

            switch (type)
            {
                case "pointerdown":
                    _down.Remove(handler);
                    break;
                case "pointermove":
                    _move.Remove(handler);
                    break;
                case "pointerup":
                    _up.Remove(handler);
                    break;
            }
        }

        private static bool IsNullish(JsValue value)
        {
            return value.IsUndefined() || value.IsNull();
        }

        internal bool Dispatch(string type, double x, double y)
        {
            List<JsValue>? handlers = null;
            switch (type)
            {
                case "pointerdown":
                    handlers = _down;
                    break;
                case "pointermove":
                    handlers = _move;
                    break;
                case "pointerup":
                case "pointercancel":
                    handlers = _up;
                    break;
            }

            if (handlers is { Count: > 0 })
            {
                var evt = new JsPointerEvent { x = x, y = y };
                var list = handlers.ToArray();
                foreach (var cb in list)
                {
                    try
                    {
                        _host.Engine.Invoke(cb, evt);
                    }
                    catch
                    {
                        // Ignore callback errors
                    }
                }

                return true;
            }

            return false;
        }
    }

    public sealed class JsPointerEvent
    {
        public double x { get; set; }
        public double y { get; set; }
    }
}
