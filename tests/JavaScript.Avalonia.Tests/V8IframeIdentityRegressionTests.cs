using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JavaScript.Avalonia.ClearScript;
using Xunit;

namespace JavaScript.Avalonia.Tests;

public sealed class V8IframeIdentityRegressionTests
{
    [AvaloniaFact]
    [Trait("Runtime", "V8Native")]
    public void ConnectedSrcLessIframeSynchronouslyExposesMutableAboutBlankBodyIdentity()
    {
        var nativePath = Environment.GetEnvironmentVariable("HTMLML_CLEARSCRIPT_NATIVE");
        if (string.IsNullOrWhiteSpace(nativePath) || !File.Exists(nativePath))
        {
            return;
        }

        var window = new Window
        {
            Width = 320,
            Height = 160,
            Content = new CssLayoutPanel()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var host = new AvaloniaBrowserHost(window);
        using var runtime = new ClearScriptV8Runtime(
            host,
            new ClearScriptV8RuntimeOptions
            {
                EnableTrustedSameOriginContextSharing = true
            });
        try
        {
            runtime.Execute("""
                const iframe = document.createElement('iframe');
                document.body.appendChild(iframe);
                const frameDocument = iframe.contentDocument;
                const state = globalThis.__htmlMlIframeIdentity = {
                  hadDocumentSynchronously: !!frameDocument,
                  hadBodySynchronously: !!(frameDocument && frameDocument.body),
                  initialLocation: frameDocument && frameDocument.location.href
                };

                frameDocument.body.id = 'frame-body';
                frameDocument.body.name = 'frame-name';
                const input = frameDocument.createElement('input');
                input.id = 'inside';
                input.name = 'inside-name';
                frameDocument.body.appendChild(input);

                state.bodyId = frameDocument.body.id;
                state.bodyName = frameDocument.body.name;
                state.idLookup = frameDocument.getElementById('inside') === input;
                state.idSelector = frameDocument.querySelector('#inside') === input;
                state.nameSelector = frameDocument.querySelector('[name="inside-name"]') === input;
                """, "v8-src-less-iframe-identity-regression.js");

            using var result = JsonDocument.Parse(Convert.ToString(runtime.Engine.Evaluate(
                "JSON.stringify(globalThis.__htmlMlIframeIdentity)")) ?? "{}");
            var state = result.RootElement;
            Assert.True(state.GetProperty("hadDocumentSynchronously").GetBoolean());
            Assert.True(state.GetProperty("hadBodySynchronously").GetBoolean());
            Assert.Equal("about:blank", state.GetProperty("initialLocation").GetString());
            Assert.Equal("frame-body", state.GetProperty("bodyId").GetString());
            Assert.Equal("frame-name", state.GetProperty("bodyName").GetString());
            Assert.True(state.GetProperty("idLookup").GetBoolean());
            Assert.True(state.GetProperty("idSelector").GetBoolean());
            Assert.True(state.GetProperty("nameSelector").GetBoolean());
            Assert.Empty(host.JavaScriptExceptionDiagnostics);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
