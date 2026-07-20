using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class UiThreadWorkBudgetSpikeTests
{
    [AvaloniaFact]
    public void ExternalEngineCallRecordsOneOutermostJavaScriptBudgetSample()
    {
        var (host, window) = HostTestUtilities.CreateHost();
        using (host)
        {
            host.UiThreadWorkBudget = TimeSpan.FromTicks(1);

            using (host.EnterExternalJavaScriptCall())
            {
                using (host.EnterExternalJavaScriptCall())
                {
                    Thread.SpinWait(20_000);
                }
            }

            var metrics = host.GetUiThreadWorkBudgetMetrics();
            Assert.Equal(1, metrics.JavaScriptSamples);
            Assert.Equal(1, metrics.JavaScriptOverruns);
            Assert.True(metrics.MaximumJavaScriptDuration > TimeSpan.Zero);
        }
    }

    [AvaloniaFact]
    public void CssAndForcedLayoutReportAtTheirSynchronousBoundaries()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var (host, window) = HostTestUtilities.CreateHost(root);
        using (host)
        {
            window.Width = 320;
            window.Height = 180;
            window.Show();
            host.UiThreadWorkBudget = TimeSpan.FromTicks(1);

            var body = HostTestUtilities.GetElement(host.Document.body);
            var element = HostTestUtilities.GetElement(host.Document.createElement("div"));
            element.setAttribute("style", "width: 120px; height: 40px; color: red");
            body.appendChild(element);

            _ = host.Document.getComputedStyle(element).getPropertyValue("color");
            element.Control.InvalidateMeasure();
            element.Control.InvalidateArrange();
            _ = element.getBoundingClientRect();

            var metrics = host.GetUiThreadWorkBudgetMetrics();
            Assert.True(metrics.CssSamples > 0);
            Assert.True(metrics.CssOverruns > 0);
            Assert.True(metrics.MaximumCssDuration > TimeSpan.Zero);
            Assert.True(metrics.LayoutSamples > 0);
            Assert.True(metrics.LayoutOverruns > 0);
            Assert.True(metrics.MaximumLayoutDuration > TimeSpan.Zero);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ZeroBudgetDisablesMeasurement()
    {
        var (host, window) = HostTestUtilities.CreateHost();
        using (host)
        {
            host.UiThreadWorkBudget = TimeSpan.Zero;

            using (host.EnterExternalJavaScriptCall())
            {
                Thread.SpinWait(20_000);
            }

            var metrics = host.GetUiThreadWorkBudgetMetrics();
            Assert.Equal(0, metrics.JavaScriptSamples);
            Assert.Equal(0, metrics.JavaScriptOverruns);
        }
    }

    [AvaloniaFact]
    public void BrowserTasksReportTheirKindAndLongestRunToCompletionUnit()
    {
        var (host, window) = HostTestUtilities.CreateHost();
        using (host)
        {
            host.CollectPerformanceMetrics = true;
            var invoked = false;
            host.BrowserWindow.setTimeout(
                new ExternalCallback(() =>
                {
                    var allocation = new byte[4096];
                    Thread.SpinWait(20_000);
                    GC.KeepAlive(allocation);
                    invoked = true;
                }),
                0);

            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!invoked && DateTime.UtcNow < timeout)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }

            Assert.True(invoked);
            var metric = Assert.Single(
                host.GetUserTaskPerformanceMetrics()
                    .Where(static item => item.Kind.StartsWith("timeout:", StringComparison.Ordinal)));
            Assert.Equal(1, metric.Count);
            Assert.True(metric.TotalDuration > TimeSpan.Zero);
            Assert.Equal(metric.TotalDuration, metric.MaximumDuration);
            Assert.True(metric.TotalAllocatedBytes >= 4096);
            Assert.Equal(metric.TotalAllocatedBytes, metric.MaximumAllocatedBytes);

            host.ResetUserTaskPerformanceMetrics();
            Assert.Empty(host.GetUserTaskPerformanceMetrics());
        }
    }

    [AvaloniaFact]
    public void RapidResizeCancellationDoesNotDisposeQueuedReconciliationState()
    {
        var root = new CssLayoutPanel { Width = 320, Height = 180 };
        var window = new Window
        {
            Width = 320,
            Height = 180,
            Content = root
        };
        var host = new AvaloniaBrowserHost(
            window,
            enableTargetOnlyInlineStyles: true);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            host.ArmTargetOnlyInlineStyles();
            _ = host.BrowserWindow.innerWidth;

            for (var width = 321; width <= 336; width++)
            {
                root.Width = width;
            }

            Dispatcher.UIThread.RunJobs();
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (host.ResizeStyleReconciliationCount == 0 && DateTime.UtcNow < timeout)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Assert.Equal(1, host.ResizeStyleReconciliationCount);
            Assert.Equal(
                host.ResizeStyleReconciliationGeneration,
                host.LastReconciledResizeStyleGeneration);

            root.Width = 337;
            host.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            host.Dispose();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class ExternalCallback(Action callback) : IExternalJavaScriptCallback
    {
        public void Invoke(object? thisValue, params object?[] arguments) => callback();

        public override string ToString() => "test-callback";
    }
}
