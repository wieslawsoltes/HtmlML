# Concurrent HtmlML Engine Design

## Purpose

HtmlML applications may host several independent JavaScript components at the
same time. A common case is multiple TradingView charts loading the same
scripts and resources concurrently. The runtime should preserve isolation
between components without repeating expensive work for identical inputs.

The primary target is:

> Concurrent requests for an identical compilation unit compile once. One
> producer performs the work, all other requesters wait for that result, and
> every requester reuses the greatest amount of immutable data that V8 safely
> permits.

This design covers compilation and resource reuse inside one process first.
Cross-process coordination and shared-isolate hosting are later extensions.

## Existing Behaviour

HtmlML currently provides several useful layers of reuse:

- V8, its platform and ICU data are initialized once per process.
- Each native engine owns a separate V8 isolate, context, heap, DOM and event
  loop.
- Each isolate retains an in-memory cache of `v8::UnboundScript` handles.
- V8 code-cache data is persisted in content-addressed `.v8cache` files.
- Resource responses are persisted separately and have a bounded process-wide
  memory cache.
- Cache entries include integrity checks and a V8 cache-version tag.
- Persistent writes use uniquely named temporary files followed by rename, so
  readers cannot observe a partially written final entry.

The compilation key contains the HtmlML runtime identity, document name and
exact source bytes. Consequently, different scripts cannot alias merely
because they use the same URL.

The remaining gap is concurrent cold misses. Two isolates can both miss the
memory and persistent caches, compile the same source independently, and race
to publish equivalent cache files. This is safe but wastes CPU, memory
bandwidth and startup time.

## Required Invariants

1. Within one process, only one producer compiles a given cold compilation key
   at a time.
2. Other requesters wait for the producer without holding a global cache lock.
3. Success, rejection and failure are delivered consistently to every waiter.
4. A failed or destroyed producer cannot leave waiters blocked indefinitely.
5. Cache entries remain bounded and evictable.
6. An isolate never receives a V8 handle created by another isolate.
7. Persistent cache files remain valid across concurrent readers and writers.
8. Different compilation keys proceed independently.
9. Cache coordination must not serialize unrelated engines, input delivery or
   rendering.
10. Metrics must distinguish compilation, waiting, bytecode consumption and
    execution.

## Process-Wide Single-Flight Compilation

Introduce a process-wide compilation coordinator keyed by the existing
compilation digest.

Each coordinator entry has one of these states:

- `producing`: one engine is compiling while zero or more engines wait;
- `ready`: immutable cached-code bytes are available;
- `failed`: the producer failed and the failure is available to waiters.

The shared result should contain:

- the compilation key and V8 cache-version tag;
- an immutable, reference-counted byte buffer containing V8 cached-code data;
- source identity metadata needed for validation;
- success or structured failure information;
- timestamps and byte counts for metrics.

The first requester atomically installs a `producing` entry and becomes its
producer. Later requesters obtain a future or condition associated with that
entry, release the coordinator lock, and wait. The producer compiles in its
own isolate, calls `v8::ScriptCompiler::CreateCodeCache`, publishes the
immutable result, persists it once, and wakes all waiters.

Waiters then consume the same immutable byte buffer using
`v8::ScriptCompiler::kConsumeCodeCache`. The pinned V8 API also supports
background code-cache consumption, which should be evaluated so waiting
isolates can move deserialization work away from their latency-sensitive
runtime thread.

The coordinator lock must protect only entry lookup and state transitions. It
must never be held while compiling, reading a large file, writing a cache file
or entering V8.

## What Can Be Shared

| Asset | Current or proposed scope | Notes |
| --- | --- | --- |
| Native engine library code | Process and operating-system mapping | One loaded image serves all instances in a process. |
| V8 platform and ICU data | Process | Already initialized once. |
| Fetched resource bytes | Process and persistent disk cache | Process cache is bounded; immutable backing storage can reduce copies further. |
| JavaScript source bytes | Process | Proposed immutable source blobs avoid one full source copy per instance. |
| V8 cached-code bytes | Process and persistent disk cache | Proposed single-flight result; safely consumable by compatible isolates. |
| `v8::UnboundScript` | One isolate | A V8 handle cannot cross isolate boundaries. |
| Bound script, context, heap and DOM | One engine/context | Required for independent global state and DOM ownership. |

Single-flight therefore means one full source compilation. Separate isolates
still consume the cached-code data and instantiate isolate-local V8 objects.
It does not imply that an `UnboundScript` or all generated machine-code objects
can be shared directly across isolates.

## Shared-Isolate Hosting

Literal sharing of an `UnboundScript` is possible only when multiple component
contexts live in the same isolate. A script can then be compiled once in that
isolate and bound to each context.

This should be treated as an optional high-density mode rather than the
default:

- JavaScript execution within an isolate is serialized.
- A long-running component can delay every context in the isolate.
- Garbage collection and fatal isolate failures affect the whole group.
- HtmlML currently stores runtime state on the isolate; multi-context hosting
  would require context-local embedder state and callback routing.
- Scheduling, timer ownership, microtask checkpoints and DOM bindings would
  need explicit per-context isolation.

