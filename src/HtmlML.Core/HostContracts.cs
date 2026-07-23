namespace HtmlML.Core;

public enum HtmlMlDispatchPriority
{
    Send,
    Input,
    Default,
    Render,
    Background
}

public interface IHtmlMlScheduledWork : IDisposable
{
    bool IsCancellationRequested { get; }

    void Cancel();
}

public interface IHtmlMlDispatcher
{
    bool CheckAccess();

    void VerifyAccess();

    void Post(Action callback, HtmlMlDispatchPriority priority = HtmlMlDispatchPriority.Default);

    IHtmlMlScheduledWork Schedule(
        TimeSpan delay,
        Action callback,
        HtmlMlDispatchPriority priority = HtmlMlDispatchPriority.Default);
}

public interface IHtmlMlClock
{
    TimeSpan Elapsed { get; }
}

public readonly record struct HtmlMlFrameRequest(long Value)
{
    public bool IsEmpty => Value == 0;
}

public interface IHtmlMlFrameScheduler
{
    HtmlMlFrameRequest RequestFrame(Action<TimeSpan> callback);

    bool CancelFrame(HtmlMlFrameRequest request);
}

public readonly record struct HtmlMlViewportMetrics(
    HtmlMlSize ClientSize,
    double DeviceScaleFactor,
    bool IsVisible)
{
    public static HtmlMlViewportMetrics Empty { get; } = new(HtmlMlSize.Empty, 1, false);
}

public sealed class HtmlMlViewportChangedEventArgs : EventArgs
{
    public HtmlMlViewportChangedEventArgs(HtmlMlViewportMetrics previous, HtmlMlViewportMetrics current)
    {
        Previous = previous;
        Current = current;
    }

    public HtmlMlViewportMetrics Previous { get; }

    public HtmlMlViewportMetrics Current { get; }
}

public interface IHtmlMlViewport
{
    HtmlMlViewportMetrics HostMetrics { get; }

    HtmlMlViewportMetrics Metrics { get; }

    event EventHandler<HtmlMlViewportChangedEventArgs>? Changed;
}

public enum HtmlMlResourceKind
{
    Script,
    StyleSheet,
    Markup,
    Image,
    Font,
    Data
}

public readonly record struct HtmlMlResourceRequest(
    string Specifier,
    string? BaseAddress,
    HtmlMlResourceKind Kind)
{
    public string? IfNoneMatch { get; init; }

    public DateTimeOffset? IfModifiedSince { get; init; }
}

public readonly record struct HtmlMlTextResource(
    string CacheKey,
    string Content,
    string DisplayName,
    string? Directory)
{
    public string? EntityTag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// The HTTP freshness boundary for this representation. A persistent cache
    /// may reuse the content without contacting the origin before this instant;
    /// after it, validators such as <see cref="EntityTag"/> must be used.
    /// </summary>
    public DateTimeOffset? FreshUntil { get; init; }

    /// <summary>
    /// Whether the representation may be stored. HTTP <c>no-store</c>
    /// responses set this to <see langword="false"/>.
    /// </summary>
    public bool IsCacheable { get; init; } = true;

    public bool NotModified { get; init; }
}

public interface IHtmlMlResourceLoader
{
    HtmlMlTextResource LoadText(in HtmlMlResourceRequest request);
}

public interface IHtmlMlClipboard
{
    string? GetText();

    void SetText(string? text);

    byte[]? GetData(string format) => null;

    void SetData(string format, ReadOnlyMemory<byte> data)
    {
    }
}

public interface IHtmlMlHostServices
{
    HtmlMlBackendHandle RootHandle { get; }

    IHtmlMlDispatcher Dispatcher { get; }

    IHtmlMlClock Clock { get; }

    IHtmlMlFrameScheduler Frames { get; }

    IHtmlMlViewport Viewport { get; }

    IHtmlMlResourceLoader Resources { get; }

    IHtmlMlClipboard Clipboard { get; }

    IHtmlMlInputSource Input { get; }
}
