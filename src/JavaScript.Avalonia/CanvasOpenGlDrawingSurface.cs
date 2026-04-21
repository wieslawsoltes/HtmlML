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

    internal CanvasWebGlRenderingContext? Context { get; set; }

    internal bool IsOpenGlAvailable { get; private set; }

    internal string RenderBackend { get; private set; } = "Avalonia OpenGL pending";

    internal void RequestRender()
    {
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        IsOpenGlAvailable = true;
        RenderBackend = BuildBackendName(gl);
        Context?.OnOpenGlInit(gl);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Context?.OnOpenGlDeinit(gl);
        IsOpenGlAvailable = false;
        RenderBackend = "Avalonia OpenGL deinitialized";
    }

    protected override void OnOpenGlLost()
    {
        Context?.OnOpenGlLost();
        IsOpenGlAvailable = false;
        RenderBackend = "Avalonia OpenGL context lost";
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        RenderBackend = BuildBackendName(gl);
        Context?.RenderOpenGl(gl, fb, GetCurrentPixelSize());
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
