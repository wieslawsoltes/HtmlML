using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssAppendOnlyStylesheetCandidateTests
{
    [AvaloniaFact]
    public void IndexedAppendMatchingPreservesCascadeAndAvoidsIrrelevantRuleScans()
    {
        var indexed = RunProbe(enableIndexedMatching: true);
        var fullScan = RunProbe(enableIndexedMatching: false);

        Assert.Equal(fullScan.Color, indexed.Color);
        Assert.Equal(fullScan.BackgroundColor, indexed.BackgroundColor);
        Assert.Equal(fullScan.BorderLeftColor, indexed.BorderLeftColor);
        Assert.Equal(fullScan.PseudoBackground, indexed.PseudoBackground);
        Assert.Equal("rgb(255, 0, 0)", indexed.Color);
        Assert.Equal("rgb(18, 52, 86)", indexed.BackgroundColor);
        Assert.Equal("rgb(171, 205, 239)", indexed.BorderLeftColor);
        Assert.Equal(global::Avalonia.Media.Color.Parse("#654321"), indexed.PseudoBackground);
        Assert.True(indexed.CandidateEvaluations > 0);
        Assert.True(
            indexed.CandidateEvaluations * 10 < fullScan.CandidateEvaluations,
            $"indexed={indexed.CandidateEvaluations}, full={fullScan.CandidateEvaluations}");
        Assert.True(indexed.ComputedElements <= fullScan.ComputedElements);
    }

    private static ProbeResult RunProbe(bool enableIndexedMatching)
    {
        var root = new CssLayoutPanel { Width = 480, Height = 320 };
        var window = new Window { Width = 480, Height = 320, Content = root };
        using var host = new AvaloniaBrowserHost(
            window,
            enableTargetOnlyInlineStyles: true,
            enableIndexedAppendStylesheetMatching: enableIndexedMatching);
        try
        {
            var document = host.Document;
            var body = HostTestUtilities.GetElement(document.body);
            var initialStyle = HostTestUtilities.GetElement(document.createElement("style"));
            initialStyle.textContent = ".seed { color: blue; }";
            document.head.appendChild(initialStyle);

            var scope = HostTestUtilities.GetElement(document.createElement("section"));
            scope.className = "scope";
            body.appendChild(scope);
            var target = HostTestUtilities.GetElement(document.createElement("div"));
            target.className = "seed target";
            target.setAttribute("data-tone", "hot");
            target.setAttribute("data-universal-hot", "true");
            scope.appendChild(target);
            var pseudoTarget = HostTestUtilities.GetElement(document.createElement("div"));
            pseudoTarget.className = "pseudo-target";
            scope.appendChild(pseudoTarget);
            for (var index = 0; index < 80; index++)
            {
                var noise = HostTestUtilities.GetElement(document.createElement("div"));
                noise.className = $"noise-{index}";
                body.appendChild(noise);
            }

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var evaluationStart = document.AppendStylesheetCandidateEvaluationCount;
            var computeStart = document.ElementStyleComputeCount;
            var irrelevantRules = string.Join(
                '\n',
                Enumerable.Range(0, 200).Select(index =>
                    $".never-matches-{index} {{ color: rgb({index % 255}, 0, 0); }}"));
            var appendedStyle = HostTestUtilities.GetElement(document.createElement("style"));
            appendedStyle.textContent = irrelevantRules + "\n" +
                                        ".scope .target { color: red; }\n" +
                                        ".target[data-tone=hot] { background-color: #123456; }\n" +
                                        "[data-universal-hot] { border-left-color: #abcdef; }\n" +
                                        ".pseudo-target::before { content: ''; background-color: #654321; }";
            document.head.appendChild(appendedStyle);
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var computed = document.getComputedStyle(target);
            var generated = Assert.IsType<CssGeneratedPseudoElement>(
                Assert.IsType<CssLayoutPanel>(pseudoTarget.Control).BeforePseudoElement);
            return new ProbeResult(
                computed.getPropertyValue("color") ?? string.Empty,
                computed.getPropertyValue("background-color") ?? string.Empty,
                computed.getPropertyValue("border-left-color") ?? string.Empty,
                Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(generated.Background).Color,
                document.AppendStylesheetCandidateEvaluationCount - evaluationStart,
                document.ElementStyleComputeCount - computeStart);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed record ProbeResult(
        string Color,
        string BackgroundColor,
        string BorderLeftColor,
        global::Avalonia.Media.Color PseudoBackground,
        long CandidateEvaluations,
        long ComputedElements);
}
