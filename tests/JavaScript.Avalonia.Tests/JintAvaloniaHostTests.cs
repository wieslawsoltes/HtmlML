using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    public void TextBoxValue_RoundTripsThroughDom()
    {
        var textBox = new TextBox { Name = "tb" };
        var (host, _) = HostTestUtilities.CreateHost(textBox);

        host.ExecuteScriptText("""
const input = document.getElementById('tb');
input.value = 'help';
globalThis.domValue = input.value;
""");

        Assert.Equal("help", Assert.IsType<string>(host.Engine.GetValue("domValue").ToObject()));
        Assert.Equal("help", textBox.Text);
    }

    [AvaloniaFact]
    public void ImageElement_LoadsDataUriAndDispatchesLoad()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        host.ExecuteScriptText("""
const image = new Image();
globalThis.imageLoaded = false;
image.addEventListener('load', () => {
  globalThis.imageLoaded = true;
});
image.addEventListener('load', function (event) {
  globalThis.imageLoadThisNodeName = this.nodeName;
  globalThis.imageLoadThisWidth = this.width;
  globalThis.imageLoadTargetNodeName = event.target.nodeName;
});
image.onload = function (event) {
  globalThis.imageOnLoadThisNodeName = this.nodeName;
  globalThis.imageOnLoadThisWidth = this.width;
  globalThis.imageOnLoadTargetNodeName = event.target.nodeName;
};
image.src = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAABHNCSVQICAgIfAhkiAAAAA1JREFUCJlj+M/A8B8ABQAB/6vONokAAAAASUVORK5CYII=';
globalThis.imageComplete = image.complete;
globalThis.imageWidth = image.naturalWidth;
globalThis.imageHeight = image.naturalHeight;
""");

        Dispatcher.UIThread.RunJobs();

        Assert.True(Convert.ToBoolean(host.Engine.GetValue("imageComplete").ToObject()));
        Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("imageWidth").ToObject()));
        Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("imageHeight").ToObject()));
        Assert.True(Convert.ToBoolean(host.Engine.GetValue("imageLoaded").ToObject()));
        Assert.Equal("IMG", Assert.IsType<string>(host.Engine.GetValue("imageLoadThisNodeName").ToObject()));
        Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("imageLoadThisWidth").ToObject()));
        Assert.Equal("IMG", Assert.IsType<string>(host.Engine.GetValue("imageLoadTargetNodeName").ToObject()));
        Assert.Equal("IMG", Assert.IsType<string>(host.Engine.GetValue("imageOnLoadThisNodeName").ToObject()));
        Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("imageOnLoadThisWidth").ToObject()));
        Assert.Equal("IMG", Assert.IsType<string>(host.Engine.GetValue("imageOnLoadTargetNodeName").ToObject()));
    }

    [AvaloniaFact]
    public void NavigatorClipboard_RoundTripsText()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        host.ExecuteScriptText("""
navigator.clipboard.writeText('clipboard text');
globalThis.clipboardText = navigator.clipboard.readText();
""");

        Assert.Equal("clipboard text", Assert.IsType<string>(host.Engine.GetValue("clipboardText").ToObject()));
    }

    [AvaloniaFact]
    public void CanvasTarget_CanReceiveFocusFromDom()
    {
        var surface = new Border { Name = "surface", Width = 320, Height = 180 };
        surface.Measure(new Size(640, 480));
        surface.Arrange(new Rect(0, 0, 640, 480));

        var (host, _) = HostTestUtilities.CreateHost(surface);

        host.ExecuteScriptText("""
const surface = document.getElementById('surface');
surface.getContext('2d');
globalThis.focusResult = surface.focus();
globalThis.isActiveSurface = document.activeElement === surface;
""");

        Assert.True(Convert.ToBoolean(host.Engine.GetValue("focusResult").ToObject()));
        Assert.True(Convert.ToBoolean(host.Engine.GetValue("isActiveSurface").ToObject()));
        Assert.True(surface.Focusable);
    }

    [AvaloniaFact]
    public void VirtualActiveElement_ReceivesTopLevelKeyboardAndTextInput()
    {
        var surface = new Border { Name = "surface", Width = 320, Height = 180 };
        surface.Measure(new Size(640, 480));
        surface.Arrange(new Rect(0, 0, 640, 480));

        var (host, window) = HostTestUtilities.CreateHost(surface);

        host.ExecuteScriptText("""
const surface = document.getElementById('surface');
surface.getContext('2d');
globalThis.keyCount = 0;
globalThis.lastKey = '';
globalThis.typedText = '';
surface.addEventListener('keydown', evt => {
    globalThis.keyCount += 1;
    globalThis.lastKey = evt.key;
});
surface.addEventListener('textinput', evt => {
    globalThis.typedText += evt.data;
});
surface.focus();
""");

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = window,
            Key = Key.A
        });

        window.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = window,
            Text = "a"
        });

        Assert.Equal(1d, Convert.ToDouble(host.Engine.GetValue("keyCount").ToObject()));
        Assert.Equal("a", Assert.IsType<string>(host.Engine.GetValue("lastKey").ToObject()));
        Assert.Equal("a", Assert.IsType<string>(host.Engine.GetValue("typedText").ToObject()));
    }

    [AvaloniaFact]
    public void ObjectGetPrototypeOf_ReturnsNullForNullPrototypeObjects()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        host.Engine.Execute("""
const value = Object.create(null);
globalThis.nullPrototype = Object.getPrototypeOf(value);
globalThis.reflectNullPrototype = typeof Reflect !== 'undefined' ? Reflect.getPrototypeOf(value) : undefined;
""");

        Assert.Null(host.Engine.GetValue("nullPrototype").ToObject());
        Assert.Null(host.Engine.GetValue("reflectNullPrototype").ToObject());
    }

    [AvaloniaFact]
    public void DomMatrix_ProvidesCanvasCompatible2DMatrix()
    {
        var (host, _) = HostTestUtilities.CreateHost();

        host.Engine.Execute("""
const matrix = new DOMMatrix([1, 2, 3, 4, 5, 6]).translateSelf(7, 8);
const point = matrix.transformPoint(new DOMPoint(2, 3));
const matrix3d = new DOMMatrix([1, 2, 0, 0, 3, 4, 0, 0, 0, 0, 1, 0, 5, 6, 0, 1]);
globalThis.domMatrixValues = [matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f, point.x, point.y, matrix3d.e, matrix3d.f];
""");

        var values = Assert.IsAssignableFrom<object[]>(host.Engine.GetValue("domMatrixValues").ToObject());
        Assert.Equal(new[] { 1d, 2d, 3d, 4d, 36d, 52d, 47d, 68d, 5d, 6d }, values.Select(Convert.ToDouble).ToArray());
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
    public void Require_ModuleErrorsCanBeCaughtInJavaScript()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var modulePath = Path.Combine(tempDir, "broken.js");
            File.WriteAllText(modulePath, "throw new Error('module failed during bootstrap');");

            host.ScriptBaseDirectory = tempDir;
            host.ExecuteScriptText("""
try {
  require('./broken.js');
  globalThis.requireCaught = false;
} catch (error) {
  globalThis.requireCaught = true;
  globalThis.requireCaughtMessage = String(error);
}
""");

            Assert.True(Convert.ToBoolean(host.Engine.GetValue("requireCaught").ToObject()));
            Assert.Contains("broken.js", Convert.ToString(host.Engine.GetValue("requireCaughtMessage").ToObject()), StringComparison.OrdinalIgnoreCase);
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
    public void Require_LoadsXtermBrowserSample()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "xtermJsSurface", Width = 720, Height = 360 },
                    new TextBox { Name = "xtermJsInput" },
                    new Button { Name = "xtermJsSend" },
                    new Button { Name = "xtermJsDemo" },
                    new Button { Name = "xtermJsClear" },
                    new TextBlock { Name = "xtermJsStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        host.Require("./Scripts/xterm-browser.js");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsStatus")).Control);
        Assert.Equal("xterm.js headless JavaScript demo ready.", status.Text);
    }

    [AvaloniaFact]
    public void Require_XtermBrowserSample_SendButtonExecutesCommand()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "xtermJsSurface", Width = 720, Height = 360 },
                    new TextBox { Name = "xtermJsInput" },
                    new Button { Name = "xtermJsSend" },
                    new Button { Name = "xtermJsDemo" },
                    new Button { Name = "xtermJsClear" },
                    new TextBlock { Name = "xtermJsStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        host.Require("./Scripts/xterm-browser.js");
        host.ExecuteScriptText("""
const input = document.getElementById('xtermJsInput');
const send = document.getElementById('xtermJsSend');
input.value = 'help';
send.dispatchEvent('click');
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsStatus")).Control);
        Assert.Equal("Ran help.", status.Text);

        var input = Assert.IsType<TextBox>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsInput")).Control);
        Assert.Equal(string.Empty, input.Text);
    }

    [AvaloniaFact]
    public void Require_XtermBrowserSample_CanvasTypingExecutesCommand()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "xtermJsSurface", Width = 720, Height = 360 },
                    new TextBox { Name = "xtermJsInput" },
                    new Button { Name = "xtermJsSend" },
                    new Button { Name = "xtermJsDemo" },
                    new Button { Name = "xtermJsClear" },
                    new TextBlock { Name = "xtermJsStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, window) = HostTestUtilities.CreateHost(root);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        host.Require("./Scripts/xterm-browser.js");
        host.ExecuteScriptText("""
const surface = document.getElementById('xtermJsSurface');
surface.focus();
""");

        window.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = window,
            Text = "m"
        });

        window.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = window,
            Text = "c"
        });

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = window,
            Key = Key.Enter
        });

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsStatus")).Control);
        Assert.Equal("Unknown command: mc", status.Text);

        var input = Assert.IsType<TextBox>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsInput")).Control);
        Assert.Equal(string.Empty, input.Text);
    }

    [AvaloniaFact]
    public void Require_XtermBrowserSample_UsesHostShellBridge()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "xtermJsSurface", Width = 720, Height = 360 },
                    new TextBox { Name = "xtermJsInput" },
                    new Button { Name = "xtermJsSend" },
                    new Button { Name = "xtermJsDemo" },
                    new Button { Name = "xtermJsClear" },
                    new TextBlock { Name = "xtermJsStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        var shell = new MockHostShellBridge();
        host.Engine.SetValue("hostShell", shell);
        host.Engine.Execute("""
if (typeof window !== 'undefined') {
  window.hostShell = hostShell;
}
if (typeof globalThis !== 'undefined') {
  globalThis.hostShell = hostShell;
}
""");

        host.Require("./Scripts/xterm-browser.js");
        host.ExecuteScriptText("""
const input = document.getElementById('xtermJsInput');
const send = document.getElementById('xtermJsSend');
input.value = 'ls';
send.dispatchEvent('click');
""");

        Assert.Equal(new[] { "ls" }, shell.commands);
        Assert.Single(shell.terminalSizes);
        Assert.True(shell.terminalSizes[0].Columns >= 40);
        Assert.True(shell.terminalSizes[0].Rows >= 12);

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsStatus")).Control);
        Assert.Equal("Shell command exited with code 0.", status.Text);
    }

    [AvaloniaFact]
    public void Require_XtermBrowserSample_StartsTuiCommandInTerminalContext()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "xtermJsSurface", Width = 1120, Height = 640 },
                    new TextBox { Name = "xtermJsInput" },
                    new Button { Name = "xtermJsSend" },
                    new Button { Name = "xtermJsDemo" },
                    new Button { Name = "xtermJsClear" },
                    new TextBlock { Name = "xtermJsStatus" }
                }
            }
        };

        root.Measure(new Size(1200, 760));
        root.Arrange(new Rect(0, 0, 1200, 760));

        var (host, _) = HostTestUtilities.CreateHost(root);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        var shell = new MockHostShellBridge { supportsTty = true };
        host.Engine.SetValue("hostShell", shell);
        host.Engine.Execute("""
if (typeof window !== 'undefined') {
  window.hostShell = hostShell;
}
if (typeof globalThis !== 'undefined') {
  globalThis.hostShell = hostShell;
}
""");

        host.Require("./Scripts/xterm-browser.js");
        host.ExecuteScriptText("""
const input = document.getElementById('xtermJsInput');
const send = document.getElementById('xtermJsSend');
input.value = 'btop';
send.dispatchEvent('click');
""");

        var started = Assert.Single(shell.sessions);
        Assert.Equal("btop", started.Command);
        Assert.True(started.Columns >= 60);
        Assert.True(started.Rows >= 24);

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("xtermJsStatus")).Control);
        Assert.Contains("Started btop", status.Text);
    }

    [AvaloniaFact]
    public void Require_LoadsXtermTypeScriptSample()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "xtermSurface", Width = 720, Height = 360 },
                    new TextBox { Name = "xtermInput" },
                    new Button { Name = "xtermSend" },
                    new Button { Name = "xtermDemo" },
                    new Button { Name = "xtermClear" },
                    new TextBlock { Name = "xtermStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        host.Require("./Scripts/xterm-demo.ts");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("xtermStatus")).Control);
        Assert.Equal("xterm.js headless ready — try the demo or type help.", status.Text);
    }

    [AvaloniaFact]
    public void ChartJs_CanRenderWithCanvasShim()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "chartSurface", Width = 540, Height = 300, Background = Brushes.White },
                    new Button { Name = "chartRandomize" },
                    new Button { Name = "chartToggle" },
                    new TextBlock { Name = "chartStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 640,
            Height = 420,
            Content = new VisualLayerManager { Child = root }
        };

        var host = new JintAvaloniaHost(window);

        host.Engine.Execute("""
const surface = document.getElementById('chartSurface');
const status = document.getElementById('chartStatus');
const context = surface.getContext('2d');

let logicalWidth = surface.offsetWidth;
let logicalHeight = surface.offsetHeight;
const updateLogicalSize = (width, height) => {
  if (typeof width === 'number' && !Number.isNaN(width)) {
    logicalWidth = width;
  }
  if (typeof height === 'number' && !Number.isNaN(height)) {
    logicalHeight = height;
  }
};

const attributes = new Map([
  ['width', String(logicalWidth)],
  ['height', String(logicalHeight)]
]);

const styleState = {
  display: 'block',
  width: `${surface.offsetWidth}px`,
  height: `${surface.offsetHeight}px`,
  boxSizing: 'border-box',
  getPropertyValue(property) {
    const key = String(property ?? '')
      .replace(/-([a-z])/g, (_, value) => value.toUpperCase());
    const raw = this[key];
    return raw == null ? '' : String(raw);
  }
};

const syncCanvasState = () => {
  attributes.set('width', String(logicalWidth));
  attributes.set('height', String(logicalHeight));
  styleState.width = `${logicalWidth}px`;
  styleState.height = `${logicalHeight}px`;
};

const canvasElement = {
  nodeName: 'CANVAS',
  style: styleState,
  ownerDocument: surface.ownerDocument ?? document,
  defaultView: typeof window !== 'undefined' ? window : undefined,
  parentNode: surface.parentNode ?? surface.parentElement ?? surface.ownerDocument?.body ?? null,
  parentElement: surface.parentElement ?? surface.parentNode ?? surface.ownerDocument?.body ?? null,
  getContext: () => context,
  addEventListener: () => {},
  removeEventListener: () => {},
  dispatchEvent: () => false,
  getAttribute: name => attributes.get(String(name ?? '').toLowerCase()) ?? null,
  setAttribute: (name, value) => {
    const key = String(name ?? '').toLowerCase();
    const text = value == null ? '' : String(value);
    attributes.set(key, text);
    if (key === 'width') {
      updateLogicalSize(Number(text), logicalHeight);
    } else if (key === 'height') {
      updateLogicalSize(logicalWidth, Number(text));
    }
    syncCanvasState();
  },
  removeAttribute: name => {
    const key = String(name ?? '').toLowerCase();
    attributes.delete(key);
    if (key === 'width') {
      updateLogicalSize(surface.offsetWidth, logicalHeight);
    } else if (key === 'height') {
      updateLogicalSize(logicalWidth, surface.offsetHeight);
    }
    syncCanvasState();
  },
  getBoundingClientRect: () => ({
    width: surface.offsetWidth,
    height: surface.offsetHeight,
    top: 0,
    left: 0,
    right: surface.offsetWidth,
    bottom: surface.offsetHeight
  })
};

Object.defineProperties(canvasElement, {
  width: {
    configurable: true,
    enumerable: true,
    get: () => logicalWidth,
    set: value => updateLogicalSize(value, logicalHeight)
  },
  height: {
    configurable: true,
    enumerable: true,
    get: () => logicalHeight,
    set: value => updateLogicalSize(logicalWidth, value)
  },
  clientWidth: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetWidth
  },
  clientHeight: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetHeight
  },
  offsetWidth: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetWidth
  },
  offsetHeight: {
    configurable: true,
    enumerable: true,
    get: () => surface.offsetHeight
  }
});

