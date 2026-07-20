# @htmlml/sdk

This package publishes the bounded HtmlML Component Profile 1 declarations and the
same compatibility rules used by the .NET SDK. It does not advertise the complete
browser `lib.dom` surface.

- `htmlml-check --manifest htmlml-component.json --source src` checks JavaScript and
  TypeScript in CI.
- `htmlml()` from `@htmlml/sdk/vite` validates source during Vite builds and emits the
  final packaged manifest.
- `htmlml()` from `@htmlml/sdk/esbuild` provides the equivalent esbuild integration.
- `htmlml.host.*.invoke()` is the asynchronous capability-based host API.
