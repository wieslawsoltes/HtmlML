# HtmlML packages

HtmlML provides portable HTML, DOM, CSS, graphics, and JavaScript contracts together
with an Avalonia presentation backend and an opt-in native V8/DOM/CSS/scene engine.

Start with the package matching the product surface:

- `HtmlML` for HTML-like Avalonia markup;
- `HtmlML.Backend.Avalonia` for the complete Avalonia backend;
- `HtmlML.Sdk` and `HtmlML.Sdk.Avalonia` for packaged React/TypeScript components;
- `HtmlML.NativeEngine.Runtime.<rid>` for the native engine on a published RID.

The complete release inventory is:

- managed libraries: `HtmlML`, `HtmlML.Core`, `HtmlML.Dom`, `HtmlML.Css`,
  `HtmlML.Graphics`, `HtmlML.JavaScript`, `HtmlML.Backend.Abstractions`,
  `HtmlML.Backend.Avalonia`, `JavaScript.Avalonia.ClearScript`, `HtmlML.Sdk`,
  and `HtmlML.Sdk.Avalonia`;
- project templates: `HtmlML.Templates`;
- native runtimes: `HtmlML.NativeEngine.Runtime.osx-arm64`,
  `HtmlML.NativeEngine.Runtime.linux-x64`, and
  `HtmlML.NativeEngine.Runtime.win-x64`.

The package line is prerelease. Managed packages in one release must use the same
version. Native applications must select the runtime package matching their explicit
`RuntimeIdentifier`.

The release workflow caches a minimal pinned V8 SDK independently for each RID. The
cache contains only the V8 headers, monolithic library, ICU data, and licenses needed
to link HtmlML's native bridge. Its key includes the hosted-runner image, ClearScript
revision, V8 build scripts, and HtmlML compatibility patches. Consequently, ordinary
HtmlML changes rebuild and relink only the native bridge; V8 is rebuilt whenever any
input capable of changing its ABI or binary output changes.

Documentation, compatibility policy, source, and issue tracking are available from
the [HtmlML repository](https://github.com/wieslawsoltes/HtmlML).
