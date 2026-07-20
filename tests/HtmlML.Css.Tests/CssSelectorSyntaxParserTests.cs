using Xunit;

namespace HtmlML.Css.Tests;

public sealed class CssSelectorSyntaxParserTests
{
    [Fact]
    public void ParsesComplexSelectorAndSpecificity()
    {
        Assert.True(CssSelectorSyntaxParser.TryParse(
            "section#chart > .toolbar button[data-mode='draw' i]:hover::before",
            out var selector));

        Assert.Equal(3, selector.Parts.Count);
        Assert.Equal(133, selector.Specificity);
        Assert.Equal(CssSelectorCombinator.Child, selector.Parts[1].CombinatorToPrevious);
        Assert.Equal(CssSelectorCombinator.Descendant, selector.Parts[2].CombinatorToPrevious);
        Assert.True(selector.Parts[2].Simple.Attributes[0].CaseInsensitive);
        Assert.True(selector.Parts[2].Simple.Pseudos[^1].IsElement);
    }

    [Fact]
    public void SplitSelectorListIgnoresNestedCommas()
    {
        var selectors = CssSelectorSyntaxParser.SplitSelectorList(
            ".a:is(.b, .c), [data-value='x,y'], div").ToArray();

        Assert.Equal(new[] { ".a:is(.b, .c)", "[data-value='x,y']", "div" }, selectors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("div|")]
    [InlineData("::before::after")]
    public void RejectsUnsupportedOrInvalidSelectors(string value)
        => Assert.False(CssSelectorSyntaxParser.TryParse(value, out _));
}
