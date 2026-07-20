using HtmlML.Backends;
using HtmlML.Core;
using Xunit;

namespace HtmlML.Backend.Abstractions.Tests;

public sealed class BackendManifestTests
{
    [Fact]
    public void ValidManifestRoundTripsAndResolvesCapabilities()
    {
        var manifest = HtmlMlBackendManifestSerializer.Parse("""
            {
              "schemaVersion": "1.0",
              "id": "example.backend",
              "displayName": "Example Backend",
              "version": "1.0.0",
              "assembly": "Example.Backend",
              "backendType": "Example.Backend.Host",
              "maximumSupportLevel": "Component",
              "capabilities": ["DomProjection", "CssLayout", "Canvas2D"],
              "targetFrameworks": ["net8.0"],
              "platforms": ["headless"]
            }
            """);

        Assert.Equal(HtmlMlBackendSupportLevel.Component, manifest.MaximumSupportLevel);
        Assert.Equal(
            HtmlMlBackendCapabilities.DomProjection
            | HtmlMlBackendCapabilities.CssLayout
            | HtmlMlBackendCapabilities.Canvas2D,
            HtmlMlBackendManifestSerializer.ResolveCapabilities(manifest));
        var roundTrip = HtmlMlBackendManifestSerializer.Parse(
            HtmlMlBackendManifestSerializer.Serialize(manifest));
        Assert.Equal(manifest.Id, roundTrip.Id);
        Assert.Equal(manifest.MaximumSupportLevel, roundTrip.MaximumSupportLevel);
        Assert.Equal(manifest.Capabilities, roundTrip.Capabilities);
        Assert.Equal(manifest.TargetFrameworks, roundTrip.TargetFrameworks);
        Assert.Equal(manifest.Platforms, roundTrip.Platforms);
    }