syncCanvasState();
if (canvasElement.ownerDocument && typeof canvasElement.ownerDocument.defaultView === 'undefined' && typeof window !== 'undefined') {
  canvasElement.ownerDocument.defaultView = window;
}
context.canvas = canvasElement;

const chartModule = require('https://cdn.jsdelivr.net/npm/chart.js@4.4.3/dist/chart.umd.js');
const Chart = chartModule?.Chart ?? chartModule?.default ?? chartModule;
if (typeof Chart.register === 'function' && chartModule?.registerables) {
  Chart.register(...chartModule.registerables);
}

const BasePlatform = Chart?.BasicPlatform ?? chartModule?.BasicPlatform;
const AvaloniaChartPlatform = typeof BasePlatform === 'function'
  ? class extends BasePlatform {}
  : undefined;

const helpers = Chart?.helpers ?? chartModule?.helpers;
if (helpers?.canvas?.acquireContext && !helpers.canvas.__avaloniaPatched) {
  const originalAcquire = helpers.canvas.acquireContext.bind(helpers.canvas);
  helpers.canvas.acquireContext = (item, ...args) => {
    if (item === surface || item === canvasElement || item === context) {
      return context;
    }
    return originalAcquire(item, ...args);
  };
  helpers.canvas.__avaloniaPatched = true;
}

