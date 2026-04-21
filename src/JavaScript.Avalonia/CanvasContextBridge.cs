using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace JavaScript.Avalonia;

internal static class CanvasContextBridge
{
    private sealed class CanvasAttachment
    {
        private readonly Control _target;
        private readonly CanvasDrawingSurface _surface;
        private readonly CanvasRenderingContext2D _context;
        private CanvasOpenGlDrawingSurface? _openGlSurface;
        private CanvasWebGlRenderingContext? _webGlContext;
        private AdornerLayer? _layer;

        public CanvasAttachment(Control target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _surface = new CanvasDrawingSurface();
            _context = _surface.Context;

            EnsureInteractiveTarget();
            _target.AttachedToVisualTree += OnAttached;
            _target.DetachedFromVisualTree += OnDetached;

            if (IsAttached(_target))
            {
                AttachToLayer();
            }
        }

        public CanvasRenderingContext2D Context => _context;

        public CanvasWebGlRenderingContext WebGlContext
        {
            get
            {
                if (_webGlContext is not null)
                {
                    return _webGlContext;
                }

                _openGlSurface = new CanvasOpenGlDrawingSurface();
                _webGlContext = new CanvasWebGlRenderingContext(_surface, _openGlSurface);
                _openGlSurface.Context = _webGlContext;

                if (_layer is not null)
                {
                    AttachSurface(_openGlSurface);
                }

                return _webGlContext;
            }
        }

        public CanvasDrawingSurface Surface => _surface;

        private static bool IsAttached(Visual visual) => visual.GetVisualRoot() is not null;

        private void EnsureInteractiveTarget()
        {
            if (!_target.Focusable)
            {
                _target.Focusable = true;
            }

            if (_target is InputElement inputElement && !inputElement.IsTabStop)
            {
                inputElement.IsTabStop = true;
            }

            _target.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            _target.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            AttachToLayer();
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            DetachFromLayer();
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, _target) || !_target.Focusable)
            {
                return;
            }

            TryFocus(_target, NavigationMethod.Pointer, e.KeyModifiers);
            e.Pointer.Capture(_target);
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (ReferenceEquals(e.Source, _target) && ReferenceEquals(e.Pointer.Captured, _target))
            {
                e.Pointer.Capture(null);
            }
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

            if (_openGlSurface is not null)
            {
                AttachSurface(_openGlSurface);
            }
        }

        private void DetachFromLayer()
        {
            if (_layer is { } layer)
            {
                layer.Children.Remove(_surface);
                if (_openGlSurface is not null)
                {
                    layer.Children.Remove(_openGlSurface);
                }

                _layer = null;
            }

            AdornerLayer.SetAdornedElement(_surface, null);
            if (_openGlSurface is not null)
            {
                AdornerLayer.SetAdornedElement(_openGlSurface, null);
            }
        }

        private void AttachSurface(Control surface)
        {
            if (_layer is not { } layer || layer.Children.Contains(surface))
            {
                return;
            }

            AdornerLayer.SetAdornedElement(surface, _target);
            AdornerLayer.SetIsClipEnabled(surface, true);
            layer.Children.Add(surface);
        }
    }

    private static readonly ConditionalWeakTable<Control, CanvasAttachment> s_attachments = new();

    public static object? GetContext(Control control, string type)
    {
        if (!string.Equals(type, "2d", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "webgl", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "experimental-webgl", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (control is null)
        {
            throw new ArgumentNullException(nameof(control));
        }

        var attachment = s_attachments.GetValue(control, static c => new CanvasAttachment(c));
        if (string.Equals(type, "webgl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "experimental-webgl", StringComparison.OrdinalIgnoreCase))
        {
            return attachment.WebGlContext;
        }

        return attachment.Context;
    }

    internal static bool TryGetSurface(Control control, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CanvasDrawingSurface? surface)
    {
        if (control is not null && s_attachments.TryGetValue(control, out var attachment))
        {
            surface = attachment.Surface;
            return true;
        }

        surface = null;
        return false;
    }

    private static bool TryFocus(Control control, NavigationMethod navigationMethod, KeyModifiers keyModifiers)
    {
        if (control.Focus(navigationMethod, keyModifiers))
        {
            return true;
        }

        var focusManager = (control.GetVisualRoot() as TopLevel)?.FocusManager;
        var focusMethod = focusManager?.GetType().GetMethod(
            "Focus",
            new[] { typeof(IInputElement), typeof(NavigationMethod), typeof(KeyModifiers) });

        if (focusMethod?.Invoke(focusManager, new object?[] { control, navigationMethod, keyModifiers }) is bool result)
        {
            return result;
        }

        return control.IsFocused;
    }
}
