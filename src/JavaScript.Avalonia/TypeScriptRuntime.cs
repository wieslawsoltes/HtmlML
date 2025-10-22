using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace JavaScript.Avalonia;

public sealed class TypeScriptRuntime
{
    private const string ResourceName = "JavaScript.Avalonia.TypeScript.typescript.js";
    private const string TranspileFunctionName = "__tsInternalTranspile";

    private readonly Engine _engine;
    private readonly object _sync = new();
    private bool _initialized;
    private readonly Dictionary<string, TypeScriptLibrary> _libraries = new(StringComparer.OrdinalIgnoreCase);

    private readonly TypeScriptCompilerOptions _defaultOptions = new();

    public TypeScriptRuntime(Engine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public IReadOnlyDictionary<string, TypeScriptLibrary> Libraries
        => new Dictionary<string, TypeScriptLibrary>(_libraries);

    public void AddLibrary(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Library name must be provided.", nameof(name));
        }

        content ??= string.Empty;
        _libraries[name] = new TypeScriptLibrary(name, content);
    }

    public bool RemoveLibrary(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return _libraries.Remove(name);
    }

    public void AddLibraryFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        var content = File.ReadAllText(path);
        var name = Path.GetFileName(path);
        AddLibrary(name, content);
    }

    public TypeScriptCompilerOptions DefaultOptions => _defaultOptions;

    public TypeScriptTranspileResult Transpile(string fileName, string source)
        => Transpile(fileName, source, _defaultOptions);

    public TypeScriptTranspileResult Transpile(string fileName, string source, TypeScriptCompilerOptions? options)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        EnsureInitialized();

        options ??= _defaultOptions;
        var libs = _libraries.Values
            .Select(static l => l.Content)
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .ToArray();

        var libsValue = JsValue.FromObject(_engine, libs);
        var optionsValue = JsValue.FromObject(_engine, options.ToDictionary());

        var functionValue = _engine.GetValue(TranspileFunctionName);
        if (!functionValue.IsObject())
        {
            throw new InvalidOperationException("TypeScript transpile function is not available.");
        }

        var result = _engine.Invoke(functionValue, fileName ?? "module.ts", source, libsValue, optionsValue);
        if (!result.IsObject())
        {
            throw new InvalidOperationException("Unexpected TypeScript transpile result.");
        }

        var resultObject = result.AsObject();
        var codeValue = resultObject.Get("code");
        var diagnosticsValue = resultObject.Get("diagnostics");
        var sourceMapValue = resultObject.Get("sourceMap");

        var code = codeValue.IsString() ? codeValue.AsString() : string.Empty;
        var sourceMap = sourceMapValue.IsString() ? sourceMapValue.AsString() : null;

        var diagnostics = new List<string>();
        if (diagnosticsValue.IsArray())
        {
            var array = diagnosticsValue.AsArray();
            var length = TypeConverter.ToInt32(array.Get("length"));
            for (var i = 0; i < length; i++)
            {
                var item = array.Get(i.ToString());
                if (item.IsString())
                {
                    diagnostics.Add(item.AsString());
                    continue;
                }

                if (item.IsObject())
                {
                    var message = BuildDiagnosticMessage(item.AsObject());
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        diagnostics.Add(message!);
                    }
                }
            }
        }

        return new TypeScriptTranspileResult(code, sourceMap, diagnostics);
    }

    private static string? BuildDiagnosticMessage(ObjectInstance diagnostic)
    {
        if (diagnostic is null)
        {
            return null;
        }

        var messageText = diagnostic.Get("messageText");
        var category = diagnostic.Get("category");
        var code = diagnostic.Get("code");
        var file = diagnostic.Get("file");
        var start = diagnostic.Get("start");

        var builder = new StringBuilder();

        var categoryText = category.IsNumber() ? DescribeCategory(TypeConverter.ToInt32(category)) : "";
        if (!string.IsNullOrEmpty(categoryText))
        {
            builder.Append('[').Append(categoryText).Append(']').Append(' ');
        }

        if (code.IsNumber())
        {
            builder.Append("TS").Append(TypeConverter.ToInt32(code)).Append(':').Append(' ');
        }

        var message = ExtractMessageText(messageText);
        if (!string.IsNullOrEmpty(message))
        {
            builder.Append(message);
        }

        if (file.IsObject())
        {
            var fileObj = file.AsObject();
            var fileNameValue = fileObj.Get("fileName");
            if (fileNameValue.IsString())
            {
                builder.Append(" (file: ").Append(fileNameValue.AsString());
                if (start.IsNumber())
                {
                    builder.Append(", offset: ").Append(TypeConverter.ToInt32(start));
                }
                builder.Append(')');
            }
        }

        var result = builder.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private static string DescribeCategory(int category)
        => category switch
        {
            0 => "Warning",
            1 => "Error",
            2 => "Suggestion",
            3 => "Message",
            _ => string.Empty
        };

    private static string ExtractMessageText(JsValue messageText)
    {
        if (messageText.IsString())
        {
            return messageText.AsString();
        }

        if (messageText.IsObject())
        {
            var obj = messageText.AsObject();
            var text = obj.Get("messageText");
            if (!text.IsUndefined())
            {
                return ExtractMessageText(text);
            }
        }

        return string.Empty;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            var assembly = typeof(TypeScriptRuntime).GetTypeInfo().Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd();
            _engine.Execute(script);
            _engine.Execute(TypeScriptHelperScript);
            _initialized = true;
        }
    }

    private const string TypeScriptHelperScript = @"(function(){
  if (typeof globalThis === 'undefined') {
    throw new Error('TypeScript runtime requires globalThis.');
  }
  if (typeof globalThis.ts === 'undefined') {
    throw new Error('TypeScript compiler not found.');
  }
  function coerceCompilerOptions(options) {
    options = options || {};
    if (typeof options.module === 'undefined') {
      options.module = globalThis.ts.ModuleKind.CommonJS;
    }
    if (typeof options.target === 'undefined') {
      options.target = globalThis.ts.ScriptTarget.ES2020;
    }
    if (options.sourceMap === true && typeof options.inlineSources === 'undefined') {
      options.inlineSources = true;
    }
    return options;
  }
  function normalizeLibraries(libraries) {
    if (!Array.isArray(libraries)) {
      return [];
    }
    return libraries.filter(function(entry){ return typeof entry === 'string' && entry.length > 0; });
  }
  function concatLibraries(libraries, source) {
    var builder = '';
    for (var i = 0; i < libraries.length; i++) {
      builder += libraries[i];
      if (libraries[i].charCodeAt(libraries[i].length - 1) !== 10) {
        builder += '\n';
      }
    }
    builder += source || '';
    return builder;
  }
  globalThis.__tsInternalTranspile = function(fileName, source, libraries, options) {
    var libs = normalizeLibraries(libraries);
    var compilerOptions = coerceCompilerOptions(options);
    var combinedSource = concatLibraries(libs, source);
    var result = globalThis.ts.transpileModule(combinedSource, {
      compilerOptions: compilerOptions,
      fileName: fileName || 'module.ts'
    });
    return {
      code: result.outputText || '',
      diagnostics: result.diagnostics || [],
      sourceMap: result.sourceMapText || null
    };
  };
})();";
}

