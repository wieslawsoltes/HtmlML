using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HtmlML.Sdk;

public static class HtmlMlComponentCapabilities
{
    public const string Dom = "dom";
    public const string CssLayout = "css.layout";
    public const string Canvas2D = "canvas.2d";
    public const string Svg = "svg";
    public const string Pointer = "input.pointer";
    public const string Keyboard = "input.keyboard";
    public const string Focus = "input.focus";
    public const string Clipboard = "clipboard";
    public const string Commands = "host.commands";
    public const string Settings = "host.settings";
    public const string Notifications = "host.notifications";
    public const string Networking = "host.network";
    public const string HostClipboard = "host.clipboard";
    public const string FileSelection = "host.files";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Dom,
        CssLayout,
        Canvas2D,
        Svg,
        Pointer,
        Keyboard,
        Focus,
        Clipboard,
        Commands,
        Settings,
        Notifications,
        Networking,
        HostClipboard,
        FileSelection
    };
}

public sealed record HtmlMlComponentLifecycle
{
    public string MountExport { get; init; } = "mount";

    public string UnmountExport { get; init; } = "unmount";
}

/// <summary>Versioned declaration carried by every packaged HtmlML component.</summary>
public sealed record HtmlMlComponentManifest
{
    public const string CurrentSchemaVersion = "1.0";
    public const string CurrentProfileVersion = "1.0";

    public required string SchemaVersion { get; init; }

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    public required string ProfileVersion { get; init; }

    public required string EntryPoint { get; init; }

    public required string[] Assets { get; init; }

    public required string[] Capabilities { get; init; }

    public HtmlMlComponentLifecycle Lifecycle { get; init; } = new();
}

public sealed record HtmlMlComponentManifestValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new InvalidDataException("Invalid HtmlML component manifest: " + string.Join("; ", Errors));
        }
    }
}

public static partial class HtmlMlComponentManifestSerializer
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static HtmlMlComponentManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize<HtmlMlComponentManifest>(json, s_jsonOptions)
                       ?? throw new InvalidDataException("The component manifest was empty.");
        Validate(manifest).ThrowIfInvalid();
        return manifest;
    }

    public static HtmlMlComponentManifest Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var manifest = JsonSerializer.Deserialize<HtmlMlComponentManifest>(stream, s_jsonOptions)
                       ?? throw new InvalidDataException("The component manifest was empty.");
        Validate(manifest).ThrowIfInvalid();
        return manifest;
    }

    public static string Serialize(HtmlMlComponentManifest manifest)
    {
        Validate(manifest).ThrowIfInvalid();
        return JsonSerializer.Serialize(manifest, s_jsonOptions);
    }

    public static HtmlMlComponentManifestValidationResult Validate(HtmlMlComponentManifest? manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            errors.Add("Manifest is required.");
            return new HtmlMlComponentManifestValidationResult(errors);
        }

        RequireExact(manifest.SchemaVersion, HtmlMlComponentManifest.CurrentSchemaVersion, "schemaVersion", errors);
        RequireExact(manifest.ProfileVersion, HtmlMlComponentManifest.CurrentProfileVersion, "profileVersion", errors);
        RequireMatch(manifest.Id, ComponentIdPattern(), "id must be a lowercase reverse-domain identifier.", errors);
        RequireValue(manifest.DisplayName, "displayName", errors);
        RequireMatch(manifest.Version, SemanticVersionPattern(), "version must be a semantic version.", errors);
        ValidateRelativePath(manifest.EntryPoint, "entryPoint", errors);

        ValidateUniqueValues(manifest.Assets, "assets", ValidateRelativePath, errors);
        if (manifest.Assets is not null
            && !string.IsNullOrWhiteSpace(manifest.EntryPoint)
            && !manifest.Assets.Contains(manifest.EntryPoint, StringComparer.Ordinal))
        {
            errors.Add("assets must include entryPoint.");
        }

        ValidateUniqueValues(manifest.Capabilities, "capabilities", (value, property, target) =>
        {
            if (!HtmlMlComponentCapabilities.Known.Contains(value))
            {
                target.Add($"Unknown capability '{value}'.");
            }
        }, errors);

        if (manifest.Lifecycle is null)
        {
            errors.Add("lifecycle is required.");
        }
        else
        {
            RequireMatch(manifest.Lifecycle.MountExport, JavaScriptIdentifierPattern(), "lifecycle.mountExport must be a JavaScript identifier.", errors);
            RequireMatch(manifest.Lifecycle.UnmountExport, JavaScriptIdentifierPattern(), "lifecycle.unmountExport must be a JavaScript identifier.", errors);
        }

        return new HtmlMlComponentManifestValidationResult(errors);
    }

    private static void RequireExact(string? value, string expected, string property, ICollection<string> errors)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            errors.Add($"{property} must be '{expected}'.");
        }
    }

    private static void RequireValue(string? value, string property, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{property} is required.");
        }
    }

    private static void RequireMatch(string? value, Regex regex, string message, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !regex.IsMatch(value))
        {
            errors.Add(message);
        }
    }

    private static void ValidateUniqueValues(
        string[]? values,
        string property,
        Action<string, string, ICollection<string>> validate,
        ICollection<string> errors)
    {
        if (values is null || values.Length == 0)
        {
            errors.Add($"{property} must contain at least one value.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{property} values must not be empty.");
            }
            else if (!seen.Add(value))
            {
                errors.Add($"{property} contains duplicate '{value}'.");
            }
            else
            {
                validate(value, property, errors);
            }
        }
    }

    private static void ValidateRelativePath(string? value, string property, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            errors.Add($"{property} must be a normalized relative package path.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentIdPattern();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptIdentifierPattern();
}
