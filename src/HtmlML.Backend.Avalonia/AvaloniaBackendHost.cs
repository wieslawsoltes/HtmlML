using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HtmlML.Core;
using JavaScript.Avalonia;

namespace HtmlML.Backends.Avalonia;

/// <summary>
/// Public Avalonia implementation of the HtmlML backend lifetime, tree projection,
/// layout, invalidation, hit-testing, capability, and host-service contracts.
/// Browser workloads use the same service instance through <see cref="AvaloniaBrowserHost.Backend"/>.
/// </summary>
public sealed class AvaloniaBackendHost : HtmlMlBackendHostBase
{
    public const HtmlMlBackendCapabilities DefaultCapabilities =
        HtmlMlBackendCapabilities.DomProjection
        | HtmlMlBackendCapabilities.CssLayout
        | HtmlMlBackendCapabilities.Canvas2D
        | HtmlMlBackendCapabilities.Svg
        | HtmlMlBackendCapabilities.Images
        | HtmlMlBackendCapabilities.PointerInput
        | HtmlMlBackendCapabilities.KeyboardInput
        | HtmlMlBackendCapabilities.TextInput
        | HtmlMlBackendCapabilities.Focus
        | HtmlMlBackendCapabilities.Clipboard
        | HtmlMlBackendCapabilities.Accessibility
        | HtmlMlBackendCapabilities.InputMethodEditor
        | HtmlMlBackendCapabilities.OpenGl;

    private readonly AvaloniaHostServices _services;
    private readonly bool _ownsServices;
    private readonly Dictionary<HtmlMlNodeId, NodeState> _nodes = new();
    private readonly Dictionary<Control, HtmlMlBackendNode> _nodesByControl =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<HtmlMlBackendDiagnostic> _diagnostics = new();

    public AvaloniaBackendHost(TopLevel topLevel)
        : this(new AvaloniaHostServices(topLevel), ownsServices: true)
    {
    }

    internal AvaloniaBackendHost(AvaloniaHostServices services, bool ownsServices)
        : base(services, services.Input, DefaultCapabilities)
    {
        _services = services;
        _ownsServices = ownsServices;
    }

    public TopLevel TopLevel => _services.TopLevel;

    public override IReadOnlyList<HtmlMlBackendDiagnostic> Diagnostics => _diagnostics;

    public override HtmlMlBackendNode CreateNode(in HtmlMlBackendNodeDescriptor descriptor)
    {
        RequireMounted();
        if (descriptor.Id.IsEmpty)
        {
            throw new ArgumentException("A backend node id must not be empty.", nameof(descriptor));
        }
        if (descriptor.Id == Root.Id || _nodes.ContainsKey(descriptor.Id))
        {
            throw new ArgumentException($"Backend node '{descriptor.Id}' already exists or is reserved.", nameof(descriptor));
        }

        Control control = descriptor.Kind switch
        {
            HtmlMlBackendNodeKind.Container => new Canvas(),
            HtmlMlBackendNodeKind.Text => new TextBlock { Text = descriptor.SemanticName },
            HtmlMlBackendNodeKind.Image => new Image(),
            HtmlMlBackendNodeKind.Canvas => new CanvasDrawingSurface(),
            HtmlMlBackendNodeKind.Svg => new AvaloniaSvgSceneSurface(),
            HtmlMlBackendNodeKind.NativeControl => new ContentControl(),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Kind, "Unknown backend node kind.")
        };
        if (!string.IsNullOrWhiteSpace(descriptor.SemanticName))
        {
            AutomationProperties.SetName(control, descriptor.SemanticName);
        }

