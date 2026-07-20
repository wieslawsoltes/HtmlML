using System.Text;

namespace HtmlML.Css;

public enum CssSelectorCombinator
{
    None,
    Descendant,
    Child,
    AdjacentSibling,
    GeneralSibling
}

public sealed record CssAttributeSelectorSyntax(
    string Name,
    string? Operator,
    string? Value,
    bool CaseInsensitive);

public sealed class CssPseudoSelectorSyntax
{
    private CssSelectorSyntax[]? _argumentSelectors;

    public CssPseudoSelectorSyntax(string name, string? argument, bool isElement)
    {
        Name = name;
        Argument = argument;
        IsElement = isElement;
    }

    public string Name { get; }

    public string? Argument { get; }

    public bool IsElement { get; }

    internal IReadOnlyList<CssSelectorSyntax> GetArgumentSelectors()
    {
        if (_argumentSelectors is not null)
        {
            return _argumentSelectors;
        }

        if (string.IsNullOrWhiteSpace(Argument))
        {
            return _argumentSelectors = [];
        }

        var selectors = new List<CssSelectorSyntax>();
        foreach (var selectorText in CssSelectorSyntaxParser.SplitSelectorList(Argument))
        {
            if (CssSelectorSyntaxParser.TryParse(selectorText, out var selector))
            {
                selectors.Add(selector);
            }
        }
        return _argumentSelectors = selectors.ToArray();
    }
}

public sealed class CssSimpleSelectorSyntax
{
    public string? Tag { get; init; }

    public string? Id { get; init; }

    public List<string> Classes { get; } = [];

    public List<CssAttributeSelectorSyntax> Attributes { get; } = [];

    public List<CssPseudoSelectorSyntax> Pseudos { get; } = [];
}

public sealed record CssSelectorPartSyntax(
    CssSimpleSelectorSyntax Simple,
    CssSelectorCombinator CombinatorToPrevious);

public sealed record CssSelectorSyntax(IReadOnlyList<CssSelectorPartSyntax> Parts, int Specificity);

public static class CssSelectorSyntaxParser
{
    public static IEnumerable<string> SplitSelectorList(string selectorText)
    {
        ArgumentNullException.ThrowIfNull(selectorText);
        var start = 0;
        var square = 0;
        var round = 0;
        char quote = '\0';
        for (var index = 0; index < selectorText.Length; index++)
        {
            var character = selectorText[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || selectorText[index - 1] != '\\'))
                {
                    quote = '\0';
                }
                continue;
            }

