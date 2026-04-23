using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Threading;
using Jint.Native;

namespace JavaScript.Avalonia;

internal sealed partial class CanvasWebGlRenderingContext
{
    private WebGlCommand[] _submittedCommands = Array.Empty<WebGlCommand>();
    private bool _submitQueued;

    internal void OnOpenGlInit(GlInterface gl)
    {
        _openGlRenderer?.Dispose(gl);
        _openGlRenderer = new OpenGlRenderer(this, gl);
        _openGlRenderBackend = BuildOpenGlBackendName(gl);
        RequestNativeRender();
    }

    internal void OnOpenGlDeinit(GlInterface gl)
    {
        _openGlRenderer?.Dispose(gl);
        _openGlRenderer = null;
        ResetNativeResourceIds();
    }

    internal void OnOpenGlLost()
    {
        _openGlRenderer = null;
        ResetNativeResourceIds();
    }

    internal void RenderOpenGl(GlInterface gl, int framebuffer, PixelSize pixelSize)
    {
        _openGlRenderer ??= new OpenGlRenderer(this, gl);
        _openGlRenderBackend = BuildOpenGlBackendName(gl);

        WebGlCommand[] commands;
        lock (_commandLock)
        {
            commands = _submittedCommands;
        }

        _openGlRenderer.Render(gl, framebuffer, pixelSize, commands);
    }

    private bool ShouldUseSoftwareFallback => _openGlSurface is null;

    private void RequestNativeRender()
    {
        if (_openGlSurface is null)
        {
            return;
        }

        lock (_commandLock)
        {
            if (_submitQueued)
            {
                return;
            }

            _submitQueued = true;
        }

        Dispatcher.UIThread.Post(() => SubmitNativeFrame(allowClearOnly: false), DispatcherPriority.Send);
    }

    private void SubmitNativeFrame(bool allowClearOnly)
    {
        CanvasOpenGlDrawingSurface? surface;
        lock (_commandLock)
        {
            var commands = _commands.ToArray();
            if (allowClearOnly || HasDrawCommand(commands) || !HasDrawCommand(_submittedCommands))
            {
                _submittedCommands = commands;
            }

            _submitQueued = false;
            surface = _openGlSurface;
        }

        surface?.RequestRender();
    }

    private static bool HasDrawCommand(IEnumerable<WebGlCommand> commands)
        => commands.Any(static command => command is WebGlDrawCommand);

    private void QueueClear(int mask)
    {
        lock (_commandLock)
        {
            if ((mask & COLOR_BUFFER_BIT) != 0 && _framebuffer is null)
            {
                _commands.Clear();
            }

            _commands.Add(new WebGlClearCommand(mask, SnapshotPipelineState()));
        }
    }

    private void QueueDraw(WebGlDrawCommand command)
    {
        lock (_commandLock)
        {
            _commands.Add(command);
        }
    }

    private Dictionary<int, WebGlVertexAttribState> SnapshotAttributes()
    {
        var source = _currentVertexArray?.Attributes ?? _attributes;
        return new Dictionary<int, WebGlVertexAttribState>(source);
    }

    private HashSet<int> SnapshotEnabledAttributes()
    {
        var source = _currentVertexArray?.EnabledAttributes ?? _enabledAttributes;
        return new HashSet<int>(source);
    }

    private Dictionary<int, WebGlTexture?> SnapshotTextures()
    {
        var result = new Dictionary<int, WebGlTexture?>();
        for (var i = 0; i < _textureUnits.Length; i++)
        {
            if (_textureUnits[i] is not null)
            {
                result[i] = _textureUnits[i];
            }
        }

        return result;
    }