const chart = new Chart(canvasElement, {
  platform: AvaloniaChartPlatform,
  type: 'line',
  data: {
    labels: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'],
    datasets: [{
      label: 'Active sessions',
      data: [41, 62, 53, 74, 68, 59, 81],
      borderColor: '#2563eb',
      backgroundColor: '#93c5fd',
      borderWidth: 2,
      fill: true,
      tension: 0.45,
      pointRadius: 4,
      pointBackgroundColor: '#1d4ed8',
      hoverRadius: 6
    }]
  },
  options: {
    responsive: false,
    animation: false,
    scales: {
      x: {
        ticks: { color: '#64748b' },
        grid: { color: 'rgba(148, 163, 184, 0.18)' }
      },
      y: {
        beginAtZero: true,
        suggestedMax: 120,
        ticks: { color: '#64748b' },
        grid: { color: 'rgba(148, 163, 184, 0.12)' }
      }
    },
    plugins: {
      legend: {
        labels: { color: '#0f172a' }
      },
      tooltip: {
        backgroundColor: '#0f172a',
        borderColor: '#1d4ed8',
        borderWidth: 1,
        padding: 12
      }
    }
  }
});
status.textContent = chart ? 'Chart.js chart created' : 'Chart.js chart missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("chartStatus")).Control);
        Assert.Equal("Chart.js chart created", status.Text);

        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("chartSurface"));
        var context = Assert.IsType<CanvasRenderingContext2D>(surfaceElement.getContext("2d"));
        Assert.True(context.CommandCount > 0);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.True(
                HasNonWhitePixel(frame!, 0, 0, 540, 300),
                "Expected Chart.js to render visible canvas pixels, but the chart surface stayed blank.");
        }
    }

    [AvaloniaFact]
    public void FabricJs_CanInitializeCanvasPreset()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "fabricSurface", Width = 560, Height = 320, Background = Brushes.White },
                    new TextBlock { Name = "fabricStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 640,
            Height = 420,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);

        host.Engine.Execute("""
const surface = document.getElementById('fabricSurface');
const status = document.getElementById('fabricStatus');

surface.width = surface.offsetWidth;
surface.height = surface.offsetHeight;

const fabricModule = require('https://cdn.jsdelivr.net/npm/fabric@5.3.0/dist/fabric.min.js');
const fabricGlobal = typeof window !== 'undefined' ? window.fabric : undefined;
const fabric = fabricModule?.fabric ?? fabricModule?.default ?? fabricGlobal ?? fabricModule;

const canvas = new fabric.Canvas(surface, {
  selection: true,
  backgroundColor: '#f8fafc',
  preserveObjectStacking: true
});
canvas.setDimensions({ width: surface.offsetWidth, height: surface.offsetHeight });
const circle = new fabric.Circle({
  left: 320,
  top: 90,
  radius: 70,
  fill: '#ec4899',
  stroke: '#be123c',
  strokeWidth: 2
});
canvas.add(
  new fabric.Rect({
    left: 80,
    top: 70,
    width: 200,
    height: 140,
    rx: 26,
    ry: 26,
    fill: '#3b82f6',
    stroke: '#1e3a8a',
    strokeWidth: 2
  }),
  circle,
  new fabric.Triangle({
    left: 235,
    top: 230,
    width: 96,
    height: 72,
    fill: '#f59e0b',
    stroke: '#92400e',
    strokeWidth: 2,
    angle: -8
  })
);
canvas.renderAll();
canvas.discardActiveObject();
globalThis.fabricTestCanvas = canvas;
globalThis.fabricUpperCanvas = canvas.upperCanvasEl ?? surface;
status.textContent = canvas ? 'Fabric.js scene ready' : 'Fabric.js canvas missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("fabricStatus")).Control);
        Assert.Equal("Fabric.js scene ready", status.Text);

        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("fabricSurface"));
        var context = Assert.IsType<CanvasRenderingContext2D>(surfaceElement.getContext("2d"));
        Assert.True(context.CommandCount > 0);

        var upperCanvasElement = Assert.IsType<AvaloniaDomElement>(host.Engine.GetValue("fabricUpperCanvas").ToObject());
        using var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var pointerDown = new PointerPressedEventArgs(
            upperCanvasElement.Control,
            pointer,
            upperCanvasElement.Control,
            new Point(390, 160),
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None);

        upperCanvasElement.Control.RaiseEvent(pointerDown);

        Assert.Equal(
            "circle",
            Convert.ToString(host.Engine.Evaluate("globalThis.fabricTestCanvas.getActiveObject()?.type ?? ''").ToObject()));

        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.True(
                HasNonWhitePixel(frame!, 0, 0, 560, 320),
                "Expected Fabric.js to render visible canvas pixels, but the canvas surface stayed blank.");
        }
    }

    [AvaloniaFact]
    public void WebGl_CanRenderIndexedTriangle()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "webglSurface", Width = 180, Height = 140, Background = Brushes.White },
                    new TextBlock { Name = "webglStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 240,
            Height = 220,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);
        host.Engine.Execute("""
const surface = document.getElementById('webglSurface');
const status = document.getElementById('webglStatus');
const gl = surface.getContext('webgl');
if (!gl) {
  throw new Error('WebGL context unavailable');
}

gl.viewport(0, 0, surface.offsetWidth, surface.offsetHeight);
gl.clearColor(1, 1, 1, 1);
gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

const vertexShader = gl.createShader(gl.VERTEX_SHADER);
gl.shaderSource(vertexShader, `
attribute vec3 position;
uniform mat4 modelViewMatrix;
uniform mat4 projectionMatrix;
void main() {
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`);
gl.compileShader(vertexShader);

const fragmentShader = gl.createShader(gl.FRAGMENT_SHADER);
gl.shaderSource(fragmentShader, `
precision mediump float;
uniform vec3 diffuse;
uniform float opacity;
void main() {
  gl_FragColor = vec4(diffuse, opacity);
}`);
gl.compileShader(fragmentShader);

const program = gl.createProgram();
gl.attachShader(program, vertexShader);
gl.attachShader(program, fragmentShader);
gl.linkProgram(program);
gl.useProgram(program);

const position = gl.getAttribLocation(program, 'position');
const positionBuffer = gl.createBuffer();
gl.bindBuffer(gl.ARRAY_BUFFER, positionBuffer);
gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([
  -0.8, -0.7, 0,
   0.8, -0.7, 0,
   0.0,  0.8, 0
]), gl.STATIC_DRAW);
gl.enableVertexAttribArray(position);
gl.vertexAttribPointer(position, 3, gl.FLOAT, false, 0, 0);

