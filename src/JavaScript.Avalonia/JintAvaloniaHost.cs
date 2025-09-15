using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Jint;
using Jint.Native;

namespace JavaScript.Avalonia;

public class JintAvaloniaHost
{
    public TopLevel TopLevel { get; }
    public Engine Engine { get; }

    private int _rafSeq;
    private readonly Dictionary<int, JsValue> _rafCallbacks = new();
    private bool _rafScheduled;

    public JintAvaloniaHost(TopLevel topLevel)
    {
        TopLevel = topLevel ?? throw new ArgumentNullException(nameof(topLevel));
        Engine = new Engine(options =>
        {
            ConfigureEngineOptions(options);
        });

        Engine.SetValue("console", CreateConsoleObject());
        Engine.SetValue("window", CreateWindowObject());
    }

    protected virtual void ConfigureEngineOptions(Options options)
    {
        options.Strict();
    }

    protected virtual object CreateConsoleObject() => new ConsoleJs();

    protected virtual object CreateWindowObject() => new WindowJs(this);

    public void ExecuteScriptText(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

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
        if (uri is null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        try
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
        catch (Exception)
        {
            // Ignore load/execute errors
        }
    }

    internal int RequestAnimationFrame(JsValue callback)
    {
        if (callback.IsUndefined() || callback.IsNull())
        {
            return 0;
        }

        var id = ++_rafSeq;
        _rafCallbacks[id] = callback;
        EnsureRafScheduled();
        return id;
    }

    internal void CancelAnimationFrame(int id)
    {
        _rafCallbacks.Remove(id);
    }

    private void EnsureRafScheduled()
    {
        if (_rafScheduled)
        {
            return;
        }

        _rafScheduled = true;
        TopLevel.RequestAnimationFrame(ts => RafTick(ts));
    }

    private void RafTick(TimeSpan ts)
    {
        _rafScheduled = false;
        var now = ts.TotalMilliseconds;
        if (_rafCallbacks.Count == 0)
        {
            return;
        }

        var list = _rafCallbacks.Values.ToArray();
        _rafCallbacks.Clear();
        foreach (var cb in list)
        {
            try
            {
                Engine.Invoke(cb, now);
            }
            catch
            {
                // Ignore callback errors
            }
        }

        if (_rafCallbacks.Count > 0)
        {
            EnsureRafScheduled();
        }
    }

    public class ConsoleJs
    {
        public virtual void log(object? value) => Debug.WriteLine(value);
    }

    public class WindowJs
    {
        private readonly JintAvaloniaHost _host;

        public WindowJs(JintAvaloniaHost host)
        {
            _host = host;
        }

        public void setTimeout(JsValue callback, int ms)
        {
            if (callback.IsUndefined() || callback.IsNull())
            {
                return;
            }

            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    _host.Engine.Invoke(callback, Array.Empty<object>());
                }
                catch
                {
                    // Ignore callback errors
                }
            }, TimeSpan.FromMilliseconds(ms));
        }

        public int requestAnimationFrame(JsValue callback) => _host.RequestAnimationFrame(callback);

        public void cancelAnimationFrame(int id) => _host.CancelAnimationFrame(id);
    }
}
