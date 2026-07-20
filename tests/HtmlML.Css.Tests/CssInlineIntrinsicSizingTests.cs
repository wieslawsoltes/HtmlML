using HtmlML.Core;
using HtmlML.Css;
using Xunit;

namespace HtmlML.Css.Tests;

public sealed class CssInlineIntrinsicSizingTests
{
    [Fact]
    public void AutoInlineFlexShrinksPreferredWidthToContainingBlockAndResolvesPercentageChild()
    {
        var root = new CssLayoutNode(1, new CssLayoutStyle
        {
            Display = CssLayoutDisplay.Block
        });
        var inlineFlex = new CssLayoutNode(2, new CssLayoutStyle
        {
            Display = CssLayoutDisplay.InlineFlex
        })
        {
            IntrinsicSize = new HtmlMlSize(160, 28)
        };
        var middleSlot = new CssLayoutNode(3, new CssLayoutStyle
        {
            Display = CssLayoutDisplay.Flex,
            FlexGrow = 1,
            FlexShrink = 1,
            OverflowX = CssLayoutOverflow.Hidden
        })
        {
            IntrinsicSize = new HtmlMlSize(160, 28)
        };
        var input = new CssLayoutNode(4, new CssLayoutStyle
        {
            Display = CssLayoutDisplay.Block,
            Width = CssLayoutLength.Percent(100),
            Height = CssLayoutLength.Pixels(28),
            MinWidth = CssLayoutLength.Pixels(0),
            HasExplicitMinWidth = true
        })
        {
            IntrinsicSize = new HtmlMlSize(160, 28)
        };
        middleSlot.Add(input);
        inlineFlex.Add(middleSlot);
        root.Add(inlineFlex);

        var snapshot = new CssArrangementEngine().Arrange(root, new HtmlMlSize(100, 28));

        Assert.Equal(100, snapshot[inlineFlex.Id].BorderBox.Width);
        Assert.Equal(100, snapshot[middleSlot.Id].BorderBox.Width);
        Assert.Equal(100, snapshot[input.Id].BorderBox.Width);
    }
}
