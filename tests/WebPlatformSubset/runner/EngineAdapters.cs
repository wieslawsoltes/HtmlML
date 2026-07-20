using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Platform;
using SkiaSharp;

namespace HtmlML.WebPlatformSubset.Runner;

internal interface IWptEngineEnvironment : IDisposable
{
    void PumpInputAction();
    string? ReadState();
    bool IsFrameComplete();
    void SettleFrame();
    WptRenderSnapshot CaptureSnapshot(string documentName);
}

internal sealed record WptRenderSnapshot(
    PixelSize PixelSize,
    Vector Dpi,
    PixelFormat Format,
    byte[] Pixels);

/// <summary>
/// Adapts the experimental native V8/DOM engine to the same observable test
/// contract as the managed Avalonia DOM. It deliberately has no managed-engine
/// fallback: missing native capabilities must appear in the parity results.
/// </summary>
internal sealed unsafe class NativeWptEngineEnvironment : IWptEngineEnvironment
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex ScriptRegex = new(
        "<script\\b(?<attributes>[^>]*)>(?<source>[\\s\\S]*?)</script\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StyleRegex = new(
        "<style\\b[^>]*>[\\s\\S]*?</style\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BodyRegex = new(
        "<body\\b[^>]*>(?<body>[\\s\\S]*?)</body\\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IntPtr _engine;
    private readonly ViewportSettings _viewport;
    private readonly NativeSceneSnapshotRenderer _renderer = new();
    private ulong _sequence;
    private bool _loaded;
    private bool _disposed;

    internal NativeWptEngineEnvironment(
        RunnerOptions options,
        ViewportSettings viewport,
        string upstreamRoot,
        string html)
    {
        _viewport = viewport;
        var libraryPath = options.NativeLibraryPath;
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            throw new ArgumentException(
                "Native WPT mode requires --native-library <path>.");
        }
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException("Native HtmlML engine library was not found.", libraryPath);
        }

        NativeApi.Configure(libraryPath);
        _engine = NativeApi.Create(options.NativeCacheDirectory);
        if (_engine == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native HtmlML engine could not be created.");
        }

        try
        {
            Enqueue(new NativeInputEvent
            {
                Kind = 6,
                Sequence = ++_sequence,
                X = viewport.Width,
                Y = viewport.Height
            });
            LoadPreparedDocument(html, upstreamRoot);
            _loaded = true;
            for (var index = 0; index < 4; index++) SettleFrame();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string? ReadState()
    {
        var json = Evaluate("window.__htmlMlWptState || null", "htmlml-wpt-read-state.js");
        return string.Equals(json, "null", StringComparison.Ordinal) ? null : json;
    }

    public bool IsFrameComplete() => _loaded;

    public void PumpInputAction()
    {
        var actionJson = Evaluate("""
            (function () {
              const queue = window.__htmlMlWptInputActions;
              return queue && queue.length ? queue.shift() : null;
            })()
            """, "htmlml-wpt-read-input.js");
        if (string.Equals(actionJson, "null", StringComparison.Ordinal)) return;

        var action = JsonSerializer.Deserialize<NativeInputAction>(actionJson, JsonOptions)
                     ?? throw new InvalidDataException("WPT input action was empty.");
        string? error = null;
        try
        {
            var target = ResolveTarget(action.TargetId);
            switch (action.Type)
            {
                case "pointerMove":
                    EnqueuePointer(1, target.X, target.Y);
                    break;
                case "click":
                    EnqueuePointer(1, target.X, target.Y);
                    EnqueuePointer(2, target.X, target.Y, flags: 1);
                    EnqueuePointer(3, target.X, target.Y);
                    break;
                case "sendKeys":
                    throw new NotSupportedException(
                        "The native input ABI does not expose keyboard events yet.");
                default:
                    throw new NotSupportedException(
                        $"WPT input action '{action.Type}' is not supported by the native adapter.");
            }
            SettleFrame();
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        Execute(
            $"window.__htmlMlCompleteInputAction({action.Id}, {JsonSerializer.Serialize(error)});",
            "htmlml-wpt-complete-input.js");
    }

    public void SettleFrame()
    {
        if (_disposed) return;
        Enqueue(new NativeInputEvent { Kind = 5, Sequence = ++_sequence });
        // A native frame consumes animation work; the no-op script then gives
        // the runtime a task-drain turn for zero-delay WPT completion timers.
        Execute("void 0", "htmlml-wpt-pump.js");
        AcquireLatestScene(waitForSequence: _sequence);
    }

    public WptRenderSnapshot CaptureSnapshot(string documentName)
    {
        AcquireLatestScene(waitForSequence: 0);
        return _renderer.Capture(
            _viewport.Width,
            _viewport.Height,
            new Vector(96 * _viewport.DeviceScaleFactor, 96 * _viewport.DeviceScaleFactor));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();
        if (_engine != IntPtr.Zero) NativeApi.EngineDestroy(_engine);
    }

    private void LoadPreparedDocument(string html, string upstreamRoot)
    {
        var scripts = ScriptRegex.Matches(html)
            .Select(match => new
            {
                Attributes = match.Groups["attributes"].Value,
                Source = match.Groups["source"].Value
            })
            .Where(script => !Regex.IsMatch(
                script.Attributes,
                "\\btype\\s*=\\s*['\"](?:application/(?:json|ld\\+json)|text/plain)['\"]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();

        var styles = string.Concat(StyleRegex.Matches(html).Select(match => match.Value));
        var bodyMatch = BodyRegex.Match(html);
        var body = bodyMatch.Success ? bodyMatch.Groups["body"].Value : html;
        body = ScriptRegex.Replace(body, string.Empty);
        body = StyleRegex.Replace(body, string.Empty);
        var markup = styles + body;

        Execute($$"""
            document.body.style.margin = '0';
            document.body.style.padding = '0';
            document.body.style.overflow = 'hidden';
            document.body.style.background = '#ffffff';
            document.body.innerHTML = {{JsonSerializer.Serialize(markup)}};
            """, Path.Combine(upstreamRoot, "htmlml-wpt-document.js"));

        for (var index = 0; index < scripts.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(scripts[index].Source)) continue;
            Execute(
                scripts[index].Source,
                Path.Combine(upstreamRoot, $"htmlml-wpt-inline-{index}.js"));
        }
        Execute(
            """
            document.readyState = 'interactive';
            document.dispatchEvent(new Event('DOMContentLoaded'));
            document.readyState = 'complete';
            window.dispatchEvent(new Event('load'));
            """,
            Path.Combine(upstreamRoot, "htmlml-wpt-load.js"));
    }

    private NativePoint ResolveTarget(string id)
    {
        var json = Evaluate($$"""
            (function () {
              const target = document.getElementById({{JsonSerializer.Serialize(id)}});
              if (!target) return null;
              const rect = target.getBoundingClientRect();
              return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
            })()
            """, "htmlml-wpt-target.js");
        if (string.Equals(json, "null", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"WPT input target '#{id}' was not found.");
        }
        return JsonSerializer.Deserialize<NativePoint>(json, JsonOptions)
               ?? throw new InvalidDataException($"WPT input target '#{id}' had no bounds.");
    }

    private void EnqueuePointer(uint kind, double x, double y, uint flags = 0)
        => Enqueue(new NativeInputEvent
        {
            Kind = kind,
            Flags = flags,
            Sequence = ++_sequence,
            X = x,
            Y = y
        });

    private void Enqueue(NativeInputEvent input)
    {
        if (NativeApi.EngineEnqueue(_engine, in input) == 0)
        {
            throw new InvalidOperationException("The native input queue rejected a WPT event.");
        }
    }

    private void Execute(string source, string documentName)
    {
        if (NativeApi.TryExecute(_engine, source, documentName)) return;
        throw new InvalidOperationException(
            $"Native script failed in '{documentName}': {NativeApi.GetLastError(_engine)}");
    }

    private string Evaluate(string source, string documentName)
    {
        if (NativeApi.TryEvaluate(_engine, source, documentName, out var json)) return json;
        throw new InvalidOperationException(
            $"Native evaluation failed in '{documentName}': {NativeApi.GetLastError(_engine)}");
    }

    private void AcquireLatestScene(ulong waitForSequence)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            var scene = NativeApi.EngineAcquireLatestScene(_engine);
            if (scene == IntPtr.Zero)
            {
                Thread.Yield();
                continue;
            }
            try
            {
                var view = (NativeSceneView*)scene;
                _renderer.Apply(view);
                NativeApi.SceneAcknowledge(scene);
                if (waitForSequence == 0 || view->Header.ConsumedInputSequence >= waitForSequence)
                {
                    return;
                }
            }
            finally
            {
                NativeApi.SceneRelease(scene);
            }
            Thread.Yield();
        }
    }

    private sealed class NativeInputAction
    {
        public int Id { get; init; }
        public required string Type { get; init; }
        public required string TargetId { get; init; }
        public string? Value { get; init; }
    }

    private sealed class NativePoint
    {
        public double X { get; init; }
        public double Y { get; init; }
    }
}