    [Fact]
    public void InvalidSchemaUnknownAndDuplicateCapabilitiesFailBeforeStartup()
    {
        var exception = Assert.Throws<InvalidDataException>(() => HtmlMlBackendManifestSerializer.Parse("""
            {
              "schemaVersion": "2.0",
              "id": "example.backend",
              "displayName": "Example Backend",
              "version": "1.0.0",
              "assembly": "Example.Backend",
              "backendType": "Example.Backend.Host",
              "maximumSupportLevel": "Experimental",
              "capabilities": ["Canvas2D", "Canvas2D", "Unknown"],
              "targetFrameworks": ["net8.0"]
            }
            """));

        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate 'Canvas2D'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unknown capability 'Unknown'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamReadAndValidationCoverThePublishedRuntimeContract()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            HtmlMlBackendManifestSerializer.Serialize(CreateManifest())));

        var manifest = HtmlMlBackendManifestSerializer.Read(stream);

        Assert.Equal("example.backend", manifest.Id);
        Assert.True(HtmlMlBackendManifestSerializer.Validate(manifest).IsValid);
        Assert.True(HtmlMlBackendManifestSerializer.Validate(null).Errors.Count > 0);
        Assert.Throws<ArgumentNullException>(() => HtmlMlBackendManifestSerializer.Read(null!));
        Assert.Throws<ArgumentException>(() => HtmlMlBackendManifestSerializer.Parse(" "));
        Assert.Throws<System.Text.Json.JsonException>(() =>
            HtmlMlBackendManifestSerializer.Read(new MemoryStream([])));
        Assert.Throws<InvalidDataException>(() => HtmlMlBackendManifestSerializer.Serialize(null!));
    }

    [Fact]
    public void RuntimeValidationMatchesSchemaUniquenessAndRequiredValueRules()
    {
        var invalid = CreateManifest() with
        {
            SchemaVersion = "0",
            Id = " ",
            DisplayName = "",
            Version = "",
            Assembly = "",
            BackendType = "",
            MaximumSupportLevel = (HtmlMlBackendSupportLevel)99,
            Capabilities = ["Canvas2D", "Canvas2D", "Canvas2D, Svg", "Nope"],
            TargetFrameworks = ["", "net8.0", "net8.0"],
            Platforms = [" ", "headless", "headless"]
        };

        var result = HtmlMlBackendManifestSerializer.Validate(invalid);

        Assert.False(result.IsValid);
        var message = Assert.Throws<InvalidDataException>(result.ThrowIfInvalid).Message;
        Assert.Contains("schemaVersion", message, StringComparison.Ordinal);
        Assert.Contains("id is required", message, StringComparison.Ordinal);
        Assert.Contains("Unknown maximumSupportLevel", message, StringComparison.Ordinal);
        Assert.Contains("targetFrameworks values must not be empty", message, StringComparison.Ordinal);
        Assert.Contains("targetFrameworks contains duplicate", message, StringComparison.Ordinal);
        Assert.Contains("platforms values must not be empty", message, StringComparison.Ordinal);
        Assert.Contains("platforms contains duplicate", message, StringComparison.Ordinal);
        Assert.Contains("Unknown capability 'Canvas2D, Svg'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArraysAreRejected()
    {
        var result = HtmlMlBackendManifestSerializer.Validate(CreateManifest() with
        {
            Capabilities = [],
            TargetFrameworks = []
        });

        Assert.Contains(result.Errors, error => error.Contains("capabilities", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("targetFrameworks", StringComparison.Ordinal));
    }

    [Fact]
    public void ContractVerifierAcceptsExactAdvancedClaimAndRejectsEveryMismatch()
    {
        var manifest = CreateManifest() with { MaximumSupportLevel = HtmlMlBackendSupportLevel.Advanced };
        var backend = new ContractBackend(
            HtmlMlBackendCapabilities.DomProjection | HtmlMlBackendCapabilities.Canvas2D);

        HtmlMlBackendContractVerifier.Verify(backend, manifest, HtmlMlBackendSupportLevel.Advanced);
        Assert.Equal(backend.Capabilities, backend.LastEnsured);
        Assert.Throws<ArgumentNullException>(() =>
            HtmlMlBackendContractVerifier.Verify(null!, manifest, HtmlMlBackendSupportLevel.Component));
        Assert.Throws<InvalidDataException>(() =>
            HtmlMlBackendContractVerifier.Verify(
                backend,
                manifest with { MaximumSupportLevel = HtmlMlBackendSupportLevel.Component },
                HtmlMlBackendSupportLevel.Application));
        Assert.Throws<InvalidDataException>(() =>
            HtmlMlBackendContractVerifier.Verify(
                new ContractBackend(HtmlMlBackendCapabilities.DomProjection),
                manifest,
                HtmlMlBackendSupportLevel.Component));
        Assert.Throws<InvalidDataException>(() =>
            HtmlMlBackendContractVerifier.Verify(
                backend,
                manifest with { Id = "" },
                HtmlMlBackendSupportLevel.Component));
    }

    private static HtmlMlBackendManifest CreateManifest() => new()
    {
        SchemaVersion = HtmlMlBackendManifest.CurrentSchemaVersion,
        Id = "example.backend",
        DisplayName = "Example Backend",
        Version = "1.0.0",
        Assembly = "Example.Backend",
        BackendType = "Example.Backend.Host",
        MaximumSupportLevel = HtmlMlBackendSupportLevel.Component,
        Capabilities = ["DomProjection", "Canvas2D"],
        TargetFrameworks = ["net8.0"],
        Platforms = ["headless"]
    };

    private sealed class ContractBackend(HtmlMlBackendCapabilities capabilities) : IHtmlMlBackendHost
    {
        public HtmlMlBackendCapabilities LastEnsured { get; private set; }
        public HtmlMlBackendState State => HtmlMlBackendState.Mounted;
        public HtmlMlBackendNode Root => default;
        public HtmlMlBackendCapabilities Capabilities { get; } = capabilities;
        public IHtmlMlHostServices Services => null!;
        public IHtmlMlInputSource Input => null!;
        public IReadOnlyList<HtmlMlBackendDiagnostic> Diagnostics => [];
        public void EnsureCapabilities(HtmlMlBackendCapabilities required) => LastEnsured = required;
        public void Mount() => throw new NotSupportedException();
        public void Unmount() => throw new NotSupportedException();
        public HtmlMlBackendNode CreateNode(in HtmlMlBackendNodeDescriptor descriptor) => throw new NotSupportedException();
        public void Attach(HtmlMlBackendNode parent, HtmlMlBackendNode child, int index) => throw new NotSupportedException();
        public void Detach(HtmlMlBackendNode node) => throw new NotSupportedException();
        public void Arrange(HtmlMlBackendNode node, HtmlMlRect bounds) => throw new NotSupportedException();
        public void SetVisible(HtmlMlBackendNode node, bool visible) => throw new NotSupportedException();
        public void SetZIndex(HtmlMlBackendNode node, int zIndex) => throw new NotSupportedException();
        public void Invalidate(HtmlMlBackendNode node, HtmlMlInvalidationKind kind) => throw new NotSupportedException();
        public HtmlMlBackendNode? HitTest(HtmlMlPoint point) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
