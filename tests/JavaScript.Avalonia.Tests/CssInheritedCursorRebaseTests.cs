using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class CssInheritedCursorRebaseTests
{
    [AvaloniaFact]
    public void ScopedClassCursorChangeRebasesInheritedBranchesAndPrunesOverrides()
    {
        var enabled = RunProbe(enableRebase: true);
        var disabled = RunProbe(enableRebase: false);

        Assert.Equal(disabled.Results, enabled.Results);
        Assert.Equal("pointer", enabled.Results.NormalCursor);
        Assert.Equal("wait", enabled.Results.OverrideCursor);
        Assert.Equal("pointer", enabled.Results.ExplicitInheritCursor);
        Assert.Equal(3, enabled.RebasedElements);
        Assert.Equal(1, enabled.PrunedBranches);
        Assert.Equal(2, enabled.ComputedElements);
        Assert.Equal(0, disabled.RebasedElements);
        Assert.Equal(0, disabled.PrunedBranches);
        Assert.True(disabled.ComputedElements > enabled.ComputedElements);
    }

    private static ProbeResult RunProbe(bool enableRebase)
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window { Width = 320, Height = 180, Content = root };
        using var host = new AvaloniaBrowserHost(
            window,
            enableTargetOnlyInlineStyles: true,
            enableInheritedCursorRebase: enableRebase);
        try
        {
            var document = host.Document;
            var style = HostTestUtilities.GetElement(document.createElement("style"));
            style.textContent = """
                .parent { cursor: crosshair; }
                .parent.active { cursor: pointer; }
                .override { cursor: wait; }
                .explicit-inherit { cursor: inherit; }
                """;
            document.head.appendChild(style);

            var parent = Append(HostTestUtilities.GetElement(document.body), "parent");
            var normal = Append(parent, string.Empty);
            var normalDeep = Append(normal, string.Empty);
            var overridden = Append(parent, "override");
            var overrideDeep = Append(overridden, string.Empty);
            var explicitInherit = Append(parent, "explicit-inherit");
            var explicitInheritDeep = Append(explicitInherit, string.Empty);

            window.Show();
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var computeStart = document.ElementStyleComputeCount;
            var rebaseStart = document.InheritedCursorRebaseElementCount;
            var pruneStart = document.InheritedPropagationPrunedElementCount;
            parent.classList.add("active");
            document.EnsureStylesCurrent();
            Dispatcher.UIThread.RunJobs();

            var results = new CursorResults(
                document.getComputedStyle(normalDeep).getPropertyValue("cursor") ?? string.Empty,
                document.getComputedStyle(overrideDeep).getPropertyValue("cursor") ?? string.Empty,
                document.getComputedStyle(explicitInheritDeep).getPropertyValue("cursor") ?? string.Empty);
            Assert.NotNull(normalDeep.Control.Cursor);
            Assert.NotNull(overrideDeep.Control.Cursor);
            Assert.NotNull(explicitInheritDeep.Control.Cursor);
            return new ProbeResult(
                results,
                document.ElementStyleComputeCount - computeStart,
                document.InheritedCursorRebaseElementCount - rebaseStart,
                document.InheritedPropagationPrunedElementCount - pruneStart);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static AvaloniaDomElement Append(object parent, string className)
    {
        var parentElement = HostTestUtilities.GetElement(parent);
        var document = parentElement.ownerDocument
                       ?? throw new InvalidOperationException("The test element has no owner document.");
        var element = HostTestUtilities.GetElement(document.createElement("div"));
        element.className = className;
        parentElement.appendChild(element);
        return element;
    }

    private sealed record CursorResults(
        string NormalCursor,
        string OverrideCursor,
        string ExplicitInheritCursor);

    private sealed record ProbeResult(
        CursorResults Results,
        long ComputedElements,
        long RebasedElements,
        long PrunedBranches);
}
