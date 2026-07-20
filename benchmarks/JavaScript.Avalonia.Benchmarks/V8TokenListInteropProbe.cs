using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using JavaScript.Avalonia;
using JavaScript.Avalonia.ClearScript;

namespace JavaScript.Avalonia.Benchmarks;

public static class V8TokenListInteropProbe
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
            var element = (AvaloniaDomElement)host.Document.createElement("div")!;
            element.classList.add("active", "chart");
            const int managedIterations = 100_000;
            var managedChecksum = 0;
            for (var index = 0; index < 1_000; index++) managedChecksum += element.classList.contains("active") ? 1 : 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var managedStarted = Stopwatch.StartNew();
            for (var index = 0; index < managedIterations; index++) managedChecksum += element.classList.contains("active") ? 1 : 0;
            managedStarted.Stop();
            var managedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            var writeElement = (AvaloniaDomElement)host.Document.createElement("div")!;
            for (var index = 0; index < 100; index++)
            {
                writeElement.classList.SetFromString((index & 1) == 0 ? "active chart" : "active chart alternate");
            }
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var managedWriteStarted = Stopwatch.StartNew();
            for (var index = 0; index < 5_000; index++)
            {
                writeElement.classList.SetFromString((index & 1) == 0 ? "active chart" : "active chart alternate");
            }
            managedWriteStarted.Stop();
            var managedWriteAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            runtime.Execute("""
                const tokenProbe = document.createElement('div');
                tokenProbe.classList.add('active', 'chart');
                for (let index = 0; index < 1000; index++) tokenProbe.classList.contains('active');
                """, "v8-token-list-warmup.js");
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var v8Started = Stopwatch.StartNew();
            runtime.Execute("""
                let tokenChecksum = 0;
                for (let index = 0; index < 20000; index++) {
                  if (tokenProbe.classList.contains('active')) tokenChecksum++;
                }
                globalThis.tokenChecksum = tokenChecksum;

                const tokenWriteProbe = document.createElement('div');
                for (let index = 0; index < 2000; index++) {
                  tokenWriteProbe.className = (index & 1) === 0 ? 'active chart' : 'active chart alternate';
                }
                """, "v8-token-list-measure.js");
            v8Started.Stop();
            var v8Allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            var passed = managedChecksum == managedIterations + 1_000
                         && Convert.ToInt32(runtime.Engine.Script.tokenChecksum) == 20_000;
            Console.WriteLine(
                $"V8 token-list interop: {(passed ? "pass" : "fail")}; " +
                $"managed={managedStarted.Elapsed.TotalMilliseconds:F1} ms/{managedAllocated / 1024.0:F1} KB " +
                $"({managedIterations} reads), " +
                $"managed-write={managedWriteStarted.Elapsed.TotalMilliseconds:F1} ms/" +
                $"{managedWriteAllocated / 1024.0:F1} KB (5000 writes), " +
                $"v8={v8Started.Elapsed.TotalMilliseconds:F1} ms/{v8Allocated / 1024.0:F1} KB " +
                "(20000 reads/2000 writes)");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"V8 token-list interop: fail; {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
