using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Styling;
using System.Runtime.CompilerServices;
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
    }

    protected override AvaloniaDomDocument CreateDocument()
        => new HtmlDocument(this);

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

    private sealed class HtmlDocument : AvaloniaDomDocument
    {
        private readonly JintHost _host;
        private readonly ConditionalWeakTable<Control, AvaloniaDomElement> _wrappers = new();

        public HtmlDocument(JintHost host)
            : base(host)
        {
            _host = host;
        }

        protected override Control? GetDocumentRoot()
            => _host._window.Content as Control ?? base.GetDocumentRoot();

        public override object? getElementById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (_host._canvasById.TryGetValue(id, out var existing))
            {
                return existing;
            }

            return base.getElementById(id);
        }

        public override object? body
        {
            get
            {
                var root = _host._window.Content as Control;
                if (root is null)
                {
                    return base.body;
                }

                foreach (var control in Traverse(root))
                {
                    if (control is body)
                    {
                        return WrapControl(control);
                    }
                }

                return base.body;
            }
        }

        public override object? createElement(string tag)
        {
            if (string.Equals(tag, "canvas", StringComparison.OrdinalIgnoreCase))
            {
                var canv = new canvas();
                var wrapper = new CanvasJsElement(_host, canv);
                RegisterWrapper(canv, wrapper);
                RegisterCanvasById(canv, wrapper);
                return wrapper;
            }

            return base.createElement(tag);
        }

        protected override Control? CreateControl(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

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
            }

            return base.CreateControl(tag);
        }

        protected override AvaloniaDomElement WrapControl(Control control)
        {
            if (control is canvas canv)
            {
                if (_wrappers.TryGetValue(control, out var existingCanvasWrapper))
                {
                    if (existingCanvasWrapper is CanvasJsElement canvasWrapperCached)
                    {
                        RegisterCanvasById(canv, canvasWrapperCached);
                        return canvasWrapperCached;
                    }
                }

                var id = canv.Name;
                if (!string.IsNullOrWhiteSpace(id) && _host._canvasById.TryGetValue(id, out var cachedById))
                {
                    _wrappers.Add(control, cachedById);
                    return cachedById;
                }

                var wrapper = new CanvasJsElement(_host, canv);
                RegisterWrapper(control, wrapper);
                RegisterCanvasById(canv, wrapper);
                return wrapper;
            }

            if (_wrappers.TryGetValue(control, out var existing))
            {
                return existing;
            }

            var created = new HtmlDomElement(_host, control);
            RegisterWrapper(control, created);
            return created;
        }

        private void RegisterWrapper(Control control, AvaloniaDomElement wrapper)
        {
            if (!_wrappers.TryGetValue(control, out _))
            {
                _wrappers.Add(control, wrapper);
            }
        }

        private void RegisterCanvasById(canvas canv, CanvasJsElement wrapper)
        {
            var id = canv.Name;
            if (!string.IsNullOrWhiteSpace(id))
            {
                _host._canvasById[id] = wrapper;
            }
        }
    }

    private class HtmlDomElement : AvaloniaDomElement
    {
        public HtmlDomElement(JintHost host, Control control)
            : base(host, control)
        {
        }

        protected override bool TrySetAttribute(string name, string? value)
        {
            if (name == "style")
            {
                HtmlElementBase.ApplyStyles(Control, value);
                return true;
            }

            return base.TrySetAttribute(name, value);
        }
    }

    private sealed class CanvasJsElement : HtmlDomElement
    {
        private readonly JintHost _host;

        private readonly List<JsValue> _down = new();
        private readonly List<JsValue> _move = new();
        private readonly List<JsValue> _up = new();

        public CanvasJsElement(JintHost host, canvas c)
            : base(host, c)
        {
            _host = host;
        }

        public override object? getContext(string type)
        {
            return ((canvas)Control).GetContext(type);
        }

        public override void addEventListener(string type, JsValue handler)
            => addEventListener(type, handler, JsValue.Undefined);

        public override void addEventListener(string type, JsValue handler, JsValue options)
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
                default:
                    base.addEventListener(type, handler, options);
                    break;
            }
        }

        public override void removeEventListener(string type, JsValue handler)
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
                default:
                    base.removeEventListener(type, handler);
                    break;
            }
        }

        public override void setAttribute(string name, string? value)
        {
            if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
            {
                var oldId = ((canvas)Control).Name;
                base.setAttribute(name, value);

                if (!string.IsNullOrWhiteSpace(oldId) && oldId != ((canvas)Control).Name)
                {
                    _host._canvasById.Remove(oldId);
                }

                var newId = ((canvas)Control).Name;
                if (!string.IsNullOrWhiteSpace(newId))
                {
                    _host._canvasById[newId] = this;
                }
                return;
            }

            base.setAttribute(name, value);
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