        var node = new HtmlMlBackendNode(descriptor.Id, HtmlMlBackendHandle.Create(control));
        _nodes.Add(descriptor.Id, new NodeState(node, control));
        _nodesByControl.Add(control, node);
        return node;
    }

    public override void Attach(HtmlMlBackendNode parent, HtmlMlBackendNode child, int index)
    {
        RequireMounted();
        var childState = Get(child);
        DetachCore(childState);

        if (parent == Root)
        {
            if (index != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "The backend root accepts one child at index zero.");
            }
            if (TopLevel.Content is Control existing && !ReferenceEquals(existing, childState.Control))
            {
                throw new InvalidOperationException("The Avalonia TopLevel already contains a different root control.");
            }
            TopLevel.Content = childState.Control;
            childState.Parent = Root;
            return;
        }

        var parentState = Get(parent);
        switch (parentState.Control)
        {
            case Panel panel:
                if ((uint)index > (uint)panel.Children.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                panel.Children.Insert(index, childState.Control);
                break;
            case ContentControl content when index == 0 && content.Content is null:
                content.Content = childState.Control;
                break;
            default:
                throw new InvalidOperationException(
                    $"Backend node '{parent.Id}' cannot accept a child at index {index}.");
        }
        childState.Parent = parent;
    }

    public override void Detach(HtmlMlBackendNode node)
    {
        RequireMounted();
        DetachCore(Get(node));
    }

    public override void Arrange(HtmlMlBackendNode node, HtmlMlRect bounds)
    {
        RequireMounted();
        if (bounds.Width < 0 || bounds.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Backend bounds must have non-negative dimensions.");
        }
        var state = Get(node);
        state.Bounds = bounds;
        if (state.Parent is { } parent
            && parent != Root
            && _nodes.TryGetValue(parent.Id, out var parentState)
            && parentState.Control is Canvas)
        {
            state.Control.SetValue(Canvas.LeftProperty, bounds.X);
            state.Control.SetValue(Canvas.TopProperty, bounds.Y);
            state.Control.Width = bounds.Width;
            state.Control.Height = bounds.Height;
        }
        state.Control.Arrange(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    public override void SetVisible(HtmlMlBackendNode node, bool visible)
    {
        RequireMounted();
        Get(node).Control.IsVisible = visible;
    }

    public override void SetZIndex(HtmlMlBackendNode node, int zIndex)
    {
        RequireMounted();
        Get(node).Control.SetValue(Canvas.ZIndexProperty, zIndex);
    }

    public override void Invalidate(HtmlMlBackendNode node, HtmlMlInvalidationKind kind)
    {
        RequireMounted();
        var control = Get(node).Control;
        if ((kind & (HtmlMlInvalidationKind.Style | HtmlMlInvalidationKind.Measure)) != 0)
        {
            control.InvalidateMeasure();
        }
        if ((kind & HtmlMlInvalidationKind.Arrange) != 0)
        {
            control.InvalidateArrange();
        }
        if ((kind & (HtmlMlInvalidationKind.Render
                     | HtmlMlInvalidationKind.HitTest
                     | HtmlMlInvalidationKind.Accessibility)) != 0)
        {
            control.InvalidateVisual();
        }
    }

    public override HtmlMlBackendNode? HitTest(HtmlMlPoint point)
    {
        RequireMounted();
        NodeState? projectedHit = null;
        var projectedZIndex = int.MinValue;
        foreach (var state in _nodes.Values)
        {
            if (state.Parent is null
                || !IsVisibleThroughAncestors(state)
                || !ContainsPoint(GetAbsoluteBounds(state), point))
            {
                continue;
            }
            var zIndex = state.Control.GetValue(Canvas.ZIndexProperty);
            if (projectedHit is null || zIndex >= projectedZIndex)
            {
                projectedHit = state;
                projectedZIndex = zIndex;
            }
        }
        if (projectedHit is not null)
        {
            return projectedHit.Node;
        }

        var hit = TopLevel.InputHitTest(new Point(point.X, point.Y)) as Visual;
        for (var current = hit; current is not null; current = current.GetVisualParent())
        {
            if (current is Control control && _nodesByControl.TryGetValue(control, out var node))
            {
                return node;
            }
        }
        return null;
    }

    private static bool ContainsPoint(HtmlMlRect bounds, HtmlMlPoint point)
        => bounds.Width > 0
           && bounds.Height > 0
           && point.X >= bounds.X
           && point.Y >= bounds.Y
           && point.X < bounds.X + bounds.Width
           && point.Y < bounds.Y + bounds.Height;

    private HtmlMlRect GetAbsoluteBounds(NodeState state)
    {
        var bounds = state.Bounds;
        var parent = state.Parent;
        while (parent is { } node && node != Root && _nodes.TryGetValue(node.Id, out var parentState))
        {
            bounds = bounds with
            {
                X = bounds.X + parentState.Bounds.X,
                Y = bounds.Y + parentState.Bounds.Y
            };
            parent = parentState.Parent;
        }
        return bounds;
    }

    private bool IsVisibleThroughAncestors(NodeState state)
    {
        for (var current = state; ;)
        {
            if (!current.Control.IsVisible)
            {
                return false;
            }
            if (current.Parent is not { } parent || parent == Root)
            {
                return current.Parent is not null;
            }
            if (!_nodes.TryGetValue(parent.Id, out current))
            {
                return false;
            }
        }
    }

    protected override void ReportDiagnostic(HtmlMlBackendDiagnostic diagnostic)
        => _diagnostics.Add(diagnostic);

    protected override void OnUnmount()
    {
        foreach (var state in _nodes.Values)
        {
            DetachCore(state);
        }
    }

    protected override void DisposeCore()
    {
        foreach (var state in _nodes.Values)
        {
            DetachCore(state);
        }
        _nodes.Clear();
        _nodesByControl.Clear();
        if (_ownsServices)
        {
            _services.Dispose();
        }
    }

    private NodeState Get(HtmlMlBackendNode node)
    {
        if (node.IsEmpty || node == Root || !_nodes.TryGetValue(node.Id, out var state) || state.Node != node)
        {
            throw new InvalidOperationException($"Unknown Avalonia backend node '{node.Id}'.");
        }
        return state;
    }

    private void DetachCore(NodeState state)
    {
        if (state.Parent is not { } parent)
        {
            return;
        }

        if (parent == Root)
        {
            if (ReferenceEquals(TopLevel.Content, state.Control))
            {
                TopLevel.Content = null;
            }
        }
        else if (_nodes.TryGetValue(parent.Id, out var parentState))
        {
            switch (parentState.Control)
            {
                case Panel panel:
                    panel.Children.Remove(state.Control);
                    break;
                case ContentControl content when ReferenceEquals(content.Content, state.Control):
                    content.Content = null;
                    break;
            }
        }
        state.Parent = null;
    }

    private sealed class NodeState(HtmlMlBackendNode node, Control control)
    {
        public HtmlMlBackendNode Node { get; } = node;
        public Control Control { get; } = control;
        public HtmlMlRect Bounds { get; set; }
        public HtmlMlBackendNode? Parent { get; set; }
    }
}
