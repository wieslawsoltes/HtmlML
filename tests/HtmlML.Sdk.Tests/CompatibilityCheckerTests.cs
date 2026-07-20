using HtmlML.Sdk;
using Xunit;

namespace HtmlML.Sdk.Tests;

public sealed class CompatibilityCheckerTests
{
    [Fact]
    public void ReportsUnsupportedApisAndMissingCapabilitiesWithLocations()
    {
        var report = HtmlMlCompatibilityChecker.Check(
            "// localStorage ignored in comments\nconst worker = new Worker('worker.js');\nhtmlml.host.files.open({});",
            ComponentManifestTests.CreateManifest(),
            "app.ts");

        Assert.False(report.IsCompatible);
        Assert.DoesNotContain(report.Diagnostics, static value => value.Code == "HTMLML1002");
        var worker = Assert.Single(report.Diagnostics, static value => value.Code == "HTMLML1003");
        Assert.Equal(2, worker.Line);
        var files = Assert.Single(report.Diagnostics, static value => value.Code == "HTMLML2007");
        Assert.Equal(HtmlMlComponentCapabilities.FileSelection, files.RequiredCapability);
    }

    [Fact]
    public void DeclaredHostCapabilitiesPassAndDirectNetworkingWarns()
    {
        var manifest = ComponentManifestTests.CreateManifest() with
        {
            Capabilities = [HtmlMlComponentCapabilities.Dom, HtmlMlComponentCapabilities.Networking]
        };
        var report = HtmlMlCompatibilityChecker.Check(
            "htmlml.host.network.request({ url: '/data' });\nfetch('/bypass');",
            manifest);

        Assert.True(report.IsCompatible);
        Assert.Single(report.Diagnostics, static value => value.Code == "HTMLML3001");
    }
}
