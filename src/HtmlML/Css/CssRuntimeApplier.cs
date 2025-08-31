using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;

namespace HtmlML;

internal static class CssRuntimeApplier
{
    private sealed class Rule
    {
        public string? TypeToken; // Avalonia target token like "TextBlock", "DockPanel"
        public string? Id;
        public List<string> Classes = new();
        public List<(string name, string value)> Decls = new();
    }

    public static void ApplyCss(string css, Control root)
    {
        var rules = ParseCss(css);
        if (rules.Count == 0)
            return;

        foreach (var ctrl in Traverse(root))
        {
            var token = GetTypeToken(ctrl);
            foreach (var r in rules)
            {
                if (!Matches(ctrl, token, r))
                    continue;
                ApplyDecls(ctrl, r.Decls);
            }
        }
    }

    private static List<Rule> ParseCss(string css)
    {
        var list = new List<Rule>();
        try
        {
            var parser = new CssParser();
            var sheet = parser.ParseStyleSheet(css);
            if (sheet is null) return list;
            foreach (var rule in sheet.Rules)
            {
                if (rule is ICssStyleRule sr)
                {
                    var selectors = sr.SelectorText.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
                    foreach (var sel in selectors)
                    {
                        var rs = ParseSelector(sel);
                        if (rs is null) continue;
                        foreach (var prop in sr.Style)
                        {
                            var name = prop.Name;
                            var val = sr.Style.GetPropertyValue(name) ?? string.Empty;
                            rs.Decls.Add((name, val));
                        }
                        list.Add(rs);
                    }
                }
            }
        }
        catch
        {
            // ignore parse errors
        }
        return list;
    }

    private static Rule? ParseSelector(string cssSelector)
    {
        // Only support first simple selector (ignore descendants/combinators)
        var firstSpace = cssSelector.IndexOfAny(new[] { ' ', '>', '+', '~' });
        if (firstSpace >= 0)
            cssSelector = cssSelector.Substring(0, firstSpace);

        string type = string.Empty;
        var classes = new List<string>();
        string id = string.Empty;

        int i = 0;
        while (i < cssSelector.Length)
        {
            char c = cssSelector[i];
            if (c == '.')
            {
                i++;
                var cls = ReadIdent(cssSelector, ref i);
                if (!string.IsNullOrWhiteSpace(cls)) classes.Add(cls);
            }
            else if (c == '#')
            {
                i++;
                id = ReadIdent(cssSelector, ref i);
            }
            else if (c == '[')
            {
                // skip attribute selector
                i++;
                while (i < cssSelector.Length && cssSelector[i] != ']') i++;
                if (i < cssSelector.Length) i++;
            }
            else if (c == ':')
            {
                // skip pseudo
                i++;
                while (i < cssSelector.Length && (char.IsLetter(cssSelector[i]) || cssSelector[i] == '-')) i++;
            }
            else
            {
                type = ReadIdent(cssSelector, ref i);
            }
        }

        var token = MapTypeToAvalonia(type);
        if (string.IsNullOrEmpty(token) && classes.Count == 0 && string.IsNullOrEmpty(id))
            return null;
        return new Rule { TypeToken = token, Id = id, Classes = classes };
    }

    private static string ReadIdent(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-')) i++;
        return s.Substring(start, i - start);
    }

