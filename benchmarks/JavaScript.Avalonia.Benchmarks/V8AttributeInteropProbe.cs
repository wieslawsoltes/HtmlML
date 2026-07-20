using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using JavaScript.Avalonia;
using JavaScript.Avalonia.ClearScript;

namespace JavaScript.Avalonia.Benchmarks;

public static class V8AttributeInteropProbe
{
    internal static int Run()
    {
        BenchmarkApp.EnsureInitialized();
        var window = new Window { Width = 320, Height = 240, Content = new CssLayoutPanel() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            using var host = new AvaloniaBrowserHost(window);
            using var runtime = new ClearScriptV8Runtime(host);
            var element = (AvaloniaDomElement)host.Document.createElement("button")!;
            element.setAttribute("aria-label", "Open chart");

            var managedChecksum = 0;
            for (var index = 0; index < 1_000; index++)
            {
                managedChecksum += element.getAttribute("aria-label")?.Length ?? 0;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var readStarted = Stopwatch.StartNew();
            for (var index = 0; index < 100_000; index++)
            {
                managedChecksum += element.getAttribute("aria-label")?.Length ?? 0;
            }
            readStarted.Stop();
            var readAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            for (var index = 0; index < 100; index++)
            {
                element.setAttribute("aria-label", (index & 1) == 0 ? "Open chart" : "Close chart");
            }
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var writeStarted = Stopwatch.StartNew();
            for (var index = 0; index < 5_000; index++)
            {
                element.setAttribute("aria-label", (index & 1) == 0 ? "Open chart" : "Close chart");
            }
            writeStarted.Stop();
            var writeAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            runtime.Execute("""
                const attributeProbe = document.createElement('button');
                attributeProbe.setAttribute('aria-label', 'Open chart');
                for (let index = 0; index < 1000; index++) {
                  globalThis.attributeWarmup = attributeProbe.getAttribute('aria-label');
                }
                """, "v8-attribute-warmup.js");
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var v8Started = Stopwatch.StartNew();
            runtime.Execute("""
                let attributeChecksum = 0;
                for (let index = 0; index < 20000; index++) {
                  attributeChecksum += attributeProbe.getAttribute('aria-label').length;
                }
                for (let index = 0; index < 2000; index++) {
                  attributeProbe.setAttribute(
                    'aria-label',
                    (index & 1) === 0 ? 'Open chart' : 'Close chart');
                }
                globalThis.attributeChecksum = attributeChecksum;
                """, "v8-attribute-measure.js");
            v8Started.Stop();
            var v8Allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            var passed = managedChecksum == 1_010_000
                         && Convert.ToInt32(runtime.Engine.Script.attributeChecksum) == 200_000
                         && element.getAttribute("aria-label") == "Close chart";

            Console.WriteLine(
                $"V8 attribute interop: {(passed ? "pass" : "fail")}; " +
                $"managed-read={readStarted.Elapsed.TotalMilliseconds:F1} ms/" +
                $"{readAllocated / 1024.0:F1} KB (100000 reads), " +
                $"managed-write={writeStarted.Elapsed.TotalMilliseconds:F1} ms/" +
                $"{writeAllocated / 1024.0:F1} KB (5000 writes), " +
                $"v8={v8Started.Elapsed.TotalMilliseconds:F1} ms/{v8Allocated / 1024.0:F1} KB " +
                "(20000 reads/2000 writes)");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"V8 attribute interop: fail; {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
