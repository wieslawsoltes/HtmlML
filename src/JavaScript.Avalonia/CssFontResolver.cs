using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace JavaScript.Avalonia;

internal enum CssFontMetricProfile
{
    None,
    MacSystemUi
}

internal readonly record struct CssFontResolution(
    FontFamily Family,
    CssFontMetricProfile MetricProfile)
{
    internal double ResolveWidthScale(double fontSize, FontWeight fontWeight)
    {
        if (MetricProfile != CssFontMetricProfile.MacSystemUi || !OperatingSystem.IsMacOS())
        {
            return 1d;
        }

        // Blink's -apple-system face and Avalonia's macOS default Helvetica
        // have slightly different advances. The difference shrinks with size
        // and weight, so one family-wide multiplier overcorrects semibold UI
        // labels. This bounded platform profile is shared by DOM and Canvas.
        var size = Math.Clamp(fontSize, 8d, 24d);
        var weight = Math.Clamp((int)fontWeight, 100, 900);
        return Math.Clamp(
            1.0222d + (16d - size) * 0.0062d - (weight - 400d) * 0.000133d,
            0.96d,
            1.08d);
    }
}

/// <summary>
/// Resolves a CSS font-family list to the first usable document face or system
/// family. Both DOM text and Canvas2D use this path so their glyph advances stay
/// identical.
/// </summary>
internal static class CssFontResolver
{
    private const int MaximumCachedResolutions = 256;
    private static readonly object s_cacheGate = new();
    private static readonly Dictionary<string, CssFontResolution> s_cache =
        new(StringComparer.Ordinal);

    internal static CssFontResolution Resolve(
        string? familyList,
        CssFontFaceRegistry? registry = null,
        FontStyle? style = null,
        FontWeight? weight = null,
        FontStretch? stretch = null)
    {
        if (registry?.Resolve(
                familyList,
                style ?? FontStyle.Normal,
                weight ?? FontWeight.Normal,
                stretch ?? FontStretch.Normal) is { } downloaded)
        {
            return downloaded;
        }

        var key = familyList?.Trim() ?? string.Empty;
        lock (s_cacheGate)
        {
            if (s_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        // FontManager is owned by the initialized Avalonia application. Do not
        // touch it from a static initializer: headless and desktop hosts install
        // their platform font managers at different points in startup.
        var resolved = ResolveUncached(key);
        lock (s_cacheGate)
        {
            if (s_cache.Count >= MaximumCachedResolutions)
            {
                s_cache.Clear();
            }
            s_cache[key] = resolved;
        }
        return resolved;
    }

    internal static IReadOnlyList<string> ParseFamilyList(string? familyList)
    {
        if (string.IsNullOrWhiteSpace(familyList))
        {
            return Array.Empty<string>();
        }

        var families = new List<string>();
        var start = 0;
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < familyList.Length; index++)
        {
            var current = familyList[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (current == '\\' && quote != '\0')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == ',')
            {
                AddFamily(familyList[start..index], families);
                start = index + 1;
            }
        }
        AddFamily(familyList[start..], families);
        return families;
    }

    // A future @font-face registry must call this when its available family set
    // changes. Keeping that invalidation seam here avoids stale system fallbacks.
    internal static void ClearCache()
    {
        lock (s_cacheGate)
        {
            s_cache.Clear();
        }
    }

    private static CssFontResolution ResolveUncached(string familyList)
    {
        var fallback = FontManager.Current.DefaultFontFamily;
        foreach (var requested in ParseFamilyList(familyList))
        {
            if (IsAppleSystemAlias(requested))
            {
                if (OperatingSystem.IsMacOS())
                {
                    // Chromium's -apple-system advances are wider than the
                    // CoreText metrics Avalonia exposes for its private default
                    // face. This existing calibration is shared with DOM text.
                    return new CssFontResolution(fallback, CssFontMetricProfile.MacSystemUi);
                }
                continue;
            }

            if (string.Equals(requested, "system-ui", StringComparison.OrdinalIgnoreCase))
            {
                return new CssFontResolution(fallback, CssFontMetricProfile.MacSystemUi);
            }

            foreach (var installed in FontManager.Current.SystemFonts)
            {
                if (string.Equals(installed.Name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return new CssFontResolution(installed, CssFontMetricProfile.None);
                }
            }

            if (IsGenericFamily(requested))
            {
                return new CssFontResolution(fallback, CssFontMetricProfile.None);
            }
        }

        return new CssFontResolution(fallback, CssFontMetricProfile.None);
    }

    private static bool IsAppleSystemAlias(string value)
        => string.Equals(value, "-apple-system", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "BlinkMacSystemFont", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericFamily(string value)
        => value.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)
           || value.Equals("ui-sans-serif", StringComparison.OrdinalIgnoreCase)
           || value.Equals("serif", StringComparison.OrdinalIgnoreCase)
           || value.Equals("ui-serif", StringComparison.OrdinalIgnoreCase)
           || value.Equals("monospace", StringComparison.OrdinalIgnoreCase)
           || value.Equals("ui-monospace", StringComparison.OrdinalIgnoreCase)
           || value.Equals("cursive", StringComparison.OrdinalIgnoreCase)
           || value.Equals("fantasy", StringComparison.OrdinalIgnoreCase);

    private static void AddFamily(string value, ICollection<string> families)
    {
        var family = value.Trim();
        if (family.Length >= 2
            && ((family[0] == '"' && family[^1] == '"')
                || (family[0] == '\'' && family[^1] == '\'')))
        {
            family = family[1..^1].Trim();
        }
        if (family.Length > 0)
        {
            families.Add(family);
        }
    }
}
