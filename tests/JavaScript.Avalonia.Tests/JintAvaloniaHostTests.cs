using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
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

    [AvaloniaFact]
    public void CreateTextNode_IsAvailableToScripts()
    {
        var panel = new StackPanel();
        var (host, _) = HostTestUtilities.CreateHost(panel);

        host.ExecuteScriptText("const node = document.createTextNode('from js'); document.body.appendChild(node); node.data = 'updated';");

        Assert.Single(panel.Children);
        var textBlock = Assert.IsType<TextBlock>(panel.Children[0]);
        Assert.Equal("updated", textBlock.Text);
    }

    [AvaloniaFact]
    public void ChildManipulation_Apis_WorkFromJavaScript()
    {
        var panel = new StackPanel();
        var first = new TextBlock { Name = "first" };
        var second = new TextBlock { Name = "second" };
        panel.Children.Add(first);
        panel.Children.Add(second);
        var (host, _) = HostTestUtilities.CreateHost(panel);

        host.ExecuteScriptText("""
const body = document.body;
const first = document.getElementById('first');
const second = document.getElementById('second');
const middle = document.createElement('TextBlock');
middle.setAttribute('name', 'middle');

body.insertBefore(middle, second);
body.removeChild(first);

const replacement = document.createElement('Border');
body.replaceChild(replacement, second);
""");

        Assert.Equal(2, panel.Children.Count);
        var middle = Assert.IsType<TextBlock>(panel.Children[0]);
        Assert.Equal("middle", middle.Name);
        Assert.IsType<Border>(panel.Children[1]);
    }

    [AvaloniaFact]
    public void Require_LoadsModuleFromFile()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var modulePath = Path.Combine(tempDir, "lib.js");
            File.WriteAllText(modulePath, "module.exports = { value: 123 };");

            host.ScriptBaseDirectory = tempDir;
            host.ExecuteScriptText("const lib = require('./lib.js'); globalThis.moduleResult = lib.value;");

            Assert.Equal(123d, Convert.ToDouble(host.Engine.GetValue("moduleResult").ToObject()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [AvaloniaFact]
    public void Require_ExecutesModuleOnlyOnce()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var modulePath = Path.Combine(tempDir, "counter.js");
            File.WriteAllText(modulePath, "globalThis.requireCount = (globalThis.requireCount || 0) + 1; module.exports = { count: globalThis.requireCount };");

            host.ScriptBaseDirectory = tempDir;
            host.ExecuteScriptText("const first = require('./counter.js'); const second = require('./counter.js'); globalThis.firstCount = first.count; globalThis.secondCount = second.count;");

            Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("firstCount").ToObject()));
            Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("secondCount").ToObject()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [AvaloniaFact]
    public void ImportScripts_ExecutesScript()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "script.js");
            File.WriteAllText(scriptPath, "globalThis.loadedValue = (globalThis.loadedValue || 0) + 1;");

            host.ScriptBaseDirectory = tempDir;
            host.ExecuteScriptText("window.importScripts('./script.js');");

            Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("loadedValue").ToObject()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
