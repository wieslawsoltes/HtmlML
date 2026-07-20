using System.Text.RegularExpressions;

namespace HtmlML.Sdk;

public enum HtmlMlCompatibilitySeverity
{
    Warning,
    Error
}

public sealed record HtmlMlCompatibilityDiagnostic(
    string Code,
    HtmlMlCompatibilitySeverity Severity,
    string Message,
    string Source,
    int Line,
    int Column,
    string? RequiredCapability = null);

public sealed record HtmlMlCompatibilityReport(IReadOnlyList<HtmlMlCompatibilityDiagnostic> Diagnostics)
{
    public bool IsCompatible => Diagnostics.All(static diagnostic => diagnostic.Severity != HtmlMlCompatibilitySeverity.Error);
}

/// <summary>Static preflight for the deliberately bounded HtmlML Component Profile 1 surface.</summary>
public static partial class HtmlMlCompatibilityChecker
{
    private sealed record Rule(
        Regex Pattern,
        string Code,
        HtmlMlCompatibilitySeverity Severity,
        string Message,
        string? Capability = null);

    private static readonly Rule[] s_rules =
    [
        Unsupported(@"\bnavigator\s*\.\s*serviceWorker\b", "HTMLML1001", "Service workers are not supported."),
        Unsupported(@"\b(?:localStorage|sessionStorage|indexedDB)\b", "HTMLML1002", "Browser storage is not supported; request host.settings instead."),
        Unsupported(@"\b(?:Worker|SharedWorker|Worklet)\s*\(", "HTMLML1003", "Web workers and worklets are not supported."),
        Unsupported(@"\b(?:RTCPeerConnection|MediaRecorder|AudioContext|webkitAudioContext)\b", "HTMLML1004", "WebRTC, recording, and Web Audio are not supported."),
        Unsupported(@"\bnavigator\s*\.\s*(?:mediaDevices|geolocation)\b", "HTMLML1005", "Media devices and geolocation are not supported."),
        Unsupported(@"\bwindow\s*\.\s*open\s*\(", "HTMLML1006", "Arbitrary browser windows and navigation are not supported."),
        Requires(@"\bnavigator\s*\.\s*clipboard\b", "HTMLML2001", HtmlMlComponentCapabilities.Clipboard, "Clipboard access must be declared."),
        Requires(@"\bhtmlml\s*\.\s*host\s*\.\s*commands\b", "HTMLML2002", HtmlMlComponentCapabilities.Commands, "Host commands must be declared."),
        Requires(@"\bhtmlml\s*\.\s*host\s*\.\s*settings\b", "HTMLML2003", HtmlMlComponentCapabilities.Settings, "Host settings must be declared."),
        Requires(@"\bhtmlml\s*\.\s*host\s*\.\s*notifications\b", "HTMLML2004", HtmlMlComponentCapabilities.Notifications, "Host notifications must be declared."),
        Requires(@"\bhtmlml\s*\.\s*host\s*\.\s*network\b", "HTMLML2005", HtmlMlComponentCapabilities.Networking, "Host networking must be declared."),
        Requires(@"\bhtmlml\s*\.\s*host\s*\.\s*clipboard\b", "HTMLML2006", HtmlMlComponentCapabilities.HostClipboard, "Host clipboard access must be declared."),
        Requires(@"\bhtmlml\s*\.\s*host\s*\.\s*files\b", "HTMLML2007", HtmlMlComponentCapabilities.FileSelection, "Host file selection must be declared."),
        new Rule(GeneratedNetworkPattern(), "HTMLML3001", HtmlMlCompatibilitySeverity.Warning, "Direct networking bypasses host policy; prefer htmlml.host.network.")
    ];

    public static HtmlMlCompatibilityReport Check(
        string source,
        HtmlMlComponentManifest manifest,
        string sourceName = "<source>")
    {
        ArgumentNullException.ThrowIfNull(source);
        HtmlMlComponentManifestSerializer.Validate(manifest).ThrowIfInvalid();
        var diagnostics = new List<HtmlMlCompatibilityDiagnostic>();
        var searchable = MaskCommentsAndStrings(source);
        foreach (var rule in s_rules)
        {
            foreach (Match match in rule.Pattern.Matches(searchable))
            {
                if (rule.Capability is not null
                    && manifest.Capabilities.Contains(rule.Capability, StringComparer.Ordinal))
                {
                    continue;
                }
                var (line, column) = GetLocation(source, match.Index);
                diagnostics.Add(new HtmlMlCompatibilityDiagnostic(
                    rule.Code,
                    rule.Severity,
                    rule.Capability is null ? rule.Message : $"{rule.Message} Missing capability '{rule.Capability}'.",
                    sourceName,
                    line,
                    column,
                    rule.Capability));
            }
        }
        return new HtmlMlCompatibilityReport(diagnostics);
    }

    public static HtmlMlCompatibilityReport CheckFiles(
        IEnumerable<string> sourceFiles,
        HtmlMlComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var diagnostics = sourceFiles
            .SelectMany(path => Check(File.ReadAllText(path), manifest, path).Diagnostics)
            .ToArray();
        return new HtmlMlCompatibilityReport(diagnostics);
    }

    private static Rule Unsupported(string pattern, string code, string message)
        => new(new Regex(pattern, RegexOptions.CultureInvariant), code, HtmlMlCompatibilitySeverity.Error, message);

    private static Rule Requires(string pattern, string code, string capability, string message)
        => new(new Regex(pattern, RegexOptions.CultureInvariant), code, HtmlMlCompatibilitySeverity.Error, message, capability);

    private static string MaskCommentsAndStrings(string source)
    {
        var chars = source.ToCharArray();
        foreach (Match match in CommentsAndStringsPattern().Matches(source))
        {
            for (var index = match.Index; index < match.Index + match.Length; index++)
            {
                if (chars[index] is not ('\r' or '\n'))
                {
                    chars[index] = ' ';
                }
            }
        }
        return new string(chars);
    }

    private static (int Line, int Column) GetLocation(string source, int index)
    {
        var line = 1;
        var column = 1;
        for (var offset = 0; offset < index; offset++)
        {
            if (source[offset] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
    }

    [GeneratedRegex(@"\b(?:fetch|WebSocket|XMLHttpRequest)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedNetworkPattern();

    [GeneratedRegex("""//[^\r\n]*|/\*[\s\S]*?\*/|'(?:\\.|[^'\\])*'|"(?:\\.|[^"\\])*"|`(?:\\.|[^`\\])*`""", RegexOptions.CultureInvariant)]
    private static partial Regex CommentsAndStringsPattern();
}