internal sealed unsafe class NativeSceneSnapshotRenderer : IDisposable
{
    private const uint SceneCheckpoint = 1;
    private const uint SceneDomReplacement = 2;
    private SKPicture? _dom;
    private ulong _revision;

    internal void Apply(NativeSceneView* view)
    {
        var header = view->Header;
        if (header.Revision == _revision) return;
        if ((header.Flags & (SceneCheckpoint | SceneDomReplacement)) == 0) return;

        _dom?.Dispose();
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(
            0, 0, Math.Max(1, header.ViewportWidth), Math.Max(1, header.ViewportHeight)));
        var commands = new ReadOnlySpan<NativeSceneCommand>(
            view->Commands,
            checked((int)header.CommandCount));

        foreach (ref readonly var command in commands)
        {
            if (command.Kind is 1 or 7)
            {
                DrawRect(canvas, command, stroke: false);
            }
            else if (command.Kind == 2)
            {
                using var paint = Paint(command.Rgba, SKPaintStyle.Stroke, command.Flags / 100f);
                canvas.DrawLine(command.X, command.Y, command.Width, command.Height, paint);
            }
        }
        foreach (ref readonly var command in commands)
        {
            switch (command.Kind)
            {
                case 3:
                    DrawText(canvas, view, command);
                    break;
                case 4:
                case 5:
                    DrawSvgPath(canvas, view, command, command.Kind == 5);
                    break;
                case 9:
                case 10:
                    DrawRect(canvas, command, stroke: false);
                    break;
                case 8:
                case 11:
                    DrawRect(canvas, command, stroke: true);
                    break;
            }
        }
        _dom = recorder.EndRecording();
        _revision = header.Revision;
    }

    internal WptRenderSnapshot Capture(int width, int height, Vector dpi)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            if (_dom is not null) canvas.DrawPicture(_dom);
            canvas.Flush();
        }

        var pixels = new byte[checked(bitmap.RowBytes * height)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        if (bitmap.RowBytes != width * 4)
        {
            var compact = new byte[checked(width * height * 4)];
            for (var row = 0; row < height; row++)
            {
                Buffer.BlockCopy(pixels, row * bitmap.RowBytes, compact, row * width * 4, width * 4);
            }
            pixels = compact;
        }
        return new WptRenderSnapshot(
            new PixelSize(width, height),
            dpi,
            PixelFormat.Bgra8888,
            pixels);
    }

    public void Dispose()
    {
        _dom?.Dispose();
        _dom = null;
    }

    private static void DrawRect(SKCanvas canvas, in NativeSceneCommand command, bool stroke)
    {
        var width = command.StrokeWidth > 0
            ? command.StrokeWidth
            : Math.Max(0.1f, (command.Flags & 0xffff) / 100f);
        using var paint = Paint(command.Rgba, stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill, width);
        var topLeft = command.RadiusTopLeft;
        var topRight = command.RadiusTopRight;
        var bottomRight = command.RadiusBottomRight;
        var bottomLeft = command.RadiusBottomLeft;
        if (topLeft <= 0 && topRight <= 0 && bottomRight <= 0 && bottomLeft <= 0)
        {
            topLeft = topRight = bottomRight = bottomLeft = (command.Flags >> 16) / 100f;
        }
        if (topLeft <= 0 && topRight <= 0 && bottomRight <= 0 && bottomLeft <= 0)
        {
            canvas.DrawRect(command.X, command.Y, command.Width, command.Height, paint);
            return;
        }
        using var rounded = new SKRoundRect();
        rounded.SetRectRadii(
            new SKRect(command.X, command.Y, command.X + command.Width, command.Y + command.Height),
            [
                new SKPoint(topLeft, topLeft),
                new SKPoint(topRight, topRight),
                new SKPoint(bottomRight, bottomRight),
                new SKPoint(bottomLeft, bottomLeft)
            ]);
        canvas.DrawRoundRect(rounded, paint);
    }

    private static void DrawText(
        SKCanvas canvas,
        NativeSceneView* view,
        in NativeSceneCommand command)
    {
        var parts = StringAt(view, command.Flags).Split('\t', 6);
        if (parts.Length != 6
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            return;
        }
        using var paint = Paint(command.Rgba, SKPaintStyle.Fill, 1);
        paint.TextSize = Math.Max(1, size);
        paint.TextAlign = parts[3] switch
        {
            "center" => SKTextAlign.Center,
            "right" or "end" => SKTextAlign.Right,
            _ => SKTextAlign.Left
        };
        var x = paint.TextAlign switch
        {
            SKTextAlign.Center => command.X + command.Width / 2,
            SKTextAlign.Right => command.X + command.Width,
            _ => command.X
        };
        paint.GetFontMetrics(out var metrics);
        var baseline = command.Y + (command.Height - (metrics.Descent + metrics.Ascent)) / 2;
        canvas.DrawText(parts[5], x, baseline, paint);
    }

    private static void DrawSvgPath(
        SKCanvas canvas,
        NativeSceneView* view,
        in NativeSceneCommand command,
        bool stroke)
    {
        var parts = StringAt(view, command.Flags).Split('\t', 4);
        if (parts.Length != 4 || command.Width <= 0 || command.Height <= 0) return;
        var numbers = parts[0].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : float.NaN)
            .ToArray();
        if (numbers.Length < 4 || numbers.Any(float.IsNaN) || numbers[2] == 0 || numbers[3] == 0) return;
        using var path = SKPath.ParseSvgPathData(parts[3]);
        if (path is null) return;
        using var paint = Paint(command.Rgba, stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill, 1);
        var save = canvas.Save();
        canvas.Translate(command.X, command.Y);
        canvas.Scale(command.Width / numbers[2], command.Height / numbers[3]);
        canvas.Translate(-numbers[0], -numbers[1]);
        canvas.DrawPath(path, paint);
        canvas.RestoreToCount(save);
    }

    private static SKPaint Paint(uint rgba, SKPaintStyle style, float width)
        => new()
        {
            IsAntialias = true,
            Style = style,
            StrokeWidth = Math.Max(0.1f, width),
            Color = new SKColor(
                (byte)(rgba >> 24),
                (byte)(rgba >> 16),
                (byte)(rgba >> 8),
                (byte)rgba)
        };

    private static string StringAt(NativeSceneView* view, uint index)
    {
        if (index >= view->StringCount || view->Strings is null || view->StringBytes is null) return string.Empty;
        var value = view->Strings[index];
        if (value.ByteOffset > view->StringByteCount
            || value.ByteLength > view->StringByteCount - value.ByteOffset)
        {
            return string.Empty;
        }
        return Encoding.UTF8.GetString(
            new ReadOnlySpan<byte>(view->StringBytes + value.ByteOffset, checked((int)value.ByteLength)));
    }
}

