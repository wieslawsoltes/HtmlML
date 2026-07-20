namespace HtmlML.Core;

[Flags]
public enum HtmlMlBackendCapabilities : ulong
{
    None = 0,
    DomProjection = 1UL << 0,
    CssLayout = 1UL << 1,
    Canvas2D = 1UL << 2,
    Svg = 1UL << 3,
    Images = 1UL << 4,
    PointerInput = 1UL << 5,
    KeyboardInput = 1UL << 6,
    TextInput = 1UL << 7,
    Focus = 1UL << 8,
    Clipboard = 1UL << 9,
    Accessibility = 1UL << 10,
    DragDrop = 1UL << 11,
    InputMethodEditor = 1UL << 12,
    OpenGl = 1UL << 13,
    WebGpu = 1UL << 14
}

public enum HtmlMlBackendState
{
    Created,
    Mounted,
    Unmounted,
    Disposed
}

public enum HtmlMlBackendNodeKind
{
    Container,
    Text,
    Image,
    Canvas,
    Svg,
    NativeControl
}

[Flags]
public enum HtmlMlInvalidationKind
{
    None = 0,
    Style = 1 << 0,
    Measure = 1 << 1,
    Arrange = 1 << 2,
    Render = 1 << 3,
    HitTest = 1 << 4,
    Accessibility = 1 << 5
}

public readonly record struct HtmlMlBackendNodeDescriptor(
    HtmlMlNodeId Id,
    HtmlMlBackendNodeKind Kind,
    string SemanticName);

public readonly record struct HtmlMlBackendDiagnostic(
    string Category,
    string Message,
    HtmlMlNodeId NodeId,
    DateTimeOffset Timestamp);

public sealed class HtmlMlBackendCapabilityException : NotSupportedException
{
    public HtmlMlBackendCapabilityException(
        HtmlMlBackendCapabilities required,
        HtmlMlBackendCapabilities available)
        : base($"Backend is missing required capabilities '{required & ~available}'. Available capabilities: '{available}'.")
    {
        Required = required;
        Available = available;
        Missing = required & ~available;
    }

    public HtmlMlBackendCapabilities Required { get; }

    public HtmlMlBackendCapabilities Available { get; }

    public HtmlMlBackendCapabilities Missing { get; }
}

public interface IHtmlMlBackendHost : IDisposable
{
    HtmlMlBackendState State { get; }

    HtmlMlBackendNode Root { get; }

    HtmlMlBackendCapabilities Capabilities { get; }

    IHtmlMlHostServices Services { get; }

    IHtmlMlInputSource Input { get; }

    IReadOnlyList<HtmlMlBackendDiagnostic> Diagnostics { get; }

    void EnsureCapabilities(HtmlMlBackendCapabilities required);

    void Mount();

    void Unmount();

    HtmlMlBackendNode CreateNode(in HtmlMlBackendNodeDescriptor descriptor);

    void Attach(HtmlMlBackendNode parent, HtmlMlBackendNode child, int index);

    void Detach(HtmlMlBackendNode node);

    void Arrange(HtmlMlBackendNode node, HtmlMlRect bounds);

    void SetVisible(HtmlMlBackendNode node, bool visible);

    void SetZIndex(HtmlMlBackendNode node, int zIndex);

    void Invalidate(HtmlMlBackendNode node, HtmlMlInvalidationKind kind);

    HtmlMlBackendNode? HitTest(HtmlMlPoint point);
}

/// <summary>
/// Enforces the backend lifetime contract while leaving native object creation and
/// presentation to an adapter. Calls are serialized by the adapter's dispatcher.
/// </summary>
public abstract class HtmlMlBackendHostBase : IHtmlMlBackendHost
{
    private bool _disposed;

    protected HtmlMlBackendHostBase(
        IHtmlMlHostServices services,
        IHtmlMlInputSource input,
        HtmlMlBackendCapabilities capabilities)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Capabilities = capabilities;
        Root = new HtmlMlBackendNode(new HtmlMlNodeId(long.MinValue), services.RootHandle);
    }

    public HtmlMlBackendState State { get; private set; } = HtmlMlBackendState.Created;

    public HtmlMlBackendNode Root { get; }

    public HtmlMlBackendCapabilities Capabilities { get; }

    public IHtmlMlHostServices Services { get; }

    public IHtmlMlInputSource Input { get; }

    public abstract IReadOnlyList<HtmlMlBackendDiagnostic> Diagnostics { get; }

    public void EnsureCapabilities(HtmlMlBackendCapabilities required)
    {
        ThrowIfDisposed();
        var missing = required & ~Capabilities;
        if (missing == HtmlMlBackendCapabilities.None)
        {
            return;
        }

        var diagnostic = new HtmlMlBackendDiagnostic(
            "backend.capability",
            $"Missing required backend capabilities: {missing}.",
            default,
            DateTimeOffset.UtcNow);
        ReportDiagnostic(diagnostic);
        throw new HtmlMlBackendCapabilityException(required, Capabilities);
    }

    public void Mount()
    {
        ThrowIfDisposed();
        if (State == HtmlMlBackendState.Mounted)
        {
            return;
        }

        if (State is not (HtmlMlBackendState.Created or HtmlMlBackendState.Unmounted))
        {
            throw new InvalidOperationException($"Cannot mount a backend in state '{State}'.");
        }

        Services.Dispatcher.VerifyAccess();
        OnMount();
        State = HtmlMlBackendState.Mounted;
    }

    public void Unmount()
    {
        ThrowIfDisposed();
        if (State == HtmlMlBackendState.Unmounted)
        {
            return;
        }

        if (State != HtmlMlBackendState.Mounted)
        {
            throw new InvalidOperationException($"Cannot unmount a backend in state '{State}'.");
        }

        Services.Dispatcher.VerifyAccess();
        OnUnmount();
        State = HtmlMlBackendState.Unmounted;
    }

    public abstract HtmlMlBackendNode CreateNode(in HtmlMlBackendNodeDescriptor descriptor);

    public abstract void Attach(HtmlMlBackendNode parent, HtmlMlBackendNode child, int index);

    public abstract void Detach(HtmlMlBackendNode node);

    public abstract void Arrange(HtmlMlBackendNode node, HtmlMlRect bounds);

    public abstract void SetVisible(HtmlMlBackendNode node, bool visible);

    public abstract void SetZIndex(HtmlMlBackendNode node, int zIndex);

    public abstract void Invalidate(HtmlMlBackendNode node, HtmlMlInvalidationKind kind);

    public abstract HtmlMlBackendNode? HitTest(HtmlMlPoint point);

    protected void RequireMounted()
    {
        ThrowIfDisposed();
        if (State != HtmlMlBackendState.Mounted)
        {
            throw new InvalidOperationException($"Backend operation requires Mounted state; current state is '{State}'.");
        }

        Services.Dispatcher.VerifyAccess();
    }

    protected virtual void OnMount()
    {
    }

    protected virtual void OnUnmount()
    {
    }

    protected virtual void DisposeCore()
    {
    }

    protected abstract void ReportDiagnostic(HtmlMlBackendDiagnostic diagnostic);

    protected void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Services.Dispatcher.VerifyAccess();
        if (State == HtmlMlBackendState.Mounted)
        {
            OnUnmount();
        }

        DisposeCore();
        _disposed = true;
        State = HtmlMlBackendState.Disposed;
        GC.SuppressFinalize(this);
    }
}