    private IReadOnlyDictionary<string, object?> SnapshotUniforms(WebGlProgram? program)
    {
        if (program is null || program.UniformValues.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, object?>(program.UniformValues.Count, StringComparer.Ordinal);
        foreach (var uniform in program.UniformValues)
        {
            result[uniform.Key] = CloneUniformValue(uniform.Value);
        }

        return result;
    }

    private static object? CloneUniformValue(object? value)
        => value is Array array ? array.Clone() : value;

    private WebGlPipelineState SnapshotPipelineState()
        => new(
            (double[])_clearColor.Clone(),
            _clearDepth,
            _clearStencil,
            (double[])_viewport.Clone(),
            (double[])_scissor.Clone(),
            new HashSet<int>(_enabledCaps),
            (bool[])_colorMask.Clone(),
            _depthMask,
            _depthFunc,
            _blendSrcRgb,
            _blendDstRgb,
            _blendSrcAlpha,
            _blendDstAlpha,
            _blendEquationRgb,
            _blendEquationAlpha,
            (double[])_blendColor.Clone(),
            _cullFaceMode,
            _frontFaceMode,
            _polygonOffsetFactor,
            _polygonOffsetUnits,
            _lineWidth,
            _framebuffer);

    private int EstimateTriangleCount(int mode, int count)
        => mode switch
        {
            var value when value == TRIANGLES => Math.Max(0, count / 3),
            var value when value == TRIANGLE_STRIP || value == TRIANGLE_FAN => Math.Max(0, count - 2),
            _ => 0
        };

    private WebGlTexture? GetBoundTexture(int target)
        => target == TEXTURE_2D ? _textureUnits[_activeTextureUnit] : null;

    private void ResetNativeResourceIds()
    {
        foreach (var buffer in _attributes.Values.Select(static state => state.Buffer).Where(static buffer => buffer is not null))
        {
            ResetBuffer(buffer!);
        }

        ResetBuffer(_arrayBuffer);
        ResetBuffer(_elementArrayBuffer);
        ResetFramebuffer(_framebuffer);
        ResetRenderbuffer(_renderbuffer);
        ResetProgram(_currentProgram);

        foreach (var texture in _textureUnits)
        {
            ResetTexture(texture);
        }

        lock (_commandLock)
        {
            foreach (var command in _commands.OfType<WebGlDrawCommand>())
            {
                ResetProgram(command.Program);
                ResetBuffer(command.ElementArrayBuffer);
                ResetFramebuffer(command.State.Framebuffer);
                foreach (var attribute in command.Attributes.Values)
                {
                    ResetBuffer(attribute.Buffer);
                }

                foreach (var texture in command.Textures.Values)
                {
                    ResetTexture(texture);
                }
            }

            foreach (var command in _commands.OfType<WebGlClearCommand>())
            {
                ResetFramebuffer(command.State.Framebuffer);
            }
        }
    }

    private static void ResetBuffer(WebGlBuffer? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        buffer.NativeId = 0;
        buffer.NativeDirty = true;
    }

    private static void ResetTexture(WebGlTexture? texture)
    {
        if (texture is null)
        {
            return;
        }

        texture.NativeId = 0;
        texture.NativeDirty = true;
    }

    private static void ResetFramebuffer(WebGlFramebuffer? framebuffer)
    {
        if (framebuffer is null)
        {
            return;
        }

        framebuffer.NativeId = 0;
        framebuffer.NativeDirty = true;
        ResetTexture(framebuffer.ColorTexture);
        ResetRenderbuffer(framebuffer.DepthRenderbuffer);
        ResetRenderbuffer(framebuffer.StencilRenderbuffer);
        ResetRenderbuffer(framebuffer.DepthStencilRenderbuffer);
    }

    private static void ResetRenderbuffer(WebGlRenderbuffer? renderbuffer)
    {
        if (renderbuffer is null)
        {
            return;
        }

        renderbuffer.NativeId = 0;
        renderbuffer.NativeDirty = true;
    }

    private static void ResetProgram(WebGlProgram? program)
    {
        if (program is null)
        {
            return;
        }

        program.NativeId = 0;
        program.NativeLinked = false;
        program.NativeDirty = true;
        foreach (var shader in program.Shaders)
        {
            shader.NativeId = 0;
            shader.NativeDirty = true;
        }
    }

    private static int ToInt(object? value)
    {
        if (value is JsValue jsValue)
        {
            return ToInt(jsValue.ToObject());
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        return 0;
    }

    private static bool ToBool(object? value)
    {
        if (value is JsValue jsValue)
        {
            return ToBool(jsValue.ToObject());
        }

        return value switch
        {
            bool boolValue => boolValue,
            IConvertible convertible => TryConvertToDouble(convertible, out var number) && number != 0,
            _ => value is not null
        };
    }

    private static string BuildOpenGlBackendName(GlInterface gl)
    {
        var renderer = string.IsNullOrWhiteSpace(gl.Renderer) ? "unknown renderer" : gl.Renderer;
        var version = string.IsNullOrWhiteSpace(gl.Version) ? "unknown version" : gl.Version;
        return $"Avalonia OpenGL ({renderer}; {version})";
    }

    private abstract record WebGlCommand;

    private sealed record WebGlClearCommand(int Mask, WebGlPipelineState State) : WebGlCommand;

    private sealed record WebGlDrawCommand(
        int Mode,
        int First,
        int Count,
        int Type,
        int Offset,
        bool Indexed,
        WebGlProgram? Program,
        IReadOnlyDictionary<int, WebGlVertexAttribState> Attributes,
        IReadOnlySet<int> EnabledAttributes,
        WebGlBuffer? ElementArrayBuffer,
        IReadOnlyDictionary<int, WebGlTexture?> Textures,
        IReadOnlyDictionary<string, object?> Uniforms,
        WebGlPipelineState State) : WebGlCommand;

    private sealed record WebGlPipelineState(
        double[] ClearColor,
        double ClearDepth,
        int ClearStencil,
        double[] Viewport,
        double[] Scissor,
        IReadOnlySet<int> EnabledCaps,
        bool[] ColorMask,
        bool DepthMask,
        int DepthFunc,
        int BlendSrcRgb,
        int BlendDstRgb,
        int BlendSrcAlpha,
        int BlendDstAlpha,
        int BlendEquationRgb,
        int BlendEquationAlpha,
        double[] BlendColor,
        int CullFaceMode,
        int FrontFaceMode,
        double PolygonOffsetFactor,
        double PolygonOffsetUnits,
        double LineWidth,
        WebGlFramebuffer? Framebuffer);

    private sealed class OpenGlRenderer
    {
        private readonly CanvasWebGlRenderingContext _context;
        private readonly HashSet<int> _buffers = new();
        private readonly HashSet<int> _shaders = new();
        private readonly HashSet<int> _programs = new();
        private readonly HashSet<int> _textures = new();
        private readonly HashSet<int> _framebuffers = new();
        private readonly HashSet<int> _renderbuffers = new();
        private readonly HashSet<int> _enabledAttributes = new();
        private OpenGlApi _api;
        private int _vertexArray;

        public OpenGlRenderer(CanvasWebGlRenderingContext context, GlInterface gl)
        {
            _context = context;
            _api = new OpenGlApi(gl);
            _context.NativeGlCapabilities = _api.CapabilitySummary;
        }

        public void Render(GlInterface gl, int framebuffer, PixelSize pixelSize, IReadOnlyList<WebGlCommand> commands)
        {
            if (!ReferenceEquals(_api.Gl, gl))
            {
                _api = new OpenGlApi(gl);
            }

            _context.NativeGlCapabilities = _api.CapabilitySummary;
            _api.BindFramebuffer(_context.FRAMEBUFFER, framebuffer);
            gl.Viewport(0, 0, pixelSize.Width, pixelSize.Height);

            var drawCalls = 0;
            var drawCommands = 0;
            var lastFailure = string.Empty;
            foreach (var command in commands)
            {
                switch (command)
                {
                    case WebGlClearCommand clear:
                        RenderClear(gl, framebuffer, pixelSize, clear);
                        break;
                    case WebGlDrawCommand draw:
                        drawCommands++;
                        if (RenderDraw(gl, framebuffer, pixelSize, draw))
                        {
                            drawCalls++;
                        }
                        else
                        {
                            lastFailure = _context.LastDrawStatus;
                        }

                        break;
                }
            }

            gl.Flush();
            string status;
            if (drawCalls > 0)
            {
                status = $"Rendered {drawCalls}/{drawCommands} WebGL draw call(s) through Avalonia OpenGL";
                if (drawCalls < drawCommands && !string.IsNullOrWhiteSpace(lastFailure))
                {
                    status += $"; last skipped draw: {lastFailure}";
                }
            }
            else if (drawCommands == 0)
            {
                status = $"Rendered WebGL frame through Avalonia OpenGL ({commands.Count} command(s), no draw commands)";
            }
            else if (!string.IsNullOrWhiteSpace(lastFailure))
            {
                status = lastFailure;
            }
            else
            {
                status = "No WebGL draw calls completed through Avalonia OpenGL";
            }

            _context.LastDrawStatus = status;
            _context.LastNativeDrawStatus = status;
        }

        public void Dispose(GlInterface gl)
        {
            if (_vertexArray != 0)
            {
                try
                {
                    gl.DeleteVertexArray(_vertexArray);
                }
                catch
                {
                    // Some embedded profiles expose vertex arrays only through extensions.
                }

                _vertexArray = 0;
            }

            foreach (var buffer in _buffers)
            {
                gl.DeleteBuffer(buffer);
            }

            foreach (var texture in _textures)
            {
                gl.DeleteTexture(texture);
            }

            foreach (var framebuffer in _framebuffers)
            {
                _api.DeleteFramebuffer(framebuffer);
            }

            foreach (var renderbuffer in _renderbuffers)
            {
                _api.DeleteRenderbuffer(renderbuffer);
            }

            foreach (var program in _programs)
            {
                gl.DeleteProgram(program);
            }

            foreach (var shader in _shaders)
            {
                gl.DeleteShader(shader);
            }

            _buffers.Clear();
            _textures.Clear();
            _framebuffers.Clear();
            _renderbuffers.Clear();
            _programs.Clear();
            _shaders.Clear();
            _enabledAttributes.Clear();
        }

        private void RenderClear(GlInterface gl, int defaultFramebuffer, PixelSize pixelSize, WebGlClearCommand command)
        {
            var state = command.State;
            if (!ApplyFramebufferAndMasks(gl, defaultFramebuffer, pixelSize, state))
            {
                return;
            }

            var targetSize = GetTargetPixelSize(pixelSize, state.Framebuffer);
            gl.Viewport(0, 0, targetSize.Width, targetSize.Height);
            gl.ClearColor(
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(0), 0, 1),
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(1), 0, 1),
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(2), 0, 1),
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(3), 0, 1));
            gl.ClearDepth((float)Math.Clamp(state.ClearDepth, 0, 1));
            gl.ClearStencil(state.ClearStencil);
            gl.Clear(command.Mask);
        }

        private bool RenderDraw(GlInterface gl, int defaultFramebuffer, PixelSize pixelSize, WebGlDrawCommand command)
        {
            DrainGlErrors(gl);

            if (command.Program is not { Deleted: false } program || !EnsureProgram(gl, program))
            {
                return false;
            }

            if (!ApplyPipelineState(gl, defaultFramebuffer, pixelSize, command.State))
            {
                return false;
            }
            if (!CheckGlError(gl, "pipeline state"))
            {
                return false;
            }

            ApplyViewport(gl, GetTargetPixelSize(pixelSize, command.State.Framebuffer), command.State.Viewport);
            if (!CheckGlError(gl, "viewport"))
            {
                return false;
            }

            if (!ApplyTextures(gl, command.Textures))
            {
                return false;
            }
            if (!CheckGlError(gl, "texture binding"))
            {
                return false;
            }

            gl.UseProgram(program.NativeId);
            if (!CheckGlError(gl, "program bind"))
            {
                return false;
            }

            ApplyUniforms(gl, program, command);
            if (!CheckGlError(gl, "uniform upload"))
            {
                return false;
            }

            EnsureVertexArray(gl);
            ApplyAttributes(gl, command);
            if (!CheckGlError(gl, "attribute binding"))
            {
                return false;
            }

            if (command.Indexed)
            {
                if (command.ElementArrayBuffer is not { Deleted: false } elementBuffer)
                {
                    _context.LastDrawStatus = "WebGL drawElements skipped: no element array buffer";
                    return false;
                }

                EnsureBuffer(gl, elementBuffer, _context.ELEMENT_ARRAY_BUFFER);
                gl.BindBuffer(_context.ELEMENT_ARRAY_BUFFER, elementBuffer.NativeId);
                gl.DrawElements(command.Mode, command.Count, command.Type, new IntPtr(command.Offset));
            }
            else
            {
                gl.DrawArrays(command.Mode, command.First, new IntPtr(command.Count));
            }

            return CheckGlError(gl, $"draw {DescribeDrawCommand(command)}");
        }

        private bool CheckGlError(GlInterface gl, string stage)
        {
            var error = gl.GetError();
            if (error == _context.NO_ERROR)
            {
                return true;
            }

            _context.LastDrawStatus = $"OpenGL {stage} failed with error 0x{error:X}";
            return false;
        }

        private void DrainGlErrors(GlInterface gl)
        {
            for (var i = 0; i < 16; i++)
            {
                if (gl.GetError() == _context.NO_ERROR)
                {
                    return;
                }
            }
        }

        private static string DescribeDrawCommand(WebGlDrawCommand command)
            => command.Indexed
                ? $"elements mode {command.Mode}, count {command.Count}, type 0x{command.Type:X}, offset {command.Offset}"
                : $"arrays mode {command.Mode}, first {command.First}, count {command.Count}";

        private void EnsureVertexArray(GlInterface gl)
        {
            if (_vertexArray != 0)
            {
                TryBindVertexArray(gl, _vertexArray);
                return;
            }

            try
            {
                _vertexArray = gl.GenVertexArray();
                if (_vertexArray != 0)
                {
                    gl.BindVertexArray(_vertexArray);
                }
            }
            catch
            {
                _vertexArray = 0;
            }
        }

        private static void TryBindVertexArray(GlInterface gl, int vertexArray)
        {
            try
            {
                gl.BindVertexArray(vertexArray);
            }
            catch
            {
                // Desktop compatibility profiles do not need a VAO; core profiles do.
            }
        }

        private bool ApplyFramebufferAndMasks(GlInterface gl, int defaultFramebuffer, PixelSize pixelSize, WebGlPipelineState state)
        {
            if (!BindFramebuffer(gl, defaultFramebuffer, state.Framebuffer))
            {
                return false;
            }
            if (!CheckGlError(gl, "framebuffer bind"))
            {
                return false;
            }

            var targetSize = GetTargetPixelSize(pixelSize, state.Framebuffer);
            gl.Viewport(0, 0, targetSize.Width, targetSize.Height);
            if (!CheckGlError(gl, "framebuffer viewport"))
            {
                return false;
            }

            _api.ColorMask(
                ToByte(state.ColorMask.ElementAtOrDefault(0)),
                ToByte(state.ColorMask.ElementAtOrDefault(1)),
                ToByte(state.ColorMask.ElementAtOrDefault(2)),
                ToByte(state.ColorMask.ElementAtOrDefault(3)));
            if (!CheckGlError(gl, "color mask"))
            {
                return false;
            }

            _api.DepthMask(ToByte(state.DepthMask));
            if (!CheckGlError(gl, "depth mask"))
            {
                return false;
            }

            Toggle(gl, _context.SCISSOR_TEST, state.EnabledCaps.Contains(_context.SCISSOR_TEST));
            if (!CheckGlError(gl, "scissor toggle"))
            {
                return false;
            }

            if (state.EnabledCaps.Contains(_context.SCISSOR_TEST))
            {
                ApplyScissor(targetSize, state.Scissor);
                if (!CheckGlError(gl, "scissor"))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ApplyPipelineState(GlInterface gl, int defaultFramebuffer, PixelSize pixelSize, WebGlPipelineState state)
        {
            if (!ApplyFramebufferAndMasks(gl, defaultFramebuffer, pixelSize, state))
            {
                return false;
            }

            gl.DepthFunc(state.DepthFunc);
            if (!CheckGlError(gl, "depth func"))
            {
                return false;
            }

            Toggle(gl, _context.DEPTH_TEST, state.EnabledCaps.Contains(_context.DEPTH_TEST));
            if (!CheckGlError(gl, "depth test toggle"))
            {
                return false;
            }

            Toggle(gl, _context.BLEND, state.EnabledCaps.Contains(_context.BLEND));
            if (!CheckGlError(gl, "blend toggle"))
            {
                return false;
            }

            Toggle(gl, _context.CULL_FACE, state.EnabledCaps.Contains(_context.CULL_FACE));
            if (!CheckGlError(gl, "cull face toggle"))
            {
                return false;
            }

            Toggle(gl, _context.POLYGON_OFFSET_FILL, state.EnabledCaps.Contains(_context.POLYGON_OFFSET_FILL));
            if (!CheckGlError(gl, "polygon offset toggle"))
            {
                return false;
            }

            _api.CullFace(state.CullFaceMode);
            if (!CheckGlError(gl, "cull face"))
            {
                return false;
            }

            _api.FrontFace(state.FrontFaceMode);
            if (!CheckGlError(gl, "front face"))
            {
                return false;
            }

            _api.LineWidth((float)state.LineWidth);
            if (!CheckGlError(gl, "line width"))
            {
                return false;
            }

            _api.PolygonOffset((float)state.PolygonOffsetFactor, (float)state.PolygonOffsetUnits);
            if (!CheckGlError(gl, "polygon offset"))
            {
                return false;
            }

            _api.BlendColor(
                (float)state.BlendColor.ElementAtOrDefault(0),
                (float)state.BlendColor.ElementAtOrDefault(1),
                (float)state.BlendColor.ElementAtOrDefault(2),
                (float)state.BlendColor.ElementAtOrDefault(3));
            if (!CheckGlError(gl, "blend color"))
            {
                return false;
            }

            _api.BlendEquationSeparate(state.BlendEquationRgb, state.BlendEquationAlpha);
            if (!CheckGlError(gl, "blend equation"))
            {
                return false;
            }

            _api.BlendFuncSeparate(state.BlendSrcRgb, state.BlendDstRgb, state.BlendSrcAlpha, state.BlendDstAlpha);
            if (!CheckGlError(gl, "blend func"))
            {
                return false;
            }

            return true;
        }

        private bool BindFramebuffer(GlInterface gl, int defaultFramebuffer, WebGlFramebuffer? framebuffer)
        {
            if (framebuffer is null || framebuffer.Deleted)
            {
                _api.BindFramebuffer(_context.FRAMEBUFFER, defaultFramebuffer);
                return true;
            }

            if (!EnsureFramebuffer(gl, framebuffer))
            {
                return false;
            }

            _api.BindFramebuffer(_context.FRAMEBUFFER, framebuffer.NativeId);
            return true;
        }

        private bool EnsureFramebuffer(GlInterface gl, WebGlFramebuffer framebuffer)
        {
            if (!framebuffer.HasAttachment || framebuffer.Width <= 0 || framebuffer.Height <= 0)
            {
                _context.LastDrawStatus = "WebGL framebuffer incomplete: missing attachment";
                return false;
            }

            if (framebuffer.NativeId == 0)
            {
                framebuffer.NativeId = _api.GenFramebuffer();
                if (framebuffer.NativeId == 0)
                {
                    _context.LastDrawStatus = "WebGL framebuffer skipped: OpenGL framebuffer API unavailable";
                    return false;
                }

                _framebuffers.Add(framebuffer.NativeId);
                framebuffer.NativeDirty = true;
            }

            _api.BindFramebuffer(_context.FRAMEBUFFER, framebuffer.NativeId);
            var relinkAttachments = framebuffer.NativeDirty;

            if (framebuffer.ColorTexture is { Deleted: false } colorTexture)
            {
                if (!EnsureTexture(gl, colorTexture))
                {
                    return false;
                }

                if (relinkAttachments)
                {
                    _api.FramebufferTexture2D(
                        _context.FRAMEBUFFER,
                        _context.COLOR_ATTACHMENT0,
                        framebuffer.ColorTextureTarget == 0 ? colorTexture.Target : framebuffer.ColorTextureTarget,
                        colorTexture.NativeId,
                        framebuffer.ColorTextureLevel);
                }
            }

            if (!AttachRenderbuffer(gl, framebuffer.DepthRenderbuffer, _context.DEPTH_ATTACHMENT, relinkAttachments) ||
                !AttachRenderbuffer(gl, framebuffer.StencilRenderbuffer, _context.STENCIL_ATTACHMENT, relinkAttachments) ||
                !AttachRenderbuffer(gl, framebuffer.DepthStencilRenderbuffer, _context.DEPTH_STENCIL_ATTACHMENT, relinkAttachments))
            {
                return false;
            }

            if (!relinkAttachments)
            {
                return true;
            }

            var status = _api.CheckFramebufferStatus(_context.FRAMEBUFFER);
            if (status != _context.FRAMEBUFFER_COMPLETE)
            {
                _context.LastDrawStatus = $"WebGL framebuffer incomplete: 0x{status:X}";
                return false;
            }

            framebuffer.NativeDirty = false;
            return true;
        }

        private bool AttachRenderbuffer(GlInterface gl, WebGlRenderbuffer? renderbuffer, int attachment, bool relink)
        {
            if (renderbuffer is not { Deleted: false })
            {
                return true;
            }

            if (!EnsureRenderbuffer(gl, renderbuffer))
            {
                return false;
            }

            if (relink)
            {
                _api.FramebufferRenderbuffer(_context.FRAMEBUFFER, attachment, _context.RENDERBUFFER, renderbuffer.NativeId);
                var error = gl.GetError();
                if (error != _context.NO_ERROR)
                {
                    _context.LastDrawStatus = $"WebGL renderbuffer attachment 0x{attachment:X} failed with error 0x{error:X}";
                    return false;
                }
            }

            return true;
        }

        private bool EnsureRenderbuffer(GlInterface gl, WebGlRenderbuffer renderbuffer)
        {
            if (renderbuffer.NativeId == 0)
            {
                renderbuffer.NativeId = _api.GenRenderbuffer();
                if (renderbuffer.NativeId == 0)
                {
                    _context.LastDrawStatus = "WebGL renderbuffer skipped: OpenGL renderbuffer API unavailable";
                    return false;
                }

                _renderbuffers.Add(renderbuffer.NativeId);
                renderbuffer.NativeDirty = true;
            }

            _api.BindRenderbuffer(_context.RENDERBUFFER, renderbuffer.NativeId);
            if (renderbuffer.NativeDirty)
            {
                var internalFormat = GetNativeRenderbufferInternalFormat(
                    renderbuffer.InternalFormat == 0 ? _context.DEPTH_COMPONENT16 : renderbuffer.InternalFormat);
                _api.RenderbufferStorage(
                    _context.RENDERBUFFER,
                    internalFormat,
                    renderbuffer.Width,
                    renderbuffer.Height);
                var error = gl.GetError();
                if (error != _context.NO_ERROR)
                {
                    _context.LastDrawStatus = $"WebGL renderbuffer storage failed for internal format 0x{internalFormat:X} with error 0x{error:X}";
                    return false;
                }

                renderbuffer.NativeDirty = false;
            }

            return true;
        }

        private int GetNativeRenderbufferInternalFormat(int internalFormat)
            => internalFormat == _context.DEPTH_STENCIL
                ? 0x88F0 // GL_DEPTH24_STENCIL8; WebGL exposes DEPTH_STENCIL as a renderbuffer storage shortcut.
                : internalFormat;

        private static PixelSize GetTargetPixelSize(PixelSize defaultPixelSize, WebGlFramebuffer? framebuffer)
        {
            if (framebuffer is { Width: > 0, Height: > 0 })
            {
                return new PixelSize(framebuffer.Width, framebuffer.Height);
            }

            return defaultPixelSize;
        }

        private void ApplyViewport(GlInterface gl, PixelSize pixelSize, IReadOnlyList<double> viewport)
        {
            var rect = ScaleRect(pixelSize, viewport);
            gl.Viewport(rect.X, rect.Y, rect.Width, rect.Height);
        }

        private void ApplyScissor(PixelSize pixelSize, IReadOnlyList<double> scissor)
        {
            var rect = ScaleRect(pixelSize, scissor);
            _api.Scissor(rect.X, rect.Y, rect.Width, rect.Height);
        }

        private GlRect ScaleRect(PixelSize pixelSize, IReadOnlyList<double> rect)
        {
            var logicalWidth = Math.Max(1, _context.GetSurfaceWidth());
            var logicalHeight = Math.Max(1, _context.GetSurfaceHeight());
            var scaleX = pixelSize.Width / logicalWidth;
            var scaleY = pixelSize.Height / logicalHeight;
            var width = rect.Count > 2 && rect[2] > 0 ? rect[2] : logicalWidth;
            var height = rect.Count > 3 && rect[3] > 0 ? rect[3] : logicalHeight;
            return new GlRect(
                Math.Max(0, (int)Math.Round(rect.ElementAtOrDefault(0) * scaleX)),
                Math.Max(0, (int)Math.Round(rect.ElementAtOrDefault(1) * scaleY)),
                Math.Max(1, (int)Math.Round(width * scaleX)),
                Math.Max(1, (int)Math.Round(height * scaleY)));
        }

        private void Toggle(GlInterface gl, int cap, bool enabled)
        {
            if (enabled)
            {
                gl.Enable(cap);
            }
            else
            {
                gl.Disable(cap);
            }
        }

        private bool EnsureProgram(GlInterface gl, WebGlProgram program)
        {
            if (program.NativeId != 0 && !program.NativeDirty && program.NativeLinked)
            {
                return true;
            }

            if (program.NativeId != 0)
            {
                gl.DeleteProgram(program.NativeId);
                _programs.Remove(program.NativeId);
                program.NativeId = 0;
            }

            var nativeProgram = gl.CreateProgram();
            _programs.Add(nativeProgram);
            program.NativeId = nativeProgram;

            foreach (var shader in program.Shaders)
            {
                if (!EnsureShader(gl, shader))
                {
                    program.NativeLinked = false;
                    program.NativeInfoLog = shader.NativeInfoLog;
                    _context.LastDrawStatus = shader.NativeInfoLog;
                    return false;
                }

                gl.AttachShader(nativeProgram, shader.NativeId);
            }

            foreach (var attribute in program.AttributeLocations)
            {
                gl.BindAttribLocationString(nativeProgram, attribute.Value, attribute.Key);
            }

            var error = gl.LinkProgramAndGetError(nativeProgram);
            program.NativeInfoLog = error ?? string.Empty;
            program.NativeLinked = error is null;
            program.NativeDirty = false;
            if (error is not null)
            {
                _context.LastDrawStatus = error;
            }

            return program.NativeLinked;
        }

        private bool EnsureShader(GlInterface gl, WebGlShader shader)
        {
            if (shader.NativeId != 0 && !shader.NativeDirty && shader.Compiled)
            {
                return true;
            }

            if (shader.NativeId != 0)
            {
                gl.DeleteShader(shader.NativeId);
                _shaders.Remove(shader.NativeId);
                shader.NativeId = 0;
            }

            var nativeShader = gl.CreateShader(shader.Type);
            _shaders.Add(nativeShader);
            shader.NativeId = nativeShader;
            var source = PrepareShaderSource(shader.Source, shader.Type == _context.FRAGMENT_SHADER, gl.ContextInfo.Version);
            var error = gl.CompileShaderAndGetError(nativeShader, source);
            shader.NativeInfoLog = error ?? string.Empty;
            shader.Compiled = error is null;
            shader.NativeDirty = false;
            return shader.Compiled;
        }

        private static string PrepareShaderSource(string source, bool fragmentShader, GlVersion version)
        {
            source ??= string.Empty;
            source = Regex.Replace(source, @"^\s*#version\s+.+$", string.Empty, RegexOptions.Multiline);
            source = Regex.Replace(source, @"^\s*precision\s+\w+\s+\w+\s*;\s*$", string.Empty, RegexOptions.Multiline);
            source = Regex.Replace(source, @"^\s*#extension\s+GL_OES_standard_derivatives\s*:\s*enable\s*$", string.Empty, RegexOptions.Multiline);

            if (version.Type == GlProfileType.OpenGLES)
            {
                return source;
            }

            if (version.Major < 3)
            {
                return "#version 120\n" + source;
            }

            source = Regex.Replace(source, @"\battribute\b", "in");
            source = Regex.Replace(source, @"\btexture2D\s*\(", "texture(");
            source = Regex.Replace(source, @"\btextureCube\s*\(", "texture(");

            if (fragmentShader)
            {
                source = Regex.Replace(source, @"\bvarying\b", "in");
                return "#version 150\nout vec4 out_FragColor;\n" + RewriteFragmentColorOutput(source);
            }

            source = Regex.Replace(source, @"\bvarying\b", "out");
            return "#version 150\n" + source;
        }

        private static string RewriteFragmentColorOutput(string source)
        {
            if (!Regex.IsMatch(source, @"\bgl_FragColor\b"))
            {
                return source;
            }

            source = Regex.Replace(source, @"\bgl_FragColor\b", "__jsavalonia_FragColor");
            return TryInsertFragmentColorWriteback(source, out var rewritten)
                ? rewritten
                : Regex.Replace(source, @"\b__jsavalonia_FragColor\b", "out_FragColor");
        }

        private static bool TryInsertFragmentColorWriteback(string source, out string rewritten)
        {
            rewritten = source;
            var match = Regex.Match(source, @"\bvoid\s+main\s*\(\s*(?:void)?\s*\)\s*\{");
            if (!match.Success)
            {
                return false;
            }

            var openBrace = match.Index + match.Length - 1;
            var closeBrace = FindMatchingBrace(source, openBrace);
            if (closeBrace < 0)
            {
                return false;
            }

            rewritten =
                source[..(openBrace + 1)] +
                "\n  vec4 __jsavalonia_FragColor = vec4(0.0);\n" +
                source[(openBrace + 1)..closeBrace] +
                "\n  out_FragColor = __jsavalonia_FragColor;\n" +
                source[closeBrace..];
            return true;
        }

        private static int FindMatchingBrace(string source, int openBrace)
        {
            var depth = 0;
            for (var i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private void EnsureBuffer(GlInterface gl, WebGlBuffer buffer, int target)
        {
            if (buffer.NativeId == 0)
            {
                buffer.NativeId = gl.GenBuffer();
                _buffers.Add(buffer.NativeId);
                buffer.NativeDirty = true;
            }

            gl.BindBuffer(target, buffer.NativeId);
            if (!buffer.NativeDirty)
            {
                return;
            }

            var bytes = ToBytes(buffer.Data, buffer.ElementTypeHint, target == _context.ELEMENT_ARRAY_BUFFER);
            if (bytes.Length == 0)
            {
                bytes = new byte[1];
            }

            WithPinned(bytes, ptr => gl.BufferData(target, new IntPtr(bytes.Length), ptr, buffer.Usage == 0 ? _context.STATIC_DRAW : buffer.Usage));
            buffer.NativeDirty = false;
        }

        private void ApplyAttributes(GlInterface gl, WebGlDrawCommand command)
        {
            foreach (var enabled in _enabledAttributes.ToArray())
            {
                if (!command.EnabledAttributes.Contains(enabled))
                {
                    _api.DisableVertexAttribArray(enabled);
                    _enabledAttributes.Remove(enabled);
                }
            }

            foreach (var index in command.EnabledAttributes)
            {
                if (!command.Attributes.TryGetValue(index, out var attribute) || attribute.Buffer is not { Deleted: false } buffer)
                {
                    continue;
                }

                EnsureBuffer(gl, buffer, _context.ARRAY_BUFFER);
                gl.BindBuffer(_context.ARRAY_BUFFER, buffer.NativeId);
                gl.VertexAttribPointer(index, attribute.Size, attribute.Type, attribute.Normalized ? 1 : 0, attribute.Stride, new IntPtr(attribute.Offset));
                gl.EnableVertexAttribArray(index);
                _enabledAttributes.Add(index);
            }
        }

        private bool ApplyTextures(GlInterface gl, IReadOnlyDictionary<int, WebGlTexture?> textures)
        {
            foreach (var entry in textures.OrderBy(static pair => pair.Key))
            {
                if (entry.Value is not { Deleted: false } texture)
                {
                    continue;
                }

                gl.ActiveTexture(_context.TEXTURE0 + entry.Key);
                if (!EnsureTexture(gl, texture))
                {
                    return false;
                }

                gl.BindTexture(texture.Target, texture.NativeId);
            }

            return true;
        }

        private bool EnsureTexture(GlInterface gl, WebGlTexture texture)
        {
            if (texture.NativeId == 0)
            {
                texture.NativeId = gl.GenTexture();
                _textures.Add(texture.NativeId);
                texture.NativeDirty = true;
            }

            gl.BindTexture(texture.Target, texture.NativeId);
            foreach (var parameter in texture.Parameters)
            {
                gl.TexParameteri(texture.Target, parameter.Key, parameter.Value);
                var parameterError = gl.GetError();
                if (parameterError != _context.NO_ERROR)
                {
                    _context.LastDrawStatus = $"WebGL texture parameter 0x{parameter.Key:X}=0x{parameter.Value:X} failed with error 0x{parameterError:X}";
                    return false;
                }
            }

            if (!texture.NativeDirty)
            {
                return true;
            }

            _api.PixelStorei(_context.UNPACK_ALIGNMENT, texture.UnpackAlignment);
            var nativeInternalFormat = GetNativeTextureInternalFormat(texture);
            var nativeFormat = GetNativeTextureFormat(texture.Format);
            var nativeType = GetNativeTextureType(texture.Type);
            var bytes = ToBytes(texture.Pixels, texture.Type, elementBuffer: false);
            if (texture.UnpackFlipY && nativeFormat == _context.RGBA && texture.Type == _context.UNSIGNED_BYTE)
            {
                bytes = FlipRgbaRows(bytes, texture.Width, texture.Height);
            }

            if (bytes.Length == 0)
            {
                gl.TexImage2D(texture.Target, texture.Level, nativeInternalFormat, texture.Width, texture.Height, texture.Border, nativeFormat, nativeType, IntPtr.Zero);
            }
            else
            {
                WithPinned(bytes, ptr => gl.TexImage2D(texture.Target, texture.Level, nativeInternalFormat, texture.Width, texture.Height, texture.Border, nativeFormat, nativeType, ptr));
            }

            var imageError = gl.GetError();
            if (imageError != _context.NO_ERROR)
            {
                _context.LastDrawStatus = $"WebGL texture upload failed target 0x{texture.Target:X}, internal 0x{nativeInternalFormat:X}, size {texture.Width}x{texture.Height}, format 0x{nativeFormat:X}, type 0x{nativeType:X} with error 0x{imageError:X}";
                return false;
            }

            if (texture.GenerateMipmap)
            {
                _api.GenerateMipmap(texture.Target);
                var mipmapError = gl.GetError();
                if (mipmapError != _context.NO_ERROR)
                {
                    _context.LastDrawStatus = $"WebGL texture mipmap failed target 0x{texture.Target:X} with error 0x{mipmapError:X}";
                    return false;
                }
            }

            texture.NativeDirty = false;
            return true;
        }

        private int GetNativeTextureInternalFormat(WebGlTexture texture)
        {
            if (texture.InternalFormat == _context.SRGB_ALPHA_EXT || texture.InternalFormat == _context.SRGB8_ALPHA8_EXT)
            {
                return _context.SRGB8_ALPHA8_EXT;
            }

            if (texture.InternalFormat == _context.SRGB_EXT || texture.InternalFormat == _context.SRGB8_EXT)
            {
                return _context.SRGB8_EXT;
            }

            if (texture.InternalFormat is 0x881A or 0x881B or 0x8814)
            {
                return texture.InternalFormat;
            }

            if (texture.InternalFormat == _context.RGBA && IsHalfFloatTextureType(texture.Type))
            {
                return _context.RGBA16F;
            }

            if (texture.InternalFormat == _context.RGBA && texture.Type == _context.FLOAT)
            {
                return _context.RGBA32F;
            }

            return texture.InternalFormat;
        }

        private int GetNativeTextureFormat(int format)
        {
            if (format == _context.SRGB_ALPHA_EXT || format == _context.SRGB8_ALPHA8_EXT)
            {
                return _context.RGBA;
            }

            if (format == _context.SRGB_EXT || format == _context.SRGB8_EXT)
            {
                return _context.RGB;
            }

            return format;
        }

        private int GetNativeTextureType(int type)
            => type == _context.HALF_FLOAT_OES ? _context.HALF_FLOAT : type;

        private bool IsHalfFloatTextureType(int type)
            => type == _context.HALF_FLOAT || type == _context.HALF_FLOAT_OES;

        private void ApplyUniforms(GlInterface gl, WebGlProgram program, WebGlDrawCommand command)
        {
            foreach (var uniform in command.Uniforms)
            {
                var name = NormalizeUniformName(uniform.Key);
                var location = gl.GetUniformLocationString(program.NativeId, name);
                if (location < 0)
                {
                    location = gl.GetUniformLocationString(program.NativeId, name + "[0]");
                }

                if (location < 0)
                {
                    continue;
                }

                var values = ExtractDoubles(uniform.Value);
                if (values.Length == 0)
                {
                    continue;
                }

                var type = GetUniformType(program, name, values);
                if (type == _context.SAMPLER_2D || type == _context.SAMPLER_CUBE || IsIntegerUniform(type))
                {
                    ApplyIntegerUniform(location, values);
                    continue;
                }

                if (type == _context.FLOAT_MAT4 || values.Length == 16)
                {
                    ApplyMatrixUniform(location, values, 4);
                }
                else if (type == _context.FLOAT_MAT3 || values.Length == 9)
                {
                    ApplyMatrixUniform(location, values, 3);
                }
                else if (type == _context.FLOAT_MAT2 || values.Length == 4 && name.Contains("Matrix", StringComparison.Ordinal))
                {
                    ApplyMatrixUniform(location, values, 2);
                }
                else
                {
                    ApplyFloatUniform(location, values);
                }
            }
        }

        private int GetUniformType(WebGlProgram program, string name, IReadOnlyList<double> values)
        {
            var symbol = program.ActiveUniforms.FirstOrDefault(s => string.Equals(NormalizeUniformName(s.Name), name, StringComparison.Ordinal));
            if (symbol is not null)
            {
                return symbol.Type;
            }

            return values.Count switch
            {
                16 => _context.FLOAT_MAT4,
                9 => _context.FLOAT_MAT3,
                4 => _context.FLOAT_VEC4,
                3 => _context.FLOAT_VEC3,
                2 => _context.FLOAT_VEC2,
                _ => _context.FLOAT
            };
        }

        private bool IsIntegerUniform(int type)
            => type == _context.INT ||
               type == _context.INT_VEC2 ||
               type == _context.INT_VEC3 ||
               type == _context.INT_VEC4 ||
               type == _context.BOOL ||
               type == _context.BOOL_VEC2 ||
               type == _context.BOOL_VEC3 ||
               type == _context.BOOL_VEC4;

        private void ApplyIntegerUniform(int location, IReadOnlyList<double> values)
        {
            var ints = values.Select(static value => (int)value).ToArray();
            switch (ints.Length)
            {
                case 1:
                    _api.Uniform1i(location, ints[0]);
                    break;
                case 2:
                    _api.Uniform2i(location, ints[0], ints[1]);
                    break;
                case 3:
                    _api.Uniform3i(location, ints[0], ints[1], ints[2]);
                    break;
                default:
                    _api.Uniform4i(location, ints.ElementAtOrDefault(0), ints.ElementAtOrDefault(1), ints.ElementAtOrDefault(2), ints.ElementAtOrDefault(3));
                    break;
            }
        }

        private void ApplyFloatUniform(int location, IReadOnlyList<double> values)
        {
            var floats = values.Select(static value => (float)value).ToArray();
            if (floats.Length > 4)
            {
                WithPinnedFloats(floats, ptr => _api.Uniform1fv(location, floats.Length, ptr));
                return;
            }

            switch (floats.Length)
            {
                case 1:
                    _api.Uniform1f(location, floats[0]);
                    break;
                case 2:
                    _api.Uniform2f(location, floats[0], floats[1]);
                    break;
                case 3:
                    _api.Uniform3f(location, floats[0], floats[1], floats[2]);
                    break;
                default:
                    _api.Uniform4f(location, floats.ElementAtOrDefault(0), floats.ElementAtOrDefault(1), floats.ElementAtOrDefault(2), floats.ElementAtOrDefault(3));
                    break;
            }
        }

        private void ApplyMatrixUniform(int location, IReadOnlyList<double> values, int dimension)
        {
            var floats = values.Select(static value => (float)value).ToArray();
            WithPinnedFloats(floats, ptr =>
            {
                if (dimension == 4)
                {
                    _api.UniformMatrix4fv(location, 1, 0, ptr);
                }
                else if (dimension == 3)
                {
                    _api.UniformMatrix3fv(location, 1, 0, ptr);
                }
                else
                {
                    _api.UniformMatrix2fv(location, 1, 0, ptr);
                }
            });
        }

        private static byte[] ToBytes(Array? data, int preferredElementType, bool elementBuffer)
        {
            if (data is null)
            {
                return Array.Empty<byte>();
            }

            return data switch
            {
                byte[] bytes => bytes.ToArray(),
                sbyte[] values => values.Select(static v => unchecked((byte)v)).ToArray(),
                short[] values => ToByteArray(values),
                ushort[] values => ToByteArray(values),
                int[] values => ToByteArray(values),
                uint[] values => ToByteArray(values),
                float[] values when IsHalfFloatElementType(preferredElementType) => ToHalfFloatByteArray(values),
                float[] values => ToByteArray(values),
                double[] values => ConvertDoubles(values, preferredElementType, elementBuffer),
                _ => ConvertEnumerable(data, preferredElementType, elementBuffer)
            };
        }

        private static byte[] ConvertDoubles(IReadOnlyList<double> values, int preferredElementType, bool elementBuffer)
        {
            if (preferredElementType == 0x1401)
            {
                return values.Select(static v => (byte)Math.Clamp((int)Math.Round(v), 0, 255)).ToArray();
            }

            if (elementBuffer || preferredElementType == 0x1403)
            {
                return ToByteArray(values.Select(static v => (ushort)Math.Clamp((int)Math.Round(v), 0, ushort.MaxValue)).ToArray());
            }

            if (preferredElementType == 0x1405)
            {
                return ToByteArray(values.Select(static v => (uint)Math.Max(0, Math.Round(v))).ToArray());
            }

            if (preferredElementType == 0x1404)
            {
                return ToByteArray(values.Select(static v => (int)Math.Round(v)).ToArray());
            }

            if (IsHalfFloatElementType(preferredElementType))
            {
                return ToHalfFloatByteArray(values.Select(static v => (float)v));
            }

            return ToByteArray(values.Select(static v => (float)v).ToArray());
        }

        private static byte[] ConvertEnumerable(IEnumerable values, int preferredElementType, bool elementBuffer)
        {
            var doubles = new List<double>();
            foreach (var value in values)
            {
                if (TryConvertToDouble(value, out var number))
                {
                    doubles.Add(number);
                }
            }

            return ConvertDoubles(doubles, preferredElementType, elementBuffer);
        }

        private static byte[] ToByteArray<T>(T[] values)
            where T : struct
        {
            var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            return bytes;
        }

        private static byte[] ToHalfFloatByteArray(IEnumerable<float> values)
            => ToByteArray(values.Select(static value => BitConverter.HalfToUInt16Bits((Half)value)).ToArray());

        private static bool IsHalfFloatElementType(int elementType)
            => elementType is 0x140B or 0x8D61;

        private static byte[] FlipRgbaRows(byte[] bytes, int width, int height)
        {
            var stride = width * 4;
            if (stride <= 0 || height <= 1 || bytes.Length < stride * height)
            {
                return bytes;
            }

            var flipped = new byte[bytes.Length];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(bytes, y * stride, flipped, (height - y - 1) * stride, stride);
            }

            return flipped;
        }

        private static void WithPinned(byte[] bytes, Action<IntPtr> action)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                action(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static void WithPinnedFloats(float[] values, Action<IntPtr> action)
        {
            var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                action(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static byte ToByte(bool value) => value ? (byte)1 : (byte)0;

        private readonly record struct GlRect(int X, int Y, int Width, int Height);
    }

    private sealed class OpenGlApi
    {
        private static readonly Lazy<IntPtr> s_nativeOpenGl = new(LoadNativeOpenGlLibrary);

        private readonly Uniform1fDelegate? _uniform1f;
        private readonly Uniform2fDelegate? _uniform2f;
        private readonly Uniform3fDelegate? _uniform3f;
        private readonly Uniform4fDelegate? _uniform4f;
        private readonly UniformVectorDelegate? _uniform1fv;
        private readonly Uniform1iDelegate? _uniform1i;
        private readonly Uniform2iDelegate? _uniform2i;
        private readonly Uniform3iDelegate? _uniform3i;
        private readonly Uniform4iDelegate? _uniform4i;
        private readonly UniformMatrixDelegate? _uniformMatrix2fv;
        private readonly UniformMatrixDelegate? _uniformMatrix3fv;
        private readonly UniformMatrixDelegate? _uniformMatrix4fv;
        private readonly DisableVertexAttribArrayDelegate? _disableVertexAttribArray;
        private readonly BlendFuncSeparateDelegate? _blendFuncSeparate;
        private readonly BlendEquationSeparateDelegate? _blendEquationSeparate;
        private readonly BlendColorDelegate? _blendColor;
        private readonly CullFaceDelegate? _cullFace;
        private readonly FrontFaceDelegate? _frontFace;
        private readonly LineWidthDelegate? _lineWidth;
        private readonly PolygonOffsetDelegate? _polygonOffset;
        private readonly ScissorDelegate? _scissor;
        private readonly ColorMaskDelegate? _colorMask;
        private readonly DepthMaskDelegate? _depthMask;
        private readonly PixelStoreiDelegate? _pixelStorei;
        private readonly GenerateMipmapDelegate? _generateMipmap;
        private readonly GenObjectsDelegate? _genFramebuffers;
        private readonly DeleteObjectsDelegate? _deleteFramebuffers;
        private readonly BindObjectDelegate? _bindFramebuffer;
        private readonly CheckFramebufferStatusDelegate? _checkFramebufferStatus;
        private readonly FramebufferTexture2DDelegate? _framebufferTexture2D;
        private readonly GenObjectsDelegate? _genRenderbuffers;
        private readonly DeleteObjectsDelegate? _deleteRenderbuffers;
        private readonly BindObjectDelegate? _bindRenderbuffer;
        private readonly RenderbufferStorageDelegate? _renderbufferStorage;
        private readonly FramebufferRenderbufferDelegate? _framebufferRenderbuffer;

        public OpenGlApi(GlInterface gl)
        {
            Gl = gl;
            _uniform1f = Load<Uniform1fDelegate>(gl, "glUniform1f");
            _uniform2f = Load<Uniform2fDelegate>(gl, "glUniform2f");
            _uniform3f = Load<Uniform3fDelegate>(gl, "glUniform3f");
            _uniform4f = Load<Uniform4fDelegate>(gl, "glUniform4f");
            _uniform1fv = Load<UniformVectorDelegate>(gl, "glUniform1fv");
            _uniform1i = Load<Uniform1iDelegate>(gl, "glUniform1i");
            _uniform2i = Load<Uniform2iDelegate>(gl, "glUniform2i");
            _uniform3i = Load<Uniform3iDelegate>(gl, "glUniform3i");
            _uniform4i = Load<Uniform4iDelegate>(gl, "glUniform4i");
            _uniformMatrix2fv = Load<UniformMatrixDelegate>(gl, "glUniformMatrix2fv");
            _uniformMatrix3fv = Load<UniformMatrixDelegate>(gl, "glUniformMatrix3fv");
            _uniformMatrix4fv = Load<UniformMatrixDelegate>(gl, "glUniformMatrix4fv");
            _disableVertexAttribArray = Load<DisableVertexAttribArrayDelegate>(gl, "glDisableVertexAttribArray");
            _blendFuncSeparate = Load<BlendFuncSeparateDelegate>(gl, "glBlendFuncSeparate");
            _blendEquationSeparate = Load<BlendEquationSeparateDelegate>(gl, "glBlendEquationSeparate");
            _blendColor = Load<BlendColorDelegate>(gl, "glBlendColor");
            _cullFace = Load<CullFaceDelegate>(gl, "glCullFace");
            _frontFace = Load<FrontFaceDelegate>(gl, "glFrontFace");
            _lineWidth = Load<LineWidthDelegate>(gl, "glLineWidth");
            _polygonOffset = Load<PolygonOffsetDelegate>(gl, "glPolygonOffset");
            _scissor = Load<ScissorDelegate>(gl, "glScissor");
            _colorMask = Load<ColorMaskDelegate>(gl, "glColorMask");
            _depthMask = Load<DepthMaskDelegate>(gl, "glDepthMask");
            _pixelStorei = Load<PixelStoreiDelegate>(gl, "glPixelStorei");
            _generateMipmap = Load<GenerateMipmapDelegate>(gl, "glGenerateMipmap");
            _genFramebuffers = Load<GenObjectsDelegate>(gl, "glGenFramebuffers");
            _deleteFramebuffers = Load<DeleteObjectsDelegate>(gl, "glDeleteFramebuffers");
            _bindFramebuffer = Load<BindObjectDelegate>(gl, "glBindFramebuffer");
            _checkFramebufferStatus = Load<CheckFramebufferStatusDelegate>(gl, "glCheckFramebufferStatus");
            _framebufferTexture2D = Load<FramebufferTexture2DDelegate>(gl, "glFramebufferTexture2D");
            _genRenderbuffers = Load<GenObjectsDelegate>(gl, "glGenRenderbuffers");
            _deleteRenderbuffers = Load<DeleteObjectsDelegate>(gl, "glDeleteRenderbuffers");
            _bindRenderbuffer = Load<BindObjectDelegate>(gl, "glBindRenderbuffer");
            _renderbufferStorage = Load<RenderbufferStorageDelegate>(gl, "glRenderbufferStorage");
            _framebufferRenderbuffer = Load<FramebufferRenderbufferDelegate>(gl, "glFramebufferRenderbuffer");
            CapabilitySummary = BuildCapabilitySummary();
        }

        public GlInterface Gl { get; }

        public string CapabilitySummary { get; }

        public void Uniform1f(int location, float x)
        {
            if (_uniform1f is not null)
            {
                _uniform1f(location, x);
            }
            else
            {
                Gl.Uniform1f(location, x);
            }
        }
        public void Uniform2f(int location, float x, float y) => _uniform2f?.Invoke(location, x, y);
        public void Uniform3f(int location, float x, float y, float z) => _uniform3f?.Invoke(location, x, y, z);
        public void Uniform4f(int location, float x, float y, float z, float w) => _uniform4f?.Invoke(location, x, y, z, w);
        public void Uniform1fv(int location, int count, IntPtr value) => _uniform1fv?.Invoke(location, count, value);
        public void Uniform1i(int location, int x)
        {
            _uniform1i?.Invoke(location, x);
        }
        public void Uniform2i(int location, int x, int y) => _uniform2i?.Invoke(location, x, y);
        public void Uniform3i(int location, int x, int y, int z) => _uniform3i?.Invoke(location, x, y, z);
        public void Uniform4i(int location, int x, int y, int z, int w) => _uniform4i?.Invoke(location, x, y, z, w);
        public void UniformMatrix2fv(int location, int count, byte transpose, IntPtr value) => _uniformMatrix2fv?.Invoke(location, count, transpose, value);
        public void UniformMatrix3fv(int location, int count, byte transpose, IntPtr value) => _uniformMatrix3fv?.Invoke(location, count, transpose, value);
        public void UniformMatrix4fv(int location, int count, byte transpose, IntPtr value) => _uniformMatrix4fv?.Invoke(location, count, transpose, value);
        public void DisableVertexAttribArray(int index) => _disableVertexAttribArray?.Invoke(index);
        public void BlendFuncSeparate(int srcRgb, int dstRgb, int srcAlpha, int dstAlpha) => _blendFuncSeparate?.Invoke(srcRgb, dstRgb, srcAlpha, dstAlpha);
        public void BlendEquationSeparate(int modeRgb, int modeAlpha) => _blendEquationSeparate?.Invoke(modeRgb, modeAlpha);
        public void BlendColor(float r, float g, float b, float a) => _blendColor?.Invoke(r, g, b, a);
        public void CullFace(int mode) => _cullFace?.Invoke(mode);
        public void FrontFace(int mode) => _frontFace?.Invoke(mode);
        public void LineWidth(float width) => _lineWidth?.Invoke(width);
        public void PolygonOffset(float factor, float units) => _polygonOffset?.Invoke(factor, units);
        public void Scissor(int x, int y, int width, int height) => _scissor?.Invoke(x, y, width, height);
        public void ColorMask(byte r, byte g, byte b, byte a) => _colorMask?.Invoke(r, g, b, a);
        public void DepthMask(byte flag) => _depthMask?.Invoke(flag);
        public void PixelStorei(int pname, int param) => _pixelStorei?.Invoke(pname, param);
        public void GenerateMipmap(int target) => _generateMipmap?.Invoke(target);
        public int GenFramebuffer()
            => Gl.GenFramebuffer();

        public void DeleteFramebuffer(int framebuffer)
        {
            if (framebuffer == 0)
            {
                return;
            }

            var id = framebuffer;
            Gl.DeleteFramebuffer(id);
        }

        public void BindFramebuffer(int target, int framebuffer)
            => Gl.BindFramebuffer(target, framebuffer);

        public int CheckFramebufferStatus(int target)
            => Gl.CheckFramebufferStatus(target);

        public void FramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
            => Gl.FramebufferTexture2D(target, attachment, textarget, texture, level);

        public int GenRenderbuffer()
            => Gl.GenRenderbuffer();

        public void DeleteRenderbuffer(int renderbuffer)
        {
            if (renderbuffer == 0)
            {
                return;
            }

            var id = renderbuffer;
            Gl.DeleteRenderbuffer(id);
        }

        public void BindRenderbuffer(int target, int renderbuffer)
            => Gl.BindRenderbuffer(target, renderbuffer);

        public void RenderbufferStorage(int target, int internalFormat, int width, int height)
            => Gl.RenderbufferStorage(target, internalFormat, width, height);

        public void FramebufferRenderbuffer(int target, int attachment, int renderbufferTarget, int renderbuffer)
            => Gl.FramebufferRenderbuffer(target, attachment, renderbufferTarget, renderbuffer);

        private string BuildCapabilitySummary()
        {
            var missing = new List<string>();
            AddMissing(missing, _uniform2f, "uniform2f");
            AddMissing(missing, _uniform3f, "uniform3f");
            AddMissing(missing, _uniform4f, "uniform4f");
            AddMissing(missing, _uniform1fv, "uniform1fv");
            AddMissing(missing, _uniform1i, "uniform1i");
            AddMissing(missing, _uniformMatrix2fv, "uniformMatrix2fv");
            AddMissing(missing, _uniformMatrix3fv, "uniformMatrix3fv");
            AddMissing(missing, _uniformMatrix4fv, "uniformMatrix4fv");
            AddMissing(missing, _blendFuncSeparate, "blendFuncSeparate");
            return missing.Count == 0
                ? "native GL entry points available"
                : "missing native GL entry points: " + string.Join(", ", missing);
        }

        private static void AddMissing(List<string> missing, Delegate? entryPoint, string name)
        {
            if (entryPoint is null)
            {
                missing.Add(name);
            }
        }

        private static T? Load<T>(GlInterface gl, string name)
            where T : Delegate
        {
            var address = gl.GetProcAddress(name);
            if (address == IntPtr.Zero)
            {
                address = TryGetNativeOpenGlExport(name);
            }

            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        private static IntPtr TryGetNativeOpenGlExport(string name)
        {
            var library = s_nativeOpenGl.Value;
            return library != IntPtr.Zero && NativeLibrary.TryGetExport(library, name, out var address)
                ? address
                : IntPtr.Zero;
        }

        private static IntPtr LoadNativeOpenGlLibrary()
        {
            var names = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[]
                {
                    "/System/Library/Frameworks/OpenGL.framework/OpenGL",
                    "/System/Library/Frameworks/OpenGL.framework/Versions/Current/OpenGL"
                }
                : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new[] { "opengl32.dll" }
                    : new[] { "libGL.so.1", "libGL.so" };

            foreach (var name in names)
            {
                if (NativeLibrary.TryLoad(name, out var handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform1fDelegate(int location, float x);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform2fDelegate(int location, float x, float y);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform3fDelegate(int location, float x, float y, float z);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform4fDelegate(int location, float x, float y, float z, float w);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void UniformVectorDelegate(int location, int count, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform1iDelegate(int location, int x);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform2iDelegate(int location, int x, int y);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform3iDelegate(int location, int x, int y, int z);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void Uniform4iDelegate(int location, int x, int y, int z, int w);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void UniformMatrixDelegate(int location, int count, byte transpose, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DisableVertexAttribArrayDelegate(int index);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void BlendFuncSeparateDelegate(int srcRgb, int dstRgb, int srcAlpha, int dstAlpha);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void BlendEquationSeparateDelegate(int modeRgb, int modeAlpha);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void BlendColorDelegate(float r, float g, float b, float a);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void CullFaceDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void FrontFaceDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void LineWidthDelegate(float width);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void PolygonOffsetDelegate(float factor, float units);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void ScissorDelegate(int x, int y, int width, int height);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void ColorMaskDelegate(byte r, byte g, byte b, byte a);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DepthMaskDelegate(byte flag);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void PixelStoreiDelegate(int pname, int param);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GenerateMipmapDelegate(int target);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void GenObjectsDelegate(int count, out int id);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DeleteObjectsDelegate(int count, ref int id);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void BindObjectDelegate(int target, int id);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CheckFramebufferStatusDelegate(int target);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void FramebufferTexture2DDelegate(int target, int attachment, int textarget, int texture, int level);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void RenderbufferStorageDelegate(int target, int internalFormat, int width, int height);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void FramebufferRenderbufferDelegate(int target, int attachment, int renderbufferTarget, int renderbuffer);
    }
}