    private static IEnumerable<Control> Traverse(Control root)
    {
        yield return root;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Control c)
                {
                    foreach (var x in Traverse(c)) yield return x;
                }
            }
        }
        else if (root is ContentControl cc && cc.Content is Control qc)
        {
            foreach (var x in Traverse(qc)) yield return x;
        }
    }

    private static string GetTypeToken(Control c)
    {
        // Return semantic html-like tokens for known text elements
        var n = c.GetType().Name;
        if (n is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p")
            return n;
        if (c is TextBlock) return "TextBlock";
        if (c is DockPanel) return "DockPanel";
        if (c is StackPanel) return "StackPanel";
        if (c is Image) return "Image";
        if (c is Separator) return "Separator";
        return c.GetType().Name;
    }

    private static bool Matches(Control c, string token, Rule r)
    {
        if (!string.IsNullOrEmpty(r.TypeToken) && !string.Equals(r.TypeToken, token, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(r.Id))
        {
            if (!string.Equals(c.Name, r.Id, StringComparison.Ordinal)) return false;
        }
        foreach (var cls in r.Classes)
        {
            if (!c.Classes.Contains(cls)) return false;
        }
        return true;
    }

    private static void ApplyDecls(Control c, List<(string name, string value)> decls)
    {
        foreach (var (name, value) in decls)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "color":
                    if (c is TextBlock tb && TryParseBrush(value, out var fg)) tb.Foreground = fg;
                    break;
                case "background":
                case "background-color":
                    if (TryParseBrush(value, out var bg))
                    {
                        if (c is Panel p) p.Background = bg;
                        else if (c is TemplatedControl tc) tc.Background = bg;
                        else if (c is canvas canv) canv.Background = bg;
                    }
                    break;
                case "font-size":
                    if (c is TextBlock tbf && TryParseDouble(value, out var fs)) tbf.FontSize = fs;
                    break;
                case "font-weight":
                    if (c is TextBlock tbw && TryParseFontWeight(value, out var fw)) tbw.FontWeight = fw;
                    break;
                case "text-align":
                    if (c is TextBlock tba && TryParseTextAlign(value, out var ta)) tba.TextAlignment = ta;
                    break;
                case "margin":
                    if (TryParseThickness(value, out var m)) c.Margin = m;
                    break;
                case "padding":
                    if (c is ContentControl contentCtrl && TryParseThickness(value, out var pad)) contentCtrl.Padding = pad;
                    break;
                case "width":
                    if (TryParseDouble(value, out var w)) c.Width = w;
                    break;
                case "height":
                    if (TryParseDouble(value, out var h)) c.Height = h;
                    break;
            }
        }
    }

    private static string MapTypeToAvalonia(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;
        switch (type.ToLowerInvariant())
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
            case "p":
                return type.ToLowerInvariant();
            case "span":
            case "strong":
            case "em":
            case "code":
            case "a":
                return "TextElement"; // will match nothing; combined with class-only selectors still work
            case "div":
            case "body":
            case "li":
                return "DockPanel";
            case "ul":
            case "ol":
            case "section":
            case "header":
            case "footer":
            case "nav":
            case "article":
            case "aside":
                return "StackPanel";
            case "img": return "Image";
            case "hr": return "Separator";
            default: return type;
        }
    }

    private static bool TryParseBrush(string s, out IBrush brush)
    {
        try { brush = Brush.Parse(s); return true; } catch { brush = Brushes.Transparent; return false; }
    }

    private static bool TryParseDouble(string s, out double d)
    {
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    private static bool TryParseFontWeight(string s, out Avalonia.Media.FontWeight fw)
    {
        s = s.Trim().ToLowerInvariant();
        switch (s)
        {
            case "normal": fw = Avalonia.Media.FontWeight.Normal; return true;
            case "bold": fw = Avalonia.Media.FontWeight.Bold; return true;
            case "bolder": fw = Avalonia.Media.FontWeight.Bold; return true;
            case "lighter": fw = Avalonia.Media.FontWeight.Light; return true;
            case "100": fw = Avalonia.Media.FontWeight.Thin; return true;
            case "200": fw = Avalonia.Media.FontWeight.ExtraLight; return true;
            case "300": fw = Avalonia.Media.FontWeight.Light; return true;
            case "400": fw = Avalonia.Media.FontWeight.Normal; return true;
            case "500": fw = Avalonia.Media.FontWeight.Medium; return true;
            case "600": fw = Avalonia.Media.FontWeight.SemiBold; return true;
            case "700": fw = Avalonia.Media.FontWeight.Bold; return true;
            case "800": fw = Avalonia.Media.FontWeight.ExtraBold; return true;
            case "900": fw = Avalonia.Media.FontWeight.Black; return true;
            default:
                fw = Avalonia.Media.FontWeight.Normal; return false;
        }
    }

    private static bool TryParseTextAlign(string s, out TextAlignment ta)
    {
        s = s.Trim().ToLowerInvariant();
        switch (s)
        {
            case "left": ta = TextAlignment.Left; return true;
            case "center": ta = TextAlignment.Center; return true;
            case "right": ta = TextAlignment.Right; return true;
            case "justify": ta = TextAlignment.Justify; return true;
            default: ta = TextAlignment.Left; return false;
        }
    }

    private static bool TryParseThickness(string s, out Thickness th)
    {
        var parts = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => NormalizeNumber(x))
                     .ToArray();
        string t, r, b, l;
        switch (parts.Length)
        {
            case 1: t = r = b = l = parts[0]; break;
            case 2: t = b = parts[0]; r = l = parts[1]; break;
            case 3: t = parts[0]; r = l = parts[1]; b = parts[2]; break;
            default:
                t = parts.ElementAtOrDefault(0) ?? "0";
                r = parts.ElementAtOrDefault(1) ?? "0";
                b = parts.ElementAtOrDefault(2) ?? "0";
                l = parts.ElementAtOrDefault(3) ?? "0";
                break;
        }
        th = new Thickness(ParseDouble(l), ParseDouble(t), ParseDouble(r), ParseDouble(b));
        return true;
    }

    private static string NormalizeNumber(string s)
    {
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return s;
    }
    private static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
