using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace JavaScript.Avalonia;

public class JintAvaloniaHost
{
    private readonly Func<JintAvaloniaHost, AvaloniaDomDocument>? _documentFactory;

    public TopLevel TopLevel { get; }
    public Engine Engine { get; }
    public AvaloniaDomDocument Document { get; }

    private int _rafSeq;
    private readonly Dictionary<int, JsValue> _rafCallbacks = new();
    private bool _rafScheduled;
    private static readonly HttpClient s_httpClient = new();
    private readonly Dictionary<string, JsValue> _moduleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Stack<JsValue> _pendingModuleResults = new();

    public string ScriptBaseDirectory { get; set; } = AppContext.BaseDirectory;

    public JintAvaloniaHost(TopLevel topLevel, Func<JintAvaloniaHost, AvaloniaDomDocument>? documentFactory = null)
    {
        TopLevel = topLevel ?? throw new ArgumentNullException(nameof(topLevel));
        _documentFactory = documentFactory;
        Engine = new Engine(options =>
        {
            ConfigureEngineOptions(options);
        });

        Engine.SetValue("console", CreateConsoleObject());
        Engine.SetValue("window", CreateWindowObject());

        Document = documentFactory?.Invoke(this) ?? CreateDocument();
        Engine.SetValue("document", Document);
        RegisterModuleSystem();
        RegisterEventConstructors();
        RegisterMutationObserver();
        try
        {
            Engine.Execute("if (typeof window !== 'undefined') { window.document = document; if (typeof globalThis !== 'undefined') { globalThis.document = document; if (typeof window.setTimeout === 'function') { globalThis.setTimeout = window.setTimeout.bind(window); } if (typeof window.clearTimeout === 'function') { globalThis.clearTimeout = window.clearTimeout.bind(window); } if (typeof window.setInterval === 'function') { globalThis.setInterval = window.setInterval.bind(window); } if (typeof window.clearInterval === 'function') { globalThis.clearInterval = window.clearInterval.bind(window); } if (typeof window.requestAnimationFrame === 'function') { globalThis.requestAnimationFrame = window.requestAnimationFrame.bind(window); } if (typeof window.cancelAnimationFrame === 'function') { globalThis.cancelAnimationFrame = window.cancelAnimationFrame.bind(window); } if (typeof window.importScripts === 'function') { globalThis.importScripts = window.importScripts.bind(window); } if (typeof window.require === 'function') { globalThis.require = window.require.bind(window); } } }");
        }
        catch
        {
        }

        Document.ScheduleReadyStateCompletion();
        TopLevel.Closed += OnTopLevelClosed;
    }

    protected virtual void ConfigureEngineOptions(Options options)
    {
        options.Strict();
    }

    protected virtual object CreateConsoleObject() => new ConsoleJs();

    protected virtual object CreateWindowObject() => new WindowJs(this);

    protected virtual AvaloniaDomDocument CreateDocument()
        => new AvaloniaDomDocument(this);

    internal double GetTimestamp() => _stopwatch.Elapsed.TotalMilliseconds;

