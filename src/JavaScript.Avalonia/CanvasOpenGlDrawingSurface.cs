using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

namespace JavaScript.Avalonia;

public sealed class CanvasOpenGlDrawingSurface : OpenGlControlBase
{
    public CanvasOpenGlDrawingSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    private CanvasWebGlRenderingContext? _context;

    internal CanvasWebGlRenderingContext? Context
    {
        get => _context;
        set
        {
            _context = value;
            RequestRender();
        }
    }

    internal bool IsOpenGlAvailable { get; private set; }

    internal string RenderBackend { get; private set; } = "Avalonia OpenGL pending";

    internal double DrawingBufferWidth { get; private set; }

    internal double DrawingBufferHeight { get; private set; }

    internal void SetDrawingBufferWidth(double width)
    {
        if (!double.IsFinite(width) || width < 0)
        {
            return;
        }

        DrawingBufferWidth = width;
        RequestRender();
    }

    internal void SetDrawingBufferHeight(double height)
    {
        if (!double.IsFinite(height) || height < 0)
        {
            return;
        }

        DrawingBufferHeight = height;
        RequestRender();
    }

    internal void RequestRender()
    {
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        IsOpenGlAvailable = true;
        RenderBackend = BuildBackendName(gl);
        _context?.OnOpenGlInit(gl);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _context?.OnOpenGlDeinit(gl);
        IsOpenGlAvailable = false;
        RenderBackend = "Avalonia OpenGL deinitialized";
    }

    protected override void OnOpenGlLost()
    {
        _context?.OnOpenGlLost();
        IsOpenGlAvailable = false;
        RenderBackend = "Avalonia OpenGL context lost";
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        RenderBackend = BuildBackendName(gl);
        _context?.RenderOpenGl(gl, fb, GetCurrentPixelSize());
    }

    private PixelSize GetCurrentPixelSize()
    {
        var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        return new PixelSize(
            Math.Max(1, (int)Math.Round(Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Round(Bounds.Height * scaling)));
    }

    private static string BuildBackendName(GlInterface gl)
    {
        var renderer = string.IsNullOrWhiteSpace(gl.Renderer) ? "unknown renderer" : gl.Renderer;
        var version = string.IsNullOrWhiteSpace(gl.Version) ? "unknown version" : gl.Version;
        return $"Avalonia OpenGL ({renderer}; {version})";
    }
}