public sealed record TypeScriptLibrary(string Name, string Content);

public sealed record TypeScriptTranspileResult(string Code, string? SourceMap, IReadOnlyList<string> Diagnostics);

public sealed class TypeScriptCompilerOptions
{
    public TypeScriptModuleKind Module { get; set; } = TypeScriptModuleKind.CommonJS;

    public TypeScriptScriptTarget Target { get; set; } = TypeScriptScriptTarget.ES2020;

    public bool SourceMap { get; set; } = true;

    public bool InlineSources { get; set; } = true;

    public bool InlineSourceMap { get; set; }

    public bool RemoveComments { get; set; }

    public bool Strict { get; set; }

    public Dictionary<string, object?> ToDictionary()
    {
        var dict = new Dictionary<string, object?>
        {
            ["module"] = (int)Module,
            ["target"] = (int)Target,
            ["sourceMap"] = SourceMap,
            ["inlineSources"] = InlineSources,
            ["inlineSourceMap"] = InlineSourceMap,
            ["removeComments"] = RemoveComments,
            ["strict"] = Strict
        };

        return dict;
    }
}

public enum TypeScriptModuleKind
{
    None = 0,
    CommonJS = 1,
    AMD = 2,
    UMD = 3,
    System = 4,
    ES2015 = 5,
    ES2020 = 6,
    ESNext = 99
}

public enum TypeScriptScriptTarget
{
    ES3 = 0,
    ES5 = 1,
    ES2015 = 2,
    ES2016 = 3,
    ES2017 = 4,
    ES2018 = 5,
    ES2019 = 6,
    ES2020 = 7,
    ES2021 = 8,
    ES2022 = 9,
    ES2023 = 10,
    ESNext = 99
}