internal static unsafe class NativeApi
{
    private const string LibraryName = "htmlml_native_engine";
    private static readonly object Gate = new();
    private static string? _libraryPath;
    private static bool _resolverInstalled;

    internal static void Configure(string libraryPath)
    {
        lock (Gate)
        {
            var fullPath = Path.GetFullPath(libraryPath);
            if (_libraryPath is not null && !string.Equals(_libraryPath, fullPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The native test adapter is already bound to '{_libraryPath}'.");
            }
            _libraryPath = fullPath;
            if (_resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(
                typeof(NativeApi).Assembly,
                (name, _, _) => name == LibraryName
                    ? NativeLibrary.Load(_libraryPath!)
                    : IntPtr.Zero);
            _resolverInstalled = true;
        }
    }

    internal static IntPtr Create(string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory)) return EngineCreate(0);
        Directory.CreateDirectory(cacheDirectory);
        var bytes = Encoding.UTF8.GetBytes(cacheDirectory);
        fixed (byte* pointer = bytes)
        {
            var options = new NativeEngineOptions
            {
                StructSize = (uint)Marshal.SizeOf<NativeEngineOptions>(),
                CompilationCacheDirectory = (IntPtr)pointer,
                CompilationCacheDirectoryLength = (nuint)bytes.Length
            };
            return EngineCreateWithOptions(in options);
        }
    }

