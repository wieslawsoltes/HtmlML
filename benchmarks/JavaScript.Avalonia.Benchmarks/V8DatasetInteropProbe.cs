using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using JavaScript.Avalonia;
using JavaScript.Avalonia.ClearScript;

namespace JavaScript.Avalonia.Benchmarks;

public static class V8DatasetInteropProbe
{
    internal static int Run()
    {
        BenchmarkApp.EnsureInitialized();
        var window = new Window
        {
            Width = 320,
            Height = 240,
            Content = new CssLayoutPanel { ClipToBounds = true }
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            using var host = new AvaloniaBrowserHost(window);
            using var runtime = new ClearScriptV8Runtime(host);
            var element = (AvaloniaDomElement)host.Document.createElement("div")!;
            element.dataset.set("chartTheme", "dark");

            const int managedIterations = 100_000;
            var managedChecksum = 0;
            for (var index = 0; index < 1_000; index++)
            {
                managedChecksum += element.dataset.has("chartTheme") ? 1 : 0;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var managedStarted = Stopwatch.StartNew();
            for (var index = 0; index < managedIterations; index++)
            {
                managedChecksum += element.dataset.has("chartTheme") ? 1 : 0;
            }
            managedStarted.Stop();
            var managedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            runtime.Execute("""
                const datasetProbe = document.createElement('div');
                datasetProbe.dataset.chartTheme = 'dark';
                for (let index = 0; index < 1000; index++) {
                  if (datasetProbe.dataset.chartTheme === 'dark') globalThis.datasetWarmup = index;
                }
                """, "v8-dataset-warmup.js");
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var v8Started = Stopwatch.StartNew();
            runtime.Execute("""
                let checksum = 0;
                for (let index = 0; index < 20000; index++) {
                  if (datasetProbe.dataset.chartTheme === 'dark') checksum++;
                }
                globalThis.datasetChecksum = checksum;
                """, "v8-dataset-measure.js");
            v8Started.Stop();
            var v8Allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            var v8Checksum = Convert.ToInt32(runtime.Engine.Script.datasetChecksum);
            var passed = managedChecksum == managedIterations + 1_000 && v8Checksum == 20_000;

            Console.WriteLine(
                $"V8 dataset interop: {(passed ? "pass" : "fail")}; " +
                $"managed={managedStarted.Elapsed.TotalMilliseconds:F1} ms/{managedAllocated / 1024.0:F1} KB " +
                $"({managedIterations} reads), " +
                $"v8={v8Started.Elapsed.TotalMilliseconds:F1} ms/{v8Allocated / 1024.0:F1} KB " +
                "(20000 reads)");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"V8 dataset interop: fail; {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
