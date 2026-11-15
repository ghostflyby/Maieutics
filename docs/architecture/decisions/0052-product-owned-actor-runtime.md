# ADR 0052: Product-owned TypeScript actor runtime

Status: Accepted

## Context

The plugin host, plugin SDK, and REPL share `worker-actor` for typed calls, value codecs, streams,
cancellation, and actor references across Workers and processes. Browser participation needs
changes to its runtime and transport boundaries. Keeping this foundation in a separate release
cycle makes product-specific adaptation and verification unnecessarily indirect.

## Decision

Maieutics owns the runtime source in `deno/maieutics-plugin-sdk/actor-runtime`. The initial import
is `worker-actor` 0.7.0, commit `bbc0d235ae54455147af6ad399edf5d64dc0be95`, from
`https://github.com/ghostflyby/worker-actor`. Retain its Apache-2.0 license and provenance.

The runtime remains a lower-level module with no dependency on the surrounding plugin SDK,
plugin host, kernel, frontend, or Agent. The SDK package contains it so published SDK consumers
receive the same implementation without an independent runtime publication. Its subpaths are
`@maieutics/plugin-sdk/actor`, `/actor/codec`, and `/actor/codecs`. SDK internals use relative
imports; other product consumers use those subpaths. Legacy bare `@ghostflyby/worker-actor`
aliases resolve to the same local files. Host-owned import mappings cannot be replaced by plugins.

Embed production runtime sources in the executable through the SDK resource glob and materialize
them for both the plugin host and REPL. Include the license and notice. Run the imported tests in
the workspace; exclude tests and fixtures from executable resources and SDK publication.

The initial import includes 0.7.0's RPC envelope helpers, node actor improvements, and type
projection updates. Existing Worker, process, plugin interop, and real-kernel tests validate this
baseline change. It does not introduce a frontend endpoint, persisted-format change, or browser
actor authority. Runtime and SDK are deployed together; direct imports of an external JSR runtime
are not part of the product's internal integration contract.

## Consequences

Runtime adaptations and regression tests can land atomically with their product consumers.
The runtime has one implementation in development, published SDKs, and materialized children.
The original repository remains intact as provenance; product builds no longer require its JSR
package.

Browser integration remains separate work: isolate browser-compatible modules, choose the wire
encoding contract, propagate decode and disconnect failures, bound transport admission and queues,
carry network transport context through product actor references, and scope capabilities to
authenticated sessions, views, and connection generations. ADR 0051's HTTP decision is unchanged
until a browser actor transport is explicitly designed and verified.
