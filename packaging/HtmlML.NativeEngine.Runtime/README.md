# HtmlML native engine runtime

This package contains one reviewed native HtmlML V8 DOM/CSS/scene engine for the RID in the
package ID. It is produced by the release pipeline, not restored as a template.

The package includes the native library, its required `icudtl.dat`, the applicable
third-party notices, and a SHA-256/ABI/V8 build manifest. The library locates ICU data
relative to its own module, so the package remains relocatable.
Applications must target the same `RuntimeIdentifier`; mixing runtime packages and
RIDs is rejected during the build.

Install the package matching the application's deployment RID, for example:

```xml
<PackageReference Include="HtmlML.NativeEngine.Runtime.osx-arm64" Version="VERSION" />
```

The initial release workflow builds `osx-arm64`, `linux-x64`, and `win-x64`. Other RIDs
listed by the package definition are reserved until their release lanes are enabled.