    internal static bool TryExecute(IntPtr engine, string source, string documentName)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var nameBytes = Encoding.UTF8.GetBytes(documentName);
        return EngineExecuteScript(
            engine,
            sourceBytes,
            (nuint)sourceBytes.Length,
            nameBytes,
            (nuint)nameBytes.Length) != 0;
    }

    internal static bool TryEvaluate(
        IntPtr engine,
        string source,
        string documentName,
        out string json)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var nameBytes = Encoding.UTF8.GetBytes(documentName);
        var destination = new byte[1024 * 1024];
        var required = EngineEvaluateJson(
            engine,
            sourceBytes,
            (nuint)sourceBytes.Length,
            nameBytes,
            (nuint)nameBytes.Length,
            destination,
            (nuint)destination.Length,
            5_000);
        if (required == 0 || required > (nuint)destination.Length)
        {
            json = string.Empty;
            return false;
        }
        json = Encoding.UTF8.GetString(destination, 0, checked((int)required - 1));
        return true;
    }

    internal static string GetLastError(IntPtr engine)
    {
        var required = EngineCopyLastError(engine, null, 0);
        if (required <= 1) return string.Empty;
        var bytes = new byte[checked((int)required)];
        EngineCopyLastError(engine, bytes, (nuint)bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
    }

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_create")]
    private static extern IntPtr EngineCreate(uint simulatedChartCommandCount);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_create_with_options")]
    private static extern IntPtr EngineCreateWithOptions(in NativeEngineOptions options);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_execute_script")]
    private static extern byte EngineExecuteScript(
        IntPtr engine,
        byte[] source,
        nuint sourceLength,
        byte[] documentName,
        nuint documentNameLength);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_evaluate_json")]
    private static extern nuint EngineEvaluateJson(
        IntPtr engine,
        byte[] source,
        nuint sourceLength,
        byte[] documentName,
        nuint documentNameLength,
        byte[] destination,
        nuint destinationCapacity,
        uint timeoutMilliseconds);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_copy_last_error")]
    private static extern nuint EngineCopyLastError(
        IntPtr engine,
        byte[]? destination,
        nuint destinationCapacity);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_destroy")]
    internal static extern void EngineDestroy(IntPtr engine);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_enqueue")]
    internal static extern byte EngineEnqueue(IntPtr engine, in NativeInputEvent input);

    [DllImport(LibraryName, EntryPoint = "htmlml_engine_acquire_latest_scene")]
    internal static extern IntPtr EngineAcquireLatestScene(IntPtr engine);

    [DllImport(LibraryName, EntryPoint = "htmlml_scene_acknowledge")]
    internal static extern byte SceneAcknowledge(IntPtr scene);

    [DllImport(LibraryName, EntryPoint = "htmlml_scene_release")]
    internal static extern void SceneRelease(IntPtr scene);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEngineOptions
{
    public uint StructSize;
    public uint SimulatedChartCommandCount;
    public IntPtr CompilationCacheDirectory;
    public nuint CompilationCacheDirectoryLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInputEvent
{
    public uint Kind;
    public uint Flags;
    public ulong Sequence;
    public double X;
    public double Y;
    public double DeltaX;
    public double DeltaY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSceneHeader
{
    public ulong Revision;
    public ulong BaseRevision;
    public ulong ConsumedInputSequence;
    public float ViewportWidth;
    public float ViewportHeight;
    public uint CommandCount;
    public uint CanvasLayerCount;
    public uint DamageRectCount;
    public uint Flags;
    public ulong ContentHash;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSceneCommand
{
    public uint Kind;
    public uint Flags;
    public float X;
    public float Y;
    public float Width;
    public float Height;
    public uint Rgba;
    public uint NodeId;
    public float RadiusTopLeft;
    public float RadiusTopRight;
    public float RadiusBottomRight;
    public float RadiusBottomLeft;
    public float StrokeWidth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSceneString
{
    public uint ByteOffset;
    public uint ByteLength;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSceneView
{
    public uint StructSize;
    public uint AbiVersion;
    public NativeSceneHeader Header;
    public NativeSceneCommand* Commands;
    public void* CanvasLayers;
    public void* CanvasCommands;
    public NativeSceneString* Strings;
    public byte* StringBytes;
    public void* DamageRects;
    public void* LeaseToken;
    public uint CanvasCommandCount;
    public uint StringCount;
    public uint StringByteCount;
    public uint Reserved;
}