const indexBuffer = gl.createBuffer();
gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array([0, 1, 2]), gl.STATIC_DRAW);

gl.uniformMatrix4fv(gl.getUniformLocation(program, 'modelViewMatrix'), false, new Float32Array([
  1, 0, 0, 0,
  0, 1, 0, 0,
  0, 0, 1, 0,
  0, 0, 0, 1
]));
gl.uniformMatrix4fv(gl.getUniformLocation(program, 'projectionMatrix'), false, new Float32Array([
  1, 0, 0, 0,
  0, 1, 0, 0,
  0, 0, 1, 0,
  0, 0, 0, 1
]));
gl.uniform3f(gl.getUniformLocation(program, 'diffuse'), 0.1, 0.45, 0.95);
gl.uniform1f(gl.getUniformLocation(program, 'opacity'), 1);
gl.drawElements(gl.TRIANGLES, 3, gl.UNSIGNED_SHORT, 0);

status.textContent = gl.CommandCount > 0 ? 'WebGL triangle rendered' : 'WebGL triangle missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("webglStatus")).Control);
        Assert.Equal("WebGL triangle rendered", status.Text);

        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("webglSurface"));
        var context = Assert.IsType<CanvasWebGlRenderingContext>(surfaceElement.getContext("webgl"));
        Assert.True(context.CommandCount > 0);

        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.True(context.TriangleCount > 0, context.LastDrawStatus);
        }
    }

    [AvaloniaFact]
    public void ThreeJs_CanRenderBasicWebGlScene()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "threeSurface", Width = 220, Height = 160, Background = Brushes.White },
                    new TextBlock { Name = "threeStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 300,
            Height = 240,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);
        host.Engine.Execute("""
const surface = document.getElementById('threeSurface');
const status = document.getElementById('threeStatus');
const gl = surface.getContext('webgl');
const threeModule = require('https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.min.js');
const THREE = threeModule?.REVISION ? threeModule : window.THREE;
if (!THREE) {
  throw new Error('Three.js did not load');
}

const renderer = new THREE.WebGLRenderer({
  canvas: surface,
  context: gl,
  antialias: false,
  alpha: true
});
renderer.setSize(surface.offsetWidth, surface.offsetHeight, false);
renderer.setPixelRatio(1);
renderer.setClearColor(0xffffff, 1);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(60, surface.offsetWidth / surface.offsetHeight, 0.1, 10);
camera.position.z = 2.4;

const geometry = new THREE.BoxGeometry(1, 1, 1);
const material = new THREE.MeshBasicMaterial({ color: 0x2563eb });
const cube = new THREE.Mesh(geometry, material);
cube.rotation.x = 0.35;
cube.rotation.y = 0.55;
scene.add(cube);

renderer.render(scene, camera);
status.textContent = gl.CommandCount > 0 ? `Three.js ${THREE.REVISION} rendered` : 'Three.js render missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("threeStatus")).Control);
        Assert.Contains("Three.js", status.Text);

        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("threeSurface"));
        var context = Assert.IsType<CanvasWebGlRenderingContext>(surfaceElement.getContext("webgl"));
        Assert.True(context.CommandCount > 0);
        Assert.True(context.DrawCallCount > 0, context.LastDrawStatus);
        Assert.True(context.TriangleCount > 0, context.LastDrawStatus);

        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.True(
                HasNonWhitePixel(frame!, 0, 0, 220, 160),
                $"Expected Three.js WebGL scene to produce visible pixels. {context.LastDrawStatus}");
        }

        Assert.NotEqual("not rendered", context.RenderBackend);
    }

    [AvaloniaFact]
    public void NativeWebGlCanvas_WidthHeightSetDrawingBufferNotLayout()
    {
        var surface = new CanvasOpenGlDrawingSurface
        {
            Name = "nativeSurface",
            Width = 720,
            Height = 420
        };
        var window = new Window
        {
            Width = 800,
            Height = 500,
            Content = new VisualLayerManager { Child = surface }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);
        var element = HostTestUtilities.GetElement(host.Document.getElementById("nativeSurface"));

        element.width = 1440;
        element.height = 840;

        var context = Assert.IsType<CanvasWebGlRenderingContext>(element.getContext("webgl"));

        Assert.Equal(720, surface.Width);
        Assert.Equal(420, surface.Height);
        Assert.Equal(720, element.offsetWidth);
        Assert.Equal(420, element.offsetHeight);
        Assert.Equal(1440, element.width);
        Assert.Equal(840, element.height);
        Assert.Equal(1440, surface.DrawingBufferWidth);
        Assert.Equal(840, surface.DrawingBufferHeight);
        Assert.Equal(1440, context.drawingBufferWidth);
        Assert.Equal(840, context.drawingBufferHeight);
    }

    [AvaloniaFact]
    public void WebGl_FramebufferBindingsExposeRenderTargetState()
    {
        var root = new Border
        {
            Name = "webglSurface",
            Width = 128,
            Height = 96,
            Background = Brushes.White
        };
        var window = new Window
        {
            Width = 180,
            Height = 140,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);
        host.Engine.Execute("""
const surface = document.getElementById('webglSurface');
const gl = surface.getContext('webgl');
const texture = gl.createTexture();
gl.bindTexture(gl.TEXTURE_2D, texture);
gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 32, 16, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

const depth = gl.createRenderbuffer();
gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, 32, 16);

