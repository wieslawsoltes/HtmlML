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
    private sealed class SelectorSegment
    {
        public string? TypeToken;
        public string? Id;
        public List<string> Classes = new();
    }

    private sealed class Rule
    {
        public List<SelectorSegment> Segments = new();
        public List<(string name, string value)> Decls = new();
    }

    public static void ApplyCss(string css, Control root)
    {
        var rules = ParseCss(css);
        if (rules.Count == 0)
            return;

        foreach (var ctrl in Traverse(root))
        {
            foreach (var r in rules)
            {
                if (Matches(ctrl, r))
                {
                    ApplyDecls(ctrl, r.Decls);
                }
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
                        var segments = ParseSelectorSegments(sel);
                        if (segments is null || segments.Count == 0) continue;
                        var ruleObj = new Rule { Segments = segments };
                        foreach (var prop in sr.Style)
                        {
                            var name = prop.Name;
                            var val = sr.Style.GetPropertyValue(name) ?? string.Empty;
                            ruleObj.Decls.Add((name, val));
                        }
                        list.Add(ruleObj);
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

    private static List<SelectorSegment>? ParseSelectorSegments(string cssSelector)
    {
        var segments = new List<SelectorSegment>();
        int i = 0;
        while (i < cssSelector.Length)
        {
            // skip whitespace/combinators between segments
            while (i < cssSelector.Length && (char.IsWhiteSpace(cssSelector[i]) || cssSelector[i] == '>' || cssSelector[i] == '+' || cssSelector[i] == '~')) i++;
            if (i >= cssSelector.Length) break;
            int start = i;
            while (i < cssSelector.Length && !char.IsWhiteSpace(cssSelector[i]) && cssSelector[i] != '>' && cssSelector[i] != '+' && cssSelector[i] != '~' && cssSelector[i] != ',') i++;
            var part = cssSelector.Substring(start, i - start);

            string type = string.Empty;
            var classes = new List<string>();
            string id = string.Empty;
            int j = 0;
            while (j < part.Length)
            {
                char c = part[j];
                if (c == '.') { j++; var cls = ReadIdent(part, ref j); if (!string.IsNullOrWhiteSpace(cls)) classes.Add(cls); }
                else if (c == '#') { j++; id = ReadIdent(part, ref j); }
                else if (c == '[') { j++; while (j < part.Length && part[j] != ']') j++; if (j < part.Length) j++; }
                else if (c == ':') { j++; while (j < part.Length && (char.IsLetter(part[j]) || part[j] == '-')) j++; }
                else { type = ReadIdent(part, ref j); }
            }
            var token = MapTypeToAvalonia(type);
            if (string.IsNullOrEmpty(token) && classes.Count == 0 && string.IsNullOrEmpty(id))
                continue;
            segments.Add(new SelectorSegment { TypeToken = token, Id = id, Classes = classes });
        }
        return segments;
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

    private static bool Matches(Control c, Rule r)
    {
        if (r.Segments.Count == 0)
            return false;
        // Match last segment on current control
        if (!MatchesSimple(c, GetTypeToken(c), r.Segments[^1]))
            return false;
        // Walk ancestors for previous segments (descendant semantics)
        int si = r.Segments.Count - 2;
        var parent = c.Parent as Control;
        while (si >= 0 && parent is not null)
        {
            if (MatchesSimple(parent, GetTypeToken(parent), r.Segments[si]))
                si--;
            parent = parent.Parent as Control;
        }
        return si < 0;
    }

    private static bool MatchesSimple(Control c, string token, SelectorSegment seg)
    {
        if (!string.IsNullOrEmpty(seg.TypeToken) && !string.Equals(seg.TypeToken, token, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(seg.Id))
        {
            if (!string.Equals(c.Name, seg.Id, StringComparison.Ordinal)) return false;
        }
        foreach (var cls in seg.Classes)
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
                case "line-height":
                    if (c is TextBlock tbl && TryParseDouble(value, out var lh)) tbl.LineHeight = lh;
                    break;
                case "border":
                    ApplyBorder(c, value);
                    break;
                case "border-color":
                    if (c is TemplatedControl tcc && TryParseBrush(value, out var bb)) tcc.BorderBrush = bb;
                    break;
                case "border-width":
                    if (c is TemplatedControl tcw && TryParseThickness(value, out var bt)) tcw.BorderThickness = bt;
                    break;
                case "visibility":
                    ApplyVisibility(c, value);
                    break;
                case "display":
                    ApplyDisplay(c, value);
                    break;
            }
        }
    }

    private static void ApplyBorder(Control c, string value)
    {
        if (c is not TemplatedControl tc) return;
        // very simple: try to find a color and a width token
        if (TryParseBrush(value, out var brush)) tc.BorderBrush = brush;
        // extract first length
        var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (TryParseThickness(p, out var th)) { tc.BorderThickness = th; break; }
            if (TryParseDouble(p, out var d)) { tc.BorderThickness = new Thickness(d); break; }
        }
    }

    private static void ApplyVisibility(Control c, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v == "hidden")
        {
            c.Opacity = 0;
            c.IsHitTestVisible = false;
        }
        else if (v == "visible")
        {
            c.Opacity = 1;
            c.IsHitTestVisible = true;
        }
    }

    private static void ApplyDisplay(Control c, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v == "none")
        {
            c.Opacity = 0;
            c.IsHitTestVisible = false;
            c.Width = 0;
            c.Height = 0;
            c.Margin = new Thickness(0);
        }
        else
        {
            // best effort restore
            c.Opacity = 1;
            c.IsHitTestVisible = true;
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
