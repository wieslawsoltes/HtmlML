# ADR 0002: Portable CSS layout is authoritative

- **Status:** Accepted
- **Date:** 2026-07-15

## Context

Allowing each UI framework to reinterpret CSS with its native layout engine would
produce different geometry, hit testing and Canvas placement for the same component.
Complex interactive components demonstrate that geometry, painting and pointer
coordinates must share one source of truth.

## Decision

The future portable CSS/layout core owns CSS box geometry, containing blocks,
invalidation and synchronous layout flush semantics. Backends provide viewport and
intrinsic measurement services, then arrange native projections to the resulting
portable rectangles.

During R1 the existing `CssLayoutPanel` remains the reference implementation. Its
observable inputs and outputs are characterized before the algorithm moves. Native
controls may report intrinsic sizes but may not silently replace CSS layout policy.

## Consequences

- Backend differences are limited to measurement and rendering capabilities.
- Geometry can be tested headlessly before presentation.
- Text shaping/fallback and intrinsic native-control sizing require explicit contracts.
- R3 owns the physical extraction; R1 only introduces values and service seams.
