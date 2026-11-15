# Maieutics Actor Runtime

The product-owned TypeScript actor runtime, imported from `worker-actor` 0.7.0 at commit
`bbc0d235ae54455147af6ad399edf5d64dc0be95` in
[the original repository](https://github.com/ghostflyby/worker-actor). The original Apache-2.0
license and provenance are retained in `LICENSE` and `NOTICE`.

This directory is the single source for Worker, process, and transport-based actors used by the
plugin host, plugin SDK, and REPL. It depends on Web platform primitives and runtime adapters, not
on plugin discovery, kernel sessions, frontend rendering, or the Agent. Product-specific actor
references remain in `../actor_ref.ts`; permission enforcement remains in the owning host.

The sources ship inside the plugin SDK so the SDK remains independently publishable without a second
runtime package. The SDK exports `@maieutics/plugin-sdk/actor`, `/actor/codec`, and `/actor/codecs`.
Existing bare `@ghostflyby/worker-actor` aliases resolve to these same local files in the workspace
and materialized process configs. No actor runtime is downloaded from JSR. See ADR 0052 for
ownership and compatibility boundaries.

Tests and their fixtures are kept with the runtime. From the SDK directory, run:

```sh
deno task test:actor
```

The workspace test command also runs this suite. Tests and fixtures are excluded from the SDK
publication and embedded executable resources. Runtime sources, license, and provenance are
materialized beside the SDK for both the plugin host and REPL.

## Permissions

Importing the runtime starts no worker, process, or network connection. Worker creation requires the
owner's module read authority; process actors require the owner's run and environment authority;
WebSocket adapters require the owner's network authority. This move adds no product permission grant
or independent process launch path.

Browser integration is not enabled by this migration. The default network adapters still use V8
serialization. Browser-safe module boundaries, transport failure propagation, pending-channel
cleanup, network-aware product actor references, and authenticated view-scoped admission need
separate changes and conformance coverage.
