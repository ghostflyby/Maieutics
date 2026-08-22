# Cross-Worker Reactive Collections — WIP Design

Status: **In progress, uncommitted.** The SDK-side remote-routing groundwork exists in the
worktree (reactive.ts / mod.ts / actor_ref.ts, ~250 added lines) but the minimal end-to-end
loop is **not closed**: the collection surface is registered and the remote `provide` route is
wired, yet the acquire protocol cannot address the collection surface distinctly from ordinary
actor surfaces, so `remoteProvideImpl` cannot yet reach a defining worker's collection.

This document records (a) the current uncommitted state, (b) the agreed design, (c) the exact
remaining work, so the feature can be finished in a focused pass.

## Background

The plugin SDK has contract-mode reactive extension points (already committed):

- `defineExtensionPoint(name)` returns an identity owned by the defining module (owner = module
  URL, captured via the load hook — unforgeable).
- `provide(ep, signal)` / `subscribe(ep)` / `snapshot(ep)` work **in-process**: `subscribe`
  aggregates the current worker's providers into an `AsyncIterable` of collection snapshots.
- Cross-worker streaming of `AsyncIterable` RPC results works (dependency-stub proxy decodes
  result frames through the worker-actor registry; committed in `3b44b8e`).

What is missing is **cross-worker aggregation**: providers in other workers contributing to a
collection owned by the defining worker, and consumers observing the aggregate.

## Agreed design (from discussion)

1. **The remote collection is physically owned by the defining worker.** The identity's owner
   (module URL) maps to exactly one worker (one module ↔ one worker entry), so the defining
   worker is the collection owner.
2. **Providers only see the identity (name); the framework routes.** A provider imports the
   contract identity (via the dependency stub — the load hook intercepts the import and the
   real `defineExtensionPoint` never runs in the provider), then calls `provide(ep, signal)`.
   The framework routes the contribution to the defining worker's collection.
3. **Strong dependency ⇒ topological start.** The provider declares the contract module as a
   dependency; `PluginDependencyGraph` starts dependencies first, so the defining worker is
   always up before any provider calls `provide`.
4. **Signal transport = current value + AsyncIterable of changes.** A signal is equivalent to
   "initial value + change events". We transmit `(initial, changes: AsyncIterable)` — the
   worker-actor iterable codec transports the `AsyncIterable` argument across workers
   (verified: an `AsyncIterable` RPC argument round-trips with values and backpressure).
5. **Unified API, async-first.** `provide` / `subscribe` / `snapshot` stay the public surface;
   local implementations are synchronous (current), remote ones go through the framework's
   acquire machinery.

## Current uncommitted state

### `deno/maieutics-plugin-sdk/reactive.ts` (+136 lines)

- `ExtensionPointIdentity` gains `defSpecifier?: string` — canonical specifier of the defining
  worker (filled by the SDK during init; carried by the dependency stub for imported
  identities). This is the routeable address of the defining worker.
- `MutableExtensionPointIdentity` + `bindDefiningWorker(ep, specifier)` — SDK attaches the
  defining worker specifier to locally-declared identities.
- `setWorkerSpecifier(specifier)` / `setRemoteProvide(fn)` — injected by mod.ts (avoids a
  reactive → actor_ref import cycle).
- `isRemoteExtensionPoint(ep)` / `isLocalExtensionPoint(ep)` — local vs remote by comparing
  `defSpecifier` against the current worker specifier.
- `changesOf(signal)` — an `AsyncIterable` of a signal's **subsequent** changes (initial value
  excluded; it travels as the separate initial argument). `return()` stops the effect.
- `provide(ep, signal)` — when the identity is remote and `remoteProvide` is installed, routes
  to `remoteProvide(defSpecifier, name, initialValue, changesOf(signal))` instead of the local
  registry, returning a `remote:`-prefixed registration.

### `deno/maieutics-plugin-sdk/mod.ts` (+99 lines)

- `initPluginWorker`: after `ownSpecifierValue = config.specifier`, calls
  `setWorkerSpecifier(config.specifier)` and `setRemoteProvide(remoteProvideImpl)`.
- `scanExports`: for contract-mode identities (`isExtensionPoint(value)`):
  - `bindDefiningWorker(value, ownSpecifierValue)`
  - registers `extensionPointIdentities.set(name, value)` (separate from `extensionPoints`,
    which stays host-callable-handler-only — identities are not `servingApi` methods)
  - `registerActorExport(name, ownSpecifierValue, collectionActorSurface(name))`