    private void RegisterModuleSystem()
    {
        Func<string, JsValue> globalRequire = specifier => RequireModule(specifier, null);
        Engine.SetValue("require", globalRequire);
        Engine.SetValue("__setLastModuleResult", new Action<JsValue>(SetPendingModuleResult));

        try
        {
            Engine.Execute("if (typeof window !== 'undefined') { window.require = require; }");
        }
        catch
        {
        }

        const string amdDefineScript = @"(function(){
  if (typeof globalThis === 'undefined') {
    return;
  }
  var setter = globalThis.__setLastModuleResult;
  if (typeof setter !== 'function') {
    return;
  }
  function normalizeDependencies(deps, factory) {
    if (deps == null) {
      deps = [];
    }
    if (typeof deps === 'string') {
      deps = [deps];
    }
    if (!Array.isArray(deps)) {
      deps = [];
    }
    if (deps.length === 0 && typeof factory === 'function' && factory.length > 0) {
      var defaultDeps = ['require', 'exports', 'module'];
      deps = defaultDeps.slice(0, factory.length);
    }
    return deps.slice();
  }
  function resolveDependencies(deps, localRequire) {
    var exportsObject = {};
    var moduleObject = { exports: exportsObject };
    var values = [];
    for (var i = 0; i < deps.length; i++) {
      var dep = deps[i];
      if (dep === 'exports') {
        values.push(exportsObject);
        continue;
      }
      if (dep === 'module') {
        values.push(moduleObject);
        continue;
      }
      if (dep === 'require') {
        values.push(localRequire);
        continue;
      }
      values.push(localRequire(dep));
    }
    return { values: values, exportsObject: exportsObject, moduleObject: moduleObject };
  }
  function selectResult(factoryResult, moduleObject, exportsObject) {
    if (typeof factoryResult !== 'undefined') {
      return factoryResult;
    }
    if (moduleObject && typeof moduleObject.exports !== 'undefined') {
      return moduleObject.exports;
    }
    return exportsObject;
  }
  function define(name, deps, factory) {
    if (typeof name !== 'string') {
      factory = deps;
      deps = name;
      name = null;
    }
    if (typeof deps === 'function' || deps == null) {
      factory = deps;
      deps = [];
    }
    if (typeof factory !== 'function') {
      setter(factory);
      return factory;
    }
    var normalized = normalizeDependencies(deps, factory);
    var resolved = resolveDependencies(normalized, require);
    var result = factory.apply(globalThis, resolved.values);
    var finalResult = selectResult(result, resolved.moduleObject, resolved.exportsObject);
    resolved.moduleObject.exports = finalResult;
    setter(finalResult);
    return finalResult;
  }
  define.amd = {};
  globalThis.define = define;
  if (typeof window !== 'undefined') {
    window.define = define;
  }
})();";

        try
        {
            Engine.Execute(amdDefineScript);
        }
        catch
        {
        }
    }

    private void RegisterMutationObserver()
    {
        Engine.SetValue("__createMutationObserverInternal", new Func<JsValue, object>(callback => Document.CreateMutationObserver(callback)));

        const string script = @"(function(){
  if (typeof globalThis === 'undefined') {
    return;
  }
  var factory = globalThis.__createMutationObserverInternal;
  if (typeof factory !== 'function') {
    return;
  }
  function MutationObserver(callback) {
    if (typeof callback !== 'function') {
      throw new TypeError('MutationObserver callback must be a function');
    }
    var impl = factory(callback);
    impl.__setJsObserver(this);
    Object.defineProperty(this, '__impl', { value: impl, enumerable: false, configurable: false, writable: false });
  }
  MutationObserver.prototype.observe = function(target, options) {
    this.__impl.observe(target, options);
  };
  MutationObserver.prototype.disconnect = function() {
    this.__impl.disconnect();
  };
  MutationObserver.prototype.takeRecords = function() {
    return this.__impl.takeRecords();
  };
  globalThis.MutationObserver = MutationObserver;
  if (typeof window !== 'undefined') {
    window.MutationObserver = MutationObserver;
  }
  try {
    delete globalThis.__createMutationObserverInternal;
  } catch (e) {}
})();";

        try
        {
            Engine.Execute(script);
        }
        catch
        {
        }
    }

    private void RegisterEventConstructors()
    {
        Engine.SetValue("__createEventInternal", new Func<JsValue, JsValue, object?>((type, options) => Document.CreateEventFromConstructor(type, options, false)));
        Engine.SetValue("__createCustomEventInternal", new Func<JsValue, JsValue, object?>((type, options) => Document.CreateEventFromConstructor(type, options, true)));

        const string script = @"(function(){
  if (typeof globalThis === 'undefined') {
    return;
  }
  var createEvent = globalThis.__createEventInternal;
  var createCustomEvent = globalThis.__createCustomEventInternal;
  if (typeof createEvent !== 'function' || typeof createCustomEvent !== 'function') {
    return;
  }
  function Event(type, options) {
    return createEvent(type, options);
  }
  function CustomEvent(type, options) {
    return createCustomEvent(type, options);
  }
  CustomEvent.prototype = Object.create(Event.prototype);
  CustomEvent.prototype.constructor = CustomEvent;
  globalThis.Event = Event;
  globalThis.CustomEvent = CustomEvent;
  if (typeof window !== 'undefined') {
    window.Event = Event;
    window.CustomEvent = CustomEvent;
  }
  try {
    delete globalThis.__createEventInternal;
    delete globalThis.__createCustomEventInternal;
  } catch (e) {}
})();";

        try
        {
            Engine.Execute(script);
        }
        catch
        {
        }
    }

    public JsValue Require(string specifier) => RequireModule(specifier, null);

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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"JavaScript execution error: {ex}");
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

    private JsValue RequireModule(string? specifier, ModuleSource? referrer)
    {
        if (string.IsNullOrWhiteSpace(specifier))
        {
            return JsValue.Undefined;
        }

        try
        {
            var source = ResolveModule(specifier, referrer);
            if (_moduleCache.TryGetValue(source.CacheKey, out var cached))
            {
                return cached;
            }

            var exports = Engine.Evaluate("({})").AsObject();
            var moduleObject = Engine.Evaluate("({})").AsObject();
            var exportsValue = JsValue.FromObject(Engine, exports);
            moduleObject.Set("exports", exportsValue, throwOnError: false);
            moduleObject.Set("id", JsValue.FromObject(Engine, source.CacheKey), throwOnError: false);
            moduleObject.Set("filename", JsValue.FromObject(Engine, source.FileName), throwOnError: false);
            moduleObject.Set("dirname", JsValue.FromObject(Engine, source.DirectoryOrBase ?? string.Empty), throwOnError: false);

            Func<string, JsValue> moduleRequire = nestedSpecifier => RequireModule(nestedSpecifier, source);
            var requireValue = JsValue.FromObject(Engine, moduleRequire);
            moduleObject.Set("require", requireValue, throwOnError: false);

            PushModuleResultFrame();

            var wrapper = "(function(require, module, exports, __filename, __dirname){\n" + source.Content + "\n})";
            var functionValue = Engine.Evaluate(wrapper);
            var moduleValue = JsValue.FromObject(Engine, moduleObject);
            var filenameValue = JsValue.FromObject(Engine, source.FileName);
            var dirnameValue = JsValue.FromObject(Engine, source.DirectoryOrBase ?? string.Empty);
            Engine.Invoke(functionValue, requireValue, moduleValue, exportsValue, filenameValue, dirnameValue);

            var result = moduleObject.Get("exports");
            var pendingResult = PopModuleResultFrame();

            if (!HasMeaningfulExport(result))
            {
                if (HasMeaningfulExport(pendingResult))
                {
                    result = pendingResult;
                    moduleObject.Set("exports", result, throwOnError: false);
                }
                else
                {
                    var globalResult = TryGetGlobalExport(source, specifier);
                    if (HasMeaningfulExport(globalResult))
                    {
                        result = globalResult;
                        moduleObject.Set("exports", result, throwOnError: false);
                    }
                }
            }

            _moduleCache[source.CacheKey] = result;
            return result;
        }
        catch
        {
            if (_pendingModuleResults.Count > 0)
            {
                PopModuleResultFrame();
            }
            return JsValue.Undefined;
        }
    }

    private void ImportScripts(ModuleSource? referrer, IEnumerable<string> specifiers)
    {
        if (specifiers is null)
        {
            return;
        }

        foreach (var specifier in specifiers)
        {
            if (string.IsNullOrWhiteSpace(specifier))
            {
                continue;
            }

            try
            {
                var source = ResolveModule(specifier, referrer);
                Engine.Execute(AddSourceInformation(source));
            }
            catch
            {
                // Ignore loading errors
            }
        }
    }

    private void ImportScripts(ModuleSource? referrer, params string[] specifiers)
    {
        var list = specifiers ?? Array.Empty<string>();
        ImportScripts(referrer, (IEnumerable<string>)list);
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

    private ModuleSource ResolveModule(string specifier, ModuleSource? referrer)
    {
        if (Uri.TryCreate(specifier, UriKind.Absolute, out var absolute))
        {
            return LoadModuleFromUri(absolute);
        }

        if (referrer is not null)
        {
            switch (referrer.Kind)
            {
                case ModuleKind.File:
                    var baseDirectory = referrer.Directory ?? ScriptBaseDirectory;
                    if (!string.IsNullOrEmpty(baseDirectory))
                    {
                        var combined = Path.GetFullPath(Path.Combine(baseDirectory!, specifier));
                        return LoadModuleFromFile(combined);
                    }
                    break;
                case ModuleKind.Avares when referrer.BaseUri is not null:
                    var avaresResolved = new Uri(referrer.BaseUri, specifier);
                    return LoadModuleFromUri(avaresResolved);
                case ModuleKind.Http when referrer.BaseUri is not null:
                    var httpResolved = new Uri(referrer.BaseUri, specifier);
                    return LoadModuleFromUri(httpResolved);
            }
        }

        if (!string.IsNullOrEmpty(ScriptBaseDirectory))
        {
            var combined = Path.GetFullPath(Path.Combine(ScriptBaseDirectory!, specifier));
            return LoadModuleFromFile(combined);
        }

        throw new InvalidOperationException($"Unable to resolve module '{specifier}'.");
    }

    private ModuleSource LoadModuleFromUri(Uri uri)
    {
        if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return LoadModuleFromFile(uri.LocalPath);
        }

        if (uri.Scheme.Equals("avares", StringComparison.OrdinalIgnoreCase))
        {
            return LoadModuleFromAvares(uri);
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return LoadModuleFromHttp(uri);
        }

        throw new NotSupportedException($"Unsupported module scheme '{uri.Scheme}'.");
    }

    private void OnTopLevelClosed(object? sender, EventArgs e)
    {
        Document.RaiseDocumentEvent("unload", bubbles: false, cancelable: false);
    }

    private ModuleSource LoadModuleFromFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            if (string.IsNullOrEmpty(Path.GetExtension(fullPath)))
            {
                var withJs = fullPath + ".js";
                if (File.Exists(withJs))
                {
                    fullPath = withJs;
                }
                else
                {
                    throw new FileNotFoundException($"Module file '{path}' not found.", fullPath);
                }
            }
            else
            {
                throw new FileNotFoundException($"Module file '{path}' not found.", fullPath);
            }
        }

        var content = File.ReadAllText(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? ScriptBaseDirectory;
        return new ModuleSource(ModuleKind.File, fullPath, content, fullPath, directory, null);
    }

    private ModuleSource LoadModuleFromAvares(Uri uri)
    {
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var baseUri = new Uri(uri, "./");
        return new ModuleSource(ModuleKind.Avares, uri.ToString(), content, uri.ToString(), null, baseUri);
    }

    private ModuleSource LoadModuleFromHttp(Uri uri)
    {
        var content = s_httpClient.GetStringAsync(uri).GetAwaiter().GetResult();
        var baseUri = new Uri(uri, "./");
        return new ModuleSource(ModuleKind.Http, uri.ToString(), content, uri.ToString(), null, baseUri);
    }

    private void PushModuleResultFrame()
    {
        _pendingModuleResults.Push(JsValue.Undefined);
    }

    private JsValue PopModuleResultFrame()
    {
        return _pendingModuleResults.Count > 0 ? _pendingModuleResults.Pop() : JsValue.Undefined;
    }

    private void SetPendingModuleResult(JsValue value)
    {
        if (_pendingModuleResults.Count == 0)
        {
            return;
        }

        _pendingModuleResults.Pop();
        _pendingModuleResults.Push(value);
    }

    private bool HasMeaningfulExport(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return false;
        }

        if (value.IsBoolean() || value.IsNumber() || value.IsString() || value.IsDate() || value.IsRegExp())
        {
            return true;
        }

        if (value.IsObject())
        {
            var obj = value.AsObject();
            var typeName = obj.GetType().Name;
            if (typeName.IndexOf("Function", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var keys = obj.GetOwnPropertyKeys();
            if (keys != null && keys.Count > 0)
            {
                return true;
            }

            return false;
        }

        return true;
    }

    private JsValue TryGetGlobalExport(ModuleSource source, string specifier)
    {
        foreach (var name in GetGlobalNameCandidates(source, specifier))
        {
            var value = GetGlobalProperty(name);
            if (HasMeaningfulExport(value))
            {
                return value;
            }
        }

        return JsValue.Undefined;
    }

    private JsValue GetGlobalProperty(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return JsValue.Undefined;
        }

        if (Engine.Global.HasProperty(name))
        {
            var descriptor = Engine.Global.GetOwnProperty(name);
            if (descriptor?.Value is { } value && !value.IsUndefined())
            {
                return value;
            }
        }

        var windowValue = Engine.GetValue("window");
        if (windowValue.IsObject())
        {
            var windowObject = windowValue.AsObject();
            if (windowObject.HasProperty(name))
            {
                var descriptor = windowObject.GetOwnProperty(name);
                if (descriptor?.Value is { } value && !value.IsUndefined())
                {
                    return value;
                }
            }
        }

        return JsValue.Undefined;
    }

    private static IEnumerable<string> GetGlobalNameCandidates(ModuleSource source, string specifier)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidates(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            foreach (var token in TokenizeName(raw.Trim()))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                set.Add(token);
                if (token.Length > 0)
                {
                    set.Add(char.ToUpperInvariant(token[0]) + token.Substring(1));
                }
            }
        }

        AddCandidates(ExtractBaseName(specifier));
        AddCandidates(ExtractBaseName(source.FileName));

        return set;
    }

    private static string? ExtractBaseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = value;
        var queryIndex = sanitized.IndexOfAny(new[] { '?', '#' });
        if (queryIndex >= 0)
        {
            sanitized = sanitized[..queryIndex];
        }

        sanitized = sanitized.Replace('\\', '/');
        var lastSlash = sanitized.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            sanitized = sanitized[(lastSlash + 1)..];
        }

        if (sanitized.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            sanitized.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            sanitized.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase))
        {
            var dotIndex = sanitized.LastIndexOf('.');
            if (dotIndex > 0)
            {
                sanitized = sanitized[..dotIndex];
            }
        }

        if (sanitized.StartsWith("@", StringComparison.Ordinal))
        {
            sanitized = sanitized[1..];
        }

        return sanitized;
    }

    private static IEnumerable<string> TokenizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var separators = new[] { '-', '.', '_', '+', ' ' };
        var parts = value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part;
            }
        }
    }

    private static string AddSourceInformation(ModuleSource source)
    {
        if (string.IsNullOrEmpty(source.FileName))
        {
            return source.Content;
        }

        return source.Content + "\n//# sourceURL=" + source.FileName;
    }

    private sealed class ModuleSource
    {
        public ModuleSource(ModuleKind kind, string cacheKey, string content, string fileName, string? directory, Uri? baseUri)
        {
            Kind = kind;
            CacheKey = cacheKey;
            Content = content;
            FileName = fileName;
            Directory = directory;
            BaseUri = baseUri;
        }

        public ModuleKind Kind { get; }
        public string CacheKey { get; }
        public string Content { get; }
        public string FileName { get; }
        public string? Directory { get; }
        public Uri? BaseUri { get; }
        public string? DirectoryOrBase => Directory ?? BaseUri?.ToString();
    }

    private enum ModuleKind
    {
        File,
        Avares,
        Http
    }

    public class ConsoleJs
    {
        public virtual void log(object? value) => Console.WriteLine(value);

        public virtual void info(object? value) => Console.WriteLine(value);

        public virtual void warn(object? value) => Console.WriteLine(value);

        public virtual void error(object? value) => Console.Error.WriteLine(value);

        public virtual void table(object? value)
        {
            if (value is null)
            {
                Console.WriteLine("null");
                return;
            }

            Console.WriteLine(value);
        }
    }

    public class WindowJs
    {
        private readonly JintAvaloniaHost _host;
        private int _timerSeq;
        private readonly Dictionary<int, DispatcherTimer> _timeouts = new();
        private readonly Dictionary<int, DispatcherTimer> _intervals = new();

        public WindowJs(JintAvaloniaHost host)
        {
            _host = host;
        }

        public int setTimeout(JsValue callback, int ms)
        {
            if (callback.IsUndefined() || callback.IsNull())
            {
                return 0;
            }

            var id = ++_timerSeq;
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(ms, 0))
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _timeouts.Remove(id);
                InvokeTimerCallback(callback);
            };

            _timeouts[id] = timer;
            timer.Start();
            return id;
        }

        public void clearTimeout(int id)
        {
            if (_timeouts.Remove(id, out var timer))
            {
                timer.Stop();
            }
        }

        public int setInterval(JsValue callback, int ms)
        {
            if (callback.IsUndefined() || callback.IsNull())
            {
                return 0;
            }

            var id = ++_timerSeq;
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(ms, 0))
            };

            timer.Tick += (_, _) => InvokeTimerCallback(callback);

            _intervals[id] = timer;
            timer.Start();
            return id;
        }

        public void clearInterval(int id)
        {
            if (_intervals.Remove(id, out var timer))
            {
                timer.Stop();
            }
        }

        public int requestAnimationFrame(JsValue callback) => _host.RequestAnimationFrame(callback);

        public void cancelAnimationFrame(int id) => _host.CancelAnimationFrame(id);

        public void importScripts(params string[] specifiers)
        {
            if (specifiers is null || specifiers.Length == 0)
            {
                return;
            }

            _host.ImportScripts(null, specifiers);
        }

        public CssComputedStyle getComputedStyle(object element)
        {
            return element is AvaloniaDomElement domElement
                ? _host.Document.getComputedStyle(domElement)
                : CssComputedStyle.Empty;
        }

        private void InvokeTimerCallback(JsValue callback)
        {
            try
            {
                _host.Engine.Invoke(callback, Array.Empty<object>());
            }
            catch
            {
                // Ignore callback errors
            }
        }
    }
}
