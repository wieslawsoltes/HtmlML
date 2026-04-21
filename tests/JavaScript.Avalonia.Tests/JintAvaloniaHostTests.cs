using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "chartSurface", Width = 540, Height = 300 },
                    new Button { Name = "chartRandomize" },
                    new Button { Name = "chartToggle" },
                    new TextBlock { Name = "chartStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);

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
  ? class extends BasePlatform {
      updateConfig() {}
    }
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
    }

    [AvaloniaFact]
    public void FabricJs_CanInitializeCanvasPreset()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "fabricSurface", Width = 560, Height = 320 },
                    new TextBlock { Name = "fabricStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);

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
status.textContent = canvas ? 'Fabric.js scene ready' : 'Fabric.js canvas missing';
""");

        var status = Assert.IsType<TextBlock>(HostTestUtilities.GetElement(host.Document.getElementById("fabricStatus")).Control);
        Assert.Equal("Fabric.js scene ready", status.Text);
    }

    [AvaloniaFact]
    public void PaperJs_CanInitializeCanvasPreset()
    {
        var root = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Name = "paperSurface", Width = 560, Height = 320 },
                    new TextBlock { Name = "paperStatus" }
                }
            }
        };

        root.Measure(new Size(900, 700));
        root.Arrange(new Rect(0, 0, 900, 700));

        var (host, _) = HostTestUtilities.CreateHost(root);

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
}
