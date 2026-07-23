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

Documentation, compatibility policy, source, and issue tracking are available from
the [HtmlML repository](https://github.com/wieslawsoltes/HtmlML).
