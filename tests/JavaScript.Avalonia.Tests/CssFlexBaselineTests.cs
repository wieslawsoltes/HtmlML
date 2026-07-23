using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssFlexBaselineTests
{
    [AvaloniaFact]
    public void FirstBaselineAlignsPaddedTextWithSynthesizedEmptyBoxBaseline()
    {
        var root = new CssLayoutPanel { Width = 360, Height = 40 };
        var window = new Window { Width = 360, Height = 40, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;
        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            #timeline { align-items: baseline; display: flex; width: 360px; }
            #day { box-sizing: content-box; font: 10px/12px sans-serif; padding-top: 3px; width: 30px; }
            #track { flex: 1 1 auto; height: 7px; }
            """;
        document.head.appendChild(style);
        var timeline = Append(document, "div", "timeline");
        var day = Append(document, "span", "day", timeline);
        day.textContent = "WED";
        var track = Append(document, "span", "track", timeline);

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var timelineRect = timeline.getBoundingClientRect();
            var dayRect = day.getBoundingClientRect();
            var trackRect = track.getBoundingClientRect();
            var dayBaseline = CssLayoutPanel.ResolveFirstBaseline(
                day.Control,
                dayRect.width,
                dayRect.height);
            Assert.Equal(15, timelineRect.height, 6);
            Assert.Equal(timelineRect.top, dayRect.top, 6);
            Assert.True(dayBaseline.HasValue);
            Assert.InRange(
                Math.Abs(
                    (trackRect.top + trackRect.height) -
                    (dayRect.top + dayBaseline.Value)),
                0,
                .6);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static AvaloniaDomElement Append(
        AvaloniaDomDocument document,
        string tag,
        string id,
        AvaloniaDomElement? parent = null)
    {
        var element = HostTestUtilities.GetElement(document.createElement(tag));
        element.id = id;
        (parent ?? HostTestUtilities.GetElement(document.body)).appendChild(element);
        return element;
    }
}
