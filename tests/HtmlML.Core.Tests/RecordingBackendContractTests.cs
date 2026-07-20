using HtmlML.Core;
using Xunit;

namespace HtmlML.Core.Tests;

public sealed class RecordingBackendContractTests
{
    [Fact]
    public void BackendRecordsOrderedTreeLayoutInvalidationAndHitTesting()
    {
        var services = new RecordingHostServices();
        using var backend = new RecordingBackend(services);

        backend.Mount();
        var root = backend.CreateNode(new HtmlMlBackendNodeDescriptor(
            new HtmlMlNodeId(1), HtmlMlBackendNodeKind.Container, "root"));
        var child = backend.CreateNode(new HtmlMlBackendNodeDescriptor(
            new HtmlMlNodeId(2), HtmlMlBackendNodeKind.Canvas, "chart"));

        backend.Attach(root, child, 0);
        backend.Arrange(root, new HtmlMlRect(0, 0, 640, 480));
        backend.Arrange(child, new HtmlMlRect(20, 30, 200, 100));
        backend.SetZIndex(child, 4);
        backend.SetVisible(child, true);
        backend.Invalidate(child, HtmlMlInvalidationKind.Arrange | HtmlMlInvalidationKind.Render);

        Assert.Equal(child, backend.HitTest(new HtmlMlPoint(50, 60)));
        backend.Detach(child);
        Assert.Equal(
            new[]
            {
                "mount", "create:1:Container", "create:2:Canvas", "attach:1:2:0",
                "arrange:1:0,0,640,480", "arrange:2:20,30,200,100", "z:2:4",
                "visible:2:True", "invalidate:2:Arrange, Render", "hit:50,60:2",
                "detach:2"
            },
            backend.Operations);
    }

    [Fact]
    public void BackendEnforcesMountUnmountRemountAndDisposeOrder()
    {
        var services = new RecordingHostServices();
        var backend = new RecordingBackend(services);

        Assert.Equal(HtmlMlBackendState.Created, backend.State);
        Assert.Throws<InvalidOperationException>(() => backend.CreateNode(default));

        backend.Mount();
        backend.Mount();
        backend.Unmount();
        backend.Unmount();
        backend.Mount();
        backend.Dispose();
        backend.Dispose();

        Assert.Equal(HtmlMlBackendState.Disposed, backend.State);
        Assert.Equal(new[] { "mount", "unmount", "mount", "unmount", "dispose" }, backend.Operations);
        Assert.Throws<ObjectDisposedException>(() => backend.Mount());
    }

    [Fact]
    public void BackendRejectsInvalidLifetimesAndUnknownNodes()
    {
        var services = new RecordingHostServices();
        using var backend = new RecordingBackend(services);

        Assert.Throws<InvalidOperationException>(() => backend.Unmount());
        backend.Mount();
        Assert.Throws<ArgumentException>(() => backend.CreateNode(default));
        Assert.Throws<InvalidOperationException>(
            () => backend.Arrange(
                new HtmlMlBackendNode(new HtmlMlNodeId(42), HtmlMlBackendHandle.Create(new object())),
                HtmlMlRect.Empty));
    }

