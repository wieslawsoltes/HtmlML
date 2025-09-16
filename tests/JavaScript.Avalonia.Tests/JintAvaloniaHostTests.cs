using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Jint.Native;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public class JintAvaloniaHostTests
{
    [AvaloniaFact]
    public void ExecuteScriptText_RunsCode()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        host.ExecuteScriptText("globalThis.testValue = 5;");

        Assert.Equal(5d, Convert.ToDouble(host.Engine.GetValue("testValue").ToObject()));
    }

    [AvaloniaFact]
    public void ExecuteScriptText_IgnoresInvalidCode()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        host.Engine.SetValue("stable", 42);

        host.ExecuteScriptText("function broken() {");

        Assert.Equal(42d, Convert.ToDouble(host.Engine.GetValue("stable").ToObject()));
    }

    [AvaloniaFact]
    public void ExecuteScriptUri_LoadsFileContent()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "globalThis.fileValue = 17;");

            host.ExecuteScriptUri(new Uri(path));

            Assert.Equal(17d, Convert.ToDouble(host.Engine.GetValue("fileValue").ToObject()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SetTimeout_InvokesCallback()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Engine.SetValue("notify", new Action(() => tcs.TrySetResult(true)));
        var handle = host.Engine.Evaluate("window.setTimeout(() => notify(), 5);");

        Assert.True(Convert.ToInt32(handle.ToObject()) > 0);

        Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [AvaloniaFact]
    public async Task SetTimeout_ReturnsHandleAndClearTimeout_PreventsCallback()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Engine.SetValue("notify", new Action(() => tcs.TrySetResult(true)));
        var handle = host.Engine.Evaluate("window.setTimeout(() => notify(), 5);").ToObject();
        host.Engine.SetValue("handleValue", handle);
        host.Engine.Execute("window.clearTimeout(handleValue);");

        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.False(tcs.Task.IsCompleted);
    }

    [AvaloniaFact]
    public async Task SetInterval_InvokesUntilCleared()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var count = 0;
        host.Engine.SetValue("increment", new Action(() => count++));

        host.Engine.Execute("var intervalHandle = window.setInterval(() => increment(), 10);");
        await Task.Delay(60);
        Dispatcher.UIThread.RunJobs();
        Assert.True(count > 0);

        host.Engine.Execute("window.clearInterval(intervalHandle);");
        var snapshot = count;
        await Task.Delay(60);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(snapshot, count);
    }

    [AvaloniaFact]
    public async Task RequestAnimationFrame_InvokesCallback()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Engine.SetValue("capture", new Action<double>(ts => tcs.TrySetResult(ts)));
        host.ExecuteScriptText("window.requestAnimationFrame(ts => capture(ts));");

        var timestamp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(timestamp >= 0);
    }

    [AvaloniaFact]
    public async Task CancelAnimationFrame_PreventsCallback()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.Engine.SetValue("mark", new Action(() => tcs.TrySetResult(true)));
        host.ExecuteScriptText("const id = window.requestAnimationFrame(() => mark()); window.cancelAnimationFrame(id);");

        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.False(tcs.Task.IsCompleted);
    }

    [AvaloniaFact]
    public void ConsoleMethods_DoNotThrow()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        host.ExecuteScriptText("console.log('log'); console.info('info'); console.warn('warn'); console.error('error'); console.table({ value: 1 });");
    }
}
