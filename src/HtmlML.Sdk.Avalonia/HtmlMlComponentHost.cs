using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using HtmlML.Core;
using HtmlML.Sdk;
using JavaScript.Avalonia;
using JavaScript.Avalonia.ClearScript;

namespace HtmlML.Sdk.Avalonia;

/// <summary>XAML-first host for one isolated packaged HtmlML component instance.</summary>
public sealed class HtmlMlComponentHost : ContentControl, IDisposable
{
    public static readonly StyledProperty<string?> PackagePathProperty =
        AvaloniaProperty.Register<HtmlMlComponentHost, string?>(nameof(PackagePath));

    public static readonly StyledProperty<bool> AutoMountProperty =
        AvaloniaProperty.Register<HtmlMlComponentHost, bool>(nameof(AutoMount), defaultValue: true);

    private static readonly HtmlMlSharedAssetCache s_assetCache = new();
    private readonly List<IHtmlMlHostCapabilityHandler> _handlers = [];
    private readonly HtmlMlDiagnosticCollector _diagnostics = new();
    private AvaloniaBrowserHost? _browserHost;
    private ClearScriptV8Runtime? _runtime;
    private HtmlMlJavaScriptHostBridgeAdapter? _bridgeAdapter;
    private HtmlMlComponentInstance? _instance;
    private bool _disposed;

    public string? PackagePath
    {
        get => GetValue(PackagePathProperty);
        set => SetValue(PackagePathProperty, value);
    }

    public bool AutoMount
    {
        get => GetValue(AutoMountProperty);
        set => SetValue(AutoMountProperty, value);
    }

    public HtmlMlComponentState? ComponentState => _instance?.State;

    public IReadOnlyList<HtmlMlSdkDiagnostic> Diagnostics => _diagnostics.Diagnostics;

    public event EventHandler<HtmlMlSdkDiagnostic>? DiagnosticReported;

    public void RegisterHostCapability(IHtmlMlHostCapabilityHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        if (_runtime is not null)
        {
            throw new InvalidOperationException("Host capabilities must be registered before mounting the component.");
        }
        if (_handlers.Any(existing => string.Equals(existing.Capability, handler.Capability, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Capability '{handler.Capability}' is already registered.");
        }
        _handlers.Add(handler);
    }

    public void MountComponent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_instance?.State == HtmlMlComponentState.Mounted)
        {
            return;
        }
        var packagePath = PackagePath;
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new InvalidOperationException("PackagePath is required before mounting an HtmlML component.");
        }
        var topLevel = TopLevel.GetTopLevel(this)
                       ?? throw new InvalidOperationException("The component host must be attached to a TopLevel before mounting.");
        var root = Content as Control;
        if (root is null)
        {
            root = new Panel();
            Content = root;
        }

        var package = HtmlMlComponentPackage.Open(ResolvePackagePath(packagePath), s_assetCache);
        EnsureBackendCapabilities(package.Manifest);
        var entryPoint = package.GetEntryPoint();
        var source = System.Text.Encoding.UTF8.GetString(entryPoint.Content.Span);
        var compatibility = HtmlMlCompatibilityChecker.Check(source, package.Manifest, package.Manifest.EntryPoint);
        foreach (var diagnostic in compatibility.Diagnostics)
        {
            Report(new HtmlMlSdkDiagnostic(
                diagnostic.Code,
                diagnostic.Severity == HtmlMlCompatibilitySeverity.Error ? HtmlMlDiagnosticSeverity.Error : HtmlMlDiagnosticSeverity.Warning,
                $"{diagnostic.Source}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Message}",
                package.Manifest.Id));
        }
        if (!compatibility.IsCompatible)
        {
            throw new InvalidDataException($"Component '{package.Manifest.Id}' uses APIs outside HtmlML Component Profile 1.");
        }

