using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.OpenGL;
using Jint.Native;

namespace JavaScript.Avalonia;

internal sealed partial class CanvasWebGlRenderingContext
{
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
            commands = _commands.ToArray();
        }

        _openGlRenderer.Render(gl, framebuffer, pixelSize, commands);
    }

    private bool ShouldUseSoftwareFallback => _openGlSurface is null || !_openGlSurface.IsOpenGlAvailable;

    private void RequestNativeRender()
    {
        _openGlSurface?.RequestRender();
    }

    private void QueueClear(int mask)
    {
        lock (_commandLock)
        {
            if ((mask & COLOR_BUFFER_BIT) != 0)
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
            _lineWidth);

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
                foreach (var attribute in command.Attributes.Values)
                {
                    ResetBuffer(attribute.Buffer);
                }

                foreach (var texture in command.Textures.Values)
                {
                    ResetTexture(texture);
                }
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
        double LineWidth);

    private sealed class OpenGlRenderer
    {
        private readonly CanvasWebGlRenderingContext _context;
        private readonly HashSet<int> _buffers = new();
        private readonly HashSet<int> _shaders = new();
        private readonly HashSet<int> _programs = new();
        private readonly HashSet<int> _textures = new();
        private readonly HashSet<int> _enabledAttributes = new();
        private OpenGlApi _api;

        public OpenGlRenderer(CanvasWebGlRenderingContext context, GlInterface gl)
        {
            _context = context;
            _api = new OpenGlApi(gl);
        }

        public void Render(GlInterface gl, int framebuffer, PixelSize pixelSize, IReadOnlyList<WebGlCommand> commands)
        {
            if (!ReferenceEquals(_api.Gl, gl))
            {
                _api = new OpenGlApi(gl);
            }

            gl.BindFramebuffer(_context.FRAMEBUFFER, framebuffer);
            gl.Viewport(0, 0, pixelSize.Width, pixelSize.Height);

            var drawCalls = 0;
            foreach (var command in commands)
            {
                switch (command)
                {
                    case WebGlClearCommand clear:
                        RenderClear(gl, pixelSize, clear);
                        break;
                    case WebGlDrawCommand draw:
                        if (RenderDraw(gl, pixelSize, draw))
                        {
                            drawCalls++;
                        }

                        break;
                }
            }

            gl.Flush();
            _context.LastDrawStatus = drawCalls > 0
                ? $"Rendered {drawCalls} WebGL draw call(s) through Avalonia OpenGL"
                : "Rendered WebGL frame through Avalonia OpenGL";
        }

        public void Dispose(GlInterface gl)
        {
            foreach (var buffer in _buffers)
            {
                gl.DeleteBuffer(buffer);
            }

            foreach (var texture in _textures)
            {
                gl.DeleteTexture(texture);
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
            _programs.Clear();
            _shaders.Clear();
            _enabledAttributes.Clear();
        }

        private void RenderClear(GlInterface gl, PixelSize pixelSize, WebGlClearCommand command)
        {
            var state = command.State;
            ApplyFramebufferAndMasks(gl, pixelSize, state);
            gl.ClearColor(
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(0), 0, 1),
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(1), 0, 1),
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(2), 0, 1),
                (float)Math.Clamp(state.ClearColor.ElementAtOrDefault(3), 0, 1));
            gl.ClearDepth((float)Math.Clamp(state.ClearDepth, 0, 1));
            gl.ClearStencil(state.ClearStencil);
            gl.Clear(command.Mask);
        }

        private bool RenderDraw(GlInterface gl, PixelSize pixelSize, WebGlDrawCommand command)
        {
            if (command.Program is not { Deleted: false } program || !EnsureProgram(gl, program))
            {
                return false;
            }

            ApplyViewport(gl, pixelSize, command.State.Viewport);
            ApplyPipelineState(gl, pixelSize, command.State);
            ApplyTextures(gl, command.Textures);
            gl.UseProgram(program.NativeId);
            ApplyUniforms(gl, program, command);
            ApplyAttributes(gl, command);

            if (command.Indexed)
            {
                if (command.ElementArrayBuffer is not { Deleted: false } elementBuffer)
                {
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

            return true;
        }

        private void ApplyFramebufferAndMasks(GlInterface gl, PixelSize pixelSize, WebGlPipelineState state)
        {
            gl.Viewport(0, 0, pixelSize.Width, pixelSize.Height);
            _api.ColorMask(
                ToByte(state.ColorMask.ElementAtOrDefault(0)),
                ToByte(state.ColorMask.ElementAtOrDefault(1)),
                ToByte(state.ColorMask.ElementAtOrDefault(2)),
                ToByte(state.ColorMask.ElementAtOrDefault(3)));
            _api.DepthMask(ToByte(state.DepthMask));
            Toggle(gl, _context.SCISSOR_TEST, state.EnabledCaps.Contains(_context.SCISSOR_TEST));
            if (state.EnabledCaps.Contains(_context.SCISSOR_TEST))
            {
                ApplyScissor(pixelSize, state.Scissor);
            }
        }

        private void ApplyPipelineState(GlInterface gl, PixelSize pixelSize, WebGlPipelineState state)
        {
            ApplyFramebufferAndMasks(gl, pixelSize, state);
            gl.DepthFunc(state.DepthFunc);
            Toggle(gl, _context.DEPTH_TEST, state.EnabledCaps.Contains(_context.DEPTH_TEST));
            Toggle(gl, _context.BLEND, state.EnabledCaps.Contains(_context.BLEND));
            Toggle(gl, _context.CULL_FACE, state.EnabledCaps.Contains(_context.CULL_FACE));
            Toggle(gl, _context.POLYGON_OFFSET_FILL, state.EnabledCaps.Contains(_context.POLYGON_OFFSET_FILL));

            _api.CullFace(state.CullFaceMode);
            _api.FrontFace(state.FrontFaceMode);
            _api.LineWidth((float)state.LineWidth);
            _api.PolygonOffset((float)state.PolygonOffsetFactor, (float)state.PolygonOffsetUnits);
            _api.BlendColor(
                (float)state.BlendColor.ElementAtOrDefault(0),
                (float)state.BlendColor.ElementAtOrDefault(1),
                (float)state.BlendColor.ElementAtOrDefault(2),
                (float)state.BlendColor.ElementAtOrDefault(3));
            _api.BlendEquationSeparate(state.BlendEquationRgb, state.BlendEquationAlpha);
            _api.BlendFuncSeparate(state.BlendSrcRgb, state.BlendDstRgb, state.BlendSrcAlpha, state.BlendDstAlpha);
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
                source = Regex.Replace(source, @"\bgl_FragColor\b", "out_FragColor");
                return "#version 150\nout vec4 out_FragColor;\n" + source;
            }

            source = Regex.Replace(source, @"\bvarying\b", "out");
            return "#version 150\n" + source;
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

        private void ApplyTextures(GlInterface gl, IReadOnlyDictionary<int, WebGlTexture?> textures)
        {
            foreach (var entry in textures.OrderBy(static pair => pair.Key))
            {
                if (entry.Value is not { Deleted: false } texture)
                {
                    continue;
                }

                gl.ActiveTexture(_context.TEXTURE0 + entry.Key);
                EnsureTexture(gl, texture);
                gl.BindTexture(texture.Target, texture.NativeId);
            }
        }

        private void EnsureTexture(GlInterface gl, WebGlTexture texture)
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
            }

            if (!texture.NativeDirty)
            {
                return;
            }

            _api.PixelStorei(_context.UNPACK_ALIGNMENT, texture.UnpackAlignment);
            var bytes = ToBytes(texture.Pixels, texture.Type, elementBuffer: false);
            if (texture.UnpackFlipY && texture.Format == _context.RGBA && texture.Type == _context.UNSIGNED_BYTE)
            {
                bytes = FlipRgbaRows(bytes, texture.Width, texture.Height);
            }

            if (bytes.Length == 0)
            {
                gl.TexImage2D(texture.Target, texture.Level, texture.InternalFormat, texture.Width, texture.Height, texture.Border, texture.Format, texture.Type, IntPtr.Zero);
            }
            else
            {
                WithPinned(bytes, ptr => gl.TexImage2D(texture.Target, texture.Level, texture.InternalFormat, texture.Width, texture.Height, texture.Border, texture.Format, texture.Type, ptr));
            }

            if (texture.GenerateMipmap)
            {
                _api.GenerateMipmap(texture.Target);
            }

            texture.NativeDirty = false;
        }

        private void ApplyUniforms(GlInterface gl, WebGlProgram program, WebGlDrawCommand command)
        {
            foreach (var uniform in program.UniformValues)
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
        private readonly Uniform1fDelegate? _uniform1f;
        private readonly Uniform2fDelegate? _uniform2f;
        private readonly Uniform3fDelegate? _uniform3f;
        private readonly Uniform4fDelegate? _uniform4f;
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

        public OpenGlApi(GlInterface gl)
        {
            Gl = gl;
            _uniform1f = Load<Uniform1fDelegate>(gl, "glUniform1f");
            _uniform2f = Load<Uniform2fDelegate>(gl, "glUniform2f");
            _uniform3f = Load<Uniform3fDelegate>(gl, "glUniform3f");
            _uniform4f = Load<Uniform4fDelegate>(gl, "glUniform4f");
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
        }

        public GlInterface Gl { get; }

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

        private static T? Load<T>(GlInterface gl, string name)
            where T : Delegate
        {
            var address = gl.GetProcAddress(name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
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
    }
}
