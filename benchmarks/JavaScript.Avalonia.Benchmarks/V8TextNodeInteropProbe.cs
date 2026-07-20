using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using JavaScript.Avalonia;
using JavaScript.Avalonia.ClearScript;

namespace JavaScript.Avalonia.Benchmarks;

public static class V8TextNodeInteropProbe
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
            var node = (AvaloniaDomTextNode)host.Document.createTextNode("Chart value")!;

            for (var index = 0; index < 100; index++)
            {
                node.data = (index & 1) == 0 ? "Chart value" : "Chart price";
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var managedStarted = Stopwatch.StartNew();
            for (var index = 0; index < 10_000; index++)
            {
                node.data = (index & 1) == 0 ? "Chart value" : "Chart price";
            }
            managedStarted.Stop();
            var managedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            for (var index = 0; index < 100; index++)
            {
                node.ApplyTextTransform((index & 1) == 0 ? "uppercase" : "none");
            }
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var transformStarted = Stopwatch.StartNew();
            for (var index = 0; index < 5_000; index++)
            {
                node.ApplyTextTransform((index & 1) == 0 ? "uppercase" : "none");
            }
            transformStarted.Stop();
            var transformAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            runtime.Execute("""
                const textNodeProbe = document.createTextNode('Chart value');
                for (let index = 0; index < 1000; index++) {
                  textNodeProbe.data = (index & 1) === 0 ? 'Chart value' : 'Chart price';
                  globalThis.textNodeWarmup = textNodeProbe.data;
                }
                """, "v8-text-node-warmup.js");
            allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var v8Started = Stopwatch.StartNew();
            runtime.Execute("""
                let textNodeChecksum = 0;
                for (let index = 0; index < 20000; index++) {
                  if (textNodeProbe.data.length === 11) textNodeChecksum++;
                }
                for (let index = 0; index < 2000; index++) {
                  textNodeProbe.data = (index & 1) === 0 ? 'Chart value' : 'Chart price';
                }
                globalThis.textNodeChecksum = textNodeChecksum;
                """, "v8-text-node-measure.js");
            v8Started.Stop();
            var v8Allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            var passed = node.data == "Chart price"
                         && ((TextBlock)node.Control).Text == "Chart price"
                         && Convert.ToInt32(runtime.Engine.Script.textNodeChecksum) == 20_000;

            Console.WriteLine(
                $"V8 text-node interop: {(passed ? "pass" : "fail")}; " +
                $"managed-write={managedStarted.Elapsed.TotalMilliseconds:F1} ms/" +
                $"{managedAllocated / 1024.0:F1} KB (10000 writes), " +
                $"transform={transformStarted.Elapsed.TotalMilliseconds:F1} ms/" +
                $"{transformAllocated / 1024.0:F1} KB (5000 updates), " +
                $"v8={v8Started.Elapsed.TotalMilliseconds:F1} ms/{v8Allocated / 1024.0:F1} KB " +
                "(20000 reads/2000 writes)");
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"V8 text-node interop: fail; {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
