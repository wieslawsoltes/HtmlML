using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class ConnectedDialogLatencySpikeTests
{
    [AvaloniaFact]
    public void OpeningConnectedDialogPreservesWarmedChartSelectorCache()
    {
        const int rowCount = 180;
        const int selectorVariants = 48;
        var panel = new CssLayoutPanel { Width = 1000, Height = 616 };
        var window = new Window
        {
            Width = 1000,
            Height = 616,
            Content = panel
        };
        using var host = new AvaloniaBrowserHost(window, enableTargetOnlyInlineStyles: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var document = host.Document;
        var css = new StringBuilder();
        for (var variant = 0; variant < selectorVariants; variant++)
        {
            css.Append(".chart .row.kind-")
                .Append(variant)
                .Append(" .cell { border-left-width: ")
                .Append((variant % 3) + 1)
                .AppendLine("px; }");
        }
        css.AppendLine(".dialog-state.open .field { color: rgb(255, 0, 0); }");
        css.AppendLine(".dialog-state.open .field + .status { background-color: rgb(0, 0, 255); }");

        var style = HostTestUtilities.GetElement(document.createElement("style"));
        style.textContent = css.ToString();
        document.head.appendChild(style);

        var chart = HostTestUtilities.GetElement(document.createElement("section"));
        chart.className = "chart";
        HostTestUtilities.GetElement(document.body).appendChild(chart);
        for (var index = 0; index < rowCount; index++)
        {
            var row = HostTestUtilities.GetElement(document.createElement("div"));
            row.className = $"row kind-{index % selectorVariants}";
            var cell = HostTestUtilities.GetElement(document.createElement("span"));
            cell.className = "cell";
            row.appendChild(cell);
            chart.appendChild(row);
        }
        document.EnsureStylesCurrent();
        host.ArmTargetOnlyInlineStyles();

        // The first inherited mutation populates the incremental selector cache
        // for the retained chart subtree, as happens during normal chart updates.
        chart.SetStyleProperty("color", "rgb(0, 128, 0)");
        document.EnsureStylesCurrent();
        var warmHitsBefore = document.MatchedRuleCacheHitCount;
        chart.SetStyleProperty("color", "rgb(128, 0, 0)");
        document.EnsureStylesCurrent();
        Assert.True(document.MatchedRuleCacheHitCount - warmHitsBefore >= rowCount * 2);

        var selectorMatchesBefore = document.SelectorMatchEvaluationCount;
        var cacheHitsBefore = document.MatchedRuleCacheHitCount;

        // Keep a chart update pending while React-style dialog construction adds
        // a connected root and then toggles a descendant state class.
        chart.SetStyleProperty("color", "rgb(0, 0, 255)");
        var dialog = HostTestUtilities.GetElement(document.createElement("div"));
        dialog.className = "dialog";
        var state = HostTestUtilities.GetElement(document.createElement("div"));
        state.className = "dialog-state";
        var field = HostTestUtilities.GetElement(document.createElement("span"));
        field.className = "field";
        var status = HostTestUtilities.GetElement(document.createElement("span"));
        status.className = "status";
        state.appendChild(field);
        state.appendChild(status);
        dialog.appendChild(state);
        HostTestUtilities.GetElement(document.body).appendChild(dialog);

        var elapsed = Stopwatch.StartNew();
        state.classList.add("open");
        document.EnsureStylesCurrent();
        elapsed.Stop();

        Assert.Equal("rgb(255, 0, 0)", document.getComputedStyle(field).getPropertyValue("color"));
        Assert.Equal("rgb(0, 0, 255)", document.getComputedStyle(status).getPropertyValue("background-color"));
        Assert.True(
            document.MatchedRuleCacheHitCount - cacheHitsBefore >= rowCount * 2,
            $"Expected the pending chart cascade to reuse its warmed selector cache; " +
            $"hits={document.MatchedRuleCacheHitCount - cacheHitsBefore}, " +
            $"matches={document.SelectorMatchEvaluationCount - selectorMatchesBefore}, " +
            $"elapsed={elapsed.Elapsed.TotalMilliseconds:F2}ms.");
        Assert.InRange(
            document.SelectorMatchEvaluationCount - selectorMatchesBefore,
            1,
            selectorVariants * 4);

        window.Close();
    }
}
