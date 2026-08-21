# Maieutics Plugin Host Redesign

A complete, self-contained specification for redesigning the Maieutics plugin host so that
cross-plugin calls are fully implemented on top of the published worker-actor API
(`@ghostflyby/worker-actor@0.1.0` on JSR), with **zero self-authored RPC**. Anyone reading this
document top to bottom can implement the system.

Status: Draft — pending review. **Implementation in progress**; the Deno SDK/host core and the
kernel dependency graph are done and tested, the remaining steps are §10.6–§10.8 (delete the
`extension.invoke` bus path, hot reload, ADR 0019).

## Implementation status

Implemented (see §10 for the order; deviations from the spec are marked inline):

| Step | Status | Notes |
|---|---|---|
| 1. SDK core (`defineActor`, `initPluginWorker`, load hook) | ✅ | `actor_ref.ts` adds a minimal byref codec (see deviation below). |
| 2. Host (`spawn()`, specifier registry, acquire router) | ✅ | Router is a per-worker `addEventListener`; uses worker-actor's standard `__serve-ref`/`__ref-acquired` frames. |
| 3. Type identity (import map + `deno check` fixture) | ⚠️ | `depActor<T>` bridges types via `typeof import(...)` + `unknown`; runtime extraction was dropped (see deviation). |
| 4. Extension points over `Remote<T>` | ✅ | Host calls `actor.McpDiscover(...)`; the `extension.invoke` bus path is retained for now. |
| 5. Dependency graph + topological start | ✅ | `PluginDependencyGraph` (missing-dep/cycle exclusion, waves) on the kernel; the host re-derives waves. |
| 6. Delete `extension.invoke` protocol | ⏳ | Deferred until the `Remote<T>` path is confirmed end-to-end by integration tests. |
| 7. Hot reload (`plugin.reload`) | ✅ | `plugin.reload` bus message + kernel `FileSystemWatcher`; the payload carries the plugin's full replacement config so permission changes rebuild the worker without a host restart. |
| 8. Docs/tests | ✅ | Unit + interop tests (real deno) pass. |

### Deviations from the spec (verified against worker-actor@0.1.0)

1. **No remote-ref codec is published by the package.** §5/§11 assumed `RemoteRef`/`remoteRef`
   are importable from `@ghostflyby/worker-actor`; the published 0.1.0 exports only the control
   plane (`registerControlHandler`, `triggerAcquire`, `dispatchControlFrame`, `connectChannel`,
   `setMainAcquire`, `openChannel`, `registerRelease`) and the codec surface. The SDK therefore
   implements a minimal byref codec in `deno/maieutics-plugin-sdk/actor_ref.ts` using only those
   public primitives — still zero self-authored RPC machinery.
2. **Acquire routing uses worker-actor's standard frames.** §7.3 proposed custom
   `__serve-actor`/`__ref-acquired` frames; `serveWorker` dispatches only `__serve-ref` and
   `__ref-acquired`, so the host routes with those (the specifier rides on `__serve-ref`).
   The refId prefix is the consumer's own worker id and the host maps by specifier, not by
   prefix rewrite.
3. **No export-name extraction.** §9's stub generation assumed per-name runtime stubs. The
   implemented `depActor<T>` returns a single lazy nested surface typed via
   `typeof import("real-module")` bridged through `unknown` — the same `Remote<T>` shape at
   compile time, zero runtime reflection.

---

## 1. Goal

