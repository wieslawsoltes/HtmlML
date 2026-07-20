using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssNamedColorTests
{
    [AvaloniaFact]
    public void InheritedGreyCustomPropertyPaintsTextWithCssNamedColor()
    {
        var panel = new CssLayoutPanel { Width = 160, Height = 40 };
        var window = new Window { Width = 160, Height = 40, Content = panel };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = ":root { --muted: grey; } .heading { color: var(--muted); }";
        document.head.appendChild(style);
        var heading = HostTestUtilities.GetElement(document.createElement("div"));
        heading.className = "heading";
        heading.appendChild(
            Assert.IsAssignableFrom<AvaloniaDomElement>(document.createTextNode("SECTION")));
        HostTestUtilities.GetElement(document.body).appendChild(heading);

        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        var headingPanel = Assert.IsType<CssLayoutPanel>(heading.Control);
        var text = Assert.IsType<DomTextBlockControl>(Assert.Single(headingPanel.Children));
        Assert.Equal("grey", heading.ComputedStyleValues["color"]);
        Assert.Equal(Colors.Gray, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void MenuSelectionClassUpdatesInheritedTextAndBackgroundPaint()
    {
        var panel = new CssLayoutPanel { Width = 180, Height = 80 };
        var window = new Window { Width = 180, Height = 80, Content = panel };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            :root {
                --menu-fill: #1e1e1e;
                --menu-text: #dbdbdb;
                --menu-selected-fill: #f1f1f1;
                --menu-selected-text: #111111;
            }
            .menu-item {
                background-color: var(--menu-fill);
                color: var(--menu-text);
            }
            .menu-item.is-active {
                background-color: var(--menu-selected-fill);
                color: var(--menu-selected-text);
            }
            .menu-label { color: inherit; }
            """;
        document.head.appendChild(style);
        var item = HostTestUtilities.GetElement(document.createElement("div"));
        item.className = "menu-item";
        var label = HostTestUtilities.GetElement(document.createElement("span"));
        label.className = "menu-label";
        label.appendChild(Assert.IsAssignableFrom<AvaloniaDomElement>(
            document.createTextNode("5 minutes")));
        item.appendChild(label);
        HostTestUtilities.GetElement(document.body).appendChild(item);

        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        var itemPanel = Assert.IsType<CssLayoutPanel>(item.Control);
        var labelPanel = Assert.IsType<CssLayoutPanel>(label.Control);
        var text = Assert.IsType<DomTextBlockControl>(Assert.Single(labelPanel.Children));
        Assert.Equal("rgb(30, 30, 30)", document.getComputedStyle(item).getPropertyValue("background-color"));
        Assert.Equal("rgb(219, 219, 219)", document.getComputedStyle(label).getPropertyValue("color"));
        Assert.Equal(Color.Parse("#1e1e1e"), Assert.IsAssignableFrom<ISolidColorBrush>(itemPanel.Background).Color);
        Assert.Equal(Color.Parse("#dbdbdb"), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

        item.className = "menu-item is-active";
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rgb(241, 241, 241)", document.getComputedStyle(item).getPropertyValue("background-color"));
        Assert.Equal("rgb(17, 17, 17)", document.getComputedStyle(label).getPropertyValue("color"));
        Assert.Equal(Color.Parse("#f1f1f1"), Assert.IsAssignableFrom<ISolidColorBrush>(itemPanel.Background).Color);
        Assert.Equal(Color.Parse("#111111"), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
