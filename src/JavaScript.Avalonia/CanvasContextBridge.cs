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
        private Decorator? _openGlAdorner;
        private Decorator? _openGlDecoratorHost;
        private CanvasWebGlRenderingContext? _webGlContext;
        private AdornerLayer? _layer;

        public CanvasAttachment(Control target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            // DOM canvases are now real drawing controls in the visual tree.
            // Existing arbitrary controls retain the adorner compatibility
            // path, but must not be used for DOM-created <canvas> elements.
            _surface = target as CanvasDrawingSurface ?? new CanvasDrawingSurface();
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

                if (_target is CanvasOpenGlDrawingSurface nativeSurface)
                {
                    _openGlSurface = nativeSurface;
                    _webGlContext = new CanvasWebGlRenderingContext(_surface, _openGlSurface);
                    _openGlSurface.Context = _webGlContext;
                    _openGlSurface.RequestRender();
                    return _webGlContext;
                }

                if (EnableInjectedOpenGlSurface)
                {
                    _openGlSurface = new CanvasOpenGlDrawingSurface();
                    _webGlContext = new CanvasWebGlRenderingContext(_surface, _openGlSurface);
                    _openGlSurface.Context = _webGlContext;

                    AttachOpenGlSurface();
                    return _webGlContext;
                }

                _webGlContext = new CanvasWebGlRenderingContext(_surface);

                return _webGlContext;
            }
        }

        public CanvasDrawingSurface Surface => _surface;

        public void Reset2D()
        {
            _context.ResetForCanvasResize();
        }

        private bool UsesNativeOpenGlTarget => ReferenceEquals(_openGlSurface, _target) || _target is CanvasOpenGlDrawingSurface;

        private bool UsesInTreeCanvasSurface => ReferenceEquals(_surface, _target);

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
            if (UsesNativeOpenGlTarget || UsesInTreeCanvasSurface)
            {
                return;
            }

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

            // Respect z-index for chart layers (axes, crosshair, plot overlays).
            // AdornerLayer paints in Children order (later on top) and also honors ZIndex on its children.
            int insertAt = layer.Children.Count;
            var targetZ = _target.GetValue(Canvas.ZIndexProperty);
            if (targetZ is int tz)
            {
                _surface.SetValue(Canvas.ZIndexProperty, tz);
                // Insert before the first existing child that has a strictly higher z (so higher z end up later).
                for (int i = 0; i < layer.Children.Count; i++)
                {
                    var ez = layer.Children[i].GetValue(Canvas.ZIndexProperty);
                    if (ez is int ezv && ezv > tz)
                    {
                        insertAt = i;
                        break;
                    }
                }
            }
            layer.Children.Insert(insertAt, _surface);

            _layer = layer;
            _surface.InvalidateVisual();

            if (_openGlSurface is not null && !ReferenceEquals(_openGlSurface, _target))
            {
                AttachOpenGlSurface();
            }
        }

        private void DetachFromLayer()
        {
            if (_layer is { } layer)
            {
                layer.Children.Remove(_surface);
                if (_openGlAdorner is not null)
                {
                    layer.Children.Remove(_openGlAdorner);
                }

                _layer = null;
            }

            AdornerLayer.SetAdornedElement(_surface, null);
            if (_openGlAdorner is not null)
            {
                AdornerLayer.SetAdornedElement(_openGlAdorner, null);
            }

            DetachOpenGlSurfaceFromTarget();
        }

        private void AttachOpenGlSurface()
        {
            if (_openGlSurface is null)
            {
                return;
            }

            if (ReferenceEquals(_openGlSurface, _target))
            {
                return;
            }

            if (_openGlSurface.GetVisualParent() is not null)
            {
                if (_openGlAdorner?.GetVisualParent() is null)
                {
                    AttachSurface(_openGlAdorner);
                }

                return;
            }

            if (_target is Decorator { Child: null } decorator)
            {
                decorator.Child = _openGlSurface;
                _openGlDecoratorHost = decorator;
                return;
            }

            _openGlAdorner = new Decorator
            {
                Child = _openGlSurface,
                ClipToBounds = true,
                IsHitTestVisible = false,
                Focusable = false
            };
            AttachSurface(_openGlAdorner);
        }

        private void DetachOpenGlSurfaceFromTarget()
        {
            if (_openGlDecoratorHost is { } host && ReferenceEquals(host.Child, _openGlSurface))
            {
                host.Child = null;
            }

            _openGlDecoratorHost = null;
        }

        private void AttachSurface(Control? surface)
        {
            if (surface is null || _layer is not { } layer || layer.Children.Contains(surface))
            {
                return;
            }

            AdornerLayer.SetAdornedElement(surface, _target);
            AdornerLayer.SetIsClipEnabled(surface, true);

            int insertAt = layer.Children.Count;
            var targetZ2 = _target.GetValue(Canvas.ZIndexProperty);
            if (targetZ2 is int tz2)
            {
                surface.SetValue(Canvas.ZIndexProperty, tz2);
                for (int i = 0; i < layer.Children.Count; i++)
                {
                    var ez = layer.Children[i].GetValue(Canvas.ZIndexProperty);
                    if (ez is int ezv && ezv > tz2)
                    {
                        insertAt = i;
                        break;
                    }
                }
            }
            layer.Children.Insert(insertAt, surface);
        }
    }

    private static readonly ConditionalWeakTable<Control, CanvasAttachment> s_attachments = new();

    private static bool EnableInjectedOpenGlSurface =>
        AppContext.TryGetSwitch("JavaScript.Avalonia.EnableInjectedOpenGlCanvasSurface", out var enabled) && enabled;

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

    internal static bool TryGetVirtualSize(Control control, out double width, out double height)
    {
        if (control is not null && s_attachments.TryGetValue(control, out var attachment))
        {
            width = attachment.Surface.VirtualWidth;
            height = attachment.Surface.VirtualHeight;
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    internal static void SetVirtualSize(Control control, double? width = null, double? height = null)
    {
        if (control is null)
        {
            return;
        }

        var attachment = s_attachments.GetValue(control, static c => new CanvasAttachment(c));
        if (width is { } w && double.IsFinite(w) && w >= 0)
        {
            attachment.Surface.VirtualWidth = w;
        }

        if (height is { } h && double.IsFinite(h) && h >= 0)
        {
            attachment.Surface.VirtualHeight = h;
        }

        attachment.Reset2D();
        attachment.Surface.InvalidateCanvasVisual();
    }

    internal static void Reset2D(Control control)
    {
        if (control is not null && s_attachments.TryGetValue(control, out var attachment))
        {
            attachment.Reset2D();
        }
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
