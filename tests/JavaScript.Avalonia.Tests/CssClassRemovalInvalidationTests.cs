using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssClassRemovalInvalidationTests
{
    [AvaloniaFact]
    public void MeasuringMenuStaysUnpaintedUntilItsFirstPositionedStyleCommit()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window { Width = 320, Height = 180, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .menuWrap { width: 120px; height: 80px; opacity: 1; position: fixed; }
                .menuWrap.isMeasuring { opacity: 0; pointer-events: none; visibility: hidden; }
                """;
            document.head.appendChild(style);
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var menu = HostTestUtilities.GetElement(document.createElement("div"));
            menu.className = "menuWrap isMeasuring";
            HostTestUtilities.GetElement(document.body).appendChild(menu);

            // A connected native control exists immediately, but it must not
            // expose its default opacity/position before CSS applies the
            // component's hidden measuring state.
            Assert.Equal(0, menu.Control.Opacity);
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, menu.Control.Opacity);

            menu.style.cssText = "left: 148px; top: 52px;";
            menu.classList.remove("isMeasuring");

            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();
            var rect = menu.getBoundingClientRect();
            Assert.Equal(1, menu.Control.Opacity);
            Assert.Equal(148, rect.x);
            Assert.Equal(52, rect.y);
            Assert.Equal(120, rect.width);
            Assert.Equal(80, rect.height);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void RemovingClassRecascadesCompoundSelectorAndRestoresNativeOpacity()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window { Width = 320, Height = 180, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .menuWrap { opacity: 0.625; background-color: rgb(1, 2, 3); }
                .menuWrap.isMeasuring { opacity: 0; background-color: rgb(4, 5, 6); }
                """;
            document.head.appendChild(style);
            var menu = HostTestUtilities.GetElement(document.createElement("div"));
            menu.className = "menuWrap isMeasuring";
            HostTestUtilities.GetElement(document.body).appendChild(menu);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("0", document.getComputedStyle(menu).getPropertyValue("opacity"));
            Assert.Equal(0, menu.Control.Opacity);
            Assert.Equal(
                Color.FromRgb(4, 5, 6),
                Assert.IsType<SolidColorBrush>(Assert.IsType<CssLayoutPanel>(menu.Control).Background).Color);

            menu.classList.remove("isMeasuring");
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("menuWrap", menu.className);
            Assert.Equal("0.625", document.getComputedStyle(menu).getPropertyValue("opacity"));
            Assert.Equal(0.625, menu.Control.Opacity);
            Assert.Equal(
                Color.FromRgb(1, 2, 3),
                Assert.IsType<SolidColorBrush>(Assert.IsType<CssLayoutPanel>(menu.Control).Background).Color);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void RemovingMeasuringClassSurvivesLazyStylesheetAppendAndRestoresOpacity()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window { Width = 320, Height = 180, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .toolbar {
                    display: flex;
                    width: 144px;
                    height: 36px;
                    opacity: 0.875;
                    pointer-events: auto;
                    background-color: rgb(8, 16, 24);
                }
                .menuWrap-Kq3ruQo8.isMeasuring-Kq3ruQo8 {
                    opacity: 0;
                    pointer-events: none;
                    position: fixed;
                    visibility: hidden;
                }
                """;
            document.head.appendChild(style);
            var toolbar = HostTestUtilities.GetElement(document.createElement("div"));
            toolbar.className = "toolbar";
            HostTestUtilities.GetElement(document.body).appendChild(toolbar);
            var menu = HostTestUtilities.GetElement(document.createElement("div"));
            menu.className = "menu-Tx5xMZww context-menu menuWrap-Kq3ruQo8 isMeasuring-Kq3ruQo8";
            HostTestUtilities.GetElement(document.body).appendChild(menu);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, menu.Control.Opacity);
            AssertToolbarPresentation(toolbar);

            menu.className = "menu-Tx5xMZww context-menu menuWrap-Kq3ruQo8";
            // A component can load another lazy CSS bundle in the same turn that
            // removes its measuring class. An append-only stylesheet reload
            // must not discard the already queued class recascade.
            var lazyStyle = HostTestUtilities.GetElement(document.createElement("style"));
            lazyStyle.textContent = ".unrelated-lazy-bundle { color: red; }";
            document.head.appendChild(lazyStyle);
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("1", document.getComputedStyle(menu).getPropertyValue("opacity"));
            Assert.Equal(1, menu.Control.Opacity);
            AssertToolbarPresentation(toolbar);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertToolbarPresentation(AvaloniaDomElement toolbar)
    {
        var panel = Assert.IsType<CssLayoutPanel>(toolbar.Control);
        Assert.True(panel.IsVisible);
        Assert.True(panel.IsHitTestVisible);
        Assert.Equal(CssDisplay.Flex, CssLayout.GetDisplay(panel));
        Assert.Equal(new CssLength(144, CssLengthUnit.Pixel), CssLayout.GetWidth(panel));
        Assert.Equal(new CssLength(36, CssLengthUnit.Pixel), CssLayout.GetHeight(panel));
        Assert.Equal(0.875, panel.Opacity);
        Assert.Equal(
            Color.FromRgb(8, 16, 24),
            Assert.IsType<SolidColorBrush>(panel.Background).Color);
    }
}