            if (character is '\'' or '"') quote = character;
            else if (character == '[') square++;
            else if (character == ']') square--;
            else if (character == '(') round++;
            else if (character == ')') round--;
            else if (character == ',' && square == 0 && round == 0)
            {
                var part = selectorText[start..index].Trim();
                if (part.Length > 0) yield return part;
                start = index + 1;
            }
        }

        var last = selectorText[start..].Trim();
        if (last.Length > 0) yield return last;
    }

    public static bool TryParse(string text, out CssSelectorSyntax selector)
    {
        selector = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            return false;
        }

        var parts = new List<CssSelectorPartSyntax>();
        var pending = CssSelectorCombinator.None;
        var specificity = 0;
        foreach (var token in tokens)
        {
            if (token.IsCombinator)
            {
                pending = token.Combinator;
                continue;
            }

            if (!TryParseSimple(token.Text, out var simple, out var simpleSpecificity))
            {
                return false;
            }

            parts.Add(new CssSelectorPartSyntax(
                simple,
                parts.Count == 0
                    ? CssSelectorCombinator.None
                    : pending == CssSelectorCombinator.None
                        ? CssSelectorCombinator.Descendant
                        : pending));
            specificity += simpleSpecificity;
            pending = CssSelectorCombinator.None;
        }

        if (parts.Count == 0)
        {
            return false;
        }

        selector = new CssSelectorSyntax(parts, specificity);
        return true;
    }

    private static List<SelectorToken> Tokenize(string text)
    {
        var result = new List<SelectorToken>();
        var current = new StringBuilder();
        var square = 0;
        var round = 0;
        char quote = '\0';
        var pendingWhitespace = false;

        void Flush()
        {
            if (current.Length == 0) return;
            result.Add(SelectorToken.Simple(current.ToString()));
            current.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                current.Append(character);
                if (character == quote && (index == 0 || text[index - 1] != '\\')) quote = '\0';
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                current.Append(character);
            }
            else if (character == '[') { square++; current.Append(character); }
            else if (character == ']') { square--; current.Append(character); }
            else if (character == '(') { round++; current.Append(character); }
            else if (character == ')') { round--; current.Append(character); }
            else if (square == 0 && round == 0 && char.IsWhiteSpace(character))
            {
                Flush();
                pendingWhitespace = result.Count > 0 && !result[^1].IsCombinator;
            }
            else if (square == 0 && round == 0 && character is '>' or '+' or '~')
            {
                Flush();
                if (result.Count > 0 && result[^1].IsCombinator) result.RemoveAt(result.Count - 1);
                result.Add(SelectorToken.ForCombinator(character switch
                {
                    '>' => CssSelectorCombinator.Child,
                    '+' => CssSelectorCombinator.AdjacentSibling,
                    _ => CssSelectorCombinator.GeneralSibling
                }));
                pendingWhitespace = false;
            }
            else
            {
                if (pendingWhitespace)
                {
                    result.Add(SelectorToken.ForCombinator(CssSelectorCombinator.Descendant));
                    pendingWhitespace = false;
                }
                current.Append(character);
            }
        }

        Flush();
        return result;
    }

    private static bool TryParseSimple(string text, out CssSimpleSelectorSyntax simple, out int specificity)
    {
        simple = new CssSimpleSelectorSyntax();
        specificity = 0;
        var index = 0;
        if (index < text.Length && (text[index] == '*' || IsIdentifierStart(text[index])))
        {
            var tag = text[index] == '*' ? "*" : ReadIdentifier(text, ref index);
            if (tag == "*") index++;
            simple = new CssSimpleSelectorSyntax { Tag = tag };
            if (tag != "*") specificity++;
        }

        while (index < text.Length)
        {
            switch (text[index])
            {
                case '#':
                    index++;
                    simple = Clone(simple, id: ReadIdentifier(text, ref index));
                    specificity += 100;
                    break;
                case '.':
                    index++;
                    simple.Classes.Add(ReadIdentifier(text, ref index));
                    specificity += 10;
                    break;
                case '[':
                    var attributeText = ReadBalanced(text, ref index, '[', ']');
                    if (!TryParseAttribute(attributeText, out var attribute)) return false;
                    simple.Attributes.Add(attribute);
                    specificity += 10;
                    break;
                case ':':
                    index++;
                    var isElement = index < text.Length && text[index] == ':';
                    if (isElement) index++;
                    var name = ReadIdentifier(text, ref index).ToLowerInvariant();
                    if (name.Length == 0) return false;
                    isElement |= name is "before" or "after";
                    string? argument = null;
                    if (index < text.Length && text[index] == '(')
                    {
                        argument = ReadBalanced(text, ref index, '(', ')');
                    }
                    if (isElement && simple.Pseudos.Any(static pseudo => pseudo.IsElement)) return false;
                    simple.Pseudos.Add(new CssPseudoSelectorSyntax(name, argument, isElement));
                    specificity += isElement ? 1 : name == "where" ? 0 : 10;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static CssSimpleSelectorSyntax Clone(CssSimpleSelectorSyntax source, string? id)
    {
        var clone = new CssSimpleSelectorSyntax { Tag = source.Tag, Id = id ?? source.Id };
        clone.Classes.AddRange(source.Classes);
        clone.Attributes.AddRange(source.Attributes);
        clone.Pseudos.AddRange(source.Pseudos);
        return clone;
    }

    private static bool TryParseAttribute(string content, out CssAttributeSelectorSyntax attribute)
    {
        attribute = null!;
        content = content.Trim();
        var caseInsensitive = content.EndsWith(" i", StringComparison.OrdinalIgnoreCase);
        if (caseInsensitive) content = content[..^2].TrimEnd();
        string? operation = null;
        var operationIndex = -1;
        foreach (var candidate in new[] { "~=", "|=", "^=", "$=", "*=", "=" })
        {
            operationIndex = content.IndexOf(candidate, StringComparison.Ordinal);
            if (operationIndex >= 0)
            {
                operation = candidate;
                break;
            }
        }

        var name = (operationIndex >= 0 ? content[..operationIndex] : content).Trim();
        if (name.Length == 0) return false;
        var value = operationIndex >= 0
            ? content[(operationIndex + operation!.Length)..].Trim().Trim('\'', '"')
            : null;
        attribute = new CssAttributeSelectorSyntax(name, operation, value, caseInsensitive);
        return true;
    }

    private static string ReadBalanced(string text, ref int index, char open, char close)
    {
        index++;
        var start = index;
        var depth = 1;
        char quote = '\0';
        while (index < text.Length)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote && text[index - 1] != '\\') quote = '\0';
            }
            else if (character is '\'' or '"') quote = character;
            else if (character == open) depth++;
            else if (character == close && --depth == 0)
            {
                var result = text[start..index];
                index++;
                return result;
            }
            index++;
        }
        return text[start..];
    }

    private static string ReadIdentifier(string text, ref int index)
    {
        var builder = new StringBuilder();
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\\' && index + 1 < text.Length)
            {
                builder.Append(text[index + 1]);
                index += 2;
            }
            else if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
                index++;
            }
            else
            {
                break;
            }
        }
        return builder.ToString();
    }

    private static bool IsIdentifierStart(char character) => char.IsLetter(character) || character is '-' or '_';

    private readonly record struct SelectorToken(string Text, bool IsCombinator, CssSelectorCombinator Combinator)
    {
        public static SelectorToken Simple(string text) => new(text, false, CssSelectorCombinator.None);

        public static SelectorToken ForCombinator(CssSelectorCombinator combinator) => new(string.Empty, true, combinator);
    }
}