const framebuffer = gl.createFramebuffer();
gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);

globalThis.framebufferComplete = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
globalThis.framebufferBound = gl.getParameter(gl.FRAMEBUFFER_BINDING) === framebuffer;
globalThis.renderbufferBound = gl.getParameter(gl.RENDERBUFFER_BINDING) === depth;
gl.clearColor(0, 0, 0, 1);
gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
gl.bindFramebuffer(gl.FRAMEBUFFER, null);
globalThis.framebufferCleared = gl.getParameter(gl.FRAMEBUFFER_BINDING) === null;
""");

        Assert.True(Convert.ToBoolean(host.Engine.GetValue("framebufferComplete").ToObject()));
        Assert.True(Convert.ToBoolean(host.Engine.GetValue("framebufferBound").ToObject()));
        Assert.True(Convert.ToBoolean(host.Engine.GetValue("renderbufferBound").ToObject()));
        Assert.True(Convert.ToBoolean(host.Engine.GetValue("framebufferCleared").ToObject()));
    }

    [AvaloniaFact]
    public void ThreeJs_CanCompositeWebGlRenderTarget()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "targetSurface", Width = 220, Height = 160, Background = Brushes.White },
                    new TextBlock { Name = "targetStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 300,
            Height = 240,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);
        host.Engine.Execute("""
