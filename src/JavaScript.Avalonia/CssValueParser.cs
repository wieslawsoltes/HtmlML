using System;
using Avalonia.Media;

namespace JavaScript.Avalonia;

internal static class CssValueParser
{
    internal static bool TryParseColor(string? value, out Color color)
    {
        if (CssColorParser.TryParseColor(value, out var portable))
        {
            color = Color.FromArgb(portable.A, portable.R, portable.G, portable.B);
            return true;
        }

        color = default;
        return false;
    }

    internal static bool TryParseFunctionalColor(string? value, out Color color)
        => TryParseColor(value, out color);

    internal static FontFamily ParseFirstFontFamily(string value)
        => CssFontResolver.Resolve(value).Family;
}