If measurements justify it, use an isolate pool where each isolate hosts a
small bounded number of contexts. This provides code sharing within a shard
while retaining parallelism and limiting failure coupling between shards.

V8 places the current isolates in its default `IsolateGroup`, which is its
most memory-efficient group configuration. An isolate group shares lower-level
V8 infrastructure, but it does not make ordinary V8 handles transferable
between isolates.

## Persistent and Cross-Process Coordination

The current temporary-file and rename protocol prevents partial final files.
Hash and version validation protect readers from invalid cache content.

Single-flight initially coordinates only engines in the same process. Two
different application processes may still compile the same cold unit. If
cross-process duplication is material, add a per-key lock file with:

- atomic ownership acquisition;
- owner process identity;
- a bounded lease or stale-owner recovery;
- final-cache recheck after acquiring the lock;
- timeout and fallback compilation;
- platform-specific validation on macOS, Linux and Windows.

Cross-process locking must be justified by measurement because its recovery
and filesystem semantics are substantially more complex than process-local
coordination.

## Lifetime, Failure and Eviction

- A coordinator entry remains alive while a producer or waiter references it.
- Ready byte buffers use reference counting and can outlive their map entry.
- The coordinator applies entry-count and byte-count limits independently of
  each isolate's `UnboundScript` cache.
- Completed entries participate in an LRU policy. Producing entries are never
  evicted.
- Producer exceptions publish a structured failure before leaving the entry.
- A rejected V8 cache result invalidates the ready entry and persistent file.
  At most one requester is elected to rebuild it.
- Runtime shutdown cancels only that runtime's wait. It does not cancel a
  producer still needed by other engines.
- Waiting must have diagnostics and a bounded shutdown path, but normal
  compilation should not be given an arbitrary short timeout.

## Resource Sharing

The existing process resource cache avoids repeated network and disk reads,
but returning resources by value can still duplicate large strings. It can be
evolved to store immutable, reference-counted resource bodies while keeping
headers and freshness metadata small.

Resource loading should use the same single-flight principle:

- one in-flight fetch or disk read per resource key;
- concurrent engines await the same immutable result;
- conditional revalidation is performed once;
- failure and cancellation follow the compilation rules;
- component-specific response objects reference shared body storage without
  sharing mutable DOM or JavaScript state.

JSON resource parsing is separate from JavaScript compilation. Source text
that is executed as JavaScript uses the compilation coordinator; fetched JSON
uses the resource coordinator and is parsed into each isolate's own object
graph.

## Metrics

Expose aggregate and per-engine counters for:

- compilation requests;
- memory script-cache hits;
- persistent code-cache hits and misses;
- single-flight producers;
- single-flight waiters;
- duplicate cold compilations;
- producer compilation duration;
- waiter duration;
- cached-code consumption duration;
- background-consumption duration;
- cached-code bytes shared, read and written;
- cache rejection and rebuild counts;
- resource producers, waiters and shared bytes;
- isolate heap usage and process resident-set size;
- incremental memory cost per additional engine.

The success target for identical simultaneous cold requests is exactly one
producer, all remaining requests recorded as waiters, and zero duplicate cold
compilations.

## Certification and Performance Gates

Add deterministic tests that create multiple engines against an initially
empty cache:

1. Start 2, 4 and 8 engines simultaneously with the same script.
2. Assert exactly one full compilation and `N - 1` waiters.
3. Assert every engine executes the same result.
4. Assert one valid persistent entry and zero temporary-file leaks.
5. Repeat warm and assert zero full compilations.
6. Repeat with different scripts and prove unrelated keys compile in parallel.
7. Inject producer compilation failure and prove every waiter completes with
   the same failure.
8. Destroy one waiter and then the producer at controlled points; prove there
   is no deadlock or stranded entry.
9. Corrupt and truncate persistent entries; prove one coordinated rebuild.
10. Run concurrent writers in separate processes to validate current atomic
    publication and any later lock-file protocol.
11. Record cold latency, warm latency, waiter latency, CPU time and peak RSS.
12. Measure resident memory at 1, 2, 4 and 8 TradingView-like instances and
    gate unexpected growth.

The tests must run under sanitizers where available and include repeated
stress runs so race freedom is demonstrated rather than inferred from a
single successful execution.

## Delivery Sequence

1. Add metrics for duplicate compilation and in-flight waiters.
2. Implement process-wide single-flight code-cache production.
3. Store ready cached-code data in bounded immutable shared buffers.
4. Add the concurrent cold/warm certification tests.
5. Apply the same coordinator pattern to resource fetches and resource bodies.
6. Benchmark multi-instance TradingView workloads and establish memory gates.
7. Evaluate background cached-code consumption.
8. Prototype an isolate-pool mode only if measured per-isolate duplication
   remains material.
9. Consider cross-process locking only if real workloads show a meaningful
   cold-start stampede between processes.

## Decision

HtmlML will retain separate isolates as its compatibility and isolation
default. It will add process-wide single-flight compilation and immutable
cache-data sharing so identical concurrent requests perform one full
compilation. Shared-isolate hosting remains an experimental density
optimization subject to responsiveness, isolation and memory benchmarks.
