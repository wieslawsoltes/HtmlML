# Native scene engine

This experiment is the product-neutral native DOM, CSS, V8, and immutable-scene
implementation used to validate HtmlML's native execution architecture.

The engine owns a V8 isolate and mutable DOM/CSS state on its worker thread. It
publishes immutable scene checkpoints and diffs through the C ABI in
`native/htmlml_native_engine.h`. A managed renderer acquires a scene pointer, traverses
the fixed-layout arrays in place, renders the changed layers, acknowledges the revision,
and releases the lease. No product assets, bootstrap code, or application APIs belong in
this directory.

## Build

The portable engine can be built without V8:

```sh
cmake -S experiments/HtmlML.NativeEngine.Probe \
  -B artifacts/native-engine-probe \
  -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/native-engine-probe --config Release
```

For the V8 build, provide the reviewed V8 headers and libraries used by the packaging
pipeline:

```sh
cmake -S experiments/HtmlML.NativeEngine.Probe \
  -B artifacts/native-engine-probe-v8 \
  -DCMAKE_BUILD_TYPE=Release \
  -DHTMLML_NATIVE_ENGINE_WITH_V8=ON \
  -DHTMLML_V8_INCLUDE_DIR=/absolute/path/to/v8/include \
  -DHTMLML_V8_LIBRARY=/absolute/path/to/v8/library
cmake --build artifacts/native-engine-probe-v8 --config Release
```

## Host contract

- `htmlml_engine_prewarm` pays the process-wide V8 initialization cost early.
- `htmlml_engine_create_with_options` creates an engine and configures its persistent
  compilation-unit cache.
- `htmlml_engine_set_resource_root` provides the filesystem root used to resolve
  component-owned iframe, script, and stylesheet resources.
- `htmlml_engine_execute_script` and `htmlml_engine_evaluate_json` are the generic
  JavaScript execution/interoperation boundary.
- Component code sets `globalThis.__htmlMlComponentReady = true` when its application
  lifecycle is ready. The generic scene flag and metric expose that state to a host.
- `htmlml_engine_acquire_latest_scene` returns an immutable pointer view. The host must
  acknowledge and release it according to the ABI comments before acquiring the next
  dependent diff.

Product hosts own their assets, bootstraps, readiness policy, API facade, screenshots,
and compatibility/performance suites. HtmlML keeps only reusable engine behavior and
the shared managed/native conformance contracts.