const surface = document.getElementById('targetSurface');
const status = document.getElementById('targetStatus');
const gl = surface.getContext('webgl');
const halfFloat = gl.getExtension('OES_texture_half_float');
const colorBufferHalfFloat = gl.getExtension('EXT_color_buffer_half_float');
const threeModule = require('https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.min.js');
const THREE = threeModule?.REVISION ? threeModule : window.THREE;

const renderer = new THREE.WebGLRenderer({
  canvas: surface,
  context: gl,
  antialias: false,
  alpha: false
});
renderer.setSize(surface.offsetWidth, surface.offsetHeight, false);
renderer.setPixelRatio(1);
renderer.setClearColor(0xffffff, 1);
renderer.autoClear = false;

const target = new THREE.WebGLRenderTarget(surface.offsetWidth, surface.offsetHeight, {
  minFilter: THREE.LinearFilter,
  magFilter: THREE.LinearFilter,
  format: THREE.RGBAFormat,
  type: THREE.HalfFloatType,
  stencilBuffer: false
});

const scene = new THREE.Scene();
const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
scene.add(new THREE.Mesh(
  new THREE.PlaneGeometry(1.5, 1.1),
  new THREE.MeshBasicMaterial({ color: 0xf97316 })
));

const screenScene = new THREE.Scene();
const screenCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
screenScene.add(new THREE.Mesh(
  new THREE.PlaneGeometry(2, 2),
  new THREE.ShaderMaterial({
    uniforms: { tDiffuse: { value: target.texture } },
    vertexShader: `
varying vec2 vUv;
void main() {
  vUv = uv;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`,
    fragmentShader: `
uniform sampler2D tDiffuse;
varying vec2 vUv;
void main() {
  gl_FragColor = texture2D(tDiffuse, vUv);
}`
  })
));

