using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace JavaScript.Avalonia;

internal static class CanvasContextBridge
{
    private sealed class CanvasAttachment
    {
        private readonly Control _target;
        private readonly CanvasDrawingSurface _surface;
        private readonly CanvasRenderingContext2D _context;
        private AdornerLayer? _layer;

        public CanvasAttachment(Control target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _surface = new CanvasDrawingSurface();
            _context = _surface.Context;

            _target.AttachedToVisualTree += OnAttached;
            _target.DetachedFromVisualTree += OnDetached;

            if (IsAttached(_target))
            {
                AttachToLayer();
            }
        }

        public CanvasRenderingContext2D Context => _context;

        private static bool IsAttached(Visual visual) => visual.GetVisualRoot() is not null;

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            AttachToLayer();
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            DetachFromLayer();
        }

        private void AttachToLayer()
        {
            if (_layer is not null)
            {
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(_target);
            if (layer is null)
            {
                return;
            }

            AdornerLayer.SetAdornedElement(_surface, _target);
            AdornerLayer.SetIsClipEnabled(_surface, true);
            layer.Children.Add(_surface);
            _layer = layer;
            _surface.InvalidateVisual();
        }

        private void DetachFromLayer()
        {
            if (_layer is { } layer)
            {
                layer.Children.Remove(_surface);
                _layer = null;
            }

            AdornerLayer.SetAdornedElement(_surface, null);
        }
    }

    private static readonly ConditionalWeakTable<Control, CanvasAttachment> s_attachments = new();

    public static object? GetContext(Control control, string type)
    {
        if (!string.Equals(type, "2d", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (control is null)
        {
            throw new ArgumentNullException(nameof(control));
        }

        var attachment = s_attachments.GetValue(control, static c => new CanvasAttachment(c));
        return attachment.Context;
    }
}
