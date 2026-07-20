using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace HtmlML.Sdk;

public enum HtmlMlComponentState
{
    Created,
    Mounted,
    Unmounted,
    Disposed
}

public enum HtmlMlDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public readonly record struct HtmlMlSdkDiagnostic(
    string Code,
    HtmlMlDiagnosticSeverity Severity,
    string Message,
    string? ComponentId = null,
    DateTimeOffset? Timestamp = null);

public interface IHtmlMlDiagnosticSink
{
    void Report(in HtmlMlSdkDiagnostic diagnostic);
}

public sealed class HtmlMlDiagnosticCollector : IHtmlMlDiagnosticSink
{
    private readonly object _gate = new();
    private readonly List<HtmlMlSdkDiagnostic> _diagnostics = [];

    public IReadOnlyList<HtmlMlSdkDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    public void Report(in HtmlMlSdkDiagnostic diagnostic)
    {
        lock (_gate)
        {
            _diagnostics.Add(diagnostic with { Timestamp = diagnostic.Timestamp ?? DateTimeOffset.UtcNow });
        }
    }
}

public sealed record HtmlMlCachedAsset(
    string ComponentId,
    string ComponentVersion,
    string Path,
    ReadOnlyMemory<byte> Content,
    string Sha256);

/// <summary>Process-wide immutable package bytes; component instance state never enters this cache.</summary>
public sealed class HtmlMlSharedAssetCache
{
    private readonly ConcurrentDictionary<string, Lazy<HtmlMlCachedAsset>> _assets = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public int Count => _assets.Count;

    public HtmlMlCachedAsset GetOrAdd(HtmlMlComponentManifest manifest, string path, Func<byte[]> loader)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(loader);
        var key = string.Concat(manifest.Id, "@", manifest.Version, "/", path);
        var created = new Lazy<HtmlMlCachedAsset>(
            () => CreateAsset(manifest, path, loader()),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var value = _assets.GetOrAdd(key, created);
        if (ReferenceEquals(value, created))
        {
            Interlocked.Increment(ref _misses);
        }
        else
        {
            Interlocked.Increment(ref _hits);
        }
        return value.Value;
    }

    private static HtmlMlCachedAsset CreateAsset(HtmlMlComponentManifest manifest, string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var immutableCopy = content.ToArray();
        return new HtmlMlCachedAsset(
            manifest.Id,
            manifest.Version,
            path,
            immutableCopy,
            Convert.ToHexString(SHA256.HashData(immutableCopy)).ToLowerInvariant());
    }
}

public sealed class HtmlMlComponentPackage
{
    private readonly string _rootDirectory;
    private readonly HtmlMlSharedAssetCache _cache;

    private HtmlMlComponentPackage(
        string rootDirectory,
        HtmlMlComponentManifest manifest,
        HtmlMlSharedAssetCache cache)
    {
        _rootDirectory = rootDirectory;
        Manifest = manifest;
        _cache = cache;
    }

    public HtmlMlComponentManifest Manifest { get; }

    public static HtmlMlComponentPackage Open(
        string rootDirectory,
        HtmlMlSharedAssetCache cache,
        string manifestFileName = "htmlml-component.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(cache);
        var root = Path.GetFullPath(rootDirectory);
        var manifestPath = Path.Combine(root, manifestFileName);
        using var stream = File.OpenRead(manifestPath);
        var manifest = HtmlMlComponentManifestSerializer.Read(stream);
        foreach (var asset in manifest.Assets)
        {
            var path = ResolveContainedPath(root, asset);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Component asset '{asset}' does not exist.", path);
            }
        }
        return new HtmlMlComponentPackage(root, manifest, cache);
    }

    public HtmlMlCachedAsset GetAsset(string path)
    {
        if (!Manifest.Assets.Contains(path, StringComparer.Ordinal))
        {
            throw new FileNotFoundException($"Asset '{path}' is not declared by component '{Manifest.Id}'.");
        }
        var fullPath = ResolveContainedPath(_rootDirectory, path);
        return _cache.GetOrAdd(Manifest, path, () => File.ReadAllBytes(fullPath));
    }

    public HtmlMlCachedAsset GetEntryPoint() => GetAsset(Manifest.EntryPoint);

    public HtmlMlComponentInstance CreateInstance(IHtmlMlDiagnosticSink? diagnostics = null)
        => new(this, diagnostics);

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Asset '{relativePath}' escapes the component package.");
        }
        return fullPath;
    }
}

public sealed class HtmlMlComponentInstance : IDisposable
{
    private readonly Dictionary<string, object?> _state = new(StringComparer.Ordinal);
    private readonly IHtmlMlDiagnosticSink? _diagnostics;

    internal HtmlMlComponentInstance(HtmlMlComponentPackage package, IHtmlMlDiagnosticSink? diagnostics)
    {
        Package = package;
        _diagnostics = diagnostics;
        InstanceId = Guid.NewGuid();
    }

    public Guid InstanceId { get; }

    public HtmlMlComponentPackage Package { get; }

    public HtmlMlComponentState State { get; private set; }

    public IReadOnlyDictionary<string, object?> StateValues => new ReadOnlyDictionary<string, object?>(_state);

    public void Mount()
    {
        ObjectDisposedException.ThrowIf(State == HtmlMlComponentState.Disposed, this);
        if (State == HtmlMlComponentState.Mounted)
        {
            return;
        }
        State = HtmlMlComponentState.Mounted;
        Report("component.mounted", HtmlMlDiagnosticSeverity.Info, $"Mounted instance {InstanceId}.");
    }

    public void Unmount()
    {
        ObjectDisposedException.ThrowIf(State == HtmlMlComponentState.Disposed, this);
        if (State != HtmlMlComponentState.Mounted)
        {
            throw new InvalidOperationException($"Cannot unmount component in state '{State}'.");
        }
        State = HtmlMlComponentState.Unmounted;
        Report("component.unmounted", HtmlMlDiagnosticSeverity.Info, $"Unmounted instance {InstanceId}.");
    }

    public void SetState(string key, object? value)
    {
        ObjectDisposedException.ThrowIf(State == HtmlMlComponentState.Disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _state[key] = value;
    }

    public bool TryGetState<T>(string key, out T? value)
    {
        if (_state.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    public void Dispose()
    {
        if (State == HtmlMlComponentState.Disposed)
        {
            return;
        }
        _state.Clear();
        State = HtmlMlComponentState.Disposed;
        Report("component.disposed", HtmlMlDiagnosticSeverity.Info, $"Disposed instance {InstanceId}.");
    }

    private void Report(string code, HtmlMlDiagnosticSeverity severity, string message)
        => _diagnostics?.Report(new HtmlMlSdkDiagnostic(code, severity, message, Package.Manifest.Id));
}