renderer.clear();
renderer.setRenderTarget(target);
renderer.clear();
renderer.render(scene, camera);
renderer.setRenderTarget(null);
renderer.render(screenScene, screenCamera);
globalThis.halfFloatExtensionAvailable = !!halfFloat && !!colorBufferHalfFloat;
status.textContent = gl.DrawCallCount > 0 && gl.CommandCount > 1 ? 'half-float render target composited' : 'render target missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("targetStatus")).Control);
        Assert.Equal("half-float render target composited", status.Text);
        Assert.True(Convert.ToBoolean(host.Engine.GetValue("halfFloatExtensionAvailable").ToObject()));

        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("targetSurface"));
        var context = Assert.IsType<CanvasWebGlRenderingContext>(surfaceElement.getContext("webgl"));
        Assert.True(context.CommandCount > 1);

        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.True(
                HasNonWhitePixel(frame!, 0, 0, 220, 160),
                $"Expected Three.js render target composition to produce visible pixels. {context.LastDrawStatus}");
        }
    }

    [AvaloniaFact]
    public void PaperJs_CanInitializeCanvasPreset()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "paperSurface", Width = 560, Height = 320, Background = Brushes.White },
                    new TextBlock { Name = "paperStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 640,
            Height = 420,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);

        host.Engine.Execute("""
const surface = document.getElementById('paperSurface');
const status = document.getElementById('paperStatus');

const paperContext = surface.getContext('2d');
const paperCanvas = paperContext.canvas ?? surface;
paperCanvas.width = surface.offsetWidth;
paperCanvas.height = surface.offsetHeight;

const paperModule = require('https://cdn.jsdelivr.net/npm/paper@0.12.17/dist/paper-full.min.js');
const paperGlobal = typeof window !== 'undefined' ? window.paper : undefined;
const paper = paperModule?.paper ?? paperModule?.default ?? paperGlobal ?? paperModule;

paper.setup(paperCanvas);
const circle = new paper.Path.Circle({
  center: [120, 90],
  radius: 48,
  fillColor: '#38bdf8'
});
paper.view.update();
status.textContent = circle ? 'Paper.js scene ready' : 'Paper.js scene missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("paperStatus")).Control);
        Assert.Equal("Paper.js scene ready", status.Text);

        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("paperSurface"));
        var context = Assert.IsType<CanvasRenderingContext2D>(surfaceElement.getContext("2d"));
        Assert.True(context.CommandCount > 0);

        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.True(
                HasNonWhitePixel(frame!, 0, 0, 560, 320),
                "Expected Paper.js to render visible canvas pixels, but the scene surface stayed blank.");
        }
    }

    [AvaloniaFact]
    public void PdfJs_CanLoadBuiltInSample()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "pdfSurface", Width = 640, Height = 460, Background = Brushes.White },
                    new Button { Name = "pdfOpen" },
                    new Button { Name = "pdfBuiltIn" },
                    new Button { Name = "pdfPrev" },
                    new Button { Name = "pdfNext" },
                    new Button { Name = "pdfZoomIn" },
                    new Button { Name = "pdfZoomOut" },
                    new TextBlock { Name = "pdfInfo" },
                    new TextBlock { Name = "pdfStatus" }
                }
            }
        };
        var window = new Window
        {
            Width = 760,
            Height = 620,
            Content = new VisualLayerManager { Child = root }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var host = new JintAvaloniaHost(window);
        host.ScriptBaseDirectory = Path.Combine(GetRepositoryRoot(), "samples", "JavaScriptPlayground");

        host.Require("./Scripts/pdf-demo.js");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("pdfStatus")).Control);
        var info = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("pdfInfo")).Control);

        Assert.True(
            WaitFor(host, () =>
                info.Text?.Contains("Built-in sample.pdf", StringComparison.Ordinal) == true &&
                status.Text?.Contains("PDF.js", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(20)),
            $"PDF.js sample did not finish loading. Stage: {host.Engine.GetValue("pdfDemoStage")}. Status: {status.Text}");

        Assert.Contains("PDF.js", status.Text);
        var surfaceElement = HostTestUtilities.GetElement(host.Document.getElementById("pdfSurface"));
        var context = Assert.IsType<CanvasRenderingContext2D>(surfaceElement.getContext("2d"));
        Assert.True(context.CommandCount > 0);
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

    [AvaloniaFact]
    public void Require_TranspilesTypeScriptModule()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var modulePath = Path.Combine(tempDir, "answer.ts");
            File.WriteAllText(modulePath, "export const answer: number = 21 * 2;");

            host.ScriptBaseDirectory = tempDir;
            host.ExecuteScriptText("const mod = require('./answer.ts'); globalThis.transpiledAnswer = mod.answer;");

            Assert.Equal(42d, Convert.ToDouble(host.Engine.GetValue("transpiledAnswer").ToObject()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [AvaloniaFact]
    public void TypeScriptRuntime_RespectsRegisteredLibraries()
    {
        var (host, _) = HostTestUtilities.CreateHost();
        host.TypeScript.DefaultOptions.Strict = true;
        host.TypeScript.AddLibrary("typed-add.d.ts", "declare function typedAdd(a: number, b: number): number;");

        var inlineResult = host.TypeScript.Transpile("inline.ts", "const value = typedAdd(1, 2); export const result = value;", host.TypeScript.DefaultOptions);
        Assert.Empty(inlineResult.Diagnostics);

        host.Engine.SetValue("typedAdd", new Func<double, double, double>((a, b) => a + b));

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var modulePath = Path.Combine(tempDir, "typed-module.ts");
            File.WriteAllText(modulePath, "const sum: number = typedAdd(19, 23); export const value = sum;");

            host.ScriptBaseDirectory = tempDir;
            host.ExecuteScriptText("const mod = require('./typed-module.ts'); globalThis.typeLibValue = mod.value;");

            Assert.Equal(42d, Convert.ToDouble(host.Engine.GetValue("typeLibValue").ToObject()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HtmlML.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
        => WaitFor(null, condition, timeout);

    private static bool WaitFor(JintAvaloniaHost? host, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            host?.ProcessPendingTasks();
            if (condition())
            {
                return true;
            }

            Task.Delay(50).GetAwaiter().GetResult();
        }

        Dispatcher.UIThread.RunJobs();
        host?.ProcessPendingTasks();
        return condition();
    }

    private sealed class MockHostShellBridge
    {
        public List<string> commands { get; } = new();

        public List<(int Columns, int Rows)> terminalSizes { get; } = new();

        public List<MockHostShellSession> sessions { get; } = new();

        public string shell => "mocksh";

        public string cwd => "/mock";

        public bool supportsTty { get; set; }

        public MockHostShellResult execute(string command)
            => execute(command, 15000, 0, 0);

        public MockHostShellResult execute(string command, int timeoutMs)
            => execute(command, timeoutMs, 0, 0);

        public MockHostShellResult execute(string command, int timeoutMs, int columns, int rows)
        {
            commands.Add(command);
            terminalSizes.Add((columns, rows));
            return new MockHostShellResult
            {
                exitCode = 0,
                stdout = "file-a\nfile-b\n",
                output = "file-a\nfile-b\n",
                timeoutMs = timeoutMs,
                shell = shell,
                cwd = cwd,
                success = true
            };
        }

        public MockHostShellSession start(string command, int columns, int rows)
        {
            var session = new MockHostShellSession(command, columns, rows);
            sessions.Add(session);
            return session;
        }
    }

    private sealed class MockHostShellSession
    {
        public MockHostShellSession(string command, int columns, int rows)
        {
            Command = command;
            Columns = columns;
            Rows = rows;
        }

        public string Command { get; }

        public int Columns { get; }

        public int Rows { get; }

        public bool isRunning => true;

        public int exitCode => 0;

        public string read() => string.Empty;

        public bool write(string data) => true;

        public bool resize(int columns, int rows) => true;

        public void kill()
        {
        }
    }

    private sealed class MockHostShellResult
    {
        public int exitCode { get; init; }

        public string stdout { get; init; } = string.Empty;

        public string stderr { get; init; } = string.Empty;

        public string output { get; init; } = string.Empty;

        public bool timedOut { get; init; }

        public int timeoutMs { get; init; }

        public string shell { get; init; } = string.Empty;

        public string cwd { get; init; } = string.Empty;

        public bool success { get; init; }
    }

    private static bool HasNonWhitePixel(Bitmap bitmap, int startX, int startY, int width, int height)
    {
        var maxX = Math.Min(bitmap.PixelSize.Width, startX + width);
        var maxY = Math.Min(bitmap.PixelSize.Height, startY + height);
        for (var y = Math.Max(0, startY); y < maxY; y += 2)
        {
            for (var x = Math.Max(0, startX); x < maxX; x += 2)
            {
                var pixel = ReadPixel(bitmap, x, y);
                if (pixel.A > 0 && (pixel.R < 245 || pixel.G < 245 || pixel.B < 245))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Color ReadPixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), buffer.Length, 4);
        }
        finally
        {
            handle.Free();
        }

        var format = bitmap.Format ?? PixelFormat.Bgra8888;
        if (format == PixelFormat.Bgra8888 || format == PixelFormat.Rgb32)
        {
            return Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
        }

        if (format == PixelFormat.Rgba8888)
        {
            return Color.FromArgb(buffer[3], buffer[0], buffer[1], buffer[2]);
        }

        throw new NotSupportedException($"Unsupported screenshot pixel format: {format}.");
    }
}