- `collectionActorSurface(name)` — the remote collection surface:
  - `add(initial, changes: AsyncIterable)` → `applyRemoteContribution`
  - `changes()` → `subscribe(ep)` (aggregated snapshots)
  - carries `COLLECTION_SURFACE_MARKER`
- `applyRemoteContribution(ep, initial, changes)` — creates a local signal from `initial`,
  `provide`s it, then `for await` over `changes` updating the signal (undefined drops the
  provider from snapshots via the existing collection semantics).
- `remoteProvideImpl(specifier, name, initial, changes)` — **intended** to acquire the defining
  worker's collection surface via `createDependencyStub(specifier)` and call `add`. **Not
  working yet** (see blocker).
- `extensionPointIdentities` map, cleared on dispose.

### `deno/maieutics-plugin-sdk/actor_ref.ts` (+17 lines)

- `COLLECTION_SURFACE_MARKER` symbol.
- `findSurfaceBySpecifier(specifier)` now prefers an ordinary actor surface over a
  collection surface when both share a specifier (a worker can export both an actor and a
  collection). This keeps the existing cross-worker actor test green while the collection
  surface exists.

## The blocker: collection surface addressing

`remoteProvideImpl` currently does `createDependencyStub(defSpecifier)` then `[name].add(...)`.
The acquire protocol (`__acquire-actor { specifier, refId }`) routes **by specifier only** and
the owner side serves the first surface matching that specifier. With
`findSurfaceBySpecifier` preferring ordinary actors, a provider's collection `add` either hits
the wrong surface or is shadowed by an ordinary actor of the same worker.

The collection surface therefore needs **name-addressed acquisition**: acquire a specific
surface by `(specifier, name)` rather than by specifier alone.

## Remaining work (to close the minimal loop)

1. **Acquire protocol extension — address by (specifier, name).**
   - `AcquireActorFrame` gains an optional `name` field.
   - `createDependencyStub` / the acquire post in mod.ts passes `name` when the caller wants a
     specific surface (the collection).
   - `findSurfaceBySpecifier(specifier, name?)` returns the exact named surface when `name` is
     given; otherwise keeps the current specifier-first behavior (backward compatible for
     existing actor interop).
   - Host `#routeAcquire` transparently forwards the `name` field (host stays agnostic to
     extension points — pure distributed).
2. **Verify `remoteProvideImpl` end-to-end** once name-addressed acquire works:
   `provide(ep, signal)` in a provider worker reaches the defining worker's `collectionActorSurface.add`,
   `applyRemoteContribution` updates the defining worker's local signal, and the defining
   worker's `subscribe(ep)` / `snapshot(ep)` include the remote provider's value.
3. **Tests.**
   - Cross-worker single provider: provider `provide(ep, signal)`, defining worker
     `subscribe(ep)` sees `[initial]` then updates as the provider's signal changes
     (including `undefined` dropping the contribution).
   - Cross-worker multiple providers: two provider workers contribute, defining worker
     aggregates both.
   - Backward compatibility: existing actor interop (consumer `depActor` over an ordinary
     actor) still resolves to the ordinary surface.
   - In-process behavior unchanged (existing `reactive_test.ts` stays green).
4. **Cleanup decisions (design still open):**
   - `remoteProvideImpl` currently uses the dependency stub; after name-addressed acquire this
     may be simplified to a direct acquire post + channel call.
   - Lifecycle: provider worker exit should drop its contribution (worker-actor liveness or an
     explicit `unprovide` round-trip). Not designed yet.
   - `snapshot` remote: async-first API — a remote `snapshot` is a request to the defining
     worker; may be added to the collection surface.

## Files touched in the WIP

| File | Change |
|---|---|
| `deno/maieutics-plugin-sdk/reactive.ts` | remote routing groundwork (defSpecifier, changesOf, isRemote, provide branch) |
| `deno/maieutics-plugin-sdk/mod.ts` | collection actor surface, applyRemoteContribution, remoteProvideImpl, init wiring |
| `deno/maieutics-plugin-sdk/actor_ref.ts` | collection-surface marker + findSurfaceBySpecifier preference |
| `deno/maieutics-plugin-host/host.ts` | (planned) forward `name` in `#routeAcquire` |
| `deno/maieutics-plugin-host/interop_test.ts` | (planned) cross-worker aggregation tests |

## Verification baseline

- Current worktree (WIP): deno tests 53 passed / 0 failed, REPL 10 passed — the WIP does not
  regress existing behavior. `git stash` can restore the clean committed state.
- Feature gate: new cross-worker aggregation tests green; `deno fmt --check`, `deno task check`,
  full `dotnet test Maieutics.slnx` clean.
