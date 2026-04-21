using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Jint.Native;

namespace JavaScript.Avalonia;

internal sealed class CanvasWebGlRenderingContext
{
    private readonly CanvasDrawingSurface _surface;
    private readonly CanvasRenderingContext2D _canvas2d;
    private readonly Dictionary<int, WebGlVertexAttribState> _attributes = new();
    private readonly HashSet<int> _enabledAttributes = new();
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);
    private readonly double[] _clearColor = { 0, 0, 0, 0 };
    private readonly double[] _viewport = { 0, 0, 300, 150 };
    private WebGlBuffer? _arrayBuffer;
    private WebGlBuffer? _elementArrayBuffer;
    private WebGlProgram? _currentProgram;
    private WebGlVertexArrayObject? _currentVertexArray;

    public CanvasWebGlRenderingContext(CanvasDrawingSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _canvas2d = _surface.Context;
        InitializeParameters();
    }

    public object? canvas { get; set; }

    public int CommandCount => _canvas2d.CommandCount;
    public int DrawCallCount { get; private set; }
    public int TriangleCount { get; private set; }
    public string LastDrawStatus { get; private set; } = "No draw call";

    public int DEPTH_BUFFER_BIT => 0x00000100;
    public int STENCIL_BUFFER_BIT => 0x00000400;
    public int COLOR_BUFFER_BIT => 0x00004000;
    public int POINTS => 0x0000;
    public int LINES => 0x0001;
    public int LINE_LOOP => 0x0002;
    public int LINE_STRIP => 0x0003;
    public int TRIANGLES => 0x0004;
    public int TRIANGLE_STRIP => 0x0005;
    public int TRIANGLE_FAN => 0x0006;
    public int ZERO => 0;
    public int ONE => 1;
    public int SRC_COLOR => 0x0300;
    public int ONE_MINUS_SRC_COLOR => 0x0301;
    public int SRC_ALPHA => 0x0302;
    public int ONE_MINUS_SRC_ALPHA => 0x0303;
    public int DST_ALPHA => 0x0304;
    public int ONE_MINUS_DST_ALPHA => 0x0305;
    public int DST_COLOR => 0x0306;
    public int ONE_MINUS_DST_COLOR => 0x0307;
    public int SRC_ALPHA_SATURATE => 0x0308;
    public int FUNC_ADD => 0x8006;
    public int BLEND_EQUATION => 0x8009;
    public int BLEND_EQUATION_RGB => 0x8009;
    public int BLEND_EQUATION_ALPHA => 0x883D;
    public int FUNC_SUBTRACT => 0x800A;
    public int FUNC_REVERSE_SUBTRACT => 0x800B;
    public int BLEND_DST_RGB => 0x80C8;
    public int BLEND_SRC_RGB => 0x80C9;
    public int BLEND_DST_ALPHA => 0x80CA;
    public int BLEND_SRC_ALPHA => 0x80CB;
    public int CONSTANT_COLOR => 0x8001;
    public int ONE_MINUS_CONSTANT_COLOR => 0x8002;
    public int CONSTANT_ALPHA => 0x8003;
    public int ONE_MINUS_CONSTANT_ALPHA => 0x8004;
    public int BLEND_COLOR => 0x8005;
    public int ARRAY_BUFFER => 0x8892;
    public int ELEMENT_ARRAY_BUFFER => 0x8893;
    public int ARRAY_BUFFER_BINDING => 0x8894;
    public int ELEMENT_ARRAY_BUFFER_BINDING => 0x8895;
    public int STREAM_DRAW => 0x88E0;
    public int STATIC_DRAW => 0x88E4;
    public int DYNAMIC_DRAW => 0x88E8;
    public int BUFFER_SIZE => 0x8764;
    public int BUFFER_USAGE => 0x8765;
    public int CURRENT_VERTEX_ATTRIB => 0x8626;
    public int FRONT => 0x0404;
    public int BACK => 0x0405;
    public int FRONT_AND_BACK => 0x0408;
    public int CULL_FACE => 0x0B44;
    public int BLEND => 0x0BE2;
    public int DITHER => 0x0BD0;
    public int STENCIL_TEST => 0x0B90;
    public int DEPTH_TEST => 0x0B71;
    public int SCISSOR_TEST => 0x0C11;
    public int POLYGON_OFFSET_FILL => 0x8037;
    public int SAMPLE_ALPHA_TO_COVERAGE => 0x809E;
    public int SAMPLE_COVERAGE => 0x80A0;
    public int NO_ERROR => 0;
    public int INVALID_ENUM => 0x0500;
    public int INVALID_VALUE => 0x0501;
    public int INVALID_OPERATION => 0x0502;
    public int OUT_OF_MEMORY => 0x0505;
    public int CW => 0x0900;
    public int CCW => 0x0901;
    public int LINE_WIDTH => 0x0B21;
    public int ALIASED_POINT_SIZE_RANGE => 0x846D;
    public int ALIASED_LINE_WIDTH_RANGE => 0x846E;
    public int CULL_FACE_MODE => 0x0B45;
    public int FRONT_FACE => 0x0B46;
    public int DEPTH_RANGE => 0x0B70;
    public int DEPTH_WRITEMASK => 0x0B72;
    public int DEPTH_CLEAR_VALUE => 0x0B73;
    public int DEPTH_FUNC => 0x0B74;
    public int STENCIL_CLEAR_VALUE => 0x0B91;
    public int STENCIL_FUNC => 0x0B92;
    public int STENCIL_FAIL => 0x0B94;
    public int STENCIL_PASS_DEPTH_FAIL => 0x0B95;
    public int STENCIL_PASS_DEPTH_PASS => 0x0B96;
    public int STENCIL_REF => 0x0B97;
    public int STENCIL_VALUE_MASK => 0x0B93;
    public int STENCIL_WRITEMASK => 0x0B98;
    public int STENCIL_BACK_FUNC => 0x8800;
    public int STENCIL_BACK_FAIL => 0x8801;
    public int STENCIL_BACK_PASS_DEPTH_FAIL => 0x8802;
    public int STENCIL_BACK_PASS_DEPTH_PASS => 0x8803;
    public int STENCIL_BACK_REF => 0x8CA3;
    public int STENCIL_BACK_VALUE_MASK => 0x8CA4;
    public int STENCIL_BACK_WRITEMASK => 0x8CA5;
    public int VIEWPORT => 0x0BA2;
    public int SCISSOR_BOX => 0x0C10;
    public int COLOR_CLEAR_VALUE => 0x0C22;
    public int COLOR_WRITEMASK => 0x0C23;
    public int UNPACK_ALIGNMENT => 0x0CF5;
    public int PACK_ALIGNMENT => 0x0D05;
    public int MAX_TEXTURE_SIZE => 0x0D33;
    public int MAX_VIEWPORT_DIMS => 0x0D3A;
    public int SUBPIXEL_BITS => 0x0D50;
    public int RED_BITS => 0x0D52;
    public int GREEN_BITS => 0x0D53;
    public int BLUE_BITS => 0x0D54;
    public int ALPHA_BITS => 0x0D55;
    public int DEPTH_BITS => 0x0D56;
    public int STENCIL_BITS => 0x0D57;
    public int POLYGON_OFFSET_UNITS => 0x2A00;
    public int POLYGON_OFFSET_FACTOR => 0x8038;
    public int TEXTURE_BINDING_2D => 0x8069;
    public int SAMPLE_BUFFERS => 0x80A8;
    public int SAMPLES => 0x80A9;
    public int SAMPLE_COVERAGE_VALUE => 0x80AA;
    public int SAMPLE_COVERAGE_INVERT => 0x80AB;
    public int COMPRESSED_TEXTURE_FORMATS => 0x86A3;
    public int DONT_CARE => 0x1100;
    public int FASTEST => 0x1101;
    public int NICEST => 0x1102;
    public int GENERATE_MIPMAP_HINT => 0x8192;
    public int BYTE => 0x1400;
    public int UNSIGNED_BYTE => 0x1401;
    public int SHORT => 0x1402;
    public int UNSIGNED_SHORT => 0x1403;
    public int INT => 0x1404;
    public int UNSIGNED_INT => 0x1405;
    public int FLOAT => 0x1406;
    public int DEPTH_COMPONENT => 0x1902;
    public int ALPHA => 0x1906;
    public int RGB => 0x1907;
    public int RGBA => 0x1908;
    public int LUMINANCE => 0x1909;
    public int LUMINANCE_ALPHA => 0x190A;
    public int UNSIGNED_SHORT_4_4_4_4 => 0x8033;
    public int UNSIGNED_SHORT_5_5_5_1 => 0x8034;
    public int UNSIGNED_SHORT_5_6_5 => 0x8363;
    public int FRAGMENT_SHADER => 0x8B30;
    public int VERTEX_SHADER => 0x8B31;
    public int MAX_VERTEX_ATTRIBS => 0x8869;
    public int MAX_VERTEX_UNIFORM_VECTORS => 0x8DFB;
    public int MAX_VARYING_VECTORS => 0x8DFC;
    public int MAX_COMBINED_TEXTURE_IMAGE_UNITS => 0x8B4D;
    public int MAX_VERTEX_TEXTURE_IMAGE_UNITS => 0x8B4C;
    public int MAX_TEXTURE_IMAGE_UNITS => 0x8872;
    public int MAX_FRAGMENT_UNIFORM_VECTORS => 0x8DFD;
    public int SHADER_TYPE => 0x8B4F;
    public int DELETE_STATUS => 0x8B80;
    public int LINK_STATUS => 0x8B82;
    public int VALIDATE_STATUS => 0x8B83;
    public int ATTACHED_SHADERS => 0x8B85;
    public int ACTIVE_UNIFORMS => 0x8B86;
    public int ACTIVE_ATTRIBUTES => 0x8B89;
    public int SHADING_LANGUAGE_VERSION => 0x8B8C;
    public int CURRENT_PROGRAM => 0x8B8D;
    public int NEVER => 0x0200;
    public int LESS => 0x0201;
    public int EQUAL => 0x0202;
    public int LEQUAL => 0x0203;
    public int GREATER => 0x0204;
    public int NOTEQUAL => 0x0205;
    public int GEQUAL => 0x0206;
    public int ALWAYS => 0x0207;
    public int KEEP => 0x1E00;
    public int REPLACE => 0x1E01;
    public int INCR => 0x1E02;
    public int DECR => 0x1E03;
    public int INVERT => 0x150A;
    public int INCR_WRAP => 0x8507;
    public int DECR_WRAP => 0x8508;
    public int VENDOR => 0x1F00;
    public int RENDERER => 0x1F01;
    public int VERSION => 0x1F02;
    public int NEAREST => 0x2600;
    public int LINEAR => 0x2601;
    public int NEAREST_MIPMAP_NEAREST => 0x2700;
    public int LINEAR_MIPMAP_NEAREST => 0x2701;
    public int NEAREST_MIPMAP_LINEAR => 0x2702;
    public int LINEAR_MIPMAP_LINEAR => 0x2703;
    public int TEXTURE_MAG_FILTER => 0x2800;
    public int TEXTURE_MIN_FILTER => 0x2801;
    public int TEXTURE_WRAP_S => 0x2802;
    public int TEXTURE_WRAP_T => 0x2803;
    public int TEXTURE_2D => 0x0DE1;
    public int TEXTURE => 0x1702;
    public int TEXTURE_CUBE_MAP => 0x8513;
    public int TEXTURE_BINDING_CUBE_MAP => 0x8514;
    public int TEXTURE_CUBE_MAP_POSITIVE_X => 0x8515;
    public int TEXTURE_CUBE_MAP_NEGATIVE_X => 0x8516;
    public int TEXTURE_CUBE_MAP_POSITIVE_Y => 0x8517;
    public int TEXTURE_CUBE_MAP_NEGATIVE_Y => 0x8518;
    public int TEXTURE_CUBE_MAP_POSITIVE_Z => 0x8519;
    public int TEXTURE_CUBE_MAP_NEGATIVE_Z => 0x851A;
    public int MAX_CUBE_MAP_TEXTURE_SIZE => 0x851C;
    public int TEXTURE0 => 0x84C0;
    public int TEXTURE1 => 0x84C1;
    public int TEXTURE31 => 0x84DF;
    public int ACTIVE_TEXTURE => 0x84E0;
    public int REPEAT => 0x2901;
    public int CLAMP_TO_EDGE => 0x812F;
    public int MIRRORED_REPEAT => 0x8370;
    public int FLOAT_VEC2 => 0x8B50;
    public int FLOAT_VEC3 => 0x8B51;
    public int FLOAT_VEC4 => 0x8B52;
    public int INT_VEC2 => 0x8B53;
    public int INT_VEC3 => 0x8B54;
    public int INT_VEC4 => 0x8B55;
    public int BOOL => 0x8B56;
    public int BOOL_VEC2 => 0x8B57;
    public int BOOL_VEC3 => 0x8B58;
    public int BOOL_VEC4 => 0x8B59;
    public int FLOAT_MAT2 => 0x8B5A;
    public int FLOAT_MAT3 => 0x8B5B;
    public int FLOAT_MAT4 => 0x8B5C;
    public int SAMPLER_2D => 0x8B5E;
    public int SAMPLER_CUBE => 0x8B60;
    public int VERTEX_ATTRIB_ARRAY_ENABLED => 0x8622;
    public int VERTEX_ATTRIB_ARRAY_SIZE => 0x8623;
    public int VERTEX_ATTRIB_ARRAY_STRIDE => 0x8624;
    public int VERTEX_ATTRIB_ARRAY_TYPE => 0x8625;
    public int VERTEX_ATTRIB_ARRAY_NORMALIZED => 0x886A;
    public int VERTEX_ATTRIB_ARRAY_POINTER => 0x8645;
    public int VERTEX_ATTRIB_ARRAY_BUFFER_BINDING => 0x889F;
    public int COMPILE_STATUS => 0x8B81;
    public int LOW_FLOAT => 0x8DF0;
    public int MEDIUM_FLOAT => 0x8DF1;
    public int HIGH_FLOAT => 0x8DF2;
    public int LOW_INT => 0x8DF3;
    public int MEDIUM_INT => 0x8DF4;
    public int HIGH_INT => 0x8DF5;
    public int FRAMEBUFFER => 0x8D40;
    public int RENDERBUFFER => 0x8D41;
    public int RGBA4 => 0x8056;
    public int RGB5_A1 => 0x8057;
    public int RGB565 => 0x8D62;
    public int DEPTH_COMPONENT16 => 0x81A5;
    public int STENCIL_INDEX8 => 0x8D48;
    public int DEPTH_STENCIL => 0x84F9;
    public int RENDERBUFFER_WIDTH => 0x8D42;
    public int RENDERBUFFER_HEIGHT => 0x8D43;
    public int RENDERBUFFER_INTERNAL_FORMAT => 0x8D44;
    public int RENDERBUFFER_RED_SIZE => 0x8D50;
    public int RENDERBUFFER_GREEN_SIZE => 0x8D51;
    public int RENDERBUFFER_BLUE_SIZE => 0x8D52;
    public int RENDERBUFFER_ALPHA_SIZE => 0x8D53;
    public int RENDERBUFFER_DEPTH_SIZE => 0x8D54;
    public int RENDERBUFFER_STENCIL_SIZE => 0x8D55;
    public int FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE => 0x8CD0;
    public int FRAMEBUFFER_ATTACHMENT_OBJECT_NAME => 0x8CD1;
    public int FRAMEBUFFER_ATTACHMENT_TEXTURE_LEVEL => 0x8CD2;
    public int FRAMEBUFFER_ATTACHMENT_TEXTURE_CUBE_MAP_FACE => 0x8CD3;
    public int COLOR_ATTACHMENT0 => 0x8CE0;
    public int DEPTH_ATTACHMENT => 0x8D00;
    public int STENCIL_ATTACHMENT => 0x8D20;
    public int DEPTH_STENCIL_ATTACHMENT => 0x821A;
    public int NONE => 0;
    public int FRAMEBUFFER_COMPLETE => 0x8CD5;
    public int FRAMEBUFFER_INCOMPLETE_ATTACHMENT => 0x8CD6;
    public int FRAMEBUFFER_INCOMPLETE_MISSING_ATTACHMENT => 0x8CD7;
    public int FRAMEBUFFER_INCOMPLETE_DIMENSIONS => 0x8CD9;
    public int FRAMEBUFFER_UNSUPPORTED => 0x8CDD;
    public int FRAMEBUFFER_BINDING => 0x8CA6;
    public int RENDERBUFFER_BINDING => 0x8CA7;
    public int MAX_RENDERBUFFER_SIZE => 0x84E8;
    public int INVALID_FRAMEBUFFER_OPERATION => 0x0506;
    public int UNPACK_FLIP_Y_WEBGL => 0x9240;
    public int UNPACK_PREMULTIPLY_ALPHA_WEBGL => 0x9241;
    public int CONTEXT_LOST_WEBGL => 0x9242;
    public int UNPACK_COLORSPACE_CONVERSION_WEBGL => 0x9243;
    public int BROWSER_DEFAULT_WEBGL => 0x9244;

    public object? getContextAttributes()
        => new
        {
            alpha = true,
            depth = true,
            stencil = false,
            antialias = false,
            premultipliedAlpha = true,
            preserveDrawingBuffer = true,
            powerPreference = "default"
        };

    public object? getExtension(string? name)
    {
        return name switch
        {
            "EXT_blend_minmax" => new { MIN_EXT = 0x8007, MAX_EXT = 0x8008 },
            "OES_element_index_uint" => new { },
            "OES_standard_derivatives" => new { FRAGMENT_SHADER_DERIVATIVE_HINT_OES = 0x8B8B },
            "EXT_frag_depth" => new { },
            "EXT_shader_texture_lod" => new { },
            "WEBGL_depth_texture" or "MOZ_WEBGL_depth_texture" or "WEBKIT_WEBGL_depth_texture" => new { UNSIGNED_INT_24_8_WEBGL = 0x84FA },
            "WEBGL_debug_renderer_info" => new { UNMASKED_VENDOR_WEBGL = 0x9245, UNMASKED_RENDERER_WEBGL = 0x9246 },
            "OES_vertex_array_object" => new WebGlVertexArrayExtension(this),
            _ => null
        };
    }

    public string[] getSupportedExtensions()
        => new[]
        {
            "EXT_blend_minmax",
            "EXT_frag_depth",
            "EXT_shader_texture_lod",
            "OES_element_index_uint",
            "OES_standard_derivatives",
            "OES_vertex_array_object",
            "WEBGL_debug_renderer_info",
            "WEBGL_depth_texture"
        };

    public object? getParameter(int pname)
    {
        if (pname == VERSION)
        {
            return "WebGL 1.0 (Avalonia software bridge)";
        }

        if (pname == SHADING_LANGUAGE_VERSION)
        {
            return "WebGL GLSL ES 1.0 (Avalonia software bridge)";
        }

        if (pname == VENDOR)
        {
            return "Avalonia";
        }

        if (pname == RENDERER)
        {
            return "JavaScript.Avalonia Canvas WebGL";
        }

        if (pname == VIEWPORT)
        {
            return (double[])_viewport.Clone();
        }

        if (pname == SCISSOR_BOX)
        {
            return (double[])_viewport.Clone();
        }

        return _parameters.TryGetValue(pname.ToString(CultureInfo.InvariantCulture), out var value) ? value : 0;
    }

    public WebGlShaderPrecisionFormat getShaderPrecisionFormat(int shaderType, int precisionType)
        => new(127, 127, precisionType is 0x8DF0 or 0x8DF3 ? 8 : precisionType is 0x8DF1 or 0x8DF4 ? 16 : 23);

    public int getError() => NO_ERROR;

    public bool isContextLost() => false;

    public WebGlBuffer createBuffer() => new();

    public void deleteBuffer(WebGlBuffer? buffer)
    {
        if (ReferenceEquals(_arrayBuffer, buffer))
        {
            _arrayBuffer = null;
        }

        if (ReferenceEquals(_elementArrayBuffer, buffer))
        {
            _elementArrayBuffer = null;
        }
    }

    public void bindBuffer(int target, WebGlBuffer? buffer)
    {
        if (target == ARRAY_BUFFER)
        {
            _arrayBuffer = buffer;
        }
        else if (target == ELEMENT_ARRAY_BUFFER)
        {
            _elementArrayBuffer = buffer;
            if (_currentVertexArray is not null)
            {
                _currentVertexArray.ElementArrayBuffer = buffer;
            }
        }
    }

    public void bufferData(int target, object? data, int usage)
    {
        var buffer = target == ARRAY_BUFFER ? _arrayBuffer : target == ELEMENT_ARRAY_BUFFER ? _elementArrayBuffer : null;
        if (buffer is null)
        {
            return;
        }

        buffer.Target = target;
        buffer.Usage = usage;
        buffer.Data = ExtractArray(data);
    }

    public void bufferSubData(int target, int offset, object? data)
    {
        var buffer = target == ARRAY_BUFFER ? _arrayBuffer : target == ELEMENT_ARRAY_BUFFER ? _elementArrayBuffer : null;
        if (buffer is null)
        {
            return;
        }

        var replacement = ExtractArray(data);
        if (offset <= 0 || buffer.Data is null)
        {
            buffer.Data = replacement;
            return;
        }

        buffer.Data = replacement;
    }

    public WebGlShader createShader(int type) => new(type);

    public void shaderSource(WebGlShader? shader, string? source)
    {
        if (shader is not null)
        {
            shader.Source = source ?? string.Empty;
        }
    }

    public void compileShader(WebGlShader? shader)
    {
        if (shader is not null)
        {
            shader.Compiled = true;
        }
    }

    public bool getShaderParameter(WebGlShader? shader, int pname)
        => pname == COMPILE_STATUS ? shader?.Compiled != false : true;

    public string getShaderInfoLog(WebGlShader? shader) => string.Empty;

    public void deleteShader(WebGlShader? shader)
    {
    }

    public WebGlProgram createProgram() => new();

    public void attachShader(WebGlProgram? program, WebGlShader? shader)
    {
        if (program is not null && shader is not null && !program.Shaders.Contains(shader))
        {
            program.Shaders.Add(shader);
        }
    }

    public void bindAttribLocation(WebGlProgram? program, int index, string? name)
    {
        if (program is not null && !string.IsNullOrWhiteSpace(name))
        {
            program.AttributeLocations[name!] = index;
        }
    }

    public void linkProgram(WebGlProgram? program)
    {
        if (program is null)
        {
            return;
        }

        program.Linked = true;
        program.ActiveUniforms.Clear();
        program.ActiveAttributes.Clear();

        foreach (var shader in program.Shaders)
        {
            foreach (var uniform in ParseUniforms(shader.Source))
            {
                program.ActiveUniforms.Add(uniform);
            }

            foreach (var attribute in ParseAttributes(shader.Source))
            {
                program.ActiveAttributes.Add(attribute);
            }
        }

        foreach (var attribute in program.ActiveAttributes)
        {
            _ = GetOrCreateAttribLocation(program, attribute.Name);
        }
    }

    public object getProgramParameter(WebGlProgram? program, int pname)
    {
        if (program is null)
        {
            return false;
        }

        if (pname == LINK_STATUS || pname == VALIDATE_STATUS)
        {
            return program.Linked;
        }

        if (pname == ACTIVE_UNIFORMS)
        {
            return program.ActiveUniforms.Count;
        }

        if (pname == ACTIVE_ATTRIBUTES)
        {
            return program.ActiveAttributes.Count;
        }

        if (pname == ATTACHED_SHADERS)
        {
            return program.Shaders.Count;
        }

        return true;
    }

    public string getProgramInfoLog(WebGlProgram? program) => string.Empty;

    public void useProgram(WebGlProgram? program) => _currentProgram = program;

    public void deleteProgram(WebGlProgram? program)
    {
        if (ReferenceEquals(_currentProgram, program))
        {
            _currentProgram = null;
        }
    }

    public WebGlActiveInfo? getActiveUniform(WebGlProgram? program, int index)
    {
        if (program is null || index < 0 || index >= program.ActiveUniforms.Count)
        {
            return null;
        }

        var uniform = program.ActiveUniforms[index];
        return new WebGlActiveInfo(uniform.Name, uniform.Size, uniform.Type);
    }

    public WebGlActiveInfo? getActiveAttrib(WebGlProgram? program, int index)
    {
        if (program is null || index < 0 || index >= program.ActiveAttributes.Count)
        {
            return null;
        }

        var attribute = program.ActiveAttributes[index];
        return new WebGlActiveInfo(attribute.Name, attribute.Size, attribute.Type);
    }

    public int getAttribLocation(WebGlProgram? program, string? name)
        => program is null || string.IsNullOrWhiteSpace(name) ? -1 : GetOrCreateAttribLocation(program, name!);

    public WebGlUniformLocation? getUniformLocation(WebGlProgram? program, string? name)
    {
        if (program is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = NormalizeUniformName(name!);
        if (!program.UniformLocations.TryGetValue(normalized, out var location))
        {
            location = new WebGlUniformLocation(program, normalized);
            program.UniformLocations[normalized] = location;
        }

        return location;
    }

    public void uniformMatrix4fv(object? location, bool transpose, object? value)
        => SetUniform(location, ExtractDoubles(value));

    public void uniformMatrix3fv(object? location, bool transpose, object? value)
        => SetUniform(location, ExtractDoubles(value));

    public void uniform4fv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform3fv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform2fv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform1fv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform4f(object? location, object? x, object? y, object? z, object? w)
        => SetUniform(location, new[] { ToDouble(x), ToDouble(y), ToDouble(z), ToDouble(w) });

    public void uniform3f(object? location, object? x, object? y, object? z)
        => SetUniform(location, new[] { ToDouble(x), ToDouble(y), ToDouble(z) });

    public void uniform2f(object? location, object? x, object? y)
        => SetUniform(location, new[] { ToDouble(x), ToDouble(y) });

    public void uniform1f(object? location, object? x)
        => SetUniform(location, new[] { ToDouble(x) });

    public void uniform4iv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform3iv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform2iv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform1iv(object? location, object? value) => SetUniform(location, ExtractDoubles(value));

    public void uniform1i(object? location, object? x)
        => SetUniform(location, new[] { ToDouble(x) });

    public void uniform2i(object? location, object? x, object? y)
        => SetUniform(location, new[] { ToDouble(x), ToDouble(y) });

    public void uniform3i(object? location, object? x, object? y, object? z)
        => SetUniform(location, new[] { ToDouble(x), ToDouble(y), ToDouble(z) });

    public void uniform4i(object? location, object? x, object? y, object? z, object? w)
        => SetUniform(location, new[] { ToDouble(x), ToDouble(y), ToDouble(z), ToDouble(w) });

    public void enableVertexAttribArray(int index) => _enabledAttributes.Add(index);

    public void disableVertexAttribArray(int index) => _enabledAttributes.Remove(index);

    public void vertexAttribPointer(int index, int size, int type, bool normalized, int stride, int offset)
    {
        var state = new WebGlVertexAttribState(_arrayBuffer, size, type, normalized, stride, offset);
        _attributes[index] = state;
        if (_currentVertexArray is not null)
        {
            _currentVertexArray.Attributes[index] = state;
        }
    }

    public void vertexAttribDivisor(int index, int divisor)
    {
    }

    public WebGlVertexArrayObject createVertexArray() => new();

    public void bindVertexArray(WebGlVertexArrayObject? vertexArray)
    {
        _currentVertexArray = vertexArray;
        if (vertexArray is not null)
        {
            _elementArrayBuffer = vertexArray.ElementArrayBuffer;
        }
    }

    public void deleteVertexArray(WebGlVertexArrayObject? vertexArray)
    {
        if (ReferenceEquals(_currentVertexArray, vertexArray))
        {
            _currentVertexArray = null;
        }
    }

    public WebGlTexture createTexture() => new();
    public void deleteTexture(WebGlTexture? texture) { }
    public void activeTexture(int texture) { }
    public void bindTexture(int target, WebGlTexture? texture) { }
    public void texParameteri(int target, int pname, int param) { }
    public void texParameterf(int target, int pname, double param) { }
    public void texImage2D(params object?[] args) { }
    public void texSubImage2D(params object?[] args) { }
    public void compressedTexImage2D(params object?[] args) { }
    public void compressedTexSubImage2D(params object?[] args) { }
    public void generateMipmap(int target) { }
    public void pixelStorei(int pname, object? param) { }

    public WebGlFramebuffer createFramebuffer() => new();
    public void bindFramebuffer(int target, WebGlFramebuffer? framebuffer) { }
    public void deleteFramebuffer(WebGlFramebuffer? framebuffer) { }
    public int checkFramebufferStatus(int target) => FRAMEBUFFER_COMPLETE;
    public void framebufferTexture2D(params object?[] args) { }
    public WebGlRenderbuffer createRenderbuffer() => new();
    public void bindRenderbuffer(int target, WebGlRenderbuffer? renderbuffer) { }
    public void renderbufferStorage(params object?[] args) { }
    public void framebufferRenderbuffer(params object?[] args) { }
    public void deleteRenderbuffer(WebGlRenderbuffer? renderbuffer) { }

    public void viewport(double x, double y, double width, double height)
    {
        _viewport[0] = x;
        _viewport[1] = y;
        _viewport[2] = width;
        _viewport[3] = height;
    }

    public void scissor(double x, double y, double width, double height) { }
    public void clearColor(double red, double green, double blue, double alpha)
    {
        _clearColor[0] = Math.Clamp(red, 0, 1);
        _clearColor[1] = Math.Clamp(green, 0, 1);
        _clearColor[2] = Math.Clamp(blue, 0, 1);
        _clearColor[3] = Math.Clamp(alpha, 0, 1);
    }

    public void clearDepth(double depth) { }
    public void clearStencil(int stencil) { }
    public void clear(int mask)
    {
        if ((mask & COLOR_BUFFER_BIT) == 0)
        {
            return;
        }

        _surface.ClearAll();
        var width = GetSurfaceWidth();
        var height = GetSurfaceHeight();
        _canvas2d.fillStyle = ToCssColor(_clearColor);
        _canvas2d.fillRect(0, 0, width, height);
    }

    public void colorMask(bool red, bool green, bool blue, bool alpha) { }
    public void depthMask(bool flag) { }
    public void stencilMask(int mask) { }
    public void stencilMaskSeparate(int face, int mask) { }
    public void enable(int cap) { }
    public void disable(int cap) { }
    public void depthFunc(int func) { }
    public void blendFunc(int sfactor, int dfactor) { }
    public void blendFuncSeparate(int srcRGB, int dstRGB, int srcAlpha, int dstAlpha) { }
    public void blendEquation(int mode) { }
    public void blendEquationSeparate(int modeRGB, int modeAlpha) { }
    public void blendColor(double red, double green, double blue, double alpha) { }
    public void cullFace(int mode) { }
    public void frontFace(int mode) { }
    public void polygonOffset(double factor, double units) { }
    public void lineWidth(double width) { }
    public void hint(int target, int mode) { }
    public void stencilFunc(int func, int reference, int mask) { }
    public void stencilFuncSeparate(int face, int func, int reference, int mask) { }
    public void stencilOp(int fail, int zfail, int zpass) { }
    public void stencilOpSeparate(int face, int fail, int zfail, int zpass) { }

    public void drawArrays(int mode, int first, int count)
    {
        DrawCallCount++;
        if (mode != TRIANGLES)
        {
            LastDrawStatus = $"Unsupported drawArrays mode {mode}";
            return;
        }

        var indices = Enumerable.Range(first, count).ToArray();
        DrawTriangles(indices);
    }

    public void drawElements(int mode, int count, int type, int offset)
    {
        DrawCallCount++;
        if (mode != TRIANGLES)
        {
            LastDrawStatus = $"Unsupported drawElements mode {mode}";
            return;
        }

        var source = (_currentVertexArray?.ElementArrayBuffer ?? _elementArrayBuffer)?.Data;
        var indices = ExtractIndices(source, type, count, offset);
        DrawTriangles(indices);
    }

    public void drawElementsInstanced(int mode, int count, int type, int offset, int instanceCount)
        => drawElements(mode, count, type, offset);

    public void drawArraysInstanced(int mode, int first, int count, int instanceCount)
        => drawArrays(mode, first, count);

    public void flush() { }
    public void finish() { }

    public bool isBuffer(WebGlBuffer? value) => value is not null;
    public bool isProgram(WebGlProgram? value) => value is not null;
    public bool isShader(WebGlShader? value) => value is not null;
    public bool isTexture(WebGlTexture? value) => value is not null;
    public bool isFramebuffer(WebGlFramebuffer? value) => value is not null;
    public bool isRenderbuffer(WebGlRenderbuffer? value) => value is not null;
    public bool isVertexArray(WebGlVertexArrayObject? value) => value is not null;

    private void DrawTriangles(IReadOnlyList<int> indices)
    {
        var program = _currentProgram;
        if (program is null || indices.Count < 3)
        {
            LastDrawStatus = program is null ? "No current program" : $"Too few indices ({indices.Count})";
            return;
        }

        var positionIndex = program.AttributeLocations.TryGetValue("position", out var location) ? location : 0;
        var attributes = _currentVertexArray?.Attributes ?? _attributes;
        if (!attributes.TryGetValue(positionIndex, out var positionState) || positionState.Buffer?.Data is null)
        {
            LastDrawStatus = $"No position attribute at location {positionIndex}";
            return;
        }

        var positions = ExtractDoubles(positionState.Buffer.Data);
        var modelView = GetUniformMatrix(program, "modelViewMatrix", Matrix4.Identity);
        var projection = GetUniformMatrix(program, "projectionMatrix", Matrix4.Identity);
        var matrix = projection * modelView;
        var color = GetUniformColor(program);
        var triangles = new List<SoftwareTriangle>();

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var p0 = ReadVertex(positions, positionState, indices[i], matrix);
            var p1 = ReadVertex(positions, positionState, indices[i + 1], matrix);
            var p2 = ReadVertex(positions, positionState, indices[i + 2], matrix);
            if (p0 is null || p1 is null || p2 is null)
            {
                LastDrawStatus = "Triangle contained unreadable vertex data";
                continue;
            }

            triangles.Add(new SoftwareTriangle(p0.Value, p1.Value, p2.Value, color));
        }

        TriangleCount += triangles.Count;
        LastDrawStatus = triangles.Count > 0 ? $"Drew {triangles.Count} triangle(s)" : "No complete triangles";

        foreach (var triangle in triangles.OrderBy(t => t.Depth))
        {
            DrawTriangle(triangle);
        }
    }

    private void DrawTriangle(SoftwareTriangle triangle)
    {
        _canvas2d.fillStyle = ToCssColor(triangle.Color);
        _canvas2d.beginPath();
        _canvas2d.moveTo(triangle.A.X, triangle.A.Y);
        _canvas2d.lineTo(triangle.B.X, triangle.B.Y);
        _canvas2d.lineTo(triangle.C.X, triangle.C.Y);
        _canvas2d.closePath();
        _canvas2d.fill();
    }

    private ProjectedVertex? ReadVertex(double[] positions, WebGlVertexAttribState state, int vertexIndex, Matrix4 matrix)
    {
        var componentCount = Math.Max(1, state.Size);
        var stride = state.Stride > 0 ? state.Stride / SizeOfType(state.Type) : componentCount;
        var start = state.Offset / SizeOfType(state.Type) + vertexIndex * stride;
        if (start < 0 || start + 2 >= positions.Length)
        {
            return null;
        }

        var x = positions[start];
        var y = componentCount > 1 ? positions[start + 1] : 0;
        var z = componentCount > 2 ? positions[start + 2] : 0;
        var clip = matrix.Transform(x, y, z, 1);
        if (Math.Abs(clip.W) < 0.000001)
        {
            return null;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        var ndcZ = clip.Z / clip.W;
        var width = _viewport[2] > 0 ? _viewport[2] : GetSurfaceWidth();
        var height = _viewport[3] > 0 ? _viewport[3] : GetSurfaceHeight();
        var screenX = _viewport[0] + (ndcX + 1) * 0.5 * width;
        var screenY = _viewport[1] + (1 - (ndcY + 1) * 0.5) * height;
        return new ProjectedVertex(screenX, screenY, ndcZ);
    }

    private Matrix4 GetUniformMatrix(WebGlProgram program, string name, Matrix4 fallback)
    {
        if (!program.UniformValues.TryGetValue(name, out var value) || value is not double[] values || values.Length < 16)
        {
            return fallback;
        }

        return Matrix4.FromColumnMajor(values);
    }

    private double[] GetUniformColor(WebGlProgram program)
    {
        var color = new[] { 1d, 1d, 1d, 1d };
        if (program.UniformValues.TryGetValue("diffuse", out var diffuseValue) && diffuseValue is double[] diffuse && diffuse.Length >= 3)
        {
            color[0] = diffuse[0];
            color[1] = diffuse[1];
            color[2] = diffuse[2];
        }

        if (program.UniformValues.TryGetValue("opacity", out var opacityValue) && opacityValue is double[] opacity && opacity.Length > 0)
        {
            color[3] = opacity[0];
        }

        return color;
    }

    private void SetUniform(object? location, double[] values)
    {
        if (location is not WebGlUniformLocation uniformLocation)
        {
            return;
        }

        uniformLocation.Program.UniformValues[uniformLocation.Name] = values;
    }

    private int GetOrCreateAttribLocation(WebGlProgram program, string name)
    {
        if (program.AttributeLocations.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var location = name switch
        {
            "position" => 0,
            "normal" => 1,
            "uv" => 2,
            "uv2" => 3,
            "color" => 4,
            "skinIndex" => 5,
            "skinWeight" => 6,
            _ => program.AttributeLocations.Count == 0 ? 0 : Math.Max(0, program.AttributeLocations.Values.DefaultIfEmpty(-1).Max() + 1)
        };
        program.AttributeLocations[name] = location;
        return location;
    }

    private void InitializeParameters()
    {
        _parameters[MAX_TEXTURE_IMAGE_UNITS.ToString(CultureInfo.InvariantCulture)] = 16;
        _parameters[MAX_VERTEX_TEXTURE_IMAGE_UNITS.ToString(CultureInfo.InvariantCulture)] = 16;
        _parameters[MAX_TEXTURE_SIZE.ToString(CultureInfo.InvariantCulture)] = 4096;
        _parameters[MAX_CUBE_MAP_TEXTURE_SIZE.ToString(CultureInfo.InvariantCulture)] = 4096;
        _parameters[MAX_VERTEX_ATTRIBS.ToString(CultureInfo.InvariantCulture)] = 16;
        _parameters[MAX_VERTEX_UNIFORM_VECTORS.ToString(CultureInfo.InvariantCulture)] = 1024;
        _parameters[MAX_VARYING_VECTORS.ToString(CultureInfo.InvariantCulture)] = 30;
        _parameters[MAX_FRAGMENT_UNIFORM_VECTORS.ToString(CultureInfo.InvariantCulture)] = 1024;
        _parameters[MAX_COMBINED_TEXTURE_IMAGE_UNITS.ToString(CultureInfo.InvariantCulture)] = 32;
        _parameters[MAX_RENDERBUFFER_SIZE.ToString(CultureInfo.InvariantCulture)] = 4096;
        _parameters[ALIASED_LINE_WIDTH_RANGE.ToString(CultureInfo.InvariantCulture)] = new[] { 1d, 1d };
        _parameters[ALIASED_POINT_SIZE_RANGE.ToString(CultureInfo.InvariantCulture)] = new[] { 1d, 64d };
        _parameters[COMPRESSED_TEXTURE_FORMATS.ToString(CultureInfo.InvariantCulture)] = Array.Empty<int>();
    }

    private double GetSurfaceWidth() => _surface.Bounds.Width > 0 ? _surface.Bounds.Width : 300;

    private double GetSurfaceHeight() => _surface.Bounds.Height > 0 ? _surface.Bounds.Height : 150;

    private static string ToCssColor(double[] color)
    {
        var r = (int)Math.Round(Math.Clamp(color.ElementAtOrDefault(0), 0, 1) * 255);
        var g = (int)Math.Round(Math.Clamp(color.ElementAtOrDefault(1), 0, 1) * 255);
        var b = (int)Math.Round(Math.Clamp(color.ElementAtOrDefault(2), 0, 1) * 255);
        var a = Math.Clamp(color.ElementAtOrDefault(3), 0, 1);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {a:0.###})");
    }

    private static string NormalizeUniformName(string name)
    {
        var bracket = name.IndexOf('[', StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket] : name;
    }

    private IEnumerable<WebGlShaderSymbol> ParseUniforms(string source)
    {
        foreach (Match match in Regex.Matches(source ?? string.Empty, @"\buniform\s+([A-Za-z_]\w*)\s+([A-Za-z_]\w*)(?:\s*\[\s*(\d+)?[^\]]*\])?\s*;"))
        {
            var type = GetShaderSymbolType(match.Groups[1].Value);
            var name = match.Groups[2].Value;
            var size = int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize)
                ? Math.Max(1, parsedSize)
                : 1;
            yield return new WebGlShaderSymbol(name, type, size);
        }
    }

    private IEnumerable<WebGlShaderSymbol> ParseAttributes(string source)
    {
        foreach (Match match in Regex.Matches(source ?? string.Empty, @"\b(?:attribute|in)\s+([A-Za-z_]\w*)\s+([A-Za-z_]\w*)\s*;"))
        {
            yield return new WebGlShaderSymbol(match.Groups[2].Value, GetShaderSymbolType(match.Groups[1].Value), 1);
        }
    }

    private int GetShaderSymbolType(string type)
        => type switch
        {
            "float" => FLOAT,
            "vec2" => FLOAT_VEC2,
            "vec3" => FLOAT_VEC3,
            "vec4" => FLOAT_VEC4,
            "int" => INT,
            "ivec2" => INT_VEC2,
            "ivec3" => INT_VEC3,
            "ivec4" => INT_VEC4,
            "bool" => BOOL,
            "bvec2" => BOOL_VEC2,
            "bvec3" => BOOL_VEC3,
            "bvec4" => BOOL_VEC4,
            "mat2" => FLOAT_MAT2,
            "mat3" => FLOAT_MAT3,
            "mat4" => FLOAT_MAT4,
            "sampler2D" => SAMPLER_2D,
            "samplerCube" => SAMPLER_CUBE,
            _ => FLOAT
        };

    private static int[] ExtractIndices(Array? array, int type, int count, int byteOffset)
    {
        var result = new int[Math.Max(0, count)];
        var elementSize = SizeOfType(type);
        var start = elementSize <= 0 ? 0 : Math.Max(0, byteOffset / elementSize);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ConvertElementToInt(array, start + i);
        }

        return result;
    }

    private static int ConvertElementToInt(Array? array, int index)
    {
        if (array is null || index < 0 || index >= array.Length)
        {
            return 0;
        }

        return Convert.ToInt32(array.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static Array? ExtractArray(object? data)
    {
        if (data is null)
        {
            return null;
        }

        if (data is Array array)
        {
            return array;
        }

        if (data is JsValue jsValue)
        {
            return ExtractArray(jsValue.ToObject());
        }

        if (data is IEnumerable enumerable && data is not string)
        {
            var values = new List<double>();
            foreach (var item in enumerable)
            {
                if (TryConvertToDouble(item, out var value))
                {
                    values.Add(value);
                }
            }

            return values.ToArray();
        }

        if (TryConvertToDouble(data, out var length))
        {
            return new byte[Math.Max(0, (int)length)];
        }

        return null;
    }

    private static double[] ExtractDoubles(object? data)
    {
        var array = ExtractArray(data);
        if (array is null)
        {
            return Array.Empty<double>();
        }

        var result = new double[array.Length];
        for (var i = 0; i < array.Length; i++)
        {
            result[i] = Convert.ToDouble(array.GetValue(i), CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case JsValue jsValue:
                return TryConvertToDouble(jsValue.ToObject(), out result);
            case IConvertible convertible:
                try
                {
                    result = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    result = 0;
                    return false;
                }
            default:
                result = 0;
                return false;
        }
    }

    private static double ToDouble(object? value)
        => TryConvertToDouble(value, out var result) ? result : 0d;

    private static int SizeOfType(int type)
        => type switch
        {
            0x1400 or 0x1401 => 1,
            0x1402 or 0x1403 => 2,
            0x1404 or 0x1405 or 0x1406 => 4,
            _ => 4
        };

    private readonly record struct ProjectedVertex(double X, double Y, double Z);

    private readonly record struct SoftwareTriangle(ProjectedVertex A, ProjectedVertex B, ProjectedVertex C, double[] Color)
    {
        public double Depth => (A.Z + B.Z + C.Z) / 3.0;
    }

    private readonly record struct Vector4(double X, double Y, double Z, double W);

    private readonly record struct Matrix4(
        double M11,
        double M12,
        double M13,
        double M14,
        double M21,
        double M22,
        double M23,
        double M24,
        double M31,
        double M32,
        double M33,
        double M34,
        double M41,
        double M42,
        double M43,
        double M44)
    {
        public static Matrix4 Identity { get; } = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

        public static Matrix4 FromColumnMajor(IReadOnlyList<double> values)
            => new(
                values[0], values[4], values[8], values[12],
                values[1], values[5], values[9], values[13],
                values[2], values[6], values[10], values[14],
                values[3], values[7], values[11], values[15]);

        public Vector4 Transform(double x, double y, double z, double w)
            => new(
                M11 * x + M12 * y + M13 * z + M14 * w,
                M21 * x + M22 * y + M23 * z + M24 * w,
                M31 * x + M32 * y + M33 * z + M34 * w,
                M41 * x + M42 * y + M43 * z + M44 * w);

        public static Matrix4 operator *(Matrix4 left, Matrix4 right)
            => new(
                left.M11 * right.M11 + left.M12 * right.M21 + left.M13 * right.M31 + left.M14 * right.M41,
                left.M11 * right.M12 + left.M12 * right.M22 + left.M13 * right.M32 + left.M14 * right.M42,
                left.M11 * right.M13 + left.M12 * right.M23 + left.M13 * right.M33 + left.M14 * right.M43,
                left.M11 * right.M14 + left.M12 * right.M24 + left.M13 * right.M34 + left.M14 * right.M44,
                left.M21 * right.M11 + left.M22 * right.M21 + left.M23 * right.M31 + left.M24 * right.M41,
                left.M21 * right.M12 + left.M22 * right.M22 + left.M23 * right.M32 + left.M24 * right.M42,
                left.M21 * right.M13 + left.M22 * right.M23 + left.M23 * right.M33 + left.M24 * right.M43,
                left.M21 * right.M14 + left.M22 * right.M24 + left.M23 * right.M34 + left.M24 * right.M44,
                left.M31 * right.M11 + left.M32 * right.M21 + left.M33 * right.M31 + left.M34 * right.M41,
                left.M31 * right.M12 + left.M32 * right.M22 + left.M33 * right.M32 + left.M34 * right.M42,
                left.M31 * right.M13 + left.M32 * right.M23 + left.M33 * right.M33 + left.M34 * right.M43,
                left.M31 * right.M14 + left.M32 * right.M24 + left.M33 * right.M34 + left.M34 * right.M44,
                left.M41 * right.M11 + left.M42 * right.M21 + left.M43 * right.M31 + left.M44 * right.M41,
                left.M41 * right.M12 + left.M42 * right.M22 + left.M43 * right.M32 + left.M44 * right.M42,
                left.M41 * right.M13 + left.M42 * right.M23 + left.M43 * right.M33 + left.M44 * right.M43,
                left.M41 * right.M14 + left.M42 * right.M24 + left.M43 * right.M34 + left.M44 * right.M44);
    }

    private sealed class WebGlVertexArrayExtension
    {
        private readonly CanvasWebGlRenderingContext _context;

        public WebGlVertexArrayExtension(CanvasWebGlRenderingContext context)
        {
            _context = context;
        }

        public WebGlVertexArrayObject createVertexArrayOES() => _context.createVertexArray();
        public void bindVertexArrayOES(WebGlVertexArrayObject? vertexArray) => _context.bindVertexArray(vertexArray);
        public void deleteVertexArrayOES(WebGlVertexArrayObject? vertexArray) => _context.deleteVertexArray(vertexArray);
        public bool isVertexArrayOES(WebGlVertexArrayObject? vertexArray) => _context.isVertexArray(vertexArray);
        public int VERTEX_ARRAY_BINDING_OES => 0x85B5;
    }
}

internal sealed class WebGlBuffer
{
    public Array? Data { get; set; }
    public int Target { get; set; }
    public int Usage { get; set; }
}

internal sealed class WebGlShader
{
    public WebGlShader(int type)
    {
        Type = type;
    }

    public int Type { get; }
    public string Source { get; set; } = string.Empty;
    public bool Compiled { get; set; }
}

internal sealed class WebGlProgram
{
    public List<WebGlShader> Shaders { get; } = new();
    public List<WebGlShaderSymbol> ActiveUniforms { get; } = new();
    public List<WebGlShaderSymbol> ActiveAttributes { get; } = new();
    public Dictionary<string, int> AttributeLocations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WebGlUniformLocation> UniformLocations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> UniformValues { get; } = new(StringComparer.Ordinal);
    public bool Linked { get; set; }
}

internal sealed record WebGlShaderSymbol(string Name, int Type, int Size);

internal sealed class WebGlUniformLocation
{
    public WebGlUniformLocation(WebGlProgram program, string name)
    {
        Program = program;
        Name = name;
    }

    public WebGlProgram Program { get; }
    public string Name { get; }
}

internal sealed class WebGlActiveInfo
{
    public WebGlActiveInfo(string name, int size, int type)
    {
        this.name = name;
        this.size = size;
        this.type = type;
    }

    public string name { get; }
    public int size { get; }
    public int type { get; }
}

internal sealed class WebGlShaderPrecisionFormat
{
    public WebGlShaderPrecisionFormat(int rangeMin, int rangeMax, int precision)
    {
        this.rangeMin = rangeMin;
        this.rangeMax = rangeMax;
        this.precision = precision;
    }

    public int rangeMin { get; }
    public int rangeMax { get; }
    public int precision { get; }
}

internal sealed class WebGlTexture;

internal sealed class WebGlFramebuffer;

internal sealed class WebGlRenderbuffer;

internal sealed record WebGlVertexAttribState(WebGlBuffer? Buffer, int Size, int Type, bool Normalized, int Stride, int Offset);

internal sealed class WebGlVertexArrayObject
{
    internal WebGlBuffer? ElementArrayBuffer { get; set; }
    internal Dictionary<int, WebGlVertexAttribState> Attributes { get; } = new();
}