        try
        {
            _browserHost = new AvaloniaBrowserHost(topLevel, host => new ComponentDocument(host, root));
            _runtime = new ClearScriptV8Runtime(_browserHost);
            var bridge = new HtmlMlHostBridge(package.Manifest, _handlers, new ForwardingDiagnosticSink(this));
            _bridgeAdapter = new HtmlMlJavaScriptHostBridgeAdapter(bridge, _runtime, _browserHost.Services.Dispatcher);
            _runtime.Engine.AddHostObject("__htmlMlHostBridge", _bridgeAdapter);
            _runtime.Execute(HtmlMlHostBridgeBootstrap.Script, "htmlml-host-bridge.js");
            _runtime.Execute(source, package.Manifest.EntryPoint);
            _instance = package.CreateInstance(new ForwardingDiagnosticSink(this));
            _instance.Mount();
            Report(new HtmlMlSdkDiagnostic(
                "component.asset",
                HtmlMlDiagnosticSeverity.Info,
                $"Loaded {package.Manifest.EntryPoint} ({entryPoint.Content.Length} bytes, sha256 {entryPoint.Sha256}).",
                package.Manifest.Id));
            Report(new HtmlMlSdkDiagnostic(
                "runtime.native",
                HtmlMlDiagnosticSeverity.Info,
                $"RID {RuntimeInformation.RuntimeIdentifier}; ClearScript {typeof(ClearScriptV8Runtime).Assembly.GetName().Version}; native override '{Environment.GetEnvironmentVariable("HTMLML_CLEARSCRIPT_NATIVE") ?? "<package resolver>"}'.",
                package.Manifest.Id));
            _runtime.Execute(
                $"globalThis[{System.Text.Json.JsonSerializer.Serialize(package.Manifest.Lifecycle.MountExport)}]?.({{ instanceId: {System.Text.Json.JsonSerializer.Serialize(_instance.InstanceId.ToString("D"))} }});",
                "htmlml-component-mount.js");
        }
        catch
        {
            UnloadComponent(invokeLifecycle: false);
            throw;
        }
    }

    public void UnmountComponent() => UnloadComponent(invokeLifecycle: true);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (AutoMount && _runtime is null)
        {
            MountComponent();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnloadComponent(invokeLifecycle: true);
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        UnloadComponent(invokeLifecycle: true);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void UnloadComponent(bool invokeLifecycle)
    {
        if (invokeLifecycle && _runtime is not null && _instance is not null)
        {
            var export = _instance.Package.Manifest.Lifecycle.UnmountExport;
            try
            {
                _runtime.Execute($"globalThis[{System.Text.Json.JsonSerializer.Serialize(export)}]?.();", "htmlml-component-unmount.js");
            }
            catch (Exception exception)
            {
                Report(new HtmlMlSdkDiagnostic("component.unmount.error", HtmlMlDiagnosticSeverity.Error, exception.Message, _instance.Package.Manifest.Id));
            }
        }
        if (_instance?.State == HtmlMlComponentState.Mounted)
        {
            _instance.Unmount();
        }
        CaptureRuntimeDiagnostics();
        _instance?.Dispose();
        _instance = null;
        _bridgeAdapter?.Dispose();
        _bridgeAdapter = null;
        _runtime?.Dispose();
        _runtime = null;
        _browserHost?.Dispose();
        _browserHost = null;
    }

    private void CaptureRuntimeDiagnostics()
    {
        if (_runtime is not null)
        {
            var cache = _runtime.SharedCacheMetrics;
            Report(new HtmlMlSdkDiagnostic(
                "runtime.cache",
                HtmlMlDiagnosticSeverity.Info,
                $"Source cache {cache.SourceHits} hits/{cache.SourceMisses} misses; code cache {cache.CodeHits} hits/{cache.CodeMisses} misses; {cache.CodeBytes} bytes.",
                _instance?.Package.Manifest.Id));
        }
        if (_browserHost is null)
        {
            return;
        }
        foreach (var exception in _browserHost.JavaScriptExceptionDiagnostics)
        {
            Report(new HtmlMlSdkDiagnostic("runtime.script", HtmlMlDiagnosticSeverity.Error, exception, _instance?.Package.Manifest.Id));
        }
        var budget = _browserHost.GetUiThreadWorkBudgetMetrics();
        if (budget.JavaScriptOverruns + budget.CssOverruns + budget.LayoutOverruns > 0)
        {
            Report(new HtmlMlSdkDiagnostic(
                "runtime.longtask",
                HtmlMlDiagnosticSeverity.Warning,
                $"UI budget {budget.Budget.TotalMilliseconds:F1} ms: JavaScript {budget.JavaScriptOverruns}, CSS {budget.CssOverruns}, layout {budget.LayoutOverruns} overruns.",
                _instance?.Package.Manifest.Id));
        }
        foreach (var diagnostic in _browserHost.Backend.Diagnostics)
        {
            Report(new HtmlMlSdkDiagnostic(
                "runtime.backend",
                HtmlMlDiagnosticSeverity.Warning,
                $"{diagnostic.Category}: {diagnostic.Message}",
                _instance?.Package.Manifest.Id));
        }
    }

    private void Report(in HtmlMlSdkDiagnostic diagnostic)
    {
        _diagnostics.Report(diagnostic);
        DiagnosticReported?.Invoke(this, diagnostic);
    }

    private static string ResolvePackagePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private static void EnsureBackendCapabilities(HtmlMlComponentManifest manifest)
    {
        var required = HtmlMlBackendCapabilities.None;
        foreach (var capability in manifest.Capabilities)
        {
            required |= capability switch
            {
                HtmlMlComponentCapabilities.Dom => HtmlMlBackendCapabilities.DomProjection,
                HtmlMlComponentCapabilities.CssLayout => HtmlMlBackendCapabilities.CssLayout,
                HtmlMlComponentCapabilities.Canvas2D => HtmlMlBackendCapabilities.Canvas2D,
                HtmlMlComponentCapabilities.Svg => HtmlMlBackendCapabilities.Svg,
                HtmlMlComponentCapabilities.Pointer => HtmlMlBackendCapabilities.PointerInput,
                HtmlMlComponentCapabilities.Keyboard => HtmlMlBackendCapabilities.KeyboardInput,
                HtmlMlComponentCapabilities.Focus => HtmlMlBackendCapabilities.Focus,
                HtmlMlComponentCapabilities.Clipboard => HtmlMlBackendCapabilities.Clipboard,
                _ => HtmlMlBackendCapabilities.None
            };
        }
        var available = HtmlML.Backends.Avalonia.AvaloniaBackendHost.DefaultCapabilities;
        if ((required & ~available) != HtmlMlBackendCapabilities.None)
        {
            throw new HtmlMlBackendCapabilityException(required, available);
        }
    }

    private sealed class ComponentDocument(AvaloniaBrowserHost host, Control root) : AvaloniaDomDocument(host)
    {
        protected override Control? GetDocumentRoot() => root;
    }

    private sealed class ForwardingDiagnosticSink(HtmlMlComponentHost owner) : IHtmlMlDiagnosticSink
    {
        public void Report(in HtmlMlSdkDiagnostic diagnostic) => owner.Report(diagnostic);
    }
}
