using System.Text.Json;
using HtmlML.Sdk;
using Xunit;

namespace HtmlML.Sdk.Tests;

public sealed class HostBridgeTests
{
    [Fact]
    public async Task BridgeRequiresDeclarationAndGrantThenReturnsJson()
    {
        var diagnostics = new HtmlMlDiagnosticCollector();
        var handler = new HtmlMlDelegateCapabilityHandler(
            HtmlMlComponentCapabilities.Commands,
            (method, arguments, _) =>
            {
                Assert.Equal("save", method);
                return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    saved = arguments.GetProperty("id").GetInt32()
                }));
            });
        var bridge = new HtmlMlHostBridge(ComponentManifestTests.CreateManifest(), [handler], diagnostics);
        var response = await bridge.InvokeAsync(Request(HtmlMlComponentCapabilities.Commands));

        Assert.True(response.Ok);
        Assert.Equal(42, response.Result!.Value.GetProperty("saved").GetInt32());
        Assert.Contains(diagnostics.Diagnostics, static value => value.Code == "bridge.completed");

        var notDeclared = await bridge.InvokeAsync(Request(HtmlMlComponentCapabilities.FileSelection));
        Assert.False(notDeclared.Ok);
        Assert.Equal("bridge.capability.denied", notDeclared.Error!.Code);
    }

    [Fact]
    public async Task BridgeReportsUnavailableHandlerExceptionsVersionsAndCancellation()
    {
        var manifest = ComponentManifestTests.CreateManifest() with
        {
            Capabilities = [HtmlMlComponentCapabilities.Dom, HtmlMlComponentCapabilities.Settings]
        };
        var bridge = new HtmlMlHostBridge(manifest, []);
        var unavailable = await bridge.InvokeAsync(Request(HtmlMlComponentCapabilities.Settings));
        Assert.Equal("bridge.capability.unavailable", unavailable.Error!.Code);

        var wrongVersion = await bridge.InvokeAsync(Request(HtmlMlComponentCapabilities.Settings) with { Version = "2.0" });
        Assert.Equal("bridge.version", wrongVersion.Error!.Code);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelling = new HtmlMlHostBridge(manifest,
        [
            new HtmlMlDelegateCapabilityHandler(
                HtmlMlComponentCapabilities.Settings,
                (_, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<JsonElement?>(null);
                })
        ]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelling.InvokeAsync(Request(HtmlMlComponentCapabilities.Settings), cancellation.Token));
    }

    private static HtmlMlHostBridgeRequest Request(string capability) => new(
        "request-1",
        HtmlMlHostBridge.CurrentVersion,
        capability,
        "save",
        JsonSerializer.SerializeToElement(new { id = 42 }));
}
