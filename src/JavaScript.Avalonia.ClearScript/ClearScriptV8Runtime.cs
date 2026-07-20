using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HtmlML.Core;
using HtmlML.JavaScript;
using JavaScript.Avalonia;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace JavaScript.Avalonia.ClearScript;

public readonly record struct V8CanvasBatchingMetrics(
    long Contexts,
    long Flushes,
    long Operations,
    long Values,
    long ReplayTicks,
    long ReplayAllocatedBytes)
{
    public static V8CanvasBatchingMetrics operator -(
        V8CanvasBatchingMetrics left,
        V8CanvasBatchingMetrics right)
        => new(
            left.Contexts - right.Contexts,
            left.Flushes - right.Flushes,
            left.Operations - right.Operations,
            left.Values - right.Values,
            left.ReplayTicks - right.ReplayTicks,
            left.ReplayAllocatedBytes - right.ReplayAllocatedBytes);
}

public readonly record struct V8TypedManagedAbiMetrics(
    bool Enabled,
    long Registrations,
    long Calls,
    long RectCalls,
    long Misses,
    long Errors);

/// <summary>
/// Experimental shared-V8-runtime owner/frame host. Each frame receives a real
/// V8 context while same-origin contexts retain direct JavaScript object identity.
/// </summary>
public sealed class ClearScriptV8Runtime :
    IHtmlMlJavaScriptRuntime,
    IExternalVirtualBrowsingContextFactory,
    IExternalWindowEventDispatcher,
    IDisposable
{
    private static readonly object s_nativeLibraryResolverLock = new();
    private static bool s_nativeLibraryResolverInstalled;
    private static IntPtr s_configuredNativeLibraryHandle;
    private static string? s_configuredNativeLibraryPath;
    private static string? s_configuredNativeLibraryRid;
    private static readonly object s_typedManagedAbiLock = new();
    private static readonly ConcurrentDictionary<long, WeakReference<object>>
        s_typedManagedAbiTargets = new();
    private static readonly HtmlMlReadDomNumericPropertyCallback s_typedManagedAbiCallback =
        ReadDomNumericPropertyFromNative;
    private static readonly HtmlMlWriteDomRectCallback s_typedManagedAbiRectCallback =
        WriteDomRectFromNative;
    private static bool s_typedManagedAbiRegistered;
    private static long s_typedManagedAbiRegistrations;
    private static long s_typedManagedAbiCalls;
    private static long s_typedManagedAbiRectCalls;
    private static long s_typedManagedAbiMisses;
    private static long s_typedManagedAbiErrors;

    private static readonly bool s_enableTypedManagedAbi =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_ENABLE_V8_TYPED_MANAGED_ABI"),
            "1",
            StringComparison.Ordinal);

    private static readonly bool s_disableFastMethodCacheLookup =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_FAST_METHOD_CACHE_LOOKUP"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableCanvasFacade =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_CANVAS_FACADE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableTypedDomRect =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_TYPED_DOM_RECT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableTypedDomClientRects =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_TYPED_DOM_CLIENT_RECTS"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableTypedDomNumericProperties =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_TYPED_DOM_NUMERIC_PROPERTIES"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disablePrimitiveTextMetrics =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_PRIMITIVE_TEXT_METRICS"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableResultClassificationCache =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_RESULT_CLASSIFICATION_CACHE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_traceDomPropertyAccess =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_TRACE_V8_DOM_PROPERTY_ACCESS"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableStableDomPropertyCache =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_STABLE_DOM_PROPERTY_CACHE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableMissingDomPropertyCache =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_MISSING_DOM_PROPERTY_CACHE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableStyleWriteShadow =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_STYLE_WRITE_SHADOW"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool s_disableAllocationFreeCanvasCommandWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_DISABLE_V8_ALLOCATION_FREE_CANVAS_COMMAND_WRITES"),
            "1",
            StringComparison.Ordinal);

    private readonly IHtmlMlJavaScriptHost _host;
    private readonly V8Runtime _runtime;
    private readonly V8ScriptEngine _engine;
    private readonly V8ExternalEventListenerAdapter _callbackAdapter;
    private readonly ScriptObject _ownerWindowContext;
    private readonly ScriptObject _ownerWindowDispatch;
    private readonly ScriptObject _ownerWindowRefreshNamedProperties;
    private readonly ScriptObject _requireFrom;
    private readonly ScriptObject _flushCanvases;
    private readonly List<V8FrameBrowsingContext> _frames = new();
    private readonly List<V8CanvasBatchSink> _canvasBatchSinks = new();
    private readonly bool _enableCanvasBatching;
    private readonly bool _enableDomMethodCaching;
    private readonly bool _enableDomTokenListWriteShadow;
    private readonly bool _enableComputedStyleReadCaching;
    private readonly bool _enableTypedComputedStyleAccess;
    private readonly bool _enableTypedInlineStyleWrites;
    private readonly bool _enableCanvasStateDeduplication;
    private readonly bool _enableNativeResizeObserverNotifications;
    private readonly bool _enableTrustedSameOriginContextSharing;
    private readonly ClearScriptV8SharedCache? _sharedCache;
    private int _frameSequence;
    private bool _disposed;

    public ClearScriptV8Runtime(
        IHtmlMlJavaScriptHost host,
        ClearScriptV8RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new ClearScriptV8RuntimeOptions();
        EnsureExternalRuntimeSlotsAreAvailable(host);
        _host = host;
        _enableCanvasBatching = options.EnableCanvasBatching;
        _enableDomMethodCaching = options.EnableDomMethodCaching;
        _enableDomTokenListWriteShadow = options.EnableDomTokenListWriteShadow;
        _enableComputedStyleReadCaching = options.EnableComputedStyleReadCaching;
        _enableTypedComputedStyleAccess = options.EnableTypedComputedStyleAccess;
        _enableTypedInlineStyleWrites = options.EnableTypedInlineStyleWrites;
        _enableCanvasStateDeduplication = options.EnableCanvasStateDeduplication;
        _enableNativeResizeObserverNotifications = options.EnableNativeResizeObserverNotifications;
        _enableTrustedSameOriginContextSharing = options.EnableTrustedSameOriginContextSharing;
        _sharedCache = options.SharedCache;
        EnsureNativeLibraryResolver();
        _runtime = new V8Runtime();
        EnsureTypedManagedAbiRegistered();
        _engine = _runtime.CreateScriptEngine(
            "htmlml-owner",
            V8ScriptEngineFlags.AddPerformanceObject);
        try
        {
            if (_enableTrustedSameOriginContextSharing)
            {
                VerifyTrustedSameOriginContextSharing(_runtime, _engine);
            }

            _callbackAdapter = CreateCallbackAdapter(_engine, "owner");

            host.ExternalCallbackAdapter = _callbackAdapter;
            if (_enableTrustedSameOriginContextSharing)
            {
                host.ExternalVirtualBrowsingContextFactory = this;
            }
            host.Document.ExternalEventListenerAdapter = _callbackAdapter;

            InitializeEngine(
                _engine,
                _callbackAdapter,
                _enableDomMethodCaching,
                _enableDomTokenListWriteShadow,
                _enableComputedStyleReadCaching,
                _enableTypedComputedStyleAccess,
                _enableTypedInlineStyleWrites,
                _enableCanvasStateDeduplication);
            _requireFrom = (ScriptObject)_engine.Script.__htmlMlRequireFrom;
            _flushCanvases = (ScriptObject)_engine.Script.__htmlMlFlushCanvases;
            _ownerWindowContext = (ScriptObject)_engine.Evaluate("globalThis");
            _ownerWindowDispatch = (ScriptObject)_ownerWindowContext.GetProperty("dispatchEvent");
            _ownerWindowRefreshNamedProperties =
                (ScriptObject)_ownerWindowContext.GetProperty("__htmlMlRefreshWindowNamedProperties");
            host.Document.ExternalWindowContext = _ownerWindowContext;
            host.Document.ExternalWindowEventDispatcher = this;
        }
        catch
        {
            DetachFromHost();
            TryDispose(_engine);
            TryDispose(_runtime);
            throw;
        }
    }

    private static void EnsureNativeLibraryResolver()
    {
        lock (s_nativeLibraryResolverLock)
        {
            if (s_nativeLibraryResolverInstalled)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(
                typeof(V8ScriptEngine).Assembly,
                ResolveClearScriptNativeLibrary);
            s_nativeLibraryResolverInstalled = true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate double HtmlMlReadDomNumericPropertyCallback(long nodeId, int property);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate sbyte HtmlMlWriteDomRectCallback(long nodeId, int kind, IntPtr values);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate sbyte HtmlMlRegisterTypedManagedAbiCallback(
        int version,
        IntPtr readDomNumericProperty,
        IntPtr writeDomRect);

    private static void EnsureTypedManagedAbiRegistered()
    {
        if (!s_enableTypedManagedAbi || s_typedManagedAbiRegistered)
        {
            return;
        }

        lock (s_typedManagedAbiLock)
        {
            if (s_typedManagedAbiRegistered)
            {
                return;
            }
            if (s_configuredNativeLibraryHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The typed managed ABI requires an explicitly resolved HtmlML ClearScript native library.");
            }
            if (!NativeLibrary.TryGetExport(
                    s_configuredNativeLibraryHandle,
                    "HtmlMlTypedManagedAbi_Register",
                    out var registerAddress))
            {
                throw new InvalidOperationException(
                    "The configured ClearScript native library does not expose HtmlMlTypedManagedAbi_Register.");
            }

            var register = Marshal.GetDelegateForFunctionPointer<HtmlMlRegisterTypedManagedAbiCallback>(
                registerAddress);
            var callbackAddress = Marshal.GetFunctionPointerForDelegate(s_typedManagedAbiCallback);
            var rectCallbackAddress = Marshal.GetFunctionPointerForDelegate(s_typedManagedAbiRectCallback);
            if (register(1, callbackAddress, rectCallbackAddress) == 0)
            {
                throw new InvalidOperationException("The ClearScript native library rejected HtmlML typed ABI version 1.");
            }

            s_typedManagedAbiRegistered = true;
            Console.Error.WriteLine(
                "[HtmlML V8 native] enabled typed managed ABI v1 " +
                "(numeric DOM reads and client rectangles).");
        }
    }

    private static long RegisterTypedManagedAbiTarget(object element)
    {
        if (!s_typedManagedAbiRegistered
            || element is not IHtmlMlDomIdentityTarget identity
            || (element is not IHtmlMlDomNumericTarget
                && element is not IHtmlMlDomRectTarget
                && element is not IHtmlMlDomClientRectsTarget))
        {
            return 0;
        }

        s_typedManagedAbiTargets[identity.HtmlMlDomIdentity] = new WeakReference<object>(element);
        Interlocked.Increment(ref s_typedManagedAbiRegistrations);
        return identity.HtmlMlDomIdentity;
    }

    private static double ReadDomNumericPropertyFromNative(long nodeId, int property)
    {
        Interlocked.Increment(ref s_typedManagedAbiCalls);
        try
        {
            if (s_typedManagedAbiTargets.TryGetValue(nodeId, out var reference)
                && reference.TryGetTarget(out var value)
                && value is IHtmlMlDomNumericTarget target)
            {
                return target.ReadDomNumericProperty((HtmlMlDomNumericProperty)property);
            }

            s_typedManagedAbiTargets.TryRemove(nodeId, out _);
            Interlocked.Increment(ref s_typedManagedAbiMisses);
            return double.NaN;
        }
        catch
        {
            Interlocked.Increment(ref s_typedManagedAbiErrors);
            return double.NaN;
        }
    }

    private static unsafe sbyte WriteDomRectFromNative(long nodeId, int kind, IntPtr values)
    {
        Interlocked.Increment(ref s_typedManagedAbiCalls);
        Interlocked.Increment(ref s_typedManagedAbiRectCalls);
        try
        {
            if (values == IntPtr.Zero
                || !s_typedManagedAbiTargets.TryGetValue(nodeId, out var reference)
                || !reference.TryGetTarget(out var value))
            {
                s_typedManagedAbiTargets.TryRemove(nodeId, out _);
                Interlocked.Increment(ref s_typedManagedAbiMisses);
                return 0;
            }

            HtmlMlRect rect;
            if (kind == 0 && value is IHtmlMlDomRectTarget boundingTarget)
            {
                rect = boundingTarget.ReadBoundingClientRect();
            }
            else if (kind == 1
                     && value is IHtmlMlDomClientRectsTarget clientTarget
                     && clientTarget.TryReadClientRect(out rect))
            {
            }
            else
            {
                return 0;
            }

            var destination = new Span<double>(values.ToPointer(), 8);
            destination[0] = rect.X;
            destination[1] = rect.Y;
            destination[2] = rect.Width;
            destination[3] = rect.Height;
            destination[4] = rect.Left;
            destination[5] = rect.Top;
            destination[6] = rect.Right;
            destination[7] = rect.Bottom;
            return 1;
        }
        catch
        {
            Interlocked.Increment(ref s_typedManagedAbiErrors);
            return 0;
        }
    }

    private static IntPtr ResolveClearScriptNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("ClearScriptV8", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string? configuredPath;
        string? configuredRid;
        lock (s_nativeLibraryResolverLock)
        {
            configuredPath = s_configuredNativeLibraryPath;
            configuredRid = s_configuredNativeLibraryRid;
        }
        configuredPath ??= Environment.GetEnvironmentVariable("HTMLML_CLEARSCRIPT_NATIVE");
        configuredRid ??= Environment.GetEnvironmentVariable("HTMLML_CLEARSCRIPT_RID");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredRid = RuntimeInformation.RuntimeIdentifier;
            var bundledFileName = GetClearScriptNativeFileName(configuredRid);
            configuredPath = bundledFileName is null
                ? null
                : Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    configuredRid,
                    "native",
                    bundledFileName);

            if (string.IsNullOrWhiteSpace(configuredPath) || !File.Exists(configuredPath))
            {
                Console.Error.WriteLine(
                    $"[HtmlML V8 native] requested '{libraryName}' by '{assembly.GetName().Name}'; " +
                    $"no bundled reviewed native asset was found for RID '{configuredRid}', " +
                    "using the platform resolver.");
                return IntPtr.Zero;
            }
        }

        var fullPath = Path.GetFullPath(configuredPath);
        configuredRid ??= "<unset>";
        lock (s_nativeLibraryResolverLock)
        {
            if (s_configuredNativeLibraryHandle != IntPtr.Zero)
            {
                return s_configuredNativeLibraryHandle;
            }

            Console.Error.WriteLine(
                $"[HtmlML V8 native] requested '{libraryName}' by '{assembly.GetName().Name}'; " +
                $"RID='{configuredRid}', path='{fullPath}', exists={File.Exists(fullPath)}, " +
                $"appBase='{AppContext.BaseDirectory}'.");
            try
            {
                s_configuredNativeLibraryHandle = NativeLibrary.Load(fullPath);
                Console.Error.WriteLine(
                    $"[HtmlML V8 native] loaded '{fullPath}' " +
                    $"(handle=0x{s_configuredNativeLibraryHandle.ToInt64():x}).");
                return s_configuredNativeLibraryHandle;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[HtmlML V8 native] failed to load '{fullPath}': {ex}");
                throw;
            }
        }
    }

    private static string? GetClearScriptNativeFileName(string rid)
    {
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            return $"ClearScriptV8.{rid}.dll";
        }

        if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            return $"ClearScriptV8.{rid}.so";
        }

        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            return $"ClearScriptV8.{rid}.dylib";
        }

        return null;
    }

    public V8ScriptEngine Engine => _engine;

    /// <summary>
    /// Selects the reviewed native ClearScript library before the first V8
    /// runtime is created. This avoids process-environment mutation in reusable
    /// controls while retaining one native V8 implementation per process.
    /// </summary>
    public static void ConfigureNativeLibrary(string path, string? runtimeIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The configured HtmlML ClearScript V8 native library was not found.",
                fullPath);
        }
        lock (s_nativeLibraryResolverLock)
        {
            if (s_configuredNativeLibraryHandle != IntPtr.Zero
                && !string.Equals(
                    s_configuredNativeLibraryPath,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ClearScript V8 is already loaded from a different native library.");
            }
            s_configuredNativeLibraryPath = fullPath;
            s_configuredNativeLibraryRid = runtimeIdentifier
                ?? RuntimeInformation.RuntimeIdentifier;
        }
    }

    public int ActiveFrameCount => _frames.Count;

    public V8SharedCacheMetrics SharedCacheMetrics
        => _sharedCache?.GetMetrics() ?? default;

    public object Window
        => _ownerWindowContext;

    public V8TypedManagedAbiMetrics GetTypedManagedAbiMetrics()
        => new(
            s_typedManagedAbiRegistered,
            Interlocked.Read(ref s_typedManagedAbiRegistrations),
            Interlocked.Read(ref s_typedManagedAbiCalls),
            Interlocked.Read(ref s_typedManagedAbiRectCalls),
            Interlocked.Read(ref s_typedManagedAbiMisses),
            Interlocked.Read(ref s_typedManagedAbiErrors));

    public void ResetTypedManagedAbiMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref s_typedManagedAbiRegistrations, 0);
        Interlocked.Exchange(ref s_typedManagedAbiCalls, 0);
        Interlocked.Exchange(ref s_typedManagedAbiRectCalls, 0);
        Interlocked.Exchange(ref s_typedManagedAbiMisses, 0);
        Interlocked.Exchange(ref s_typedManagedAbiErrors, 0);
    }

    public string RunTypedManagedAbiMicroprobe(int iterations = 200_000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!s_typedManagedAbiRegistered)
        {
            return "{\"enabled\":false}";
        }
        if (iterations < 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        return Convert.ToString(_engine.Evaluate($$"""
            JSON.stringify((function() {
              const target = document.getElementById('chart_container') || document.documentElement;
              const raw = target && target.__htmlMlRawHostObject;
              const handle = raw ? Number(__htmlMlRegisterTypedManagedAbiTarget(raw)) || 0 : 0;
              if (!raw || handle <= 0 ||
                  typeof __htmlMlNativeReadDomNumericProperty !== 'function' ||
                  typeof __htmlMlNativeWriteDomRect !== 'function') {
                return { enabled: true, available: false };
              }
              const blocks = 8;
              const perBlock = Math.max(1, Math.floor({{iterations}} / blocks));
              const generic = [];
              const native = [];
              const genericRect = [];
              const nativeRect = [];
              let checksum = 0;
              function measure(callback) {
                const started = performance.now();
                for (let index = 0; index < perBlock; index++) checksum += callback();
                return performance.now() - started;
              }
              const genericRead = function() { return __htmlMlReadDomNumericProperty(raw, 2); };
              const nativeRead = function() { return __htmlMlNativeReadDomNumericProperty(handle, 2); };
              const genericRectValues = new Float64Array(8);
              const nativeRectValues = new Float64Array(8);
              const genericRectRead = function() {
                __htmlMlWriteBoundingClientRect(raw, genericRectValues);
                return genericRectValues[2];
              };
              const nativeRectRead = function() {
                __htmlMlNativeWriteDomRect(handle, 0, nativeRectValues);
                return nativeRectValues[2];
              };
              for (let warmup = 0; warmup < 1000; warmup++) {
                checksum += genericRead() + nativeRead() + genericRectRead() + nativeRectRead();
              }
              for (let block = 0; block < blocks; block++) {
                if ((block & 1) === 0) {
                  generic.push(measure(genericRead));
                  native.push(measure(nativeRead));
                  genericRect.push(measure(genericRectRead));
                  nativeRect.push(measure(nativeRectRead));
                } else {
                  native.push(measure(nativeRead));
                  generic.push(measure(genericRead));
                  nativeRect.push(measure(nativeRectRead));
                  genericRect.push(measure(genericRectRead));
                }
              }
              const total = function(values) { return values.reduce(function(sum, value) { return sum + value; }, 0); };
              const genericMs = total(generic);
              const nativeMs = total(native);
              const genericRectMs = total(genericRect);
              const nativeRectMs = total(nativeRect);
              const calls = perBlock * blocks;
              return {
                enabled: true,
                available: true,
                calls: calls,
                genericNsPerCall: genericMs * 1000000 / calls,
                nativeNsPerCall: nativeMs * 1000000 / calls,
                speedup: genericMs / nativeMs,
                genericRectNsPerCall: genericRectMs * 1000000 / calls,
                nativeRectNsPerCall: nativeRectMs * 1000000 / calls,
                rectSpeedup: genericRectMs / nativeRectMs,
                checksum: checksum
              };
            })())
            """)) ?? "{}";
    }

    /// <summary>
    /// Generates or loads reusable V8 code-cache units on a thread-pool thread.
    /// The returned bytes are runtime-neutral cache input; JavaScript execution and
    /// every Avalonia DOM mutation still occur through the owning runtime/UI thread.
    /// Concurrent callers are single-flight per compilation key.
    /// </summary>
    public static Task<V8PrecompileResult> PrecompileAsync(
        ClearScriptV8SharedCache cache,
        IEnumerable<V8CompilationSource> sources,
        bool includeRuntimeBootstrap = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sources);
        EnsureNativeLibraryResolver();

        var requested = sources.ToList();
        if (includeRuntimeBootstrap)
        {
            requested.Insert(
                0,
                new V8CompilationSource(
                    "htmlml-browser-runtime.js",
                    BrowserRuntimeSetup));
        }
        foreach (var source in requested)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.DocumentName);
            ArgumentNullException.ThrowIfNull(source.Code);
        }

        return Task.Run(
            () =>
            {
                V8Runtime? compiler = null;
                var compiled = 0;
                var reused = 0;
                try
                {
                    foreach (var source in requested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = cache.CreateCodeKey(source.DocumentName, source.Code);
                        using var keyLease = cache.EnterCodeCompilation(key);
                        if (cache.TryGetCode(key, out _))
                        {
                            reused++;
                            continue;
                        }

                        compiler ??= new V8Runtime("htmlml-background-compiler");
                        using var script = compiler.Compile(
                            source.DocumentName,
                            source.Code,
                            V8CacheKind.Code,
                            out var generatedBytes);
                        cache.RecordCompilationLeader();
                        cache.StoreCode(key, generatedBytes);
                        compiled++;
                    }

                    return new V8PrecompileResult(
                        requested.Count,
                        compiled,
                        reused,
                        Environment.CurrentManagedThreadId,
                        Thread.CurrentThread.IsThreadPoolThread);
                }
                finally
                {
                    compiler?.Dispose();
                }
            },
            cancellationToken);
    }

    public static V8CompilationSource CreateCommonJsCompilationSource(
        string fileName,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        return new V8CompilationSource(
            fileName + "#commonjs",
            "(function(require,exports,module,__filename,__dirname){\n" +
            content + "\n})\n//# sourceURL=" + fileName);
    }

    public void DispatchWindowEvent(string type, object eventObject)
        => _ownerWindowDispatch.InvokeAsFunction(eventObject);

    public void Execute(string code, string? documentName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        using var scope = _host.EnterExternalJavaScriptCall();
        _ownerWindowRefreshNamedProperties.InvokeAsFunction();
        ExecuteCompiled(_engine, code, documentName ?? "htmlml-owner.js");
    }

    /// <summary>
    /// Evaluates control-plane JavaScript in the owner context through the same
    /// compilation-unit cache used by <see cref="Execute(string, string?)"/>.
    /// Chart controllers should schedule this call on the runtime's engine thread.
    /// </summary>
    public object? Evaluate(string code, string? documentName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        using var scope = _host.EnterExternalJavaScriptCall();
        _ownerWindowRefreshNamedProperties.InvokeAsFunction();
        return EvaluateCompiled(_engine, code, documentName ?? "htmlml-owner-eval.js");
    }

    public object? Invoke(object callback, params object?[] arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (callback is not ScriptObject scriptCallback)
        {
            throw new ArgumentException("The callback is not a ClearScript function object.", nameof(callback));
        }

        using var scope = _host.EnterExternalJavaScriptCall();
        return scriptCallback.InvokeAsFunction(arguments);
    }

    public void ProcessPendingTasks()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _flushCanvases.InvokeAsFunction();
    }

    public void ExecuteOwnerScript(string specifier)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var scope = _host.EnterExternalJavaScriptCall();
        _ownerWindowRefreshNamedProperties.InvokeAsFunction();
        _requireFrom.InvokeAsFunction(specifier, null);
    }

    public IExternalVirtualBrowsingContext Create(
        IHtmlMlJavaScriptHost host,
        IHtmlMlJavaScriptDocument frameDocument,
        object frameElement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_enableTrustedSameOriginContextSharing)
        {
            throw new InvalidOperationException(
                "Virtual iframe contexts require explicit trusted same-origin context sharing.");
        }
        var frameSequence = ++_frameSequence;
        var frameEngine = _runtime.CreateScriptEngine(
            $"htmlml-frame-{frameSequence}",
            V8ScriptEngineFlags.AddPerformanceObject);
        V8ExternalEventListenerAdapter? frameCallbackAdapter = null;
        try
        {
            frameCallbackAdapter = CreateCallbackAdapter(frameEngine, $"frame-{frameSequence}");
            frameDocument.ExternalEventListenerAdapter = frameCallbackAdapter;
            InitializeEngine(
                frameEngine,
                frameCallbackAdapter,
                _enableDomMethodCaching,
                _enableDomTokenListWriteShadow,
                _enableComputedStyleReadCaching,
                _enableTypedComputedStyleAccess,
                _enableTypedInlineStyleWrites,
                _enableCanvasStateDeduplication);
            var createFrameRuntime = (ScriptObject)frameEngine.Script.__htmlMlCreateFrameRuntime;
            var state = (ScriptObject)createFrameRuntime.InvokeAsFunction(
                frameDocument.JavaScriptObject,
                frameElement,
                frameDocument.Location,
                _engine.Script);
            var context = new V8FrameBrowsingContext(
                host,
                frameDocument,
                frameElement,
                frameEngine,
                state,
                frameCallbackAdapter,
                ExecuteCompiled,
                RemoveFrame);
            _frames.Add(context);
            return context;
        }
        catch
        {
            if (frameCallbackAdapter is not null
                && ReferenceEquals(frameDocument.ExternalEventListenerAdapter, frameCallbackAdapter))
            {
                frameDocument.ExternalEventListenerAdapter = null;
            }
            frameCallbackAdapter?.Clear();
            TryDispose(frameEngine);
            throw;
        }
    }

    public V8RuntimeHeapInfo GetHeapInfo() => _engine.GetRuntimeHeapInfo();

    public void CollectGarbage(bool exhaustive = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runtime.CollectGarbage(exhaustive);
    }

    public string DescribeDomProxyRetention()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToString(_engine.Evaluate("""
            JSON.stringify((function() {
              const reports = [];
              const collect = function(label, scope) {
                try {
                  if (scope && typeof scope.__htmlMlDescribeDomProxyRetention === 'function') {
                    reports.push({
                      scope: label,
                      retention: scope.__htmlMlDescribeDomProxyRetention()
                    });
                  }
                } catch (_) {}
              };
              collect('owner', globalThis);
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame, index) {
                  collect('frame-' + (index + 1), frame && frame.contentWindow);
                });
              } catch (_) {}
              return reports;
            })())
            """)) ?? "[]";
    }

    public bool BeginCpuProfile(string name)
        => _runtime.BeginCpuProfile(name, V8CpuProfileFlags.EnableSampleCollection);

    public V8CpuProfile? EndCpuProfile(string name)
        => _runtime.EndCpuProfile(name);

    public V8CanvasBatchingMetrics GetCanvasBatchingMetrics()
        => new(
            _canvasBatchSinks.Count,
            _canvasBatchSinks.Sum(item => item.FlushCount),
            _canvasBatchSinks.Sum(item => item.OperationCount),
            _canvasBatchSinks.Sum(item => item.ValueCount),
            _canvasBatchSinks.Sum(item => item.ReplayTicks),
            _canvasBatchSinks.Sum(item => item.ReplayAllocatedBytes));

    public string DescribeCanvasBatching()
    {
        var metrics = GetCanvasBatchingMetrics();
        return $"contexts={metrics.Contexts}, flushes={metrics.Flushes}, " +
               $"operations={metrics.Operations}, values={metrics.Values}, " +
               $"managed-replay={metrics.ReplayTicks * 1000d / Stopwatch.Frequency:F1} ms/" +
               $"{metrics.ReplayAllocatedBytes / (1024d * 1024d):F2} MB";
    }

    public void ResetDomPropertyAccessMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.Execute("""
            (function() {
              const scopes = [globalThis];
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame) {
                  if (frame && frame.contentWindow) scopes.push(frame.contentWindow);
                });
              } catch (_) {}
              scopes.forEach(function(scope) {
                try {
                  if (scope && typeof scope.__htmlMlResetDomPropertyAccessMetrics === 'function') {
                    scope.__htmlMlResetDomPropertyAccessMetrics();
                  }
                } catch (_) {}
              });
            })();
            """);
    }

    public string DescribeDomPropertyAccessMetrics(int limit = 40)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        limit = Math.Clamp(limit, 1, 500);
        return Convert.ToString(_engine.Evaluate($$"""
            JSON.stringify((function() {
              const reports = [];
              const collect = function(label, scope) {
                try {
                  if (scope && typeof scope.__htmlMlDescribeDomPropertyAccessMetrics === 'function') {
                    reports.push({
                      scope: label,
                      properties: scope.__htmlMlDescribeDomPropertyAccessMetrics({{limit}})
                    });
                  }
                } catch (_) {}
              };
              collect('owner', globalThis);
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame, index) {
                  collect('frame-' + (index + 1), frame && frame.contentWindow);
                });
              } catch (_) {}
              return reports;
            })())
            """)) ?? "[]";
    }

    public void ResetComputedStyleReadCacheMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.Execute("""
            (function() {
              const scopes = [globalThis];
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame) {
                  if (frame && frame.contentWindow) scopes.push(frame.contentWindow);
                });
              } catch (_) {}
              scopes.forEach(function(scope) {
                try {
                  if (scope && typeof scope.__htmlMlResetComputedStyleReadCacheMetrics === 'function') {
                    scope.__htmlMlResetComputedStyleReadCacheMetrics();
                  }
                } catch (_) {}
              });
            })();
            """);
    }

    public string DescribeComputedStyleReadCacheMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToString(_engine.Evaluate("""
            JSON.stringify((function() {
              const reports = [];
              const collect = function(label, scope) {
                try {
                  if (scope && typeof scope.__htmlMlDescribeComputedStyleReadCacheMetrics === 'function') {
                    reports.push({
                      scope: label,
                      metrics: scope.__htmlMlDescribeComputedStyleReadCacheMetrics()
                    });
                  }
                } catch (_) {}
              };
              collect('owner', globalThis);
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame, index) {
                  collect('frame-' + (index + 1), frame && frame.contentWindow);
                });
              } catch (_) {}
              return reports;
            })())
            """)) ?? "[]";
    }

    public void ResetDomTokenListWriteShadowMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.Execute("""
            (function() {
              const scopes = [globalThis];
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame) {
                  if (frame && frame.contentWindow) scopes.push(frame.contentWindow);
                });
              } catch (_) {}
              scopes.forEach(function(scope) {
                try {
                  if (scope && typeof scope.__htmlMlResetDomTokenListWriteShadowMetrics === 'function') {
                    scope.__htmlMlResetDomTokenListWriteShadowMetrics();
                  }
                } catch (_) {}
              });
            })();
            """);
    }

    public string DescribeDomTokenListWriteShadowMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToString(_engine.Evaluate("""
            JSON.stringify((function() {
              const reports = [];
              const collect = function(label, scope) {
                try {
                  if (scope && typeof scope.__htmlMlDescribeDomTokenListWriteShadowMetrics === 'function') {
                    reports.push({
                      scope: label,
                      metrics: scope.__htmlMlDescribeDomTokenListWriteShadowMetrics()
                    });
                  }
                } catch (_) {}
              };
              collect('owner', globalThis);
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame, index) {
                  collect('frame-' + (index + 1), frame && frame.contentWindow);
                });
              } catch (_) {}
              return reports;
            })())
            """)) ?? "[]";
    }

    public void ResetTypedInlineStyleWriteMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.Execute("""
            (function() {
              const scopes = [globalThis];
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame) {
                  if (frame && frame.contentWindow) scopes.push(frame.contentWindow);
                });
              } catch (_) {}
              scopes.forEach(function(scope) {
                try {
                  if (scope && typeof scope.__htmlMlResetTypedInlineStyleWriteMetrics === 'function') {
                    scope.__htmlMlResetTypedInlineStyleWriteMetrics();
                  }
                } catch (_) {}
              });
            })();
            """);
    }

    public string DescribeTypedInlineStyleWriteMetrics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToString(_engine.Evaluate("""
            JSON.stringify((function() {
              const reports = [];
              const collect = function(label, scope) {
                try {
                  if (scope && typeof scope.__htmlMlDescribeTypedInlineStyleWriteMetrics === 'function') {
                    reports.push({
                      scope: label,
                      metrics: scope.__htmlMlDescribeTypedInlineStyleWriteMetrics()
                    });
                  }
                } catch (_) {}
              };
              collect('owner', globalThis);
              try {
                Array.from(document.querySelectorAll('iframe')).forEach(function(frame, index) {
                  collect('frame-' + (index + 1), frame && frame.contentWindow);
                });
              } catch (_) {}
              return reports;
            })())
            """)) ?? "[]";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var frame in _frames.ToArray())
        {
            frame.Dispose();
        }
        _frames.Clear();
        TryInvoke(_flushCanvases);
        _canvasBatchSinks.Clear();
        TryInvoke(_engine.Script.__htmlMlDisposeBrowsingContext as ScriptObject);
        DetachFromHost();
        _callbackAdapter.Clear();
        TryDispose(_engine);
        TryDispose(_runtime);
    }

    private static void TryInvoke(ScriptObject? callback)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback.InvokeAsFunction();
        }
        catch (Exception)
        {
            // Runtime teardown is best-effort. A disposed script object or a
            // user-defined observer/canvas callback must not prevent the host
            // slots, remaining frame contexts, and native V8 runtime from
            // being released.
        }
    }

    private void RemoveFrame(V8FrameBrowsingContext frame)
    {
        _frames.Remove(frame);
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception)
        {
            // Continue releasing the remaining runtime graph. Disposal is an
            // idempotent lifecycle boundary and cannot be retried safely after
            // a partially torn-down native context.
        }
    }

    private void DetachFromHost()
    {
        if (ReferenceEquals(_host.ExternalVirtualBrowsingContextFactory, this))
        {
            _host.ExternalVirtualBrowsingContextFactory = null;
        }
        if (_callbackAdapter is not null
            && ReferenceEquals(_host.ExternalCallbackAdapter, _callbackAdapter))
        {
            _host.ExternalCallbackAdapter = null;
        }
        if (_callbackAdapter is not null
            && ReferenceEquals(_host.Document.ExternalEventListenerAdapter, _callbackAdapter))
        {
            _host.Document.ExternalEventListenerAdapter = null;
        }
        if (_ownerWindowContext is not null
            && ReferenceEquals(_host.Document.ExternalWindowContext, _ownerWindowContext))
        {
            _host.Document.ExternalWindowContext = null;
        }
        if (ReferenceEquals(_host.Document.ExternalWindowEventDispatcher, this))
        {
            _host.Document.ExternalWindowEventDispatcher = null;
        }
    }

    private static void EnsureExternalRuntimeSlotsAreAvailable(IHtmlMlJavaScriptHost host)
    {
        if (host.ExternalCallbackAdapter is not null
            || host.ExternalVirtualBrowsingContextFactory is not null
            || host.Document.ExternalEventListenerAdapter is not null
            || host.Document.ExternalWindowContext is not null
            || host.Document.ExternalWindowEventDispatcher is not null)
        {
            throw new InvalidOperationException(
                "The HtmlML host is already attached to an external JavaScript runtime.");
        }
    }

    private static void VerifyTrustedSameOriginContextSharing(
        V8Runtime runtime,
        V8ScriptEngine ownerEngine)
    {
        using var frameProbe = runtime.CreateScriptEngine("htmlml-context-sharing-probe");
        ownerEngine.Execute(
            "globalThis.__htmlMlContextSharingProbe = Object.freeze({ value: 41, add: function(value) { return this.value + value; } });");

        try
        {
            frameProbe.Script.ownerWindow = ownerEngine.Script;
            var result = frameProbe.Evaluate(
                "ownerWindow.__htmlMlContextSharingProbe.add(1)");
            if (Convert.ToInt32(result) != 42)
            {
                throw new InvalidOperationException(
                    "The V8 owner/iframe context-sharing probe returned an unexpected result.");
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Trusted same-origin V8 iframe sharing is unavailable. " +
                "HtmlML currently requires the reviewed patched ClearScript native library; " +
                "stock ClearScript blocks owner/iframe objects. See third-party/clearscript-patches/README.md.",
                exception);
        }
        finally
        {
            ownerEngine.Execute("delete globalThis.__htmlMlContextSharingProbe;");
        }
    }

    private void InitializeEngine(
        V8ScriptEngine engine,
        V8ExternalEventListenerAdapter callbackAdapter,
        bool enableDomMethodCaching,
        bool enableDomTokenListWriteShadow,
        bool enableComputedStyleReadCaching,
        bool enableTypedComputedStyleAccess,
        bool enableTypedInlineStyleWrites,
        bool enableCanvasStateDeduplication)
    {
        engine.AddHostObject("__htmlMlOwnerDocument", _host.Document.JavaScriptObject);
        engine.AddHostObject("__htmlMlWindowBackend", _host.BrowserWindow);
        engine.AddHostObject("__htmlMlModuleBackend", new ModuleBackend(_host, _sharedCache));
        engine.AddHostObject(
            "__htmlMlCompileModuleFactory",
            new Func<string, string, object?>((code, documentName) =>
                EvaluateCompiled(engine, code, documentName)));
        engine.AddHostObject("__htmlMlDomParserBackend", new DomParserBackend());
        engine.AddHostType("__htmlMlUrlBackend", _host.UrlBackendType);
        engine.AddHostObject("__htmlMlBase64Backend", new Base64Backend());
        engine.AddHostObject(
            "__htmlMlGetComputedStyleValue",
            new Func<object?, string, string>((style, propertyName) =>
                style is IHtmlMlComputedStyleTarget computed
                    ? computed.GetPropertyValue(propertyName)
                    : string.Empty));
        engine.AddHostObject(
            "__htmlMlGetComputedStyleLength",
            new Func<object?, int>(style =>
                style is IHtmlMlComputedStyleTarget computed ? computed.Length : 0));
        engine.AddHostObject(
            "__htmlMlGetComputedStyleItem",
            new Func<object?, int, string>((style, index) =>
                style is IHtmlMlComputedStyleTarget computed
                    ? computed.Item(index)
                    : string.Empty));
        engine.AddHostObject(
            "__htmlMlTrySetInlineStyleProperty",
            new Func<object?, string, string, bool>((style, propertyName, value) =>
            {
                if (style is not IHtmlMlCssStyleDeclarationTarget target)
                {
                    return false;
                }

                target.SetProperty(propertyName, value);
                return true;
            }));
        engine.AddHostObject("__htmlMlNow", new Func<double>(_host.GetPerformanceTimestamp));
        engine.AddHostObject(
            "__htmlMlRegisterTypedManagedAbiTarget",
            new Func<object, long>(RegisterTypedManagedAbiTarget));
        engine.Script.__htmlMlEnableCanvasBatching = _enableCanvasBatching;
        engine.Script.__htmlMlEnableDomMethodCaching = enableDomMethodCaching;
        engine.Script.__htmlMlEnableDomTokenListWriteShadow = enableDomTokenListWriteShadow;
        engine.Script.__htmlMlEnableComputedStyleReadCaching = enableComputedStyleReadCaching;
        engine.Script.__htmlMlEnableTypedComputedStyleAccess = enableTypedComputedStyleAccess;
        engine.Script.__htmlMlEnableTypedInlineStyleWrites = enableTypedInlineStyleWrites;
        engine.Script.__htmlMlEnableFastMethodCacheLookup = !s_disableFastMethodCacheLookup;
        engine.Script.__htmlMlEnableCanvasFacade = !s_disableCanvasFacade;
        engine.Script.__htmlMlEnableTypedDomRect = !s_disableTypedDomRect;
        engine.Script.__htmlMlEnableTypedDomClientRects = !s_disableTypedDomClientRects;
        engine.Script.__htmlMlEnableTypedDomNumericProperties = !s_disableTypedDomNumericProperties;
        engine.Script.__htmlMlEnableTypedManagedAbi = s_typedManagedAbiRegistered;
        engine.Script.__htmlMlEnablePrimitiveTextMetrics = !s_disablePrimitiveTextMetrics;
        engine.Script.__htmlMlEnableResultClassificationCache = !s_disableResultClassificationCache;
        engine.Script.__htmlMlTraceDomPropertyAccess = s_traceDomPropertyAccess;
        engine.Script.__htmlMlEnableStableDomPropertyCache = !s_disableStableDomPropertyCache;
        engine.Script.__htmlMlEnableMissingDomPropertyCache = !s_disableMissingDomPropertyCache;
        engine.Script.__htmlMlEnableStyleWriteShadow =
            !s_disableStyleWriteShadow && !s_disableStableDomPropertyCache;
        engine.Script.__htmlMlEnableAllocationFreeCanvasCommandWrites =
            !s_disableAllocationFreeCanvasCommandWrites;
        engine.Script.__htmlMlTraceV8CallbackErrors = string.Equals(
            Environment.GetEnvironmentVariable("HTMLML_TRACE_V8_CALLBACK_ERRORS"),
            "1",
            StringComparison.Ordinal);
        engine.Script.__htmlMlEnableCanvasStateDeduplication = enableCanvasStateDeduplication;
        engine.Script.__htmlMlEnableNativeResizeObserverNotifications =
            _enableNativeResizeObserverNotifications;
        engine.AddHostObject("__htmlMlCallbackAdapter", callbackAdapter);
        engine.AddHostObject(
            "__htmlMlCreatePath2DBackend",
            new Func<object?, object>(_host.CreateCanvasPath));
        engine.AddHostObject(
            "__htmlMlCreateCanvasBatchSink",
            new Func<object, V8CanvasBatchSink>(CreateCanvasBatchSink));
        engine.AddHostObject(
            "__htmlMlWriteBoundingClientRect",
            new Action<object, ITypedArray<double>>(WriteBoundingClientRect));
        engine.AddHostObject(
            "__htmlMlWriteClientRect",
            new Func<object, ITypedArray<double>, bool>(WriteClientRect));
        engine.AddHostObject(
            "__htmlMlReadDomNumericProperty",
            new Func<object, int, double>(ReadDomNumericProperty));
        engine.AddHostObject(
            "__htmlMlMeasureTextWidth",
            new Func<object, string, double>(MeasureTextWidth));
        ExecuteCompiled(engine, BrowserRuntimeSetup, "htmlml-browser-runtime.js");
    }

    private void ExecuteCompiled(V8ScriptEngine engine, string code, string documentName)
    {
        using var script = Compile(code, documentName);
        engine.Execute(script);
    }

    private object? EvaluateCompiled(V8ScriptEngine engine, string code, string documentName)
    {
        using var script = Compile(code, documentName);
        return engine.Evaluate(script);
    }

    private V8Script Compile(string code, string documentName)
    {
        if (_sharedCache is null)
        {
            return _runtime.Compile(documentName, code);
        }

        var key = _sharedCache.CreateCodeKey(documentName, code);
        using var keyLease = _sharedCache.EnterCodeCompilation(key);
        if (!_sharedCache.TryGetCode(key, out var cachedBytes))
        {
            var script = _runtime.Compile(
                documentName,
                code,
                V8CacheKind.Code,
                out var generatedBytes);
            _sharedCache.RecordCompilationLeader();
            _sharedCache.StoreCode(key, generatedBytes);
            return script;
        }

        var workingBytes = cachedBytes.ToArray();
        var compiled = _runtime.Compile(
            documentName,
            code,
            V8CacheKind.Code,
            ref workingBytes,
            out var result);
        _sharedCache.RecordCodeResult(result);
        if (result == V8CacheResult.Updated)
        {
            _sharedCache.StoreCode(key, workingBytes);
        }
        return compiled;
    }

    private static double MeasureTextWidth(object context, string text)
    {
        if (context is not IHtmlMlCanvasTextTarget target)
        {
            throw new ArgumentException(
                "The text measurement target is not a Canvas2D context.",
                nameof(context));
        }

        return target.MeasureTextWidth(text);
    }

    private static void WriteBoundingClientRect(
        object element,
        ITypedArray<double> destination)
    {
        if (element is not IHtmlMlDomRectTarget target)
        {
            throw new ArgumentException(
                "The bounding rectangle target is not a DOM element.",
                nameof(element));
        }
        if (destination.Length < 8)
        {
            throw new ArgumentException("The bounding rectangle destination is too small.", nameof(destination));
        }

        var rect = target.ReadBoundingClientRect();
        ReadOnlySpan<double> values =
        [
            rect.X, rect.Y, rect.Width, rect.Height,
            rect.Left, rect.Top, rect.Right, rect.Bottom
        ];
        destination.Write(values, 0, (ulong)values.Length, 0);
    }

    private static bool WriteClientRect(
        object element,
        ITypedArray<double> destination)
    {
        if (element is not IHtmlMlDomClientRectsTarget target)
        {
            return false;
        }
        if (destination.Length < 8)
        {
            throw new ArgumentException("The client rectangle destination is too small.", nameof(destination));
        }
        if (!target.TryReadClientRect(out var rect))
        {
            return false;
        }

        ReadOnlySpan<double> values =
        [
            rect.X, rect.Y, rect.Width, rect.Height,
            rect.Left, rect.Top, rect.Right, rect.Bottom
        ];
        destination.Write(values, 0, (ulong)values.Length, 0);
        return true;
    }

    private static double ReadDomNumericProperty(object element, int property)
        => element is IHtmlMlDomNumericTarget target
            ? target.ReadDomNumericProperty((HtmlMlDomNumericProperty)property)
            : double.NaN;

    private V8CanvasBatchSink CreateCanvasBatchSink(object context)
    {
        var sink = new V8CanvasBatchSink(context);
        _canvasBatchSinks.Add(sink);
        return sink;
    }

    private static V8ExternalEventListenerAdapter CreateCallbackAdapter(
        V8ScriptEngine engine,
        string scopeName)
    {
        var eventApply = (ScriptObject)engine.Evaluate(
            "(function(callback, currentTarget, event) { currentTarget = __htmlMlWrapHostObject(currentTarget); event = __htmlMlWrapHostObject(event); try { return callback.call(currentTarget, event); } finally { if (typeof __htmlMlFlushCanvases === 'function') __htmlMlFlushCanvases(); } })");
        var eventBatchApply = (ScriptObject)engine.Evaluate(
            "(function(callbacks, currentTarget, event, control) { currentTarget = __htmlMlWrapHostObject(currentTarget); event = __htmlMlWrapHostObject(event); try { callbacks = Array.from(callbacks); for (let index = 0; index < callbacks.length; index++) { control.BeforeInvoke(index); try { callbacks[index].call(currentTarget, event); } catch (error) { control.ReportError(index, String(error && error.stack || error)); } finally { control.AfterInvoke(index); } if (control.ShouldStop) break; } } finally { if (typeof __htmlMlFlushCanvases === 'function') __htmlMlFlushCanvases(); } })");
        var apply = (ScriptObject)engine.Evaluate(
            "(function(callback, currentTarget, args) { try { return callback.apply(currentTarget, Array.from(args)); } finally { if (typeof __htmlMlFlushCanvases === 'function') __htmlMlFlushCanvases(); } })");
        return new V8ExternalEventListenerAdapter(eventApply, eventBatchApply, apply, scopeName);
    }

    private sealed class V8FrameBrowsingContext : IExternalVirtualBrowsingContext
    {
        private IHtmlMlJavaScriptHost? _host;
        private IHtmlMlJavaScriptDocument? _document;
        private object? _frameElement;
        private V8ExternalEventListenerAdapter? _callbackAdapter;
        private object? _window;
        private V8ScriptEngine? _engine;
        private ScriptObject? _state;
        private ScriptObject? _dispatch;
        private ScriptObject? _refreshNamedProperties;
        private ScriptObject? _describe;
        private ScriptObject? _flushCanvases;
        private ScriptObject? _disposeBrowsingContext;
        private Action<V8ScriptEngine, string, string>? _executeCompiled;
        private Action<V8FrameBrowsingContext>? _onDisposed;

        internal V8FrameBrowsingContext(
            IHtmlMlJavaScriptHost host,
            IHtmlMlJavaScriptDocument document,
            object frameElement,
            V8ScriptEngine engine,
            ScriptObject state,
            V8ExternalEventListenerAdapter callbackAdapter,
            Action<V8ScriptEngine, string, string> executeCompiled,
            Action<V8FrameBrowsingContext> onDisposed)
        {
            _host = host;
            _document = document;
            _frameElement = frameElement;
            _engine = engine;
            _state = state;
            _callbackAdapter = callbackAdapter;
            _executeCompiled = executeCompiled;
            _onDisposed = onDisposed;
            _window = state.GetProperty("window")
                      ?? throw new InvalidOperationException("V8 frame window was not created.");
            _dispatch = (ScriptObject)state.GetProperty("dispatch");
            _refreshNamedProperties = (ScriptObject)state.GetProperty("refreshNamedProperties");
            _describe = (ScriptObject)state.GetProperty("describe");
            _flushCanvases = (ScriptObject)engine.Script.__htmlMlFlushCanvases;
            _disposeBrowsingContext =
                (ScriptObject)engine.Script.__htmlMlDisposeBrowsingContext;
        }

        public object Window => _window
            ?? throw new ObjectDisposedException(nameof(V8FrameBrowsingContext));

        public void Execute(string code, string? documentName = null)
        {
            ObjectDisposedException.ThrowIf(_engine is null, this);
            var host = _host ?? throw new ObjectDisposedException(nameof(V8FrameBrowsingContext));
            try
            {
                using var scope = host.EnterExternalJavaScriptCall();
                _refreshNamedProperties!.InvokeAsFunction();
                _executeCompiled!(
                    _engine,
                    code,
                    documentName ?? "virtual-frame.js");
            }
            catch
            {
                Console.Error.WriteLine(
                    $"V8 frame contract before failure in '{documentName ?? "virtual-frame.js"}': " +
                    _describe!.InvokeAsFunction());
                throw;
            }
        }

        public void ExecuteClassicScript(string specifier)
            => (_host ?? throw new ObjectDisposedException(nameof(V8FrameBrowsingContext)))
                .ExecuteExternalClassicScript(
                    specifier,
                    (code, fileName) => Execute(code, fileName));

        public void ExecuteInlineClassicScript(
            string code,
            object currentScript,
            string? documentName = null)
            => (_host ?? throw new ObjectDisposedException(nameof(V8FrameBrowsingContext)))
                .ExecuteExternalInlineClassicScript(
                    currentScript,
                    () => Execute(code, documentName));

        public void ProcessPendingTasks()
        {
            // ClearScript performs V8's microtask checkpoint on script return.
            // Host timers/rAF remain serialized by AvaloniaBrowserHost's task queue.
            _flushCanvases?.InvokeAsFunction();
        }

        public void DispatchWindowEvent(string type, object eventObject)
        {
            if (_state is not null)
            {
                _dispatch!.InvokeAsFunction(type, eventObject);
            }
        }

        public void Dispose()
        {
            if (_engine is null)
            {
                return;
            }
            TryInvoke(_disposeBrowsingContext);
            _disposeBrowsingContext = null;
            TryInvoke(_flushCanvases);
            _flushCanvases = null;
            _dispatch = null;
            _refreshNamedProperties = null;
            _describe = null;
            _state = null;
            if (_document is not null
                && _callbackAdapter is not null
                && ReferenceEquals(_document.ExternalEventListenerAdapter, _callbackAdapter))
            {
                _document.ExternalEventListenerAdapter = null;
            }
            if (_document is not null
                && _window is not null
                && ReferenceEquals(_document.ExternalWindowContext, _window))
            {
                _document.ExternalWindowContext = null;
            }
            if (_document is not null && _frameElement is not null)
            {
                ExternalVirtualBrowsingContextLifecycle.Detach(
                    _document,
                    _frameElement,
                    this);
            }
            _callbackAdapter?.Clear();
            _callbackAdapter = null;
            _executeCompiled = null;
            var engine = _engine;
            _engine = null;
            _window = null;
            _document = null;
            _frameElement = null;
            _host = null;
            TryDispose(engine);
            var onDisposed = _onDisposed;
            _onDisposed = null;
            onDisposed?.Invoke(this);
        }
    }

    public sealed class ModuleBackend
    {
        private readonly IHtmlMlJavaScriptHost _host;
        private readonly ClearScriptV8SharedCache? _sharedCache;

        internal ModuleBackend(
            IHtmlMlJavaScriptHost host,
            ClearScriptV8SharedCache? sharedCache)
        {
            _host = host;
            _sharedCache = sharedCache;
        }

        public ModuleSourceView Resolve(string specifier, string? referrerDirectory)
        {
            var resolutionKey = string.Concat(
                _host.ScriptBaseDirectory,
                "\n",
                referrerDirectory,
                "\n",
                specifier);
            var source = _sharedCache?.ResolveSource(
                resolutionKey,
                () => _host.ResolveExternalScript(specifier, referrerDirectory))
                ?? _host.ResolveExternalScript(specifier, referrerDirectory);
            return new ModuleSourceView(
                source.CacheKey,
                source.Content,
                source.FileName,
                source.Directory ?? string.Empty);
        }
    }

    public sealed record ModuleSourceView(
        string CacheKey,
        string Content,
        string FileName,
        string Directory);

    public sealed class Base64Backend
    {
        public string Encode(string value)
            => Convert.ToBase64String(Encoding.Latin1.GetBytes(value ?? string.Empty));

        public string Decode(string value)
            => Encoding.Latin1.GetString(Convert.FromBase64String(value ?? string.Empty));
    }

    public sealed class DomParserBackend
    {
        public object Parse(IHtmlMlJavaScriptDocument document, string markup, string mimeType)
            => document.ParseMarkupDocument(markup, mimeType);
    }

    public sealed class V8CanvasBatchSink
    {
        private readonly IHtmlMlCanvasBatchTarget _target;

        public V8CanvasBatchSink(object context)
        {
            _target = context as IHtmlMlCanvasBatchTarget
                ?? throw new ArgumentException(
                    "The batch target is not a Canvas2D context.",
                    nameof(context));
        }

        public long FlushCount { get; private set; }

        public long OperationCount { get; private set; }

        public long ValueCount { get; private set; }

        public long ReplayTicks { get; private set; }

        public long ReplayAllocatedBytes { get; private set; }

        public void Flush(ITypedArray<double> source, int valueCount, string stringPayload)
            => ReplaySource(source, valueCount, stringPayload);

        private void ReplaySource(
            ITypedArray<double> source,
            int valueCount,
            string stringPayload)
        {
            if (source is null || valueCount <= 0)
            {
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var values = ArrayPool<double>.Shared.Rent(valueCount);
            try
            {
                var read = source.Read(0, (ulong)valueCount, values, 0);
                if (read != (ulong)valueCount)
                {
                    throw new InvalidOperationException($"Expected {valueCount} canvas values but received {read}.");
                }

                var strings = string.IsNullOrEmpty(stringPayload)
                    ? Array.Empty<string>()
                    : JsonSerializer.Deserialize<string[]>(stringPayload) ?? Array.Empty<string>();
                OperationCount += _target.ReplayCanvasBatch(
                    values.AsSpan(0, valueCount),
                    strings);
                FlushCount++;
                ValueCount += valueCount;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(values);
                ReplayTicks += Stopwatch.GetTimestamp() - started;
                ReplayAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            }
        }

    }

    private const string BrowserRuntimeSetup = """
        (function () {
          const ownerDocument = __htmlMlOwnerDocument;
          const windowBackend = __htmlMlWindowBackend;
          const moduleCache = new Map();
          const performanceObject = (function() {
            const nativePerformance = typeof Performance === 'object' &&
              Performance !== null && typeof Performance.now === 'function'
              ? Performance
              : null;
            const timeOrigin = nativePerformance && typeof nativePerformance.timeOrigin === 'number'
              ? nativePerformance.timeOrigin
              : Date.now();
            const entries = [];
            function now() { return nativePerformance ? nativePerformance.now() : __htmlMlNow(); }
            function remove(entryType, name) {
              for (let index = entries.length - 1; index >= 0; index--) {
                if (entries[index].entryType === entryType &&
                    (name === undefined || entries[index].name === String(name))) entries.splice(index, 1);
              }
            }
            return {
              timeOrigin: timeOrigin,
              now: now,
              mark: function(name) {
                const entry = { name: String(name), entryType: 'mark', startTime: now(), duration: 0 };
                entries.push(entry); return entry;
              },
              measure: function(name, startMark, endMark) {
                let startTime = 0, endTime = now();
                if (startMark !== undefined) {
                  const starts = this.getEntriesByName(startMark, 'mark');
                  if (starts.length) startTime = starts[starts.length - 1].startTime;
                }
                if (endMark !== undefined) {
                  const ends = this.getEntriesByName(endMark, 'mark');
                  if (ends.length) endTime = ends[ends.length - 1].startTime;
                }
                const entry = { name: String(name), entryType: 'measure', startTime: startTime, duration: Math.max(0, endTime - startTime) };
                entries.push(entry); return entry;
              },
              getEntries: function() { return entries.slice(); },
              getEntriesByName: function(name, type) {
                name = String(name);
                return entries.filter(function(entry) { return entry.name === name && (type === undefined || entry.entryType === String(type)); });
              },
              getEntriesByType: function(type) {
                type = String(type); return entries.filter(function(entry) { return entry.entryType === type; });
              },
              clearMarks: function(name) { remove('mark', name); },
              clearMeasures: function(name) { remove('measure', name); },
              clearResourceTimings: function() { remove('resource'); }
            };
          })();
          const hostToProxy = new WeakMap();
          const proxyToHost = new WeakMap();
          const domIdentityToProxy = new Map();
          let nextDomIdentityToken = 1;
          const domIdentityFinalizer = typeof FinalizationRegistry === 'function'
            ? new FinalizationRegistry(function(held) {
                const current = domIdentityToProxy.get(held.identity);
                if (current && current.token === held.token) domIdentityToProxy.delete(held.identity);
              })
            : null;
          const nonDomHostResults = new WeakSet();
          const domPropertyAccessMetrics = __htmlMlTraceDomPropertyAccess ? new Map() : null;
          const stableDomObjectProperties = new Set([
            'style', 'classList', 'dataset', 'ownerDocument', 'defaultView'
          ]);
          const booleanDomProperties = new Set([
            'checked', 'selected', 'disabled'
          ]);
          const domStringMapToProxy = new WeakMap();
          const domTokenListToProxy = new WeakMap();
          const domTokenListWriteShadowMetrics = {
            hostWrites: 0,
            skippedWrites: 0,
            refreshes: 0,
            invalidations: 0
          };
          const cssStyleDeclarationToProxy = new WeakMap();
          const typedInlineStyleWriteMetrics = {
            typedWrites: 0,
            fallbackWrites: 0
          };
          const computedStyleToProxy = new WeakMap();
          const computedStyleProxies = new WeakSet();
          const computedStyleReadCacheMetrics = {
            typedMethodHits: 0,
            facadeHits: 0,
            facadeMisses: 0,
            valueHits: 0,
            valueMisses: 0
          };
          const canvasContextToProxy = new WeakMap();
          const canvasHostToBatch = new WeakMap();
          const activeCanvasBatches = new Set();
          let domConstructors = Object.create(null);

          const htmlElementConstructorNames = Object.freeze({
            A: 'HTMLAnchorElement',
            BUTTON: 'HTMLButtonElement',
            CANVAS: 'HTMLCanvasElement',
            DIV: 'HTMLDivElement',
            FORM: 'HTMLFormElement',
            IFRAME: 'HTMLIFrameElement',
            IMG: 'HTMLImageElement',
            INPUT: 'HTMLInputElement',
            LINK: 'HTMLLinkElement',
            OPTION: 'HTMLOptionElement',
            SCRIPT: 'HTMLScriptElement',
            SELECT: 'HTMLSelectElement',
            SPAN: 'HTMLSpanElement',
            STYLE: 'HTMLStyleElement',
            TEXTAREA: 'HTMLTextAreaElement'
          });

          function DOMStringMap() {}

          function wrapDomStringMap(backend) {
            if (domStringMapToProxy.has(backend)) return domStringMapToProxy.get(backend);
            const map = new Proxy(new DOMStringMap(), {
              get: function(target, property, receiver) {
                if (typeof property !== 'string' || Reflect.has(target, property)) {
                  return Reflect.get(target, property, receiver);
                }
                return backend.has(property) ? backend.get(property) : undefined;
              },
              set: function(target, property, value, receiver) {
                if (typeof property !== 'string' || Reflect.has(target, property)) {
                  return Reflect.set(target, property, value, receiver);
                }
                backend.set(property, String(value));
                return true;
              },
              deleteProperty: function(target, property) {
                return typeof property !== 'string' || !backend.has(property) || backend.delete(property);
              },
              has: function(target, property) {
                return typeof property === 'string' && backend.has(property) || Reflect.has(target, property);
              },
              ownKeys: function() { return Array.from(backend.keys()); },
              getOwnPropertyDescriptor: function(target, property) {
                if (typeof property === 'string' && backend.has(property)) {
                  return { value: backend.get(property), writable: true, enumerable: true, configurable: true };
                }
                return Reflect.getOwnPropertyDescriptor(target, property);
              }
            });
            domStringMapToProxy.set(backend, map);
            return map;
          }

          function wrapDomTokenList(backend) {
            if (domTokenListToProxy.has(backend)) return domTokenListToProxy.get(backend);
            let shadowTokens = null;
            function parseTokens(value) {
              return new Set(String(value || '').split(/\s+/).filter(Boolean));
            }
            function currentTokens() {
              if (shadowTokens === null) {
                shadowTokens = parseTokens(backend.value);
                domTokenListWriteShadowMetrics.refreshes++;
              }
              return shadowTokens;
            }
            function invalidateWriteShadow() {
              shadowTokens = null;
              domTokenListWriteShadowMetrics.invalidations++;
            }
            function recordHostWrite() {
              domTokenListWriteShadowMetrics.hostWrites++;
            }
            function canUseForcedToggleShadow(token) {
              return token.length > 0 && !/[\t\n\f\r ]/.test(token);
            }
            const list = {
              add: function() {
                for (let index = 0; index < arguments.length; index++) {
                  const token = String(arguments[index]);
                  recordHostWrite();
                  try { backend.add(token); }
                  finally { if (__htmlMlEnableDomTokenListWriteShadow) shadowTokens = null; }
                }
              },
              remove: function() {
                for (let index = 0; index < arguments.length; index++) {
                  const token = String(arguments[index]);
                  recordHostWrite();
                  try { backend.remove(token); }
                  finally { if (__htmlMlEnableDomTokenListWriteShadow) shadowTokens = null; }
                }
              },
              contains: function(token) { return Boolean(backend.contains(String(token))); },
              toggle: function(token, force) {
                token = String(token);
                if (arguments.length < 2) {
                  recordHostWrite();
                  try { return Boolean(backend.toggle(token)); }
                  finally { if (__htmlMlEnableDomTokenListWriteShadow) shadowTokens = null; }
                }
                force = Boolean(force);
                if (__htmlMlEnableDomTokenListWriteShadow &&
                    canUseForcedToggleShadow(token) &&
                    currentTokens().has(token) === force) {
                  domTokenListWriteShadowMetrics.skippedWrites++;
                  return force;
                }
                recordHostWrite();
                try {
                  const result = Boolean(backend.toggle(token, force));
                  if (__htmlMlEnableDomTokenListWriteShadow &&
                      canUseForcedToggleShadow(token) && shadowTokens !== null) {
                    if (result) shadowTokens.add(token);
                    else shadowTokens.delete(token);
                  }
                  return result;
                } finally {
                  if (__htmlMlEnableDomTokenListWriteShadow &&
                      !canUseForcedToggleShadow(token)) shadowTokens = null;
                }
              },
              item: function(index) {
                const values = String(backend.value || '').split(/\s+/).filter(Boolean);
                index = Number(index);
                return index >= 0 && index < values.length ? values[index] : null;
              },
              toString: function() { return String(backend.value || ''); }
            };
            Object.defineProperties(list, {
              value: {
                get: function() { return String(backend.value || ''); },
                set: function(value) {
                  value = String(value || '');
                  recordHostWrite();
                  try { backend.SetFromString(value); }
                  finally { if (__htmlMlEnableDomTokenListWriteShadow) shadowTokens = null; }
                },
                enumerable: true
              },
              length: {
                get: function() {
                  const value = String(backend.value || '').trim();
                  return value ? value.split(/\s+/).length : 0;
                }
              },
              [Symbol.iterator]: {
                value: function() {
                  return String(backend.value || '').split(/\s+/).filter(Boolean)[Symbol.iterator]();
                }
              }
            });
            Object.defineProperty(list, '__htmlMlInvalidateWriteShadow', {
              value: invalidateWriteShadow,
              enumerable: false
            });
            domTokenListToProxy.set(backend, list);
            return list;
          }

          globalThis.__htmlMlResetDomTokenListWriteShadowMetrics = function() {
            domTokenListWriteShadowMetrics.hostWrites = 0;
            domTokenListWriteShadowMetrics.skippedWrites = 0;
            domTokenListWriteShadowMetrics.refreshes = 0;
            domTokenListWriteShadowMetrics.invalidations = 0;
          };
          globalThis.__htmlMlDescribeDomTokenListWriteShadowMetrics = function() {
            return {
              hostWrites: domTokenListWriteShadowMetrics.hostWrites,
              skippedWrites: domTokenListWriteShadowMetrics.skippedWrites,
              refreshes: domTokenListWriteShadowMetrics.refreshes,
              invalidations: domTokenListWriteShadowMetrics.invalidations
            };
          };

          globalThis.__htmlMlResetTypedInlineStyleWriteMetrics = function() {
            typedInlineStyleWriteMetrics.typedWrites = 0;
            typedInlineStyleWriteMetrics.fallbackWrites = 0;
          };
          globalThis.__htmlMlDescribeTypedInlineStyleWriteMetrics = function() {
            return {
              typedWrites: typedInlineStyleWriteMetrics.typedWrites,
              fallbackWrites: typedInlineStyleWriteMetrics.fallbackWrites
            };
          };

          function wrapCssStyleDeclaration(backend) {
            if (cssStyleDeclarationToProxy.has(backend)) return cssStyleDeclarationToProxy.get(backend);
            const writtenValues = new Map();
            const proxyTarget = {};
            function normalizeStylePropertyName(name) {
              name = String(name).trim();
              if (name.indexOf('-') >= 0) return name.toLowerCase();
              return name.replace(/[A-Z]/g, function(character, index) {
                return (index > 0 ? '-' : '') + character.toLowerCase();
              });
            }
            function invalidateWriteShadow() { writtenValues.clear(); }
            function setStyleProperty(name, value) {
              const cacheKey = normalizeStylePropertyName(name);
              if (__htmlMlEnableStyleWriteShadow &&
                  writtenValues.has(cacheKey) &&
                  writtenValues.get(cacheKey) === value) {
                return;
              }
              if (__htmlMlEnableTypedInlineStyleWrites &&
                  __htmlMlTrySetInlineStyleProperty(backend, name, value)) {
                typedInlineStyleWriteMetrics.typedWrites++;
              } else {
                backend.setProperty(name, value);
                typedInlineStyleWriteMetrics.fallbackWrites++;
              }
              if (__htmlMlEnableStyleWriteShadow) writtenValues.set(cacheKey, value);
            }
            Object.defineProperty(proxyTarget, '__htmlMlInvalidateWriteShadow', {
              value: invalidateWriteShadow,
              enumerable: false
            });
            const declaration = new Proxy(proxyTarget, {
              get: function(target, property, receiver) {
                if (property === 'cssText') return String(backend.GetCssText() || '');
                if (property === 'setProperty') {
                  return function(name, value) {
                    setStyleProperty(String(name), String(value));
                  };
                }
                if (property === 'removeProperty') {
                  return function(name) {
                    name = String(name);
                    const previous = backend.getPropertyValue(name);
                    backend.removeProperty(name);
                    writtenValues.delete(normalizeStylePropertyName(name));
                    return previous == null ? '' : String(previous);
                  };
                }
                if (property === 'getPropertyValue') {
                  return function(name) {
                    name = String(name);
                    const value = backend.getPropertyValue(name);
                    if (__htmlMlEnableStyleWriteShadow) {
                      writtenValues.set(
                        normalizeStylePropertyName(name),
                        value == null ? '' : String(value));
                    }
                    return value == null ? '' : String(value);
                  };
                }
                if (property === 'getPropertyPriority') return function() { return ''; };
                if (property === 'length') return Number(backend.length) || 0;
                if (property === 'item') {
                  return function(index) { return String(backend.item(Number(index) || 0) || ''); };
                }
                if (property === Symbol.iterator) {
                  return function*() {
                    const length = Number(backend.length) || 0;
                    for (let index = 0; index < length; index++) yield String(backend.item(index) || '');
                  };
                }
                if (typeof property !== 'string' || Reflect.has(target, property)) {
                  return Reflect.get(target, property, receiver);
                }
                const value = backend.getPropertyValue(property);
                if (__htmlMlEnableStyleWriteShadow) {
                  writtenValues.set(
                    normalizeStylePropertyName(property),
                    value == null ? '' : String(value));
                }
                return value == null ? '' : String(value);
              },
              set: function(target, property, value, receiver) {
                  if (property === 'cssText') {
                    backend.SetCssText(String(value || ''));
                  invalidateWriteShadow();
                  return true;
                }
                if (typeof property !== 'string' || Reflect.has(target, property)) {
                  return Reflect.set(target, property, value, receiver);
                }
                setStyleProperty(property, value == null ? '' : String(value));
                return true;
              }
            });
            cssStyleDeclarationToProxy.set(backend, declaration);
            return declaration;
          }

          function unwrapHost(value) {
            return value && typeof value === 'object' && proxyToHost.has(value)
              ? proxyToHost.get(value)
              : value;
          }

          function wrapComputedStyle(raw) {
            if (raw && typeof raw === 'object' && computedStyleProxies.has(raw)) return raw;
            if (__htmlMlEnableComputedStyleReadCaching && computedStyleToProxy.has(raw)) {
              computedStyleReadCacheMetrics.facadeHits++;
              return computedStyleToProxy.get(raw);
            }
            computedStyleReadCacheMetrics.facadeMisses++;
            const propertyValues = new Map();
            const getPropertyValue = function(propertyName) {
              propertyName = String(propertyName);
              if (__htmlMlEnableComputedStyleReadCaching && propertyValues.has(propertyName)) {
                computedStyleReadCacheMetrics.valueHits++;
                return propertyValues.get(propertyName);
              }
              computedStyleReadCacheMetrics.valueMisses++;
              const value = String(__htmlMlGetComputedStyleValue(raw, propertyName) || '');
              if (__htmlMlEnableComputedStyleReadCaching) propertyValues.set(propertyName, value);
              return value;
            };
            const item = function(index) {
              return String(__htmlMlGetComputedStyleItem(raw, Number(index) || 0) || '');
            };
            const proxy = new Proxy({}, {
              get: function(_, property) {
                if (property === 'getPropertyValue') return getPropertyValue;
                if (property === 'getPropertyPriority') return function() { return ''; };
                if (property === 'item') return item;
                if (property === 'length') return Number(__htmlMlGetComputedStyleLength(raw)) || 0;
                if (property === 'cssText') return '';
                if (property === Symbol.iterator) {
                  return function*() {
                    const length = Number(__htmlMlGetComputedStyleLength(raw)) || 0;
                    for (let index = 0; index < length; index++) yield item(index);
                  };
                }
                return typeof property === 'string' ? getPropertyValue(property) : undefined;
              }
            });
            if (__htmlMlEnableComputedStyleReadCaching) computedStyleToProxy.set(raw, proxy);
            computedStyleProxies.add(proxy);
            return proxy;
          }

          globalThis.__htmlMlResetComputedStyleReadCacheMetrics = function() {
            computedStyleReadCacheMetrics.facadeHits = 0;
            computedStyleReadCacheMetrics.typedMethodHits = 0;
            computedStyleReadCacheMetrics.facadeMisses = 0;
            computedStyleReadCacheMetrics.valueHits = 0;
            computedStyleReadCacheMetrics.valueMisses = 0;
          };
          globalThis.__htmlMlDescribeComputedStyleReadCacheMetrics = function() {
            return {
              typedMethodHits: computedStyleReadCacheMetrics.typedMethodHits,
              facadeHits: computedStyleReadCacheMetrics.facadeHits,
              facadeMisses: computedStyleReadCacheMetrics.facadeMisses,
              valueHits: computedStyleReadCacheMetrics.valueHits,
              valueMisses: computedStyleReadCacheMetrics.valueMisses
            };
          };

          function getDomIdentity(raw) {
            try {
              const identity = raw.__htmlMlDomIdentity;
              return identity == null ? null : String(identity);
            } catch (_) {
              return null;
            }
          }

          function getDomIdentityProxy(raw, identity) {
            if (arguments.length < 2) identity = getDomIdentity(raw);
            if (identity === null) return null;
            const entry = domIdentityToProxy.get(identity);
            if (!entry) return null;
            const proxy = entry.proxy !== undefined
              ? entry.proxy
              : entry.ref && typeof entry.ref.deref === 'function'
                ? entry.ref.deref()
                : undefined;
            if (proxy !== undefined) {
              updateDomIdentityRetention(raw, entry, proxy);
              return proxy;
            }
            domIdentityToProxy.delete(identity);
            return null;
          }

          function shouldRetainDomProxy(raw) {
            try { return Number(raw.nodeType) === 9 || Boolean(raw.isConnected); }
            catch (_) { return false; }
          }

          function updateDomIdentityRetention(raw, entry, proxy) {
            if (shouldRetainDomProxy(raw)) entry.proxy = proxy;
            else if (entry.proxy !== undefined) delete entry.proxy;
          }

          function forceDomIdentityRetention(raw, retain) {
            const identity = getDomIdentity(raw);
            if (identity === null) return;
            const entry = domIdentityToProxy.get(identity);
            if (!entry) return;
            if (!retain) {
              if (entry.proxy !== undefined) delete entry.proxy;
              return;
            }
            const proxy = entry.proxy !== undefined
              ? entry.proxy
              : entry.ref && typeof entry.ref.deref === 'function'
                ? entry.ref.deref()
                : undefined;
            if (proxy !== undefined) entry.proxy = proxy;
          }

          function sweepDisconnectedDomProxyRetention() {
            for (const [identity, entry] of domIdentityToProxy.entries()) {
              if (entry.proxy !== undefined) {
                const raw = proxyToHost.get(entry.proxy);
                if (raw == null || !shouldRetainDomProxy(raw)) delete entry.proxy;
              }
              if (entry.proxy === undefined && entry.ref &&
                  typeof entry.ref.deref === 'function' && entry.ref.deref() === undefined) {
                domIdentityToProxy.delete(identity);
              }
            }
          }

          let disconnectedDomProxySweepScheduled = false;
          function scheduleDisconnectedDomProxyRetentionSweep() {
            if (disconnectedDomProxySweepScheduled) return;
            disconnectedDomProxySweepScheduled = true;
            queueMicrotask(function() {
              disconnectedDomProxySweepScheduled = false;
              sweepDisconnectedDomProxyRetention();
            });
          }

          function rememberDomIdentity(raw, proxy, identity) {
            if (arguments.length < 3) identity = getDomIdentity(raw);
            if (identity === null) return;
            const token = nextDomIdentityToken++;
            if (typeof WeakRef === 'function') {
              const entry = { ref: new WeakRef(proxy), token: token };
              updateDomIdentityRetention(raw, entry, proxy);
              domIdentityToProxy.set(identity, entry);
              if (domIdentityFinalizer !== null) {
                domIdentityFinalizer.register(proxy, { identity: identity, token: token });
              }
            } else {
              domIdentityToProxy.set(identity, { proxy: proxy, token: token });
            }
          }

          function isSvgHost(raw) {
            try { return String(raw.namespaceURI || '').toLowerCase().indexOf('svg') >= 0; }
            catch (_) { return false; }
          }

          function constructorForHost(raw) {
            let nodeType;
            try { nodeType = raw.nodeType; } catch (_) { return Object; }
            if (nodeType === 9) return domConstructors.HTMLDocument || domConstructors.Document || Object;
            if (nodeType !== 1) return domConstructors.Node || Object;
            let nodeName = '';
            try { nodeName = String(raw.nodeName || '').toUpperCase(); } catch (_) {}
            if (isSvgHost(raw)) {
              return nodeName === 'SVG'
                ? domConstructors.SVGSVGElement || domConstructors.SVGElement || domConstructors.Element || Object
                : domConstructors.SVGElement || domConstructors.Element || Object;
            }
            const constructorName = htmlElementConstructorNames[nodeName];
            return constructorName && domConstructors[constructorName]
              || domConstructors.HTMLElement
              || domConstructors.Element
              || Object;
          }

          function getDomPropertyMetric(property) {
            if (domPropertyAccessMetrics === null) return;
            const key = typeof property === 'symbol' ? property.toString() : String(property);
            let metric = domPropertyAccessMetrics.get(key);
            if (!metric) {
              metric = {
                property: key,
                accesses: 0,
                rawReads: 0,
                methodHits: 0,
                stableHits: 0,
                fastNumericHits: 0,
                missingHits: 0,
                expandoHits: 0,
                invocations: 0,
                invokeDurationMs: 0,
                rawReadDurationMs: 0
              };
              domPropertyAccessMetrics.set(key, metric);
            }
            return metric;
          }

          function recordDomPropertyAccess(property, field) {
            const metric = getDomPropertyMetric(property);
            if (!metric) return;
            metric[field]++;
          }

          function recordDomPropertyDuration(property, field, duration) {
            const metric = getDomPropertyMetric(property);
            if (!metric) return;
            metric[field] += duration;
          }

          globalThis.__htmlMlResetDomPropertyAccessMetrics = function() {
            if (domPropertyAccessMetrics !== null) domPropertyAccessMetrics.clear();
          };
          globalThis.__htmlMlDescribeDomPropertyAccessMetrics = function(limit) {
            if (domPropertyAccessMetrics === null) return [];
            const maximum = Math.max(1, Number(limit) || 40);
            return Array.from(domPropertyAccessMetrics.values()).sort(function(left, right) {
              return right.invokeDurationMs - left.invokeDurationMs
                || right.rawReadDurationMs - left.rawReadDurationMs
                || right.invocations - left.invocations
                || right.rawReads - left.rawReads
                || right.accesses - left.accesses
                || left.property.localeCompare(right.property);
            }).slice(0, maximum);
          };
          globalThis.__htmlMlDescribeDomProxyRetention = function() {
            sweepDisconnectedDomProxyRetention();
            const result = { entries: 0, strong: 0, weakLive: 0, weakDead: 0 };
            for (const entry of domIdentityToProxy.values()) {
              result.entries++;
              if (entry.proxy !== undefined) {
                result.strong++;
              } else if (entry.ref && typeof entry.ref.deref === 'function' &&
                         entry.ref.deref() !== undefined) {
                result.weakLive++;
              } else {
                result.weakDead++;
              }
            }
            return result;
          };

          function wrapResult(value) {
            if (value == null || (typeof value !== 'object' && typeof value !== 'function')) return value;
            if (proxyToHost.has(value)) return value;
            if (__htmlMlEnableResultClassificationCache) {
              if (hostToProxy.has(value)) return hostToProxy.get(value);
              if (nonDomHostResults.has(value)) return value;
            }
            const identityProxy = getDomIdentityProxy(value);
            if (identityProxy !== null) return identityProxy;
            if (Array.isArray(value)) return value.map(wrapResult);
            let nodeType;
            try { nodeType = value.nodeType; } catch (_) { nodeType = undefined; }
            if (typeof nodeType === 'number') return wrapHost(value);
            try {
              if (typeof value.getElementsByTagName === 'function' && 'documentElement' in value) {
                return wrapHost(value);
              }
            } catch (_) {}
            try {
              if (typeof value.length === 'number') return Array.from(value, wrapResult);
            } catch (_) {}
            try {
              if (typeof value.Length === 'number') {
                const array = new Array(value.Length);
                for (let index = 0; index < value.Length; index++) array[index] = wrapResult(value[index]);
                return array;
              }
            } catch (_) {}
            if (__htmlMlEnableResultClassificationCache) nonDomHostResults.add(value);
            return value;
          }

          function DOMRectResult(values) {
            this.x = values[0]; this.y = values[1];
            this.width = values[2]; this.height = values[3];
            this.left = values[4]; this.top = values[5];
            this.right = values[6]; this.bottom = values[7];
          }
          DOMRectResult.prototype.toJSON = function() {
            return {
              x: this.x, y: this.y, width: this.width, height: this.height,
              left: this.left, top: this.top, right: this.right, bottom: this.bottom
            };
          };
          function TextMetricsResult(width) { this.width = width; }

          function flushCanvasBatches() {
            for (const batch of activeCanvasBatches) batch.flush();
          }

          function wrapCanvasContext(raw) {
            if (raw == null || typeof raw !== 'object') return raw;
            if (canvasContextToProxy.has(raw)) return canvasContextToProxy.get(raw);

            const sink = __htmlMlCreateCanvasBatchSink(raw);
            const buffer = new Float64Array(16384);
            const strings = [];
            const stringIndices = new Map();
            const methods = new Map();
            const initialShadowState = {
              font: String(raw.font),
              lineWidth: Number(raw.lineWidth),
              textBaseline: String(raw.textBaseline)
            };
            let shadowState = Object.assign({}, initialShadowState);
            const shadowStateStack = [];
            let deduplicatedState = Object.create(null);
            const deduplicatedStateStack = [];
            let valueCount = 0;

            function stringIndex(value) {
              value = String(value);
              if (stringIndices.has(value)) return stringIndices.get(value);
              const index = strings.length;
              strings.push(value);
              stringIndices.set(value, index);
              return index;
            }

            function resetBuffer() {
              valueCount = 0;
              strings.length = 0;
              stringIndices.clear();
            }

            function flush() {
              if (valueCount === 0) return;
              sink.Flush(buffer, valueCount, JSON.stringify(strings));
              resetBuffer();
            }

            function ensureCapacity(argumentCount) {
              const required = argumentCount + 2;
              if (required > buffer.length) throw new Error('Canvas2D command exceeds the batch buffer capacity.');
              if (valueCount + required > buffer.length) flush();
            }

            function push(opcode, args) {
              ensureCapacity(args.length);
              buffer[valueCount++] = opcode;
              buffer[valueCount++] = args.length;
              for (let index = 0; index < args.length; index++) buffer[valueCount++] = Number(args[index]);
            }

            function pushArguments(opcode, args) {
              if (!__htmlMlEnableAllocationFreeCanvasCommandWrites) {
                push(opcode, Array.from(args));
                return;
              }
              const length = args.length;
              ensureCapacity(length);
              buffer[valueCount++] = opcode;
              buffer[valueCount++] = length;
              for (let index = 0; index < length; index++) buffer[valueCount++] = Number(args[index]);
            }

            function push0(opcode) {
              if (!__htmlMlEnableAllocationFreeCanvasCommandWrites) {
                push(opcode, []);
                return;
              }
              ensureCapacity(0);
              buffer[valueCount++] = opcode;
              buffer[valueCount++] = 0;
            }

            function push1(opcode, first) {
              if (!__htmlMlEnableAllocationFreeCanvasCommandWrites) {
                push(opcode, [first]);
                return;
              }
              ensureCapacity(1);
              buffer[valueCount++] = opcode;
              buffer[valueCount++] = 1;
              buffer[valueCount++] = Number(first);
            }

            function push3(opcode, first, second, third) {
              if (!__htmlMlEnableAllocationFreeCanvasCommandWrites) {
                push(opcode, [first, second, third]);
                return;
              }
              ensureCapacity(3);
              buffer[valueCount++] = opcode;
              buffer[valueCount++] = 3;
              buffer[valueCount++] = Number(first);
              buffer[valueCount++] = Number(second);
              buffer[valueCount++] = Number(third);
            }

            function push6(opcode, first, second, third, fourth, fifth, sixth) {
              if (!__htmlMlEnableAllocationFreeCanvasCommandWrites) {
                push(opcode, [first, second, third, fourth, fifth, sixth]);
                return;
              }
              ensureCapacity(6);
              buffer[valueCount++] = opcode;
              buffer[valueCount++] = 6;
              buffer[valueCount++] = Number(first);
              buffer[valueCount++] = Number(second);
              buffer[valueCount++] = Number(third);
              buffer[valueCount++] = Number(fourth);
              buffer[valueCount++] = Number(fifth);
              buffer[valueCount++] = Number(sixth);
            }

            function pushLineDash(values) {
              if (!__htmlMlEnableAllocationFreeCanvasCommandWrites) {
                push(19, [values.length].concat(values));
                return;
              }
              ensureCapacity(values.length + 1);
              buffer[valueCount++] = 19;
              buffer[valueCount++] = values.length + 1;
              buffer[valueCount++] = values.length;
              for (let index = 0; index < values.length; index++) {
                buffer[valueCount++] = Number(values[index]);
              }
            }

            function invokeRaw(property, args) {
              flushCanvasBatches();
              const value = raw[property];
              const result = wrapResult(value(...Array.from(args, argument =>
                argument && typeof argument === 'object' && argument.__htmlMlNativePath2D
                  ? argument.__htmlMlNativePath2D
                  : unwrapHost(argument))));
              deduplicatedState = Object.create(null);
              return result;
            }

            function shouldSkipStateWrite(property, value) {
              if (!__htmlMlEnableCanvasStateDeduplication) return false;
              if (!Object.prototype.hasOwnProperty.call(deduplicatedState, property)) return false;
              return Object.is(deduplicatedState[property], value);
            }

            function rememberStateWrite(property, value) {
              deduplicatedState[property] = value;
            }

            const fixedMethods = {
              resetTransform: [3, 0],
              beginPath: [9, 0], closePath: [10, 0],
              moveTo: [11, 2], lineTo: [12, 2], bezierCurveTo: [13, 6],
              quadraticCurveTo: [14, 4], arcTo: [16, 5], rect: [17, 4],
              stroke: [20, 0], fillRect: [22, 4], strokeRect: [23, 4], clearRect: [24, 4]
            };
            const stringProperties = {
              fillStyle: 40, strokeStyle: 41, lineCap: 43, lineJoin: 44,
              font: 48, textAlign: 49, textBaseline: 50, imageSmoothingQuality: 52,
              globalCompositeOperation: 53, shadowColor: 54
            };
            const numberProperties = {
              lineWidth: 42, miterLimit: 45, globalAlpha: 46, lineDashOffset: 47,
              shadowBlur: 55, shadowOffsetX: 56, shadowOffsetY: 57
            };

            function createMethod(property) {
              if (__htmlMlEnablePrimitiveTextMetrics && property === 'measureText') {
                return function(text) {
                  flushCanvasBatches();
                  deduplicatedState = Object.create(null);
                  return new TextMetricsResult(__htmlMlMeasureTextWidth(raw, String(text)));
                };
              }
              if (property === 'save') {
                return function() {
                  if (arguments.length !== 0) return invokeRaw(property, arguments);
                  shadowStateStack.push(Object.assign({}, shadowState));
                  deduplicatedStateStack.push(Object.assign(Object.create(null), deduplicatedState));
                  push0(1);
                };
              }
              if (property === 'restore') {
                return function() {
                  if (arguments.length !== 0) return invokeRaw(property, arguments);
                  if (shadowStateStack.length) {
                    shadowState = shadowStateStack.pop();
                    deduplicatedState = deduplicatedStateStack.pop() || Object.create(null);
                  }
                  push0(2);
                };
              }
              if (fixedMethods[property]) {
                const descriptor = fixedMethods[property];
                return function() {
                  if (arguments.length !== descriptor[1]) return invokeRaw(property, arguments);
                  pushArguments(descriptor[0], arguments);
                };
              }
              if (property === 'setTransform' || property === 'transform') {
                return function() {
                  if (arguments.length !== 6) return invokeRaw(property, arguments);
                  pushArguments(property === 'setTransform' ? 4 : 5, arguments);
                };
              }
              if (property === 'translate' || property === 'scale') {
                return function() {
                  if (arguments.length !== 2) return invokeRaw(property, arguments);
                  pushArguments(property === 'translate' ? 6 : 7, arguments);
                };
              }
              if (property === 'rotate') {
                return function() {
                  if (arguments.length !== 1) return invokeRaw(property, arguments);
                  push1(8, arguments[0]);
                };
              }
              if (property === 'arc') {
                return function() {
                  if (arguments.length < 5 || arguments.length > 6) return invokeRaw(property, arguments);
                  push6(15, arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], Boolean(arguments[5]) ? 1 : 0);
                };
              }
              if (property === 'clip') {
                return function() {
                  if (arguments.length > 1 || arguments.length === 1 && typeof arguments[0] !== 'string') return invokeRaw(property, arguments);
                  push0(18);
                };
              }
              if (property === 'setLineDash') {
                return function(segments) {
                  let values;
                  try { values = Array.from(segments || [], Number); } catch (_) { return invokeRaw(property, arguments); }
                  pushLineDash(values);
                };
              }
              if (property === 'fill') {
                return function() {
                  if (arguments.length > 1 || arguments.length === 1 && typeof arguments[0] !== 'string') return invokeRaw(property, arguments);
                  push0(21);
                };
              }
              if (property === 'fillText' || property === 'strokeText') {
                return function(text, x, y) {
                  if (arguments.length < 3) return invokeRaw(property, arguments);
                  push3(property === 'fillText' ? 25 : 26, stringIndex(text), x, y);
                };
              }
              return function() { return invokeRaw(property, arguments); };
            }

            function readProperty(property) {
              if (property === 'font' || property === 'lineWidth' || property === 'textBaseline') return shadowState[property];
              flush();
              let rawValue;
              try { rawValue = raw[property]; } catch (_) { return undefined; }
              return wrapResult(rawValue);
            }

            function writeProperty(property, value) {
              if (Object.prototype.hasOwnProperty.call(stringProperties, property) && typeof value === 'string') {
                if (shouldSkipStateWrite(property, value)) return true;
                ensureCapacity(1);
                push1(stringProperties[property], stringIndex(value));
                rememberStateWrite(property, value);
                if (property === 'font' && value.trim()) shadowState.font = value;
                if (property === 'textBaseline' && /^(alphabetic|top|hanging|middle|ideographic|bottom)$/.test(value)) shadowState.textBaseline = value;
                return true;
              }
              if (Object.prototype.hasOwnProperty.call(numberProperties, property) && typeof value === 'number') {
                if (shouldSkipStateWrite(property, value)) return true;
                push1(numberProperties[property], value);
                rememberStateWrite(property, value);
                if (property === 'lineWidth' && value > 0) shadowState.lineWidth = value;
                return true;
              }
              if (property === 'imageSmoothingEnabled') {
                const normalized = Boolean(value);
                if (shouldSkipStateWrite(property, normalized)) return true;
                push1(51, normalized ? 1 : 0);
                rememberStateWrite(property, normalized);
                return true;
              }
              flushCanvasBatches();
              deduplicatedState = Object.create(null);
              try { raw[property] = unwrapHost(value); return true; } catch (_) { return false; }
            }

            let proxy;
            if (__htmlMlEnableCanvasFacade) {
              proxy = {};
              Object.defineProperties(proxy, {
                __htmlMlRawHostObject: { value: raw, configurable: true },
                __htmlMlFlushCanvas: { value: flush, configurable: true },
                canvas: { get: function() { return wrapHost(raw.canvas); }, configurable: true, enumerable: true }
              });
              const methodNames = [
                'save', 'restore', 'resetTransform', 'setTransform', 'transform',
                'translate', 'scale', 'rotate', 'beginPath', 'closePath', 'moveTo',
                'lineTo', 'bezierCurveTo', 'quadraticCurveTo', 'arc', 'arcTo', 'rect',
                'clip', 'setLineDash', 'getLineDash', 'createLinearGradient',
                'createRadialGradient', 'createPattern', 'stroke', 'fill', 'fillRect',
                'strokeRect', 'clearRect', 'fillText', 'strokeText', 'measureText',
                'getTransform', 'createImageData', 'getImageData', 'putImageData', 'drawImage'
              ];
              for (const property of methodNames) {
                Object.defineProperty(proxy, property, {
                  value: createMethod(property), writable: true, configurable: true
                });
              }
              const stateProperties = Array.from(new Set(
                Object.keys(stringProperties).concat(
                  Object.keys(numberProperties),
                  ['imageSmoothingEnabled', 'mozCurrentTransform', 'mozCurrentTransformInverse'])));
              for (const property of stateProperties) {
                Object.defineProperty(proxy, property, {
                  get: function() { return readProperty(property); },
                  set: property === 'mozCurrentTransform' || property === 'mozCurrentTransformInverse'
                    ? undefined
                    : function(value) { writeProperty(property, value); },
                  configurable: true,
                  enumerable: true
                });
              }
            } else {
              proxy = new Proxy({}, {
                get: function(_, property) {
                  if (property === '__htmlMlRawHostObject') return raw;
                  if (property === '__htmlMlFlushCanvas') return flush;
                  if (property === 'canvas') return wrapHost(raw.canvas);
                  if (property === 'font' || property === 'lineWidth' || property === 'textBaseline') return shadowState[property];
                  if (__htmlMlEnableFastMethodCacheLookup && methods.has(property)) return methods.get(property);
                  let rawValue;
                  try { rawValue = raw[property]; } catch (_) { return undefined; }
                  if (typeof rawValue === 'function') {
                    if (!methods.has(property)) methods.set(property, createMethod(property));
                    return methods.get(property);
                  }
                  return readProperty(property);
                },
                set: function(_, property, value) { return writeProperty(property, value); },
                has: function(_, property) {
                  if (property === '__htmlMlRawHostObject' || property === '__htmlMlFlushCanvas') return true;
                  try { return property in raw; } catch (_) { return false; }
                }
              });
            }
            canvasContextToProxy.set(raw, proxy);
            proxyToHost.set(proxy, raw);
            const batch = {
              flush: flush,
              resetState: function() {
                shadowState = Object.assign({}, initialShadowState);
                shadowStateStack.length = 0;
                deduplicatedState = Object.create(null);
                deduplicatedStateStack.length = 0;
              }
            };
            activeCanvasBatches.add(batch);
            try {
              const canvas = raw.canvas;
              if (canvas && typeof canvas === 'object') canvasHostToBatch.set(canvas, batch);
            } catch (_) {}
            return proxy;
          }

          function wrapHost(target) {
            if (target == null || typeof target !== 'object') return target;
            if (proxyToHost.has(target)) return target;
            if (hostToProxy.has(target)) return hostToProxy.get(target);
            // A new host object used to cross the native identity property twice:
            // once to find an existing DOM proxy and again while remembering the
            // newly created proxy. Reuse the first result, including the common
            // null result for event and other non-DOM host objects.
            const identity = getDomIdentity(target);
            const identityProxy = getDomIdentityProxy(target, identity);
            if (identityProxy !== null) return identityProxy;
              const methods = new Map();
              const stableValues = new Map();
              const missingProperties = new Set();
            function invalidateClassListWriteShadow() {
              const classList = stableValues.get('classList');
              if (classList && typeof classList.__htmlMlInvalidateWriteShadow === 'function') {
                classList.__htmlMlInvalidateWriteShadow();
              }
            }
            const raw = target;
            let typedManagedAbiHandle;
            function getTypedManagedAbiHandle() {
              if (!__htmlMlEnableTypedManagedAbi) return 0;
              if (typedManagedAbiHandle === undefined) {
                typedManagedAbiHandle = Number(__htmlMlRegisterTypedManagedAbiTarget(raw)) || 0;
              }
              return typedManagedAbiHandle;
            }
            const proxyTarget = {};
            const proxy = new Proxy(proxyTarget, {
              get: function(local, property, receiver) {
                recordDomPropertyAccess(property, 'accesses');
                if (property === '__htmlMlRawHostObject') return raw;
                if (property === 'constructor') return constructorForHost(raw);
                if (Reflect.has(local, property)) {
                  recordDomPropertyAccess(property, 'expandoHits');
                  return Reflect.get(local, property, receiver);
                }
                if (typeof property === 'string' && property.length > 2 && property.slice(0, 2) === 'on') return null;
                if (property === 'clientTop' || property === 'clientLeft') return 0;
                if (__htmlMlEnableFastMethodCacheLookup && __htmlMlEnableDomMethodCaching && methods.has(property)) {
                  recordDomPropertyAccess(property, 'methodHits');
                  return methods.get(property);
                }
                if (__htmlMlEnableStableDomPropertyCache && stableValues.has(property)) {
                  recordDomPropertyAccess(property, 'stableHits');
                  return stableValues.get(property);
                }
                if (__htmlMlEnableMissingDomPropertyCache && missingProperties.has(property)) {
                  recordDomPropertyAccess(property, 'missingHits');
                  return undefined;
                }
                let numericProperty = -1;
                switch (property) {
                  case 'width': numericProperty = 0; break;
                  case 'height': numericProperty = 1; break;
                  case 'clientWidth': numericProperty = 2; break;
                  case 'clientHeight': numericProperty = 3; break;
                  case 'offsetWidth': numericProperty = 4; break;
                  case 'offsetHeight': numericProperty = 5; break;
                  case 'offsetTop': numericProperty = 6; break;
                  case 'offsetLeft': numericProperty = 7; break;
                }
                if (__htmlMlEnableTypedDomNumericProperties && numericProperty >= 0) {
                  let numericValue;
                  if (__htmlMlEnableTypedManagedAbi &&
                      typeof __htmlMlNativeReadDomNumericProperty === 'function') {
                    const handle = getTypedManagedAbiHandle();
                    numericValue = handle > 0
                      ? __htmlMlNativeReadDomNumericProperty(handle, numericProperty)
                      : NaN;
                  } else {
                    numericValue = __htmlMlReadDomNumericProperty(raw, numericProperty);
                  }
                  if (numericValue === numericValue) {
                    recordDomPropertyAccess(property, 'fastNumericHits');
                    return numericValue;
                  }
                }
                let value;
                recordDomPropertyAccess(property, 'rawReads');
                if (domPropertyAccessMetrics === null) {
                  try { value = raw[property]; } catch (_) { return undefined; }
                } else {
                  const rawReadStarted = performance.now();
                  try { value = raw[property]; }
                  catch (_) {
                    recordDomPropertyDuration(property, 'rawReadDurationMs', performance.now() - rawReadStarted);
                    return undefined;
                  }
                  recordDomPropertyDuration(property, 'rawReadDurationMs', performance.now() - rawReadStarted);
                }
                if (typeof value === 'undefined') {
                  const prototype = constructorForHost(raw).prototype;
                  if (prototype && property in prototype) {
                    return Reflect.get(prototype, property, receiver);
                  }
                }
                if (__htmlMlEnableMissingDomPropertyCache && typeof value === 'undefined') {
                  missingProperties.add(property);
                  return undefined;
                }
                if (property === 'dataset' && value != null) {
                  const wrappedDataset = wrapDomStringMap(value);
                  if (__htmlMlEnableStableDomPropertyCache) stableValues.set(property, wrappedDataset);
                  return wrappedDataset;
                }
                if (property === 'classList' && value != null) {
                  const wrappedClassList = wrapDomTokenList(value);
                  if (__htmlMlEnableStableDomPropertyCache) stableValues.set(property, wrappedClassList);
                  return wrappedClassList;
                }
                if (property === 'style' && value != null) {
                  const wrappedStyle = wrapCssStyleDeclaration(value);
                  if (__htmlMlEnableStableDomPropertyCache) stableValues.set(property, wrappedStyle);
                  return wrappedStyle;
                }
                if (property === 'addedNodes' || property === 'removedNodes') {
                  try { return Array.from(value || [], wrapResult); } catch (_) { return []; }
                }
                if (typeof value === 'function') {
                  const createMethod = function() {
                    if (__htmlMlEnableTypedComputedStyleAccess && property === 'getComputedStyle') {
                      return function(element) {
                        computedStyleReadCacheMetrics.typedMethodHits++;
                        return wrapComputedStyle(value(unwrapHost(element)));
                      };
                    }
                    if (__htmlMlEnableTypedDomRect && property === 'getBoundingClientRect') {
                        const values = new Float64Array(8);
                        return function() {
                          const handle = getTypedManagedAbiHandle();
                          if (!(handle > 0 &&
                                typeof __htmlMlNativeWriteDomRect === 'function' &&
                                __htmlMlNativeWriteDomRect(handle, 0, values))) {
                            __htmlMlWriteBoundingClientRect(raw, values);
                          }
                          return new DOMRectResult(values);
                        };
                    }
                    if (__htmlMlEnableTypedDomRect &&
                        __htmlMlEnableTypedDomClientRects &&
                        property === 'getClientRects') {
                        const values = new Float64Array(8);
                        return function() {
                          const handle = getTypedManagedAbiHandle();
                          const hasRect = handle > 0 && typeof __htmlMlNativeWriteDomRect === 'function'
                            ? __htmlMlNativeWriteDomRect(handle, 1, values)
                            : __htmlMlWriteClientRect(raw, values);
                          return hasRect
                            ? [new DOMRectResult(values)]
                            : [];
                      };
                    }
                    if (__htmlMlEnableCanvasBatching && property === 'getContext') {
                      let cached2dContext;
                      return function(type) {
                        const normalizedType = String(type || '').toLowerCase();
                        if (normalizedType === '2d' && cached2dContext !== undefined) {
                          return cached2dContext;
                        }
                        const result = value(normalizedType);
                        const wrapped = normalizedType === '2d'
                          ? wrapCanvasContext(result)
                          : wrapResult(result);
                        if (normalizedType === '2d') cached2dContext = wrapped;
                        return wrapped;
                      };
                    }
                    return function() {
                      const args = Array.from(arguments, unwrapHost);
                      let result;
                      if (domPropertyAccessMetrics === null) {
                        result = value(...args);
                      } else {
                        const invocationStarted = performance.now();
                        try { result = value(...args); }
                        finally {
                          recordDomPropertyAccess(property, 'invocations');
                          recordDomPropertyDuration(
                            property,
                            'invokeDurationMs',
                            performance.now() - invocationStarted);
                        }
                      }
                      const wrappedResult = __htmlMlEnableCanvasBatching && property === 'getContext' && String(args[0] || '').toLowerCase() === '2d'
                        ? wrapCanvasContext(result)
                        : wrapResult(result);
                      if (property === 'appendChild' || property === 'insertBefore') {
                        forceDomIdentityRetention(result, true);
                      } else if (property === 'append' || property === 'prepend') {
                        for (const arg of args) forceDomIdentityRetention(arg, true);
                      } else if (property === 'replaceChild') {
                        forceDomIdentityRetention(result, false);
                        forceDomIdentityRetention(args[0], true);
                      } else if (property === 'removeChild') {
                        forceDomIdentityRetention(result, false);
                      }
                      if (property === 'remove' || property === 'replaceChildren') {
                        if (property === 'remove') forceDomIdentityRetention(raw, false);
                        scheduleDisconnectedDomProxyRetentionSweep();
                        if (property === 'replaceChildren') {
                          for (const arg of args) forceDomIdentityRetention(arg, true);
                        }
                      }
                      if ((property === 'setAttribute' || property === 'removeAttribute') &&
                          args.length > 0 &&
                          String(args[0]).toLowerCase() === 'style') {
                        const style = stableValues.get('style');
                        if (style && typeof style.__htmlMlInvalidateWriteShadow === 'function') {
                          style.__htmlMlInvalidateWriteShadow();
                        }
                      }
                      if ((property === 'setAttribute' || property === 'removeAttribute' ||
                           property === 'setAttributeNS' || property === 'removeAttributeNS') &&
                          args.length > 0 &&
                          String(args[property === 'setAttributeNS' || property === 'removeAttributeNS' ? 1 : 0])
                            .toLowerCase() === 'class') {
                        invalidateClassListWriteShadow();
                      }
                      return wrappedResult;
                    };
                  };
                  if (!__htmlMlEnableDomMethodCaching) return createMethod();
                  if (!methods.has(property)) methods.set(property, createMethod());
                  return methods.get(property);
                }
                const wrappedValue = wrapResult(value);
                if (__htmlMlEnableStableDomPropertyCache && stableDomObjectProperties.has(property)) {
                  stableValues.set(property, wrappedValue);
                }
                return wrappedValue;
              },
              set: function(local, property, value, receiver) {
                if ((property === 'width' || property === 'height') && canvasHostToBatch.has(raw)) {
                  const canvasBatch = canvasHostToBatch.get(raw);
                  canvasBatch.flush();
                  canvasBatch.resetState();
                }
                if (typeof property === 'string' && property.length > 2 && property.slice(0, 2) === 'on') {
                  const eventType = property.slice(2);
                  const previous = Reflect.get(local, property, receiver);
                  if (typeof previous === 'function') {
                    try { raw.removeEventListener(eventType, previous); } catch (_) {}
                  }
                  const next = typeof value === 'function' ? value : null;
                  Reflect.defineProperty(local, property, {
                    value: next, writable: true, enumerable: true, configurable: true
                  });
                  if (next) {
                    try { raw.addEventListener(eventType, next); } catch (_) {}
                  }
                  return true;
                }
                if (Object.prototype.hasOwnProperty.call(local, property)) {
                  return Reflect.set(local, property, value, receiver);
                }
                try {
                  if (!(property in raw)) {
                    missingProperties.delete(property);
                    return Reflect.set(local, property, value, receiver);
                  }
                  raw[property] = booleanDomProperties.has(property)
                    ? Boolean(value)
                    : unwrapHost(value);
                  missingProperties.delete(property);
                  if (property === 'className') invalidateClassListWriteShadow();
                  if (property === 'innerHTML' || property === 'innerText' || property === 'textContent') {
                    scheduleDisconnectedDomProxyRetentionSweep();
                  }
                  return true;
                } catch (_) {
                  missingProperties.delete(property);
                  return Reflect.set(local, property, value, receiver);
                }
              },
              defineProperty: function(local, property, descriptor) {
                missingProperties.delete(property);
                return Reflect.defineProperty(local, property, descriptor);
              },
              deleteProperty: function(local, property) {
                if (typeof property === 'string' && property.length > 2 && property.slice(0, 2) === 'on') {
                  const previous = Reflect.get(local, property);
                  if (typeof previous === 'function') {
                    try { raw.removeEventListener(property.slice(2), previous); } catch (_) {}
                  }
                }
                if (Object.prototype.hasOwnProperty.call(local, property)) {
                  return Reflect.deleteProperty(local, property);
                }
                // Native DOM members are prototype properties from JavaScript's
                // perspective. Deleting one with no JS-own shadow is a successful
                // no-op; forwarding delete to a CLR host item can incorrectly
                // report a non-configurable own member.
                return true;
              },
              has: function(local, property) {
                if (Reflect.has(local, property)) return true;
                try {
                  if (property in raw) return true;
                  const prototype = constructorForHost(raw).prototype;
                  return prototype ? property in prototype : false;
                } catch (_) { return false; }
              },
              getPrototypeOf: function() {
                return constructorForHost(raw).prototype;
              }
            });
            hostToProxy.set(target, proxy);
            proxyToHost.set(proxy, target);
            rememberDomIdentity(target, proxy, identity);
            return proxy;
          }

          function requireFrom(specifier, referrerDirectory) {
            const source = __htmlMlModuleBackend.Resolve(String(specifier), referrerDirectory || null);
            if (moduleCache.has(source.CacheKey)) return moduleCache.get(source.CacheKey).exports;
            const module = { exports: {} };
            moduleCache.set(source.CacheKey, module);
            const factorySource =
              '(function(require,exports,module,__filename,__dirname){\n' +
              source.Content + '\n})\n//# sourceURL=' + source.FileName;
            const factory = __htmlMlCompileModuleFactory(
              factorySource,
              source.FileName + '#commonjs');
            const localRequire = function(child) { return requireFrom(child, source.Directory); };
            factory.call(module.exports, localRequire, module.exports, module, source.FileName, source.Directory);
            return module.exports;
          }

          function Event(type, init) {
            init = init || {};
            this.type = String(type || '');
            this.bubbles = Boolean(init.bubbles);
            this.cancelable = Boolean(init.cancelable);
            this.composed = Boolean(init.composed);
            this.defaultPrevented = false;
            this.target = null;
            this.currentTarget = null;
            this.timeStamp = performance.now();
          }
          Event.prototype.preventDefault = function() {
            if (this.cancelable) this.defaultPrevented = true;
          };
          Event.prototype.stopPropagation = function() { this.cancelBubble = true; };
          Event.prototype.stopImmediatePropagation = function() {
            this.cancelBubble = true;
            this.__immediateStopped = true;
          };
          function DOMException(message, name) {
            this.message = message === undefined ? '' : String(message);
            this.name = name === undefined ? 'Error' : String(name);
            const error = Error(this.message);
            if (error.stack) this.stack = this.name + ': ' + this.message + '\n' +
              String(error.stack).split('\n').slice(1).join('\n');
          }
          DOMException.prototype = Object.create(Error.prototype);
          DOMException.prototype.constructor = DOMException;
          function CustomEvent(type, init) {
            Event.call(this, type, init);
            this.detail = init && init.detail;
          }
          CustomEvent.prototype = Object.create(Event.prototype);
          CustomEvent.prototype.constructor = CustomEvent;
          function UIEvent(type, init) {
            init = init || {};
            Event.call(this, type, init);
            this.view = init.view === undefined ? null : init.view;
            this.detail = Number(init.detail) || 0;
            this.which = Number(init.which) || 0;
          }
          UIEvent.prototype = Object.create(Event.prototype);
          UIEvent.prototype.constructor = UIEvent;
          function MouseEvent(type, init) { UIEvent.call(this, type, init); Object.assign(this, init || {}); }
          MouseEvent.prototype = Object.create(UIEvent.prototype);
          MouseEvent.prototype.constructor = MouseEvent;
          const PointerEvent = MouseEvent;
          const WheelEvent = MouseEvent;
          function KeyboardEvent(type, init) {
            init = init || {};
            UIEvent.call(this, type, init);
            this.key = init.key === undefined ? '' : String(init.key);
            this.code = init.code === undefined ? '' : String(init.code);
            this.location = Number(init.location) || 0;
            this.ctrlKey = Boolean(init.ctrlKey);
            this.shiftKey = Boolean(init.shiftKey);
            this.altKey = Boolean(init.altKey);
            this.metaKey = Boolean(init.metaKey);
            this.repeat = Boolean(init.repeat);
            this.isComposing = Boolean(init.isComposing);
            this.charCode = Number(init.charCode) || 0;
            this.keyCode = Number(init.keyCode) || 0;
            this.which = Number(init.which) || this.keyCode;
            this.__modifiers = {
              AltGraph: Boolean(init.modifierAltGraph),
              CapsLock: Boolean(init.modifierCapsLock),
              Fn: Boolean(init.modifierFn),
              FnLock: Boolean(init.modifierFnLock),
              Hyper: Boolean(init.modifierHyper),
              NumLock: Boolean(init.modifierNumLock),
              ScrollLock: Boolean(init.modifierScrollLock),
              Super: Boolean(init.modifierSuper),
              Symbol: Boolean(init.modifierSymbol),
              SymbolLock: Boolean(init.modifierSymbolLock)
            };
          }
          KeyboardEvent.prototype = Object.create(UIEvent.prototype);
          KeyboardEvent.prototype.constructor = KeyboardEvent;
          KeyboardEvent.prototype.getModifierState = function(keyArg) {
            const key = String(keyArg || '');
            if (key === 'Alt') return this.altKey;
            if (key === 'Control') return this.ctrlKey;
            if (key === 'Meta') return this.metaKey;
            if (key === 'Shift') return this.shiftKey;
            return Boolean(this.__modifiers && this.__modifiers[key]);
          };
          KeyboardEvent.DOM_KEY_LOCATION_STANDARD = KeyboardEvent.prototype.DOM_KEY_LOCATION_STANDARD = 0;
          KeyboardEvent.DOM_KEY_LOCATION_LEFT = KeyboardEvent.prototype.DOM_KEY_LOCATION_LEFT = 1;
          KeyboardEvent.DOM_KEY_LOCATION_RIGHT = KeyboardEvent.prototype.DOM_KEY_LOCATION_RIGHT = 2;
          KeyboardEvent.DOM_KEY_LOCATION_NUMPAD = KeyboardEvent.prototype.DOM_KEY_LOCATION_NUMPAD = 3;

          function AbortSignal() {
            this.aborted = false;
            this.reason = undefined;
            this.onabort = null;
            this.__listeners = [];
          }
          AbortSignal.prototype.addEventListener = function(type, callback, options) {
            if (String(type) !== 'abort' || typeof callback !== 'function') return;
            if (!this.__listeners.some(function(entry) { return entry.callback === callback; })) {
              this.__listeners.push({ callback: callback, once: Boolean(options && options.once) });
            }
          };
          AbortSignal.prototype.removeEventListener = function(type, callback) {
            if (String(type) !== 'abort') return;
            const index = this.__listeners.findIndex(function(entry) { return entry.callback === callback; });
            if (index >= 0) this.__listeners.splice(index, 1);
          };
          AbortSignal.prototype.throwIfAborted = function() {
            if (this.aborted) throw this.reason;
          };
          AbortSignal.abort = function(reason) {
            const controller = new AbortController();
            controller.abort(reason);
            return controller.signal;
          };
          function AbortController() { this.signal = new AbortSignal(); }
          AbortController.prototype.abort = function(reason) {
            const signal = this.signal;
            if (signal.aborted) return;
            signal.aborted = true;
            signal.reason = reason === undefined ? new Error('This operation was aborted') : reason;
            const event = new Event('abort');
            event.target = event.currentTarget = signal;
            if (typeof signal.onabort === 'function') signal.onabort.call(signal, event);
            for (const entry of signal.__listeners.slice()) {
              entry.callback.call(signal, event);
              if (entry.once) signal.removeEventListener('abort', entry.callback);
            }
          };

          function readMatrixValue(source, name, fallback) {
            const value = source && source[name];
            return typeof value === 'number' && isFinite(value) ? value : fallback;
          }
          function assignMatrix(target, a, b, c, d, e, f) {
            target.a = target.m11 = a; target.b = target.m12 = b;
            target.c = target.m21 = c; target.d = target.m22 = d;
            target.e = target.m41 = e; target.f = target.m42 = f;
            target.m13 = target.m14 = target.m23 = target.m24 = 0;
            target.m31 = target.m32 = target.m34 = target.m43 = 0;
            target.m33 = target.m44 = 1;
            target.is2D = true;
            target.isIdentity = a === 1 && b === 0 && c === 0 && d === 1 && e === 0 && f === 0;
            return target;
          }
          function multiplyMatrix(left, right) {
            return [
              left.a * right.a + left.c * right.b,
              left.b * right.a + left.d * right.b,
              left.a * right.c + left.c * right.d,
              left.b * right.c + left.d * right.d,
              left.a * right.e + left.c * right.f + left.e,
              left.b * right.e + left.d * right.f + left.f
            ];
          }
          function DOMMatrix(init) {
            if (Array.isArray(init) || (init && typeof init.length === 'number')) {
              if (init.length >= 16) {
                assignMatrix(this, Number(init[0]) || 0, Number(init[1]) || 0, Number(init[4]) || 0, Number(init[5]) || 0, Number(init[12]) || 0, Number(init[13]) || 0);
                return;
              }
              if (init.length >= 6) {
                assignMatrix(this, Number(init[0]) || 0, Number(init[1]) || 0, Number(init[2]) || 0, Number(init[3]) || 0, Number(init[4]) || 0, Number(init[5]) || 0);
                return;
              }
            }
            if (init && typeof init === 'object') {
              assignMatrix(this,
                readMatrixValue(init, 'a', readMatrixValue(init, 'm11', 1)),
                readMatrixValue(init, 'b', readMatrixValue(init, 'm12', 0)),
                readMatrixValue(init, 'c', readMatrixValue(init, 'm21', 0)),
                readMatrixValue(init, 'd', readMatrixValue(init, 'm22', 1)),
                readMatrixValue(init, 'e', readMatrixValue(init, 'm41', 0)),
                readMatrixValue(init, 'f', readMatrixValue(init, 'm42', 0)));
              return;
            }
            assignMatrix(this, 1, 0, 0, 1, 0, 0);
          }
          DOMMatrix.fromMatrix = function(source) { return new DOMMatrix(source); };
          DOMMatrix.fromFloat32Array = function(source) { return new DOMMatrix(source); };
          DOMMatrix.fromFloat64Array = function(source) { return new DOMMatrix(source); };
          DOMMatrix.prototype.multiplySelf = function(other) {
            const result = multiplyMatrix(this, new DOMMatrix(other));
            return assignMatrix(this, result[0], result[1], result[2], result[3], result[4], result[5]);
          };
          DOMMatrix.prototype.preMultiplySelf = function(other) {
            const result = multiplyMatrix(new DOMMatrix(other), this);
            return assignMatrix(this, result[0], result[1], result[2], result[3], result[4], result[5]);
          };
          DOMMatrix.prototype.multiply = function(other) { return new DOMMatrix(this).multiplySelf(other); };
          DOMMatrix.prototype.translateSelf = function(tx, ty) { return this.multiplySelf([1, 0, 0, 1, Number(tx) || 0, Number(ty) || 0]); };
          DOMMatrix.prototype.translate = function(tx, ty) { return new DOMMatrix(this).translateSelf(tx, ty); };
          DOMMatrix.prototype.scaleSelf = function(scaleX, scaleY) {
            const sx = typeof scaleX === 'number' ? scaleX : 1;
            const sy = typeof scaleY === 'number' ? scaleY : sx;
            return this.multiplySelf([sx, 0, 0, sy, 0, 0]);
          };
          DOMMatrix.prototype.scale = function(scaleX, scaleY) { return new DOMMatrix(this).scaleSelf(scaleX, scaleY); };
          DOMMatrix.prototype.rotateSelf = function(angle) {
            const radians = (Number(angle) || 0) * Math.PI / 180;
            const cos = Math.cos(radians), sin = Math.sin(radians);
            return this.multiplySelf([cos, sin, -sin, cos, 0, 0]);
          };
          DOMMatrix.prototype.rotate = function(angle) { return new DOMMatrix(this).rotateSelf(angle); };
          DOMMatrix.prototype.inverse = function() { return new DOMMatrix(this).invertSelf(); };
          DOMMatrix.prototype.invertSelf = function() {
            const determinant = this.a * this.d - this.b * this.c;
            if (!determinant || !isFinite(determinant)) return assignMatrix(this, NaN, NaN, NaN, NaN, NaN, NaN);
            return assignMatrix(this, this.d / determinant, -this.b / determinant, -this.c / determinant, this.a / determinant,
              (this.c * this.f - this.d * this.e) / determinant,
              (this.b * this.e - this.a * this.f) / determinant);
          };
          DOMMatrix.prototype.transformPoint = function(point) {
            return new DOMPoint(
              this.a * readMatrixValue(point, 'x', 0) + this.c * readMatrixValue(point, 'y', 0) + this.e,
              this.b * readMatrixValue(point, 'x', 0) + this.d * readMatrixValue(point, 'y', 0) + this.f,
              readMatrixValue(point, 'z', 0), readMatrixValue(point, 'w', 1));
          };
          DOMMatrix.prototype.toFloat32Array = function() { return Float32Array.from([this.a,this.b,0,0,this.c,this.d,0,0,0,0,1,0,this.e,this.f,0,1]); };
          DOMMatrix.prototype.toFloat64Array = function() { return Float64Array.from([this.a,this.b,0,0,this.c,this.d,0,0,0,0,1,0,this.e,this.f,0,1]); };
          function DOMPoint(x, y, z, w) {
            this.x = Number(x) || 0; this.y = Number(y) || 0; this.z = Number(z) || 0; this.w = w === undefined ? 1 : Number(w);
          }
          DOMPoint.fromPoint = function(point) { return new DOMPoint(readMatrixValue(point, 'x', 0), readMatrixValue(point, 'y', 0), readMatrixValue(point, 'z', 0), readMatrixValue(point, 'w', 1)); };
          DOMPoint.prototype.matrixTransform = function(matrix) {
            matrix = new DOMMatrix(matrix); return new DOMPoint(matrix.a * this.x + matrix.c * this.y + matrix.e, matrix.b * this.x + matrix.d * this.y + matrix.f, this.z, this.w);
          };

          function utf8Bytes(value) {
            const encoded = unescape(encodeURIComponent(String(value)));
            const bytes = new Uint8Array(encoded.length);
            for (let index = 0; index < encoded.length; index++) bytes[index] = encoded.charCodeAt(index);
            return bytes;
          }
          function blobPartBytes(part) {
            if (part instanceof Blob) return part.__bytes;
            if (part instanceof ArrayBuffer) return new Uint8Array(part);
            if (ArrayBuffer.isView(part)) {
              return new Uint8Array(part.buffer, part.byteOffset, part.byteLength);
            }
            return utf8Bytes(part == null ? '' : part);
          }
          function concatBlobParts(parts) {
            const chunks = Array.from(parts || [], blobPartBytes);
            const size = chunks.reduce(function(total, chunk) { return total + chunk.byteLength; }, 0);
            const bytes = new Uint8Array(size);
            let offset = 0;
            for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
            return bytes;
          }
          function bytesToBase64(bytes) {
            let binary = '';
            for (let offset = 0; offset < bytes.byteLength; offset += 32768) {
              binary += String.fromCharCode.apply(null, bytes.subarray(offset, offset + 32768));
            }
            return __htmlMlBase64Backend.Encode(binary);
          }
          function base64ToBytes(value) {
            const binary = __htmlMlBase64Backend.Decode(String(value || ''));
            const bytes = new Uint8Array(binary.length);
            for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index) & 255;
            return bytes;
          }
          function Blob(parts, options) {
            this.parts = Array.from(parts || []);
            this.options = options || {};
            this.type = String(this.options.type || '').toLowerCase();
            this.__bytes = concatBlobParts(this.parts);
            this.size = this.__bytes.byteLength;
          }
          Blob.prototype.arrayBuffer = function() {
            const copy = Uint8Array.from(this.__bytes);
            return Promise.resolve(copy.buffer);
          };
          Blob.prototype.text = function() {
            let binary = '';
            for (let offset = 0; offset < this.__bytes.byteLength; offset += 32768) {
              binary += String.fromCharCode.apply(null, this.__bytes.subarray(offset, offset + 32768));
            }
            return Promise.resolve(decodeURIComponent(escape(binary)));
          };
          Blob.prototype.slice = function(start, end, type) {
            return new Blob([this.__bytes.slice(start, end)], { type: type || '' });
          };
          function blobFromDataUrl(value) {
            const dataUrl = String(value || '');
            const comma = dataUrl.indexOf(',');
            if (comma < 0 || dataUrl.slice(0, 5).toLowerCase() !== 'data:') return new Blob([]);
            const metadata = dataUrl.slice(5, comma);
            const segments = metadata.split(';');
            const type = segments[0] || 'text/plain';
            const bytes = segments.some(function(segment) { return segment.toLowerCase() === 'base64'; })
              ? base64ToBytes(dataUrl.slice(comma + 1))
              : utf8Bytes(decodeURIComponent(dataUrl.slice(comma + 1)));
            return new Blob([bytes], { type: type });
          }
          function ClipboardItem(items) {
            if (!items || typeof items !== 'object') throw new TypeError('ClipboardItem data is required');
            this.__items = items;
            this.types = Object.keys(items);
          }
          ClipboardItem.prototype.getType = function(type) {
            type = String(type);
            if (!Object.prototype.hasOwnProperty.call(this.__items, type)) {
              return Promise.reject(new DOMException('Clipboard type is unavailable', 'NotFoundError'));
            }
            return Promise.resolve(this.__items[type]).then(function(value) {
              return value instanceof Blob ? value : new Blob([value], { type: type });
            });
          };
          ClipboardItem.supports = function(type) {
            return String(type).toLowerCase() === 'image/png' || String(type).toLowerCase() === 'text/plain';
          };
          function Url(url, base) {
            this.backend = new __htmlMlUrlBackend(String(url), base == null ? null : String(base));
          }
          ['href','protocol','host','hostname','port','pathname','search','hash','origin'].forEach(function(name) {
            Object.defineProperty(Url.prototype, name, { get: function() { return this.backend[name]; } });
          });
          Url.prototype.toString = function() { return String(this.backend.href); };
          Url.createObjectURL = function(blob) {
            if (blob instanceof Blob) {
              return __htmlMlUrlBackend.createObjectURLBase64(bytesToBase64(blob.__bytes), blob.type);
            }
            return __htmlMlUrlBackend.createObjectURLText(String(blob == null ? '' : blob));
          };
          Url.revokeObjectURL = function(url) { __htmlMlUrlBackend.revokeObjectURL(String(url)); };
          function TextEncoder() {}
          TextEncoder.prototype.encode = function(value) { return Uint8Array.from(unescape(encodeURIComponent(String(value))).split('').map(function(c) { return c.charCodeAt(0); })); };
          function TextDecoder() {}
          TextDecoder.prototype.decode = function(value) { return decodeURIComponent(escape(String.fromCharCode.apply(null, Array.from(value || [])))); };

          function Path2D(pathInfo) {
            this.__htmlMlNativePath2D = __htmlMlCreatePath2DBackend(
              pathInfo && pathInfo.__htmlMlNativePath2D ? pathInfo.__htmlMlNativePath2D : pathInfo);
          }
          Path2D.prototype.addPath = function(path, transform) {
            if (!path || !path.__htmlMlNativePath2D) return;
            const matrix = transform == null ? new DOMMatrix() : new DOMMatrix(transform);
            this.__htmlMlNativePath2D.addPath(
              path.__htmlMlNativePath2D,
              matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f);
          };
          Path2D.prototype.closePath = function() { this.__htmlMlNativePath2D.closePath(); };
          Path2D.prototype.moveTo = function(x, y) { this.__htmlMlNativePath2D.moveTo(x, y); };
          Path2D.prototype.lineTo = function(x, y) { this.__htmlMlNativePath2D.lineTo(x, y); };
          Path2D.prototype.bezierCurveTo = function(cp1x, cp1y, cp2x, cp2y, x, y) { this.__htmlMlNativePath2D.bezierCurveTo(cp1x, cp1y, cp2x, cp2y, x, y); };
          Path2D.prototype.quadraticCurveTo = function(cpx, cpy, x, y) { this.__htmlMlNativePath2D.quadraticCurveTo(cpx, cpy, x, y); };
          Path2D.prototype.arc = function(x, y, radius, startAngle, endAngle, counterClockwise) { this.__htmlMlNativePath2D.arc(x, y, radius, startAngle, endAngle, !!counterClockwise); };
          Path2D.prototype.arcTo = function(x1, y1, x2, y2, radius) { this.__htmlMlNativePath2D.arcTo(x1, y1, x2, y2, radius); };
          Path2D.prototype.ellipse = function() {};
          Path2D.prototype.rect = function(x, y, width, height) { this.__htmlMlNativePath2D.rect(x, y, width, height); };

          function installWindow(scope, document, frameElement, parentWindow) {
            document = wrapHost(document);
            frameElement = wrapHost(frameElement);
            const listeners = new Map();
            const timeoutIds = new Set();
            const intervalIds = new Set();
            const animationFrameIds = new Set();
            const mutationObservers = new Set();
            let browsingContextDisposed = false;
            let messageChannelSequence = 0;
            scope.__htmlMlMessageChannelTrace = [];
            function traceMessageChannel(id, phase) {
              if (!__htmlMlTraceV8CallbackErrors) return;
              const trace = scope.__htmlMlMessageChannelTrace;
              if (trace.length < 500) trace.push({ id: id, phase: phase, time: performanceObject.now() });
            }
            Object.defineProperties(scope, {
              globalThis: { value: scope, configurable: true },
              window: { value: scope, configurable: true },
              self: { value: scope, configurable: true },
              document: { value: document, configurable: true },
              frameElement: { value: frameElement || null, configurable: true },
              parent: { value: parentWindow || scope, configurable: true },
              top: { value: parentWindow || scope, configurable: true },
              location: { value: frameElement ? frameElement.ownerDocument.location : windowBackend.location, configurable: true },
              innerWidth: { get: function() { return frameElement ? frameElement.clientWidth : windowBackend.innerWidth; }, configurable: true },
              innerHeight: { get: function() { return frameElement ? frameElement.clientHeight : windowBackend.innerHeight; }, configurable: true },
              devicePixelRatio: { get: function() { return windowBackend.devicePixelRatio; }, configurable: true }
            });
            function refreshWindowNamedProperties() {
              let candidates;
              try { candidates = Array.from(document.querySelectorAll('[id]')); }
              catch (_) { return; }
              for (const candidate of candidates) {
                const name = String(candidate && candidate.id || '');
                if (!name || name in scope) continue;
                Object.defineProperty(scope, name, {
                  configurable: true,
                  enumerable: false,
                  get: function() { return document.getElementById(name); },
                  set: function(value) {
                    // Window named properties sit behind ordinary globals in
                    // browsers. An assignment made by a later script (for
                    // example testharness publishing window.test) must replace
                    // the named lookup rather than being swallowed by it.
                    Object.defineProperty(scope, name, {
                      value: value,
                      writable: true,
                      enumerable: true,
                      configurable: true
                    });
                  }
                });
              }
            }
            Object.defineProperty(scope, '__htmlMlRefreshWindowNamedProperties', {
              value: refreshWindowNamedProperties,
              configurable: true
            });
            function adaptCallback(callback) {
              return __htmlMlCallbackAdapter.GetCallback(callback, true);
            }
            scope.setTimeout = function(callback, delay) {
              if (browsingContextDisposed) return 0;
              let id = 0;
              id = windowBackend.setTimeout(adaptCallback(function() {
                timeoutIds.delete(id);
                if (!browsingContextDisposed) return callback.apply(scope, arguments);
              }), Number(delay) || 0);
              timeoutIds.add(id);
              return id;
            };
            scope.clearTimeout = function(id) {
              id = Number(id) || 0;
              timeoutIds.delete(id);
              return windowBackend.clearTimeout(id);
            };
            scope.setInterval = function(callback, delay) {
              if (browsingContextDisposed) return 0;
              const id = windowBackend.setInterval(adaptCallback(function() {
                if (!browsingContextDisposed) return callback.apply(scope, arguments);
              }), Number(delay) || 0);
              intervalIds.add(id);
              return id;
            };
            scope.clearInterval = function(id) {
              id = Number(id) || 0;
              intervalIds.delete(id);
              return windowBackend.clearInterval(id);
            };
            scope.requestAnimationFrame = function(callback) {
              if (browsingContextDisposed) return 0;
              let id = 0;
              id = windowBackend.requestAnimationFrame(adaptCallback(function() {
                animationFrameIds.delete(id);
                if (!browsingContextDisposed) return callback.apply(scope, arguments);
              }));
              animationFrameIds.add(id);
              return id;
            };
            scope.cancelAnimationFrame = function(id) {
              id = Number(id) || 0;
              animationFrameIds.delete(id);
              return windowBackend.cancelAnimationFrame(id);
            };
            scope.queueMicrotask = function(callback) {
              return Promise.resolve().then(function() {
                if (!browsingContextDisposed) return callback();
              });
            };
            scope.getComputedStyle = function(element) {
              // A virtual iframe owns an independent document/style engine.
              // Routing this through the owner window backend flushes the wrong
              // stylesheet state and can return stale or empty frame values.
              return wrapComputedStyle(document.getComputedStyle(unwrapHost(element)));
            };
            scope.matchMedia = function(query) { return windowBackend.matchMedia(String(query)); };
            scope.addEventListener = function(type, callback, options) {
              type = String(type); if (!callback) return;
              let entries = listeners.get(type); if (!entries) listeners.set(type, entries = []);
              const capture = options === true || Boolean(options && options.capture);
              if (!entries.some(function(entry) { return entry.callback === callback && entry.capture === capture; })) {
                entries.push({ callback: callback, capture: capture, once: Boolean(options && options.once) });
              }
            };
            scope.removeEventListener = function(type, callback, options) {
              const entries = listeners.get(String(type)); if (!entries) return;
              const capture = options === true || Boolean(options && options.capture);
              const index = entries.findIndex(function(entry) { return entry.callback === callback && entry.capture === capture; });
              if (index >= 0) entries.splice(index, 1);
            };
            scope.dispatchEvent = function(event) {
              if (!event || !event.type) throw new TypeError('Event type is required');
              try { event.target = event.target || scope; event.currentTarget = scope; } catch (_) {}
              const property = scope['on' + event.type];
              if (typeof property === 'function') property.call(scope, event);
              const entries = (listeners.get(String(event.type)) || []).slice();
              for (const entry of entries) {
                entry.callback.call(scope, event);
                if (entry.once) scope.removeEventListener(event.type, entry.callback, entry.capture);
                if (event.__immediateStopped) break;
              }
              return !event.defaultPrevented;
            };
            scope.postMessage = function(message) {
              scope.setTimeout(function() { scope.dispatchEvent({ type: 'message', data: message, source: scope, origin: String(scope.location.origin || 'null') }); }, 0);
            };

            scope.Event = Event; scope.DOMException = DOMException;
            scope.CustomEvent = CustomEvent; scope.UIEvent = UIEvent;
            scope.MouseEvent = MouseEvent; scope.KeyboardEvent = KeyboardEvent;
            scope.PointerEvent = PointerEvent; scope.WheelEvent = WheelEvent;
            scope.AbortSignal = AbortSignal; scope.AbortController = AbortController;
            scope.DOMStringMap = DOMStringMap;
            scope.Blob = Blob; scope.URL = Url; scope.TextEncoder = TextEncoder; scope.TextDecoder = TextDecoder;
            scope.DOMMatrix = DOMMatrix; scope.DOMMatrixReadOnly = DOMMatrix;
            scope.DOMPoint = DOMPoint; scope.DOMPointReadOnly = DOMPoint; scope.Path2D = Path2D;
            scope.ClipboardItem = ClipboardItem;
            const navigatorBackend = windowBackend.navigator;
            const clipboardBackend = navigatorBackend.clipboard;
            const clipboardFacade = {
              readText: function() { return Promise.resolve(String(clipboardBackend.readText() || '')); },
              writeText: function(value) {
                return clipboardBackend.writeText(String(value))
                  ? Promise.resolve()
                  : Promise.reject(new DOMException('Clipboard write failed', 'NotAllowedError'));
              },
              write: async function(items) {
                for (const item of Array.from(items || [])) {
                  for (const type of Array.from(item && item.types || [])) {
                    const blob = await item.getType(type);
                    if (String(type).toLowerCase() === 'text/plain') {
                      if (!clipboardBackend.writeText(await blob.text())) {
                        throw new DOMException('Clipboard write failed', 'NotAllowedError');
                      }
                    } else if (!clipboardBackend.writeBase64(String(type), bytesToBase64(blob.__bytes))) {
                      throw new DOMException('Clipboard write failed', 'NotAllowedError');
                    }
                  }
                }
              }
            };
            scope.navigator = new Proxy({}, {
              get: function(_, property) {
                if (property === 'clipboard') return clipboardFacade;
                const value = navigatorBackend[property];
                return typeof value === 'function' ? value.bind(navigatorBackend) : value;
              },
              has: function(_, property) { return property === 'clipboard' || property in navigatorBackend; }
            });
            scope.screen = windowBackend.screen;
            scope.console = console;
            scope.performance = performanceObject;
            scope.atob = function(value) { return __htmlMlBase64Backend.Decode(String(value)); };
            scope.btoa = function(value) { return __htmlMlBase64Backend.Encode(String(value)); };
            scope.crypto = { getRandomValues: function(array) { for (let i = 0; i < array.length; i++) array[i] = Math.floor(Math.random() * 256); return array; } };
            scope.CSS = { supports: function() { return false; }, escape: function(value) { return String(value).replace(/[^a-zA-Z0-9_-]/g, function(c) { return '\\' + c; }); } };
            scope.localStorage = scope.sessionStorage = (function() {
              const values = new Map();
              return { getItem: function(k) { k = String(k); return values.has(k) ? values.get(k) : null; }, setItem: function(k,v) { values.set(String(k), String(v)); }, removeItem: function(k) { values.delete(String(k)); }, clear: function() { values.clear(); } };
            })();
            scope.Image = function() { return wrapHost(document.createElement('img')); };
            scope.DOMParser = function() {};
            scope.DOMParser.prototype.parseFromString = function(markup, mimeType) {
              return wrapHost(__htmlMlDomParserBackend.Parse(
                unwrapHost(document),
                String(markup),
                String(mimeType || 'text/html')));
            };
            scope.XMLSerializer = function() {};
            scope.XMLSerializer.prototype.serializeToString = function(node) {
              if (node == null) return '';
              if (node.documentElement) node = node.documentElement;
              return String(node.outerHTML || node.textContent || '');
            };
            scope.MutationObserver = function(callback) {
              const observer = this;
              mutationObservers.add(this);
              scope.__htmlMlExternalMutationObservers = (scope.__htmlMlExternalMutationObservers || 0) + 1;
              this.backend = document.__htmlMlCreateExternalMutationObserver(adaptCallback(function(records) {
                scope.__htmlMlExternalMutationDeliveries = (scope.__htmlMlExternalMutationDeliveries || 0) + 1;
                callback(Array.from(records, wrapHost), observer);
              }));
              this.backend.__htmlMlSetExternalObserver(this);
            };
            scope.MutationObserver.prototype.observe = function(target, options) {
              options = options || {};
              mutationObservers.add(this);
              scope.__htmlMlExternalMutationObservations = (scope.__htmlMlExternalMutationObservations || 0) + 1;
              this.backend.__htmlMlObserve(unwrapHost(target), Boolean(options.childList), Boolean(options.attributes), Boolean(options.subtree), Boolean(options.attributeOldValue));
            };
            scope.MutationObserver.prototype.disconnect = function() {
              this.backend.disconnect();
              mutationObservers.delete(this);
            };
            scope.MutationObserver.prototype.takeRecords = function() { return Array.from(this.backend.takeRecords()); };
            function resizeObserverRect(target) {
              if (!target || typeof target.getBoundingClientRect !== 'function') {
                return { x: 0, y: 0, width: 0, height: 0, top: 0, right: 0, bottom: 0, left: 0 };
              }
              const source = target.getBoundingClientRect();
              const x = Number(source.x !== undefined ? source.x : source.left || 0);
              const y = Number(source.y !== undefined ? source.y : source.top || 0);
              const width = Number(source.width || 0);
              const height = Number(source.height || 0);
              return {
                x: x, y: y, width: width, height: height,
                top: Number(source.top !== undefined ? source.top : y),
                right: Number(source.right !== undefined ? source.right : x + width),
                bottom: Number(source.bottom !== undefined ? source.bottom : y + height),
                left: Number(source.left !== undefined ? source.left : x)
              };
            }
            function sameResizeObserverSize(left, right) {
              return Boolean(left) && Boolean(right) &&
                Math.abs(left.width - right.width) < 0.001 &&
                Math.abs(left.height - right.height) < 0.001;
            }
            function ResizeObserverSize(inlineSize, blockSize) {
              this.inlineSize = inlineSize;
              this.blockSize = blockSize;
            }
            function ResizeObserverEntry(target, rect) {
              const size = new ResizeObserverSize(rect.width, rect.height);
              const ratio = Number(scope.devicePixelRatio || 1);
              const deviceSize = new ResizeObserverSize(
                Math.round(rect.width * ratio),
                Math.round(rect.height * ratio));
              this.target = target;
              this.contentRect = rect;
              this.contentBoxSize = [size];
              this.borderBoxSize = [size];
              this.devicePixelContentBoxSize = [deviceSize];
            }
            ResizeObserverEntry.prototype.target = null;
            ResizeObserverEntry.prototype.contentRect = null;
            ResizeObserverEntry.prototype.contentBoxSize = null;
            ResizeObserverEntry.prototype.borderBoxSize = null;
            ResizeObserverEntry.prototype.devicePixelContentBoxSize = null;
            scope.ResizeObserverSize = ResizeObserverSize;
            scope.ResizeObserverEntry = ResizeObserverEntry;
            const resizeObservers = new Set();
            function deliverNativeResizeObservers() {
              for (const observer of Array.from(resizeObservers)) observer._deliver();
            }
            scope.__htmlMlDisconnectResizeObservers = function() {
              for (const observer of Array.from(resizeObservers)) observer.disconnect();
              resizeObservers.clear();
            };
            scope.ResizeObserver = function(callback) {
              if (typeof callback !== 'function') throw new TypeError('ResizeObserver callback must be a function');
              this._callback = callback;
              this._observations = [];
              this._timer = 0;
            };
            scope.ResizeObserver.prototype._hasPolledObservation = function() {
              return this._observations.some(function(observation) { return !observation.nativeCallback; });
            };
            scope.ResizeObserver.prototype._schedule = function() {
              const observer = this;
              if (observer._timer || !observer._hasPolledObservation()) return;
              observer._timer = scope.setInterval(function() { observer._deliver(); }, 16);
            };
            scope.ResizeObserver.prototype._stopPollingIfUnused = function() {
              if (this._timer && !this._hasPolledObservation()) {
                scope.clearInterval(this._timer);
                this._timer = 0;
              }
            };
            scope.ResizeObserver.prototype._deliver = function() {
              if (this._observations.length === 0) return;
              const entries = [];
              for (const observation of this._observations) {
                const rect = resizeObserverRect(observation.target);
                if (!sameResizeObserverSize(observation.lastRect, rect)) {
                  observation.lastRect = rect;
                  entries.push(new ResizeObserverEntry(observation.target, rect));
                }
              }
              if (entries.length) this._callback(entries, this);
            };
            scope.ResizeObserver.prototype.observe = function(target) {
              if (!target) throw new TypeError('ResizeObserver.observe target is required');
              if (this._observations.some(function(observation) { return observation.target === target; })) return;
              resizeObservers.add(this);
              const observation = { target: target, lastRect: null, nativeCallback: null };
              this._observations.push(observation);
              if (__htmlMlEnableNativeResizeObserverNotifications &&
                  typeof target.__htmlMlObserveResize === 'function') {
                observation.nativeCallback = deliverNativeResizeObservers;
                target.__htmlMlObserveResize(observation.nativeCallback);
              } else {
                const observer = this;
                scope.setTimeout(function() { observer._deliver(); }, 0);
                this._schedule();
              }
            };
            scope.ResizeObserver.prototype.unobserve = function(target) {
              const index = this._observations.findIndex(function(observation) { return observation.target === target; });
              if (index < 0) return;
              const observation = this._observations[index];
              this._observations.splice(index, 1);
              if (observation.nativeCallback && typeof target.__htmlMlUnobserveResize === 'function') {
                target.__htmlMlUnobserveResize(observation.nativeCallback);
              }
              this._stopPollingIfUnused();
            };
            scope.ResizeObserver.prototype.disconnect = function() {
              for (const observation of this._observations.slice()) this.unobserve(observation.target);
              if (this._timer) {
                scope.clearInterval(this._timer);
                this._timer = 0;
              }
              resizeObservers.delete(this);
            };
            function normalizeIntersectionThresholds(value) {
              const source = value === undefined ? [0] : Array.isArray(value) ? value : [value];
              const thresholds = Array.from(new Set(source.map(function(item) {
                const threshold = Number(item);
                if (!Number.isFinite(threshold) || threshold < 0 || threshold > 1) {
                  throw new RangeError('IntersectionObserver threshold must be between 0 and 1');
                }
                return threshold;
              }))).sort(function(left, right) { return left - right; });
              return thresholds.length ? thresholds : [0];
            }
            function normalizeIntersectionRootMargin(value) {
              const parts = String(value === undefined ? '0px' : value).trim().split(/\s+/).filter(Boolean);
              if (parts.length < 1 || parts.length > 4 || parts.some(function(part) {
                return !/^-?(?:\d+|\d*\.\d+)(?:px|%)$/.test(part);
              })) {
                throw new SyntaxError('IntersectionObserver rootMargin must use px or % lengths');
              }
              if (parts.length === 1) parts.push(parts[0], parts[0], parts[0]);
              else if (parts.length === 2) parts.push(parts[0], parts[1]);
              else if (parts.length === 3) parts.push(parts[1]);
              return parts.join(' ');
            }
            function intersectionMarginPixels(value, referenceLength) {
              return value.endsWith('%')
                ? parseFloat(value) * referenceLength / 100
                : parseFloat(value);
            }
            function intersectionRootRect(observer) {
              const base = observer.root
                ? resizeObserverRect(observer.root)
                : {
                    x: 0, y: 0, left: 0, top: 0,
                    width: Number(scope.innerWidth || 0),
                    height: Number(scope.innerHeight || 0),
                    right: Number(scope.innerWidth || 0),
                    bottom: Number(scope.innerHeight || 0)
                  };
              const margin = observer.rootMargin.split(/\s+/);
              const top = intersectionMarginPixels(margin[0], base.height);
              const right = intersectionMarginPixels(margin[1], base.width);
              const bottom = intersectionMarginPixels(margin[2], base.height);
              const left = intersectionMarginPixels(margin[3], base.width);
              return {
                x: base.left - left,
                y: base.top - top,
                left: base.left - left,
                top: base.top - top,
                right: base.right + right,
                bottom: base.bottom + bottom,
                width: Math.max(0, base.width + left + right),
                height: Math.max(0, base.height + top + bottom)
              };
            }
            function IntersectionObserverEntry(target, rootBounds, targetRect, intersectionRect, isIntersecting, ratio) {
              this.time = performanceObject.now();
              this.target = target;
              this.rootBounds = rootBounds;
              this.boundingClientRect = targetRect;
              this.intersectionRect = intersectionRect;
              this.isIntersecting = isIntersecting;
              this.intersectionRatio = ratio;
            }
            IntersectionObserverEntry.prototype.time = 0;
            IntersectionObserverEntry.prototype.target = null;
            IntersectionObserverEntry.prototype.rootBounds = null;
            IntersectionObserverEntry.prototype.boundingClientRect = null;
            IntersectionObserverEntry.prototype.intersectionRect = null;
            IntersectionObserverEntry.prototype.isIntersecting = false;
            IntersectionObserverEntry.prototype.intersectionRatio = 0;
            const intersectionObservers = new Set();
            scope.IntersectionObserverEntry = IntersectionObserverEntry;
            scope.IntersectionObserver = function(callback, options) {
              if (typeof callback !== 'function') {
                throw new TypeError('IntersectionObserver callback must be a function');
              }
              options = options || {};
              this.root = options.root == null ? null : options.root;
              this.rootMargin = normalizeIntersectionRootMargin(options.rootMargin);
              this.thresholds = normalizeIntersectionThresholds(options.threshold);
              this._callback = callback;
              this._observations = [];
              this._timer = 0;
              this._queuedEntries = [];
            };
            scope.IntersectionObserver.prototype._thresholdBucket = function(ratio) {
              let bucket = 0;
              while (bucket < this.thresholds.length && ratio >= this.thresholds[bucket]) bucket++;
              return bucket;
            };
            scope.IntersectionObserver.prototype._check = function() {
              if (!this._observations.length) return;
              const rootBounds = intersectionRootRect(this);
              const entries = [];
              for (const observation of this._observations) {
                const targetRect = resizeObserverRect(observation.target);
                const left = Math.max(rootBounds.left, targetRect.left);
                const top = Math.max(rootBounds.top, targetRect.top);
                const right = Math.min(rootBounds.right, targetRect.right);
                const bottom = Math.min(rootBounds.bottom, targetRect.bottom);
                const width = Math.max(0, right - left);
                const height = Math.max(0, bottom - top);
                const isIntersecting = width > 0 && height > 0;
                const targetArea = Math.max(0, targetRect.width) * Math.max(0, targetRect.height);
                const ratio = targetArea > 0 ? width * height / targetArea : isIntersecting ? 1 : 0;
                const bucket = this._thresholdBucket(ratio);
                if (!observation.initialized || observation.isIntersecting !== isIntersecting || observation.bucket !== bucket) {
                  observation.initialized = true;
                  observation.isIntersecting = isIntersecting;
                  observation.bucket = bucket;
                  entries.push(new IntersectionObserverEntry(
                    observation.target,
                    rootBounds,
                    targetRect,
                    {
                      x: left, y: top, left: left, top: top,
                      right: right, bottom: bottom, width: width, height: height
                    },
                    isIntersecting,
                    ratio));
                }
              }
              if (entries.length) {
                this._callback(entries, this);
                // A callback may synchronously change target geometry. Queue
                // one follow-up sampling task, matching the next rendering
                // update without requiring a busy polling loop.
                const observer = this;
                scope.queueMicrotask(function() { observer._check(); });
              }
            };
            scope.IntersectionObserver.prototype.observe = function(target) {
              if (!target) throw new TypeError('IntersectionObserver.observe target is required');
              if (this._observations.some(function(observation) { return observation.target === target; })) return;
              this._observations.push({ target: target, initialized: false, isIntersecting: false, bucket: -1 });
              intersectionObservers.add(this);
              const observer = this;
              scope.setTimeout(function() { observer._check(); }, 0);
              if (!this._timer) this._timer = scope.setInterval(function() { observer._check(); }, 32);
            };
            scope.IntersectionObserver.prototype.unobserve = function(target) {
              const index = this._observations.findIndex(function(observation) { return observation.target === target; });
              if (index >= 0) this._observations.splice(index, 1);
              if (!this._observations.length && this._timer) {
                scope.clearInterval(this._timer);
                this._timer = 0;
              }
            };
            scope.IntersectionObserver.prototype.disconnect = function() {
              this._observations.length = 0;
              this._queuedEntries.length = 0;
              if (this._timer) {
                scope.clearInterval(this._timer);
                this._timer = 0;
              }
              intersectionObservers.delete(this);
            };
            scope.IntersectionObserver.prototype.takeRecords = function() {
              const records = this._queuedEntries.slice();
              this._queuedEntries.length = 0;
              return records;
            };
            scope.__htmlMlDisconnectIntersectionObservers = function() {
              for (const observer of Array.from(intersectionObservers)) observer.disconnect();
              intersectionObservers.clear();
            };
            scope.__htmlMlDisposeBrowsingContext = function() {
              if (browsingContextDisposed) return;
              browsingContextDisposed = true;
              for (const id of Array.from(timeoutIds)) windowBackend.clearTimeout(id);
              timeoutIds.clear();
              for (const id of Array.from(intervalIds)) windowBackend.clearInterval(id);
              intervalIds.clear();
              for (const id of Array.from(animationFrameIds)) windowBackend.cancelAnimationFrame(id);
              animationFrameIds.clear();
              scope.__htmlMlDisconnectResizeObservers();
              scope.__htmlMlDisconnectIntersectionObservers();
              for (const observer of Array.from(mutationObservers)) observer.disconnect();
              mutationObservers.clear();
              listeners.clear();
            };
            scope.MessagePort = function MessagePort() {
              this.onmessage = null;
              this._listeners = [];
              this._peer = null;
              this._closed = false;
              this._channelId = 0;
            };
            scope.MessagePort.prototype.postMessage = function(message) {
              if (this._closed || !this._peer || this._peer._closed) return;
              const target = this._peer;
              traceMessageChannel(this._channelId, 'post');
              scope.setTimeout(function() {
                if (target._closed) return;
                traceMessageChannel(target._channelId, 'deliver');
                const event = {
                  type: 'message', data: message, target: target, currentTarget: target,
                  ports: [], origin: '', lastEventId: ''
                };
                if (typeof target.onmessage === 'function') target.onmessage.call(target, event);
                const listeners = target._listeners.slice();
                for (let index = 0; index < listeners.length; index++) listeners[index].call(target, event);
              }, 0);
            };
            scope.MessagePort.prototype.addEventListener = function(type, listener) {
              if (type === 'message' && typeof listener === 'function' && this._listeners.indexOf(listener) < 0) {
                this._listeners.push(listener);
              }
            };
            scope.MessagePort.prototype.removeEventListener = function(type, listener) {
              if (type !== 'message') return;
              const index = this._listeners.indexOf(listener);
              if (index >= 0) this._listeners.splice(index, 1);
            };
            scope.MessagePort.prototype.start = function() {};
            scope.MessagePort.prototype.close = function() { this._closed = true; };
            scope.MessageChannel = function MessageChannel() {
              const channelId = ++messageChannelSequence;
              this.port1 = new scope.MessagePort();
              this.port2 = new scope.MessagePort();
              this.port1._channelId = channelId;
              this.port2._channelId = channelId;
              this.port1._peer = this.port2;
              this.port2._peer = this.port1;
            };
            domConstructors = Object.create(null);
            function defineDomConstructor(name, parentName, tagName) {
              const parent = parentName && domConstructors[parentName];
              const ctor = function() {};
              if (parent) {
                ctor.prototype = Object.create(parent.prototype);
                Object.defineProperty(ctor.prototype, 'constructor', {
                  value: ctor, writable: true, configurable: true
                });
              }
              try { Object.defineProperty(ctor, Symbol.hasInstance, { value: function(value) {
                if (name === 'Window') return value === scope;
                const raw = unwrapHost(value);
                let nodeType;
                try { nodeType = raw && raw.nodeType; } catch (_) { return false; }
                if (name === 'Node') return typeof nodeType === 'number';
                if (name === 'Document' || name === 'HTMLDocument') return nodeType === 9;
                if (name === 'Element') return nodeType === 1;
                if (name === 'SVGElement') return nodeType === 1 && isSvgHost(raw);
                if (name === 'SVGSVGElement') {
                  return nodeType === 1 && isSvgHost(raw) && String(raw.nodeName).toUpperCase() === 'SVG';
                }
                if (name === 'HTMLElement') return nodeType === 1 && !isSvgHost(raw);
                return nodeType === 1 && !isSvgHost(raw)
                  && String(raw.nodeName).toUpperCase() === tagName;
              } }); } catch (_) {}
              domConstructors[name] = ctor;
              scope[name] = ctor;
              return ctor;
            }
            defineDomConstructor('Node', null, null);
            defineDomConstructor('Document', 'Node', null);
            defineDomConstructor('HTMLDocument', 'Document', null);
            defineDomConstructor('Element', 'Node', null);
            defineDomConstructor('HTMLElement', 'Element', null);
            Object.keys(htmlElementConstructorNames).forEach(function(tagName) {
              defineDomConstructor(htmlElementConstructorNames[tagName], 'HTMLElement', tagName);
            });
            defineDomConstructor('SVGElement', 'Element', null);
            defineDomConstructor('SVGSVGElement', 'SVGElement', 'SVG');
            defineDomConstructor('Window', null, null);
            function installReflectedAccessor(constructorName, property) {
              const ctor = domConstructors[constructorName];
              Object.defineProperty(ctor.prototype, property, {
                configurable: true,
                enumerable: true,
                get: function() {
                  const raw = unwrapHost(this);
                  return raw == null ? undefined : wrapResult(raw[property]);
                },
                set: function(value) {
                  const raw = unwrapHost(this);
                  if (raw != null) raw[property] = unwrapHost(value);
                }
              });
            }
            installReflectedAccessor('HTMLInputElement', 'value');
            installReflectedAccessor('HTMLInputElement', 'checked');
            installReflectedAccessor('HTMLTextAreaElement', 'value');
            installReflectedAccessor('HTMLSelectElement', 'value');
            installReflectedAccessor('HTMLOptionElement', 'selected');
            function installAssociatedFormAccessor(constructorName) {
              const ctor = domConstructors[constructorName];
              Object.defineProperty(ctor.prototype, 'form', {
                configurable: true,
                enumerable: true,
                get: function() {
                  let current = this.parentElement;
                  while (current) {
                    if (String(current.tagName).toUpperCase() === 'FORM') return current;
                    current = current.parentElement;
                  }
                  return null;
                }
              });
            }
            installAssociatedFormAccessor('HTMLButtonElement');
            installAssociatedFormAccessor('HTMLInputElement');
            installAssociatedFormAccessor('HTMLSelectElement');
            installAssociatedFormAccessor('HTMLTextAreaElement');
            const canvasPrototype = domConstructors.HTMLCanvasElement.prototype;
            Object.defineProperty(canvasPrototype, 'toDataURL', {
              configurable: true,
              writable: true,
              value: function(type, quality) {
                const raw = unwrapHost(this);
                const batch = canvasHostToBatch.get(raw);
                if (batch) batch.flush();
                if (arguments.length === 0) return String(raw.__htmlMlCanvasToDataURL());
                if (arguments.length === 1) return String(raw.__htmlMlCanvasToDataURL(String(type)));
                return String(raw.__htmlMlCanvasToDataURL(String(type), Number(quality)));
              }
            });
            Object.defineProperty(canvasPrototype, 'toBlob', {
              configurable: true,
              writable: true,
              value: function(callback, type, quality) {
                if (typeof callback !== 'function') throw new TypeError('A callback is required');
                const dataUrl = arguments.length < 2
                  ? this.toDataURL()
                  : this.toDataURL(type, quality);
                const blob = blobFromDataUrl(dataUrl);
                scope.setTimeout(function() { callback(blob); }, 0);
              }
            });
            return scope;
          }

          globalThis.require = function(specifier) { return requireFrom(specifier, null); };
          globalThis.__htmlMlRequireFrom = requireFrom;
          globalThis.__htmlMlFlushCanvases = flushCanvasBatches;
          globalThis.__htmlMlWrapHostObject = wrapHost;
          installWindow(globalThis, ownerDocument, null, null);

          globalThis.__htmlMlCreateFrameRuntime = function(frameDocument, frameElement, frameLocation, parentWindow) {
            const frame = globalThis;
            installWindow(frame, frameDocument, frameElement, parentWindow);
            const locationView = {};
            function browserFrameHref() {
              const href = String(frameLocation.href || '');
              const queryIndex = href.indexOf('?');
              // The browser-facing chart frame uses the opaque blob fragment
              // for its bootstrap payload. HtmlML's resource URL keeps the
              // equivalent query suffix so object-URL child resolution works.
              return href.startsWith('blob:') && queryIndex >= 0 && href.indexOf('#') < 0
                ? href + '#' + href.slice(queryIndex + 1)
                : href;
            }
            Object.defineProperties(locationView, {
              href: { get: browserFrameHref, set: function(value) { frameLocation.href = String(value); } },
              protocol: { get: function() { return String(frameLocation.protocol || ''); } },
              host: { get: function() { return String(frameLocation.host || ''); } },
              hostname: { get: function() { return String(frameLocation.hostname || ''); } },
              port: { get: function() { return String(frameLocation.port || ''); } },
              pathname: { get: function() { return String(frameLocation.pathname || ''); } },
              search: { get: function() { return String(frameLocation.search || ''); } },
              hash: { get: function() { const href = browserFrameHref(); const index = href.indexOf('#'); return index >= 0 ? href.slice(index) : String(frameLocation.hash || ''); } },
              origin: { get: function() { return String(parentWindow && parentWindow.location && parentWindow.location.origin || 'null'); } }
            });
            locationView.assign = function(value) { frameLocation.href = String(value); };
            locationView.replace = locationView.assign;
            locationView.reload = function() {};
            locationView.toString = browserFrameHref;
            Object.defineProperty(frame, 'location', { value: locationView, configurable: true });
            Object.defineProperties(frame.document, {
              location: { value: locationView, configurable: true },
              URL: { value: browserFrameHref(), configurable: true },
              documentURI: { value: browserFrameHref(), configurable: true },
              baseURI: { value: browserFrameHref(), configurable: true },
              referrer: { value: String(parentWindow && parentWindow.location && parentWindow.location.href || ''), configurable: true }
            });
            return {
              window: frame,
              refreshNamedProperties: frame.__htmlMlRefreshWindowNamedProperties,
              dispatch: function(type, eventObject) {
                const event = wrapHost(eventObject);
                try { if (!event.type) event.type = String(type); } catch (_) {}
                return frame.dispatchEvent(event);
              },
              describe: function() {
                return JSON.stringify({
                  href: String(frame.location && frame.location.href || ''),
                  documentLocationSame: frame.document.location === frame.location,
                  search: String(frame.location && frame.location.search || ''),
                  protocol: String(frame.location && frame.location.protocol || ''),
                  parentIsSelf: frame.parent === frame,
                  topIsParent: frame.top === frame.parent,
                  hasFrameElement: Boolean(frame.frameElement),
                  defaultViewIsWindow: frame.document && frame.document.defaultView === frame,
                  referrer: String(frame.document && frame.document.referrer || ''),
                  readyState: String(frame.document && frame.document.readyState || ''),
                  urlParams: (function() {
                    const value = frame.urlParams;
                    return {
                      type: typeof value,
                      uid: value && value.uid == null ? '' : String(value && value.uid || ''),
                      keys: value && typeof value === 'object' ? Object.keys(value).slice(0, 24) : []
                    };
                  })(),
                  ownerRegistration: (function() {
                    const href = String(frame.location && frame.location.href || '');
                    const match = /(?:#|&)uid=([^&]+)/.exec(href);
                    const uid = frame.urlParams && frame.urlParams.uid != null
                      ? String(frame.urlParams.uid)
                      : (match ? decodeURIComponent(match[1]) : '');
                    const registration = uid && frame.parent ? frame.parent[uid] : undefined;
                    return {
                      uid: uid,
                      present: registration !== undefined,
                      type: typeof registration,
                      keys: registration && typeof registration === 'object' ? Object.keys(registration).slice(0, 24) : []
                    };
                  })()
                });
              }
            };
          };
        })();
        """;
}