1. Plugin interop (one plugin calling another plugin's exported surface) is built entirely on
   worker-actor's public primitives: `spawn`, `serveWorker`, `Remote<T>`, the reference codec
   pattern (`ref_test.ts`), `registerControlHandler` / `triggerAcquire` / `dispatchControlFrame`.
   Maieutics writes **no RPC machinery of its own** — no frames, no proxies, no codecs, no channels.
2. Dependency declarations drive start order, cascade teardown, crash handling, and hot reload
   (the orchestration model from `worker-actor/PLUGIN-SYSTEM.md` §4–§8).
3. Compile-time and runtime types agree: a plugin module exports actor surfaces with
   `defineActor`, whose return type is worker-actor's `Remote<T>` projection. A dependency plugin
   importing the module sees the same `Remote<T>` type its runtime stub implements.

## 2. Non-goals

- No new worker-actor API. Everything below uses only what `@ghostflyby/worker-actor@0.1.0`
  publishes (`.` root, `./codec`, `./codecs`).
- No channel construction in Maieutics. Every `MessageChannel`, port, and liveness pair is created
  inside worker-actor (its `routeAcquire`/`link` internals, driven by `ref_test.ts` semantics).
- No constant exports from actor surfaces. Worker-actor's `Remote<T>` maps non-function members to
  `never`; plugins expose values as zero-argument methods.
- No Maieutics `extension.invoke` control protocol. Extension-point calls go through the spawned
  actor's `Remote<T>` directly (see §7.2), so the `extension.*` bus messages are deleted once the
  wiring is confirmed.

## 3. worker-actor public API surface (the only allowed primitives)

From `@ghostflyby/worker-actor@0.1.0` (JSR):

| Symbol | Where | Purpose |
|---|---|---|
| `spawn<Remote<typeof Module.rpc>>(worker, opts)` | `.` | Create a worker-backed actor; returns `Remote<T> & ActorHandle`. Runs handshake; worker must call `serveWorker`. Maintains `workersById` and the acquire router internally. |
| `serveWorker(rpc, { codecs, onLink })` | `.` | Worker-side runtime: handles `handshake`, `__worker-id`, `__link`, `__serve-ref`/`__ref-acquired` (via `dispatchControlFrame`), `request`/`response` frames. `onLink` receives a `LinkHandle`. |
| `Remote<T>` | `.` | Type projection: method → `Promise<Awaited<R>>`; `AsyncIterable` returns stay lazy; non-function members → `never`. |
| `LinkHandle` | `.` | Per-link handle: `rpc` (bidirectional peer proxy), `serve(api)` (replace peer-facing surface), `close()`. |
| `registerControlHandler(type, fn)` | `.` (re-exported) | Worker-side registry for control frames (used by the ref codec). |
| `dispatchControlFrame(frame)` | `.` | Dispatch a control frame to registered handlers. |
| `triggerAcquire(refId)` | `.` | Worker side: posts `__acquire-ref` to main; main (spawn's router) bootstraps a fresh channel. |
| `setMainAcquire(fn)` | `.` | Main side: register the acquire router (spawn does this). |
| `PayloadCodecRegistry`, `Codec`, `CodecState` | `./codec` | Value codecs over RPC payloads. |
| `iterableCodec`, `errorCodec`, `abortSignalCodec`, `callbackCodec` | `./codecs` | Built-in codecs. |
| `connectChannel(port, opts)` | `./codec` | Wrap a `MessagePort` into a `Channel` (used by the ref codec on `__serve-ref`/`__ref-acquired`). |
| `RemoteRef<T>` / `remoteRef(obj)` / `releaseRef(obj)` | app-level (see §5) | Reference token + factory, following `examples/remote_ref/ref_codec.ts`. |
| `ActorDiedError`, `RemoteError` | `.` | Error types rebuilt across channels. |

The reference codec in `examples/remote_ref/ref_codec.ts` and the tests in `ref_test.ts` are the
canonical implementation/tests of the acquire semantics this design reuses. Maieutics does not
rewrite them; it uses the same patterns through the public surface above.

## 4. Architecture

```
                 ┌─────────────────────────────────────────────┐
                 │  Maieutics host process (deno run, JS main)  │
                 │                                               │
                 │  owns: worker registry (specifier→Worker),   │
                 │        dependency graph, cascade/reload       │
                 └──────┬───────────────┬───────────────┬────────┘
                        │ spawn()       │ spawn()       │ ...
                        ▼               ▼
                 ┌─────────────┐  ┌─────────────┐
                 │ plugin A    │  │ plugin B    │
                 │ worker      │  │ worker      │
                 │ serveWorker │  │ serveWorker │
                 │ + SDK       │  │ + SDK       │
                 └─────────────┘  └─────────────┘
                        ▲               ▲
                        └── interop channel (worker-actor built,
                            triggered by SDK acquire, routed by host)
```

- **Host** = the `deno run` plugin-host process (already exists). It is the JS main thread: it
  creates every plugin worker with `spawn()`, holds the `Worker` handles, owns the dependency graph
  and the lifecycle state machine.
- **Plugin worker** = a `spawn`ed worker running `serveWorker` plus the Maieutics plugin SDK. The
  SDK file (the module containing `defineActor`) contains the initialization wiring.
- **Interop channel** = a worker-actor-built channel (per `routeAcquire`/`ref_test.ts`): created by
  worker-actor's main-side router, never by Maieutics code.

## 5. Reference semantics (reused from worker-actor, not rewritten)

`examples/remote_ref/ref_codec.ts` defines a reference codec on top of the public surface:

- `remoteRef(obj)` wraps a live object into a `RemoteRef<T>` token whose identity is
  `refId = "<ownerWorkerId>:<localCount>"`.
- A `RemoteRef` can travel through RPC payloads (codec placeholder). On the receiving side it is a
  proxy that queues calls until the channel is acquired.
- **Acquire** (ref_test.ts "acquire: ..." tests): the holder's first call triggers
  `triggerAcquire(refId)`; main's `routeAcquire` resolves `refId` → owner worker via the worker-id
  prefix, creates a `MessageChannel`, sends `__serve-ref` (port1) to the owner and
  `__ref-acquired` (port2) to the holder; both sides wrap their port with `connectChannel` and
  register it (the owner with `__serve-ref`, the holder with `__ref-acquired`, via
  `registerControlHandler`). The channel is now direct, liveness-paired, and worker-actor-managed.
- Identity: the same `refId` resolves to the same proxy on each side; multi-hop sharing
  (A→main→B→C) reaches the same owner object; restore collapses a ref back to its owner to a local
  call; release broadcasts death; liveness detects dead holders/owners.

Maieutics reuses all of this **as-is**. It does not re-implement any of it.

## 6. Plugin model

### 6.1 Actor surfaces

A plugin export module may export any number of actor surfaces. Each is declared with `defineActor`
from the SDK:

```ts
// plugins/dep/mod.ts
import { defineActor } from "@maieutics/plugin-sdk";

export const math = defineActor({
  double(n: number): number { return n * 2; },
  add(a: number, b: number): Promise<number> { return Promise.resolve(a + b); },
});

export const greet = defineActor({
  hello(name: string): string { return `hi ${name}`; },
});
```

`defineActor(surface)`:

- returns the surface **typed as `Remote<T>`** (worker-actor's projection; methods → promises);
- attaches a runtime marker symbol (`Symbol.for("maieutics/actor/v1/surface")`) so the SDK can
  distinguish actor exports from plain exports;
- rejects non-function members at the type level (a constant export is a type error, matching
  `Remote<T>`'s `never` mapping).

### 6.2 The canonical specifier

The interop identity of one plugin export is `<deno.json name>/<exports subpath>` (the `.` export
is the bare name). E.g. a plugin whose `deno.json` is `{ "name": "@maieutics/dep", "exports": {
"./main": "./mod.ts" } }` has specifier `@maieutics/dep/main`.

### 6.3 Dependency declaration

`deno.json` gains `maieutics.dependencies: string[]` (plugin ids = directory names). The kernel
builds the dependency graph (§8) and writes the enabled plugins into the host config; the host
re-derives ordering and cascade closures at runtime from the same edges.

## 7. Wiring (all inside the SDK file)

The SDK file (`deno/maieutics-plugin-sdk/mod.ts`) contains the initialization logic. It is imported
by the plugin worker entry, which is otherwise a thin shim.

### 7.1 Worker entry

```ts
// deno/maieutics-plugin-host/worker_entry.ts
import { initPluginWorker } from "../maieutics-plugin-sdk/mod.ts";
initPluginWorker();
```

`initPluginWorker()` (in the SDK) does, in order:

1. `serveWorker({}, { onLink })` — the worker-actor runtime. The `api` object is empty: the
   peer-facing surface is provided per-link via `onLink` → `link.serve(...)`. `serveWorker` takes
   over `self.onmessage` and handles handshake / `__worker-id` / `__link` / `__serve-ref` /
   `__ref-acquired` / `request` / `response`.
2. `self.addEventListener("message", ...)` — a **separate** listener (independent of `serveWorker`'s
   `onmessage` property) that handles Maieutics' own control frames:
   - `init { entryUrl }` → `await import(entryUrl)`, scan top-level exports for extension-point
     markers and actor markers, record the module namespace, post `ready`.
   - `dispose` → cleanup (close links, reject in-flight).
3. `registerControlHandler("__serve-ref", ...)` / `registerControlHandler("__ref-acquired", ...)` —
   these are already handled by `serveWorker`'s dispatch for the ref codec; the SDK does not need to
   re-register them unless it adds Maieutics-specific acquire frames (§7.3).

`onLink` (from `serveWorker`):

- `link.serve(<flattened plugin namespace>)` — expose the plugin's actor surfaces to the peer.
  The flattened surface maps each export:
  - an actor export `math` → methods `math.double`, `math.add`, ... (method name = `"math.double"`);
  - a plain function export `helper` → method `"helper"`;
  - non-function plain exports are ignored (constants are not exposed).
- The peer (consumer) calls through `link.rpc` cast to the contract type
  (`link.rpc as unknown as PeerRpc<Contract>`), exactly as `link_b.ts`/`link_c.ts` do.

### 7.2 Extension points (replacing `extension.invoke`)

Extension points are Maieutics-specific exports (`McpDiscover`, `ToolPreInvoke`, `ToolPostInvoke`,
identified by marker symbols, unchanged from ADR 0016). The host calls them through the spawned
actor's `Remote<T>`: the plugin worker's `api` (passed to `serveWorker`) is the extension-point
surface. But `serveWorker`'s `api` must exist at module top level, and the plugin module is only
known after `init`. Resolution: the worker entry's `api` is a **mutable object** that the SDK
repopulates after `init` — `serveWorker` resolves methods at call time via the object, so updating
its properties after init is sufficient:

```ts
const extensionApi: Record<string, (...args: unknown[]) => unknown> = {};
serveWorker(extensionApi, { onLink });
// after init: extensionApi["McpDiscover"] = foundMcpDiscover;
```

The host then calls `await actor.McpDiscover(payload)` through `Remote<T>`. The `extension.invoke`
/ `extension.result` / `extension.error` bus messages and the `ExtensionInvokePayload` /
`ExtensionResultPayload` / `ExtensionErrorPayload` DTOs are deleted once this path is confirmed
(§10 step 6).

### 7.3 Acquire (interop trigger)

A consumer plugin calls a dependency's surface via its stub (§9). The stub's first call must reach
the owner worker. Because the identity is a **specifier** (not a worker-id-prefixed refId), the
SDK maintains a specifier→ref mapping:

- On `init`, the SDK learns its own specifier (from the host config) and the specifiers of its
  declared dependencies.
- The SDK registers a control frame type `__acquire-actor` (a Maieutics extension of the
  worker-actor control plane, registered via `registerControlHandler`):
  - **Consumer side**: the stub's first call posts
    `{ type: "__acquire-actor", specifier, refId: "<consumerWorkerId>:<n>" }` to main (host).
  - **Host side**: the host receives `__acquire-actor`, maps `specifier → owner workerId` (its own
    registry; workerId is an opaque id, mapping it is correct per the approved design), rewrites
    the refId prefix to the owner's workerId, then runs the worker-actor acquire path
    (`routeAcquire`-equivalent: create a channel, `__serve-ref` to owner, `__ref-acquired` to
    consumer). The channel construction is worker-actor's `connectChannel`/`routeAcquire` internals
    — the host only invokes them.
  - **Owner side**: `__serve-ref` handler serves the requested specifier's surface (the flattened
    namespace or the specific actor) over the acquired channel.

Concretely, the host-side router is Maieutics code but delegates channel creation to worker-actor's
public primitives: the host calls `spawn`'s router by posting the standard `__acquire-ref` /
`__serve-ref` / `__ref-acquired` frames itself (the host IS the main thread; it can invoke
`dispatchControlFrame` and use `connectChannel` on the returned ports). The mapping step is the only
Maieutics-specific logic:

```
consumer worker:  stub call → { type:"__acquire-actor", specifier, refId:"wC:n" }
host (main):      specifier → ownerWorkerId  (own registry)
                  refId' = ownerWorkerId + ":" + n
                  → worker-actor acquire path (routeAcquire semantics, §5)
owner worker:     __serve-ref → serve flattened namespace over port
consumer worker:  __ref-acquired → connectChannel(port) → direct calls
```

After acquisition the two workers communicate **directly** over the worker-actor-managed channel;
the host is out of the data path.

## 8. Lifecycle orchestration (unchanged from the approved design)

- `maieutics.dependencies` + `PluginDependencyGraph` (kernel): missing dependency → exclude plugin
  and its transitive dependents (`missing_dependency:<id>`); cycle → exclude members and dependents
  (`dependency_cycle`). Degraded, not fatal.
- Topological start: dependencies first, in waves; wave-parallel, waves-serial.
- Per-plugin states: `stopped → starting → running → stopping → stopped`, plus `failed` (reason) and
  `disabled` (crash budget exhausted).
- Cascade: reverse-topological waves, bounded-grace termination, then topological restart.
- Crash → disabled → cascade dependents.
- `extension.registry` republished on every state change with per-plugin `states` (backward
  compatible new optional field).

## 9. Stub and type identity

- **Runtime**: the plugin worker's load hook (in the SDK, using `node:module.registerHooks`,
  verified on Deno 2.9.5) intercepts declared-dependency specifiers and serves a synthesized
  virtual stub (plain JS) that binds each export to a remote callable forwarding to the acquired
  channel (§7.3).
- **Development**: each plugin's `deno.json` references a kernel-generated `import_map.json` at the
  plugins root that maps dependency specifiers → real module files. `deno check`/editors resolve to
  the real module and see `defineActor`'s `Remote<T>` return type. The runtime hook is the only
  runtime resolver; the import map is never referenced at runtime (verified: a plugin's own
  `deno.json` imports do not resolve inside a worker without the hook — no conflict).
- **Agreement**: the stub's runtime callable for `math.double` has the same shape
  (`(n: number) => Promise<number>`) as the type `Remote<typeof real["math"]>` gives at compile
  time. The plugin author writes `await math.double(21)` in both cases.

## 10. Implementation order

1. **SDK core**: `defineActor` (marker + `Remote<T>` return), `initPluginWorker` (serveWorker with
   mutable api + `onLink` + `init`/`dispose` listeners), load hook for dependency specifiers.
2. **Host**: replace `new Worker` with `spawn()`; maintain `specifier → workerId` registry;
   implement the `__acquire-actor` main-side router (mapping specifier → owner, then the
   worker-actor acquire path).
3. **Type identity**: kernel generates `import_map.json`; plugin `deno.json` references it; a
   `deno check` fixture proves consumer types equal runtime shapes.
4. **Extension points over `Remote<T>`**: host calls `actor.McpDiscover(...)`; keep
   `extension.invoke` temporarily, delete after integration tests pass.
5. **Dependency graph + topological start + cascade + crash→disable** (kernel graph; host
   re-derives waves).
6. **Delete `extension.invoke` protocol** (messages, DTOs, payload records) after the
   `Remote<T>` path is confirmed by tests.
7. **Hot reload**: debounced `FileSystemWatcher` → `plugin.reload` (in-process cascade rebuild) for
   source edits. The reload payload carries the plugin's full replacement config, so permission and
   manifest changes also rebuild the worker in-process with the new grants — no host-process restart.
8. **Docs/tests**: ADR 0019 update; unit + integration tests (real deno).

## 11. File inventory

Deno side:
- `deno/maieutics-plugin-sdk/mod.ts` — `defineActor`, `initPluginWorker`, load hook, acquire wiring.
- `deno/maieutics-plugin-host/worker_entry.ts` — thin: `import { initPluginWorker } ...; initPluginWorker();`
- `deno/maieutics-plugin-host/host.ts` — spawn-based worker management, specifier→workerId
  registry, acquire router, dependency graph/cascade/reload.
- `deno/maieutics-plugin-host/mod.ts` — bus wiring; deletes `extension.invoke` handling.
- `deno/maieutics-plugin-host/host_test.ts`, `deno/maieutics-plugin-sdk/mod_test.ts` — tests.

.NET side:
- `Maieutics/Plugins/PluginManifest.cs` — reads `maieutics.dependencies`.
- `Maieutics/Plugins/PluginDependencyGraph.cs` — graph validation/ordering.
- `Maieutics/Plugins/PluginHostManager.cs` — spawn-based start, watcher, reload, registry states.
- `Maieutics/Control/ReplControlMessages.cs` — deletes `extension.*` messages; adds `plugin.reload`.
- `Maieutics/Configuration` / `MaieuticsHost.cs` — `Maieutics:Plugins` section.
- Tests under `Maieutics.Jupyter.Tests/`.

## 12. Verification

- `deno test` (sdk, plugin-host) — interop round-trips, type fixtures, cascade/reload.
- `dotnet test Maieutics.slnx` — integration tests with a real deno host.
- `dotnet build Maieutics.slnx --no-restore -warnaserror`.
- `git diff --check`.

## References

- `worker-actor/PLUGIN-SYSTEM.md` (orchestration model, §3–§8)
- `worker-actor/ref_test.ts` and `examples/remote_ref/ref_codec.ts` (acquire/ref semantics)
- ADR 0016 (script plugins), ADR 0018 (permission store and Deno execution)
