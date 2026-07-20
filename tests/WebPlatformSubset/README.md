# HtmlML Web Platform subset

This directory implements the curated standards and connected-component test lane.
It is deliberately a bounded **HtmlML component profile**, not a claim that HtmlML is
a general-purpose browser or that every value of a listed CSS property is supported.

## Scope rule

A WPT case enters this directory only when it maps to an observed connected-component
capability or a prerequisite needed to make that capability correct. The profile
uses four explicit states in `htmlml-component-profile.json`:

- `required`: published HtmlML component-profile behavior; failures make the runner fail;
- `candidate`: relevant tests being evaluated, but not yet part of the support claim;
- `harnessBlocked`: relevant tests that need an adapter facility rather than a product fix;
- `excluded`: intentionally unsupported areas and the reason they are out of scope.

This keeps the suite narrow. Do not import a WPT directory, implement a feature just
because WPT contains tests for it, or turn candidate failures into blanket expected
failures. New product defects first get a small product-neutral contract, then the
nearest useful WPT case, then an application-owned parity check.

First-party documents under `contracts/` cover behavior that spans several standards
or requires repeated lifecycle phases, such as responsive resize and resize-back.
The unchanged contract document runs through `managed`, `native`, or `both` using the
same process-isolated engine adapters as the WPT selection.

## Pinned upstream content

`upstream/` contains unmodified files from the official WPT repository at the exact
revision in `upstream-revision.txt`. The original paths are retained. The upstream
BSD-3-Clause license is stored at `upstream/LICENSE.md`. `upstream-files.json` records
the SHA-256 digest of every vendored file so accidental edits are reviewable.

The runner transforms documents only in memory and presents the resulting test to
one of two first-class engine adapters:

- `managed` uses the existing ClearScript/Avalonia DOM and remains the compatibility oracle;
- `native` uses the off-thread V8/native DOM and reads its immutable scene directly;
- `both` runs the identical selection through both adapters and writes separate artifacts.

There is no fallback from native to managed. An unsupported native facility is
reported as a native failure or harness error so the matrix remains useful.

The document preparation then:

- the pinned `testharness.js` is inlined to avoid a general HTTP server;
- the pinned `check-layout-th.js` helper is inlined unchanged for selected geometry assertions;
- selected relative classic scripts are inlined only when their resolved path is present in the pinned upstream provenance manifest;
- the visual `testharnessreport.js` UI is removed;
- selected element-origin `test_driver.Actions().pointerMove()` calls are
  translated to native headless pointer-boundary events;
- selected `test_driver.click(element)` and `test_driver.send_keys(element, Tab)`
  calls are translated to Avalonia pointer and keyboard focus modalities plus
  their corresponding routed input events;
- incidental legacy window-named element references in the selected hover case
  are normalized in memory to explicit `getElementById` lookups;
- XHTML reftest CDATA wrappers are removed in memory because the local blob
  loader currently uses the HTML parser rather than an XML MIME path;
- a result callback records stable JSON for the host;
- managed documents run in a fresh trusted local V8 iframe context; native
  documents run in a fresh native engine instance against the same prepared source.

Do not edit vendored cases to make HtmlML pass. Update them only by reviewing a new
explicit WPT revision and refreshing the provenance metadata.

## Running

From the repository root, run the managed compatibility lane with:

```sh
HTMLML_CLEARSCRIPT_NATIVE=/Volumes/SSD/tmp/HtmlML-ClearScript-751/bin/Release/Unix/ClearScriptV8.osx-arm64.dylib \
HTMLML_CLEARSCRIPT_RID=osx-arm64 \
dotnet run --project tests/WebPlatformSubset/runner/HtmlML.WebPlatformSubset.Runner.csproj \
  -c Release -- --engine managed --selection required
```

After building the native spike, run the exact same profile through both engines:

```sh
dotnet run --project tests/WebPlatformSubset/runner/HtmlML.WebPlatformSubset.Runner.csproj \
  -c Release -- \
  --engine both \
  --native-library "$PWD/artifacts/native-engine-probe-v8/libhtmlml_native_engine.dylib" \
  --native-cache-directory "$PWD/artifacts/native-engine-probe-v8/code-cache" \
  --selection required \
  --output "$PWD/TestResults/WebPlatformSubset-engine-matrix"
```

Use `libhtmlml_native_engine.so` on Linux and `htmlml_native_engine.dll` on Windows.
The two result files are written below `managed/` and `native/`; each records its
engine identity. The native cache option preserves the spike's persistent V8
compilation-unit cache in the test lane.

Useful diagnostic forms:

```sh
# See the manifest selection without loading V8.
dotnet run --project tests/WebPlatformSubset/runner/HtmlML.WebPlatformSubset.Runner.csproj \
  -c Release -- --selection all --list

# Run one family or path substring.
dotnet run --project tests/WebPlatformSubset/runner/HtmlML.WebPlatformSubset.Runner.csproj \
  -c Release -- --selection all --test css-transforms

# Report candidate behavior without making candidate failures gate the command.
dotnet run --project tests/WebPlatformSubset/runner/HtmlML.WebPlatformSubset.Runner.csproj \
  -c Release -- --selection candidate
```

For a single engine, the stable result file is written to
`TestResults/WebPlatformSubset/results.json`.
Failed reftests also produce `actual.png`, `reference.png`, and `diff.png`. Required
failures return a nonzero exit code; candidate-only failures remain report-only.

## Current adapter boundary

Both engines consume the same prepared, pinned WPT source. The assertion adapter
supports self-contained local `testharness.js` documents and
resolves relative stylesheet links against each test's directory when the target is
present in the pinned upstream provenance manifest. The
rendering adapter supports exact-match local reftests at an 800×600, DPR 1 viewport.
It does not currently implement the WPT HTTP server, general testdriver/WebDriver
actions, remote origins, physical-device input, fonts, or all shared WPT helper scripts.

The native adapter currently supports DOM load/evaluation, V8 script execution,
pointer move/click, frame pumping, and immutable-scene capture. Keyboard input is
explicitly unsupported by the current native ABI. Native testharness load/timer
completion, full canvas replay, full SVG replay, and text/font pixel parity remain
adapter work; their failures must not be converted to managed fallback behavior.

The next high-value adapter addition is the small flexbox support stylesheet needed
by a selected dynamic flex alignment case. Input actions are intentionally limited
to the element-origin mouse move, primary click, and WebDriver Tab key used by the
selected hover and focus-visible cases; broader action sequences remain out of scope.
