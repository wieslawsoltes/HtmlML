using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssRelativeInsetDefinitenessTests
{
    [AvaloniaFact]
    public void PercentageCalcRelativeTopIsAutoForIndefiniteHeightAndResolvesForDefiniteHeight()
    {
        var root = new CssLayoutPanel { Width = 240, Height = 240 };
        var window = new Window { Width = 240, Height = 240, Content = root };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        var document = host.Document;

        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = """
            html, body { margin: 0; }
            .containing-block { width: 100px; }
            .indefinite { min-height: 100px; }
            .definite { height: 100px; }
            .subject {
                position: relative;
                top: calc(10px + 10%);
                width: 100px;
                height: 100px;
            }
            """;
        document.head.appendChild(style);

        var indefinite = HostTestUtilities.GetElement(document.createElement("div"));
        indefinite.className = "containing-block indefinite";
        var indefiniteSubject = HostTestUtilities.GetElement(document.createElement("div"));
        indefiniteSubject.className = "subject";
        indefinite.appendChild(indefiniteSubject);

        var definite = HostTestUtilities.GetElement(document.createElement("div"));
        definite.className = "containing-block definite";
        var definiteSubject = HostTestUtilities.GetElement(document.createElement("div"));
        definiteSubject.className = "subject";
        definite.appendChild(definiteSubject);

        var bodyElement = HostTestUtilities.GetElement(document.body);
        bodyElement.appendChild(indefinite);
        bodyElement.appendChild(definite);

        try
        {
            window.Show();
            document.EnsureStylesCurrent();
            var body = Assert.IsType<CssLayoutPanel>(bodyElement.Control);
            CssLayout.SetNativeLayoutHotPath(body, true);
            Dispatcher.UIThread.RunJobs();

            var indefiniteRect = indefinite.getBoundingClientRect();
            var indefiniteSubjectRect = indefiniteSubject.getBoundingClientRect();
            var definiteRect = definite.getBoundingClientRect();
            var definiteSubjectRect = definiteSubject.getBoundingClientRect();
            Assert.Equal(indefiniteRect.y, indefiniteSubjectRect.y);
            Assert.Equal(definiteRect.y + 20, definiteSubjectRect.y);

            var portable = AvaloniaCssLayoutProjection.Capture(body, new Size(240, 240));
            var portableIndefinite = portable.GetBox(indefinite.Control).BorderBox;
            var portableIndefiniteSubject = portable.GetBox(indefiniteSubject.Control).BorderBox;
            var portableDefinite = portable.GetBox(definite.Control).BorderBox;
            var portableDefiniteSubject = portable.GetBox(definiteSubject.Control).BorderBox;
            Assert.Equal(portableIndefinite.Y, portableIndefiniteSubject.Y);
            Assert.Equal(portableDefinite.Y + 20, portableDefiniteSubject.Y);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
