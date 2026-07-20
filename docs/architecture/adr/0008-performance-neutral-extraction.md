# ADR 0008: Architectural extraction must be performance-neutral

- **Status:** Accepted
- **Date:** 2026-07-15

## Context

R2 separates the JavaScript runtime and DOM cores from Avalonia. Straightforward
abstraction can add interface dispatch, wrapper objects, copied values, handle maps,
delegate closures, boxing, conversions, or smaller calls that defeat existing DOM and
Canvas batching. Those costs would affect React and complex interactive workloads even
though the refactor is not intended to change product behavior.

The architecture exists to enable more backends. It is not valuable if the reference
Avalonia backend becomes slower, more allocation-heavy, less responsive, or less
scalable simply to make the dependency graph cleaner.

## Decision

R2 is a performance-neutral extraction with no architecture tax. A repeatable
regression outside the recorded R0 host-noise envelope blocks the extraction slice.
Architecture, portability, interface uniformity, and package separation cannot waive
that gate or justify a baseline increase. If a cost-neutral boundary cannot yet be
designed, the affected code remains in place until it can be separated safely.

Every R2 slice is measured before and after on the same host with the same Release
configuration, target framework, native V8 build, assets, viewport/DPR, warm-up, and
inputs. Evidence includes multiple iterations, p50/p95 latency, allocation, memory,
cache behavior, lifecycle, and multi-instance scaling where applicable. The protected
paths include DOM interop, callback dispatch, task/microtask/rAF scheduling, Canvas
packets, React scheduling/focus, complex-workload load, warm wheel/pan/resize,
disposal, and concurrent component instances.

Portable contracts define semantics, but they do not require one uniform execution
mechanism. Implementations may preserve or add:

- typed and batched hot-path contracts alongside general contracts;
- cached, allocation-free adapters and stable opaque-handle mappings;
- backend- or engine-specialized fast paths with identical observable semantics;
- compact packet/value representations that avoid boxing and collection copies; and
- direct internal calls where measurement shows that an abstraction would add cost,
  provided architecture tests still enforce dependency direction.

No R2 path may introduce reflection, dynamic dispatch, dictionary-shaped property
bags, per-call wrappers, avoidable delegate/closure creation, or repeated native/value
conversion merely to satisfy the new package structure. Optimizations are verified by
behavior and performance tests rather than assumed from design.

## Consequences

- R2 changes land as small, benchmarked slices instead of one large physical move.
- A clean dependency graph is necessary but not sufficient for an R2 slice to land.
- General portability APIs can coexist with deliberately specialized internal paths.
- Some code may remain temporarily in its current assembly when extraction would add
  measurable overhead; that is a roadmap delay, not permission to compromise runtime
  performance.
- Complex-workload progress, existing coverage, cache behavior, pixels, event ordering
  and lifecycle gates remain protected throughout the extraction.
