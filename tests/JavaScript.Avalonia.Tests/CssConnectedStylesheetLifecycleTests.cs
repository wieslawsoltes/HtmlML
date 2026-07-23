using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssConnectedStylesheetLifecycleTests
{
    [AvaloniaFact]
    public void BodyStyleInsertionRemovalReinsertionAndTextMutationRecascade()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window { Width = 320, Height = 180, Content = root };
        using var host = new AvaloniaBrowserHost(window);
        try
        {
            var document = host.Document;
            var body = HostTestUtilities.GetElement(document.body);
            var target = HostTestUtilities.GetElement(document.createElement("div"));
            target.className = "connected-style-lifecycle-target";
            body.appendChild(target);
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = ".connected-style-lifecycle-target { display: none; }";

            window.Show();
            body.appendChild(style);
            AssertDisplay(document, target, "none");

            style.remove();
            AssertDisplay(document, target, "block");

            style.textContent = ".connected-style-lifecycle-target { display: inline-block; }";
            body.appendChild(style);
            AssertDisplay(document, target, "inline-block");

            style.textContent = ".connected-style-lifecycle-target { display: none; }";
            AssertDisplay(document, target, "none");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertDisplay(
        AvaloniaDomDocument document,
        AvaloniaDomElement target,
        string expected)
    {
        document.EnsureStylesCurrent();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(expected, document.getComputedStyle(target).getPropertyValue("display"));
    }
}