    [Fact]
    public void CapabilityNegotiationIsImmutableDiagnosticAndFailFast()
    {
        var services = new RecordingHostServices();
        using var backend = new RecordingBackend(services);
        var advertised = backend.Capabilities;

        backend.EnsureCapabilities(HtmlMlBackendCapabilities.DomProjection | HtmlMlBackendCapabilities.Canvas2D);
        var error = Assert.Throws<HtmlMlBackendCapabilityException>(
            () => backend.EnsureCapabilities(
                HtmlMlBackendCapabilities.DomProjection | HtmlMlBackendCapabilities.Accessibility));

        Assert.Equal(advertised, backend.Capabilities);
        Assert.Equal(HtmlMlBackendCapabilities.Accessibility, error.Missing);
        Assert.Equal(advertised, error.Available);
        Assert.Single(backend.Diagnostics);
        Assert.Equal("backend.capability", backend.Diagnostics[0].Category);
        Assert.Contains("Accessibility", backend.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendRejectsOperationsFromTheWrongDispatcher()
    {
        var services = new RecordingHostServices();
        var backend = new RecordingBackend(services);
        services.DispatcherImpl.HasAccess = false;

        Assert.Throws<InvalidOperationException>(() => backend.Mount());
        services.DispatcherImpl.HasAccess = true;
        backend.Dispose();
    }

    [Fact]
    public void BackendHandleUsesReferenceIdentityAndKeepsNativeTypeOpaque()
    {
        var native = new object();
        var first = HtmlMlBackendHandle.Create(native);
        var second = HtmlMlBackendHandle.Create(native);
        var other = HtmlMlBackendHandle.Create(new object());

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(typeof(object), first.NativeType);
        Assert.Same(native, first.GetRequired<object>());
        Assert.False(first.TryGet<string>(out _));
    }

    [Fact]
    public void PortableValuesHaveStableEmptyBoundaryAndIdentitySemantics()
    {
        Assert.True(HtmlMlSize.Empty.IsEmpty);
        Assert.True(new HtmlMlSize(-1, 2).IsEmpty);
        Assert.False(new HtmlMlSize(1, 2).IsEmpty);

        Assert.Equal(new HtmlMlSize(30, 40), new HtmlMlRect(10, 20, 30, 40).Size);
        Assert.True(new HtmlMlRect(10, 20, 30, 40).Contains(new HtmlMlPoint(10, 20)));
        Assert.False(new HtmlMlRect(10, 20, 30, 40).Contains(new HtmlMlPoint(40, 60)));
        Assert.Equal(HtmlMlRect.Empty, new HtmlMlRect());

        Assert.Equal(new HtmlMlColor(0, 0, 0, 0), HtmlMlColor.Transparent);
        Assert.Equal(new HtmlMlColor(255, 1, 2, 3), HtmlMlColor.FromRgb(1, 2, 3));
        Assert.True(default(HtmlMlNodeId).IsEmpty);
        Assert.Equal("123", new HtmlMlNodeId(123).ToString());

        Assert.True(default(HtmlMlBackendHandle).IsEmpty);
        Assert.Equal(0, default(HtmlMlBackendHandle).GetHashCode());
        Assert.Throws<ArgumentNullException>(() => HtmlMlBackendHandle.Create(null!));
        Assert.Throws<InvalidOperationException>(() => default(HtmlMlBackendHandle).GetRequired<object>());

        var native = new object();
        var first = HtmlMlBackendHandle.Create(native);
        var second = HtmlMlBackendHandle.Create(native);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals((object)second));
        Assert.NotEqual(0, first.GetHashCode());

        Assert.True(default(HtmlMlBackendNode).IsEmpty);
        Assert.False(new HtmlMlBackendNode(new HtmlMlNodeId(1), first).IsEmpty);
    }

    [Fact]
    public void PortableTimingAndViewportValuesPreserveEmptyAndChangeState()
    {
        Assert.True(default(HtmlMlFrameRequest).IsEmpty);
        Assert.False(new HtmlMlFrameRequest(1).IsEmpty);
        Assert.Equal(new HtmlMlSize(0, 0), HtmlMlViewportMetrics.Empty.ClientSize);
        Assert.Equal(1, HtmlMlViewportMetrics.Empty.DeviceScaleFactor);
        Assert.False(HtmlMlViewportMetrics.Empty.IsVisible);

        var previous = HtmlMlViewportMetrics.Empty;
        var current = new HtmlMlViewportMetrics(new HtmlMlSize(800, 600), 2, true);
        var changed = new HtmlMlViewportChangedEventArgs(previous, current);
        Assert.Equal(previous, changed.Previous);
        Assert.Equal(current, changed.Current);
    }

    [Fact]
    public void BaseBackendDefaultHooksRemainValidForMinimalAdapters()
    {
        var services = new RecordingHostServices();
        var backend = new MinimalBackend(services);

        backend.Mount();
        backend.Unmount();
        backend.Dispose();

        Assert.Equal(HtmlMlBackendState.Disposed, backend.State);
    }

    private sealed class RecordingBackend : HtmlMlBackendHostBase
    {
        private readonly Dictionary<HtmlMlNodeId, NodeState> _nodes = new();
        private readonly List<HtmlMlBackendDiagnostic> _diagnostics = new();

        public RecordingBackend(IHtmlMlHostServices services)
            : base(
                services,
                new RecordingInputSource(),
                HtmlMlBackendCapabilities.DomProjection
                | HtmlMlBackendCapabilities.CssLayout
                | HtmlMlBackendCapabilities.Canvas2D
                | HtmlMlBackendCapabilities.PointerInput)
        {
        }

        public List<string> Operations { get; } = new();

        public override IReadOnlyList<HtmlMlBackendDiagnostic> Diagnostics => _diagnostics;

        protected override void OnMount() => Operations.Add("mount");

        protected override void OnUnmount() => Operations.Add("unmount");

        protected override void DisposeCore() => Operations.Add("dispose");

        protected override void ReportDiagnostic(HtmlMlBackendDiagnostic diagnostic)
            => _diagnostics.Add(diagnostic);

        public override HtmlMlBackendNode CreateNode(in HtmlMlBackendNodeDescriptor descriptor)
        {
            RequireMounted();
            if (descriptor.Id.IsEmpty)
            {
                throw new ArgumentException("A non-empty DOM node id is required.", nameof(descriptor));
            }

            var handle = HtmlMlBackendHandle.Create(new object());
            var node = new HtmlMlBackendNode(descriptor.Id, handle);
            _nodes.Add(descriptor.Id, new NodeState(node));
            Operations.Add($"create:{descriptor.Id}:{descriptor.Kind}");
            return node;
        }

        public override void Attach(HtmlMlBackendNode parent, HtmlMlBackendNode child, int index)
        {
            RequireMounted();
            var parentState = Get(parent);
            var childState = Get(child);
            childState.Parent = parent.Id;
            parentState.Children.Insert(index, child.Id);
            Operations.Add($"attach:{parent.Id}:{child.Id}:{index}");
        }

        public override void Detach(HtmlMlBackendNode node)
        {
            RequireMounted();
            var state = Get(node);
            if (state.Parent is { } parent && _nodes.TryGetValue(parent, out var parentState))
            {
                parentState.Children.Remove(node.Id);
            }

            state.Parent = null;
            Operations.Add($"detach:{node.Id}");
        }

        public override void Arrange(HtmlMlBackendNode node, HtmlMlRect bounds)
        {
            RequireMounted();
            Get(node).Bounds = bounds;
            Operations.Add($"arrange:{node.Id}:{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
        }

        public override void SetVisible(HtmlMlBackendNode node, bool visible)
        {
            RequireMounted();
            Get(node).Visible = visible;
            Operations.Add($"visible:{node.Id}:{visible}");
        }

        public override void SetZIndex(HtmlMlBackendNode node, int zIndex)
        {
            RequireMounted();
            Get(node).ZIndex = zIndex;
            Operations.Add($"z:{node.Id}:{zIndex}");
        }

        public override void Invalidate(HtmlMlBackendNode node, HtmlMlInvalidationKind kind)
        {
            RequireMounted();
            Get(node).Invalidation |= kind;
            Operations.Add($"invalidate:{node.Id}:{kind}");
        }

        public override HtmlMlBackendNode? HitTest(HtmlMlPoint point)
        {
            RequireMounted();
            var hit = _nodes.Values
                .Where(node => node.Visible && node.Bounds.Contains(point))
                .OrderByDescending(node => node.ZIndex)
                .ThenByDescending(node => node.Node.Id.Value)
                .Select(node => (HtmlMlBackendNode?)node.Node)
                .FirstOrDefault();
            Operations.Add($"hit:{point.X},{point.Y}:{hit?.Id.ToString() ?? "none"}");
            return hit;
        }

        private NodeState Get(HtmlMlBackendNode node)
            => _nodes.TryGetValue(node.Id, out var state) && state.Node.Handle == node.Handle
                ? state
                : throw new InvalidOperationException($"Unknown backend node '{node.Id}'.");

        private sealed class NodeState(HtmlMlBackendNode node)
        {
            public HtmlMlBackendNode Node { get; } = node;
            public HtmlMlNodeId? Parent { get; set; }
            public List<HtmlMlNodeId> Children { get; } = new();
            public HtmlMlRect Bounds { get; set; }
            public bool Visible { get; set; } = true;
            public int ZIndex { get; set; }
            public HtmlMlInvalidationKind Invalidation { get; set; }
        }
    }

    private sealed class MinimalBackend(IHtmlMlHostServices services)
        : HtmlMlBackendHostBase(services, services.Input, HtmlMlBackendCapabilities.None)
    {
        public override IReadOnlyList<HtmlMlBackendDiagnostic> Diagnostics
            => Array.Empty<HtmlMlBackendDiagnostic>();

        protected override void ReportDiagnostic(HtmlMlBackendDiagnostic diagnostic)
        {
        }

        public override HtmlMlBackendNode CreateNode(in HtmlMlBackendNodeDescriptor descriptor)
            => throw new NotSupportedException();
        public override void Attach(HtmlMlBackendNode parent, HtmlMlBackendNode child, int index)
            => throw new NotSupportedException();
        public override void Detach(HtmlMlBackendNode node) => throw new NotSupportedException();
        public override void Arrange(HtmlMlBackendNode node, HtmlMlRect bounds) => throw new NotSupportedException();
        public override void SetVisible(HtmlMlBackendNode node, bool visible) => throw new NotSupportedException();
        public override void SetZIndex(HtmlMlBackendNode node, int zIndex) => throw new NotSupportedException();
        public override void Invalidate(HtmlMlBackendNode node, HtmlMlInvalidationKind kind)
            => throw new NotSupportedException();
        public override HtmlMlBackendNode? HitTest(HtmlMlPoint point) => null;
    }

    private sealed class RecordingHostServices : IHtmlMlHostServices
    {
        private readonly object _root = new();

        public RecordingHostServices()
        {
            DispatcherImpl = new RecordingDispatcher();
            Dispatcher = DispatcherImpl;
        }

        public RecordingDispatcher DispatcherImpl { get; }
        public HtmlMlBackendHandle RootHandle => HtmlMlBackendHandle.Create(_root);
        public IHtmlMlDispatcher Dispatcher { get; }
        public IHtmlMlClock Clock { get; } = new RecordingClock();
        public IHtmlMlFrameScheduler Frames { get; } = new RecordingFrames();
        public IHtmlMlViewport Viewport { get; } = new RecordingViewport();
        public IHtmlMlResourceLoader Resources { get; } = new RecordingResources();
        public IHtmlMlClipboard Clipboard { get; } = new RecordingClipboard();
        public IHtmlMlInputSource Input { get; } = new RecordingInputSource();
    }

    private sealed class RecordingDispatcher : IHtmlMlDispatcher
    {
        public bool HasAccess { get; set; } = true;
        public bool CheckAccess() => HasAccess;
        public void VerifyAccess()
        {
            if (!HasAccess) throw new InvalidOperationException("The operation requires dispatcher access.");
        }
        public void Post(Action callback, HtmlMlDispatchPriority priority = HtmlMlDispatchPriority.Default) => callback();
        public IHtmlMlScheduledWork Schedule(TimeSpan delay, Action callback, HtmlMlDispatchPriority priority = HtmlMlDispatchPriority.Default)
        {
            callback();
            return new RecordingScheduledWork();
        }
    }

    private sealed class RecordingScheduledWork : IHtmlMlScheduledWork
    {
        public bool IsCancellationRequested { get; private set; }
        public void Cancel() => IsCancellationRequested = true;
        public void Dispose() => Cancel();
    }

    private sealed class RecordingClock : IHtmlMlClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
    }

    private sealed class RecordingFrames : IHtmlMlFrameScheduler
    {
        public HtmlMlFrameRequest RequestFrame(Action<TimeSpan> callback)
        {
            callback(TimeSpan.Zero);
            return new HtmlMlFrameRequest(1);
        }
        public bool CancelFrame(HtmlMlFrameRequest request) => !request.IsEmpty;
    }

    private sealed class RecordingViewport : IHtmlMlViewport
    {
        public HtmlMlViewportMetrics HostMetrics { get; } = new(new HtmlMlSize(800, 600), 1, true);
        public HtmlMlViewportMetrics Metrics { get; } = new(new HtmlMlSize(800, 600), 1, true);
        public event EventHandler<HtmlMlViewportChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingResources : IHtmlMlResourceLoader
    {
        public HtmlMlTextResource LoadText(in HtmlMlResourceRequest request)
            => new(request.Specifier, string.Empty, request.Specifier, null);
    }

    private sealed class RecordingClipboard : IHtmlMlClipboard
    {
        public string? Text { get; private set; }
        public string? GetText() => Text;
        public void SetText(string? text) => Text = text;
    }

    private sealed class RecordingInputSource : IHtmlMlInputSource
    {
        public event EventHandler<HtmlMlPointerInputEventArgs>? Pointer
        {
            add { }
            remove { }
        }
        public event EventHandler<HtmlMlKeyboardInputEventArgs>? Keyboard
        {
            add { }
            remove { }
        }
        public event EventHandler<HtmlMlTextInputEventArgs>? TextInput
        {
            add { }
            remove { }
        }
    }
}
