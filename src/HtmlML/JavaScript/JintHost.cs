using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Jint;
using Jint.Native;

namespace HtmlML;

public class JintHost
{
    private readonly html _window;
    public Engine Engine { get; }

    private readonly Dictionary<string, CanvasJsElement> _canvasById = new();

    public JintHost(html window)
    {
        _window = window;
        Engine = new Engine(options =>
        {
            options.Strict();
        });

        // console.log
        Engine.SetValue("console", new ConsoleJs());

        // document API
        Engine.SetValue("document", new DocumentJs(this));
        Engine.SetValue("window", new WindowJs());
    }

    public void RegisterCanvas(canvas c)
    {
        var id = c.Name;
        if (string.IsNullOrWhiteSpace(id))
            return;
        _canvasById[id] = new CanvasJsElement(this, c);
    }

    public void DispatchPointer(string id, string type, double x, double y)
    {
        if (id is null) return;
        if (_canvasById.TryGetValue(id, out var el))
        {
            el.Dispatch(type, x, y);
        }
        // Global fallback handler: onPointerEvent(id, type, x, y)
        try
        {
            Engine.Invoke("onPointerEvent", id, type, x, y);
        }
        catch { }
    }

    public void ExecuteScriptText(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            Engine.Execute(code);
        }
        catch (Exception)
        {
            // Ignore malformed script; do not crash the app
        }
    }

    public void ExecuteScriptUri(Uri uri)
    {
        if (uri.Scheme == "avares")
        {
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var code = reader.ReadToEnd();
            Engine.Execute(code);
            return;
        }
        if (uri.IsFile && File.Exists(uri.LocalPath))
        {
            var code = File.ReadAllText(uri.LocalPath);
            Engine.Execute(code);
        }
    }

    public sealed class ConsoleJs
    {
        public void log(object? value) => System.Diagnostics.Debug.WriteLine(value);
    }

    public sealed class WindowJs
    {
    }

    public sealed class DocumentJs
    {
        private readonly JintHost _host;
        public DocumentJs(JintHost host) { _host = host; }

        public object? getElementById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return _host._canvasById.TryGetValue(id, out var el) ? el : null;
        }
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
            if (handler.IsUndefined() || handler.IsNull()) return;
            switch (type)
            {
                case "pointerdown": _down.Add(handler); break;
                case "pointermove": _move.Add(handler); break;
                case "pointerup": _up.Add(handler); break;
            }
        }

        public void removeEventListener(string type, JsValue handler)
        {
            if (handler.IsUndefined() || handler.IsNull()) return;
            switch (type)
            {
                case "pointerdown": _down.Remove(handler); break;
                case "pointermove": _move.Remove(handler); break;
                case "pointerup": _up.Remove(handler); break;
            }
        }

        internal void Dispatch(string type, double x, double y)
        {
            // Prefer global handler to avoid function instance type coupling
            try
            {
                _host.Engine.Invoke("onPointerEvent", _canvas.Name ?? string.Empty, type, x, y);
            }
            catch { }
        }
    }

    public sealed class JsPointerEvent
    {
        public double x { get; set; }
        public double y { get; set; }
    }
}
