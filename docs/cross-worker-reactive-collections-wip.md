# Cross-Worker Reactive Collections — WIP Design

Status: **In progress.** The minimal end-to-end loop is **closed and verified**:
a provider in one worker contributes a reactive value to an extension point
owned by another worker, the defining worker aggregates it into its local
collection, and consumers observe the aggregate. Two integration tests cover
the single-provider and multi-provider cases.

Remaining (designed but not implemented): stub identity replacement so a
provider's `import { ep } from "contract"` carries the real identity instead of
a hand-built substitute, remote `snapshot`/`unprovide` round-trips, and
lifecycle cleanup for remote contributions.

## Background

The plugin SDK has contract-mode reactive extension points (already committed):

- `defineExtensionPoint(name)` returns an identity owned by the defining module (owner = module
  URL, captured via the load hook — unforgeable).
- `provide(ep, signal)` / `subscribe(ep)` / `snapshot(ep)` work **in-process**: `subscribe`
  aggregates the current worker's providers into an `AsyncIterable` of collection snapshots.
- Cross-worker streaming of `AsyncIterable` RPC results works (dependency-stub proxy decodes
  result frames through the worker-actor registry; committed in `3b44b8e`).

What was missing is **cross-worker aggregation**: providers in other workers contributing to a
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

## Implemented: the closed loop

### Acquire protocol: address by (specifier, name)

The remote collection is addressed by the extension point's name, not by the worker's
default actor surface. The acquire protocol gained an optional `name` field:

- `AcquireActorFrame` (`actor_ref.ts`) has `name?: string`.
- `createDependencyStub(specifier, surfaceName?)` — when `surfaceName` is given, the acquire
  carries the name and the owner serves exactly that named surface. Named stubs are cached
  independently (cache key `specifier\0name`), so a worker can hold both the default actor
  surface and one named collection per extension point. When `surfaceName` is absent the
  behavior is unchanged (backward compatible).
- `findSurfaceBySpecifier(specifier, name?)` — with `name`, resolves the exact named surface
  (ordinary actor or collection); without it, keeps the specifier-first behavior that prefers
  an ordinary actor over a collection sharing the specifier.
- Host `#routeAcquire` forwards the optional `name` on the `__serve-ref` frame; the host stays
  agnostic to extension points (pure distributed).

### Collection surface on the defining worker

`scanExports` registers, for each contract-mode identity, a collection actor surface under the
**identity's name** (not the export key, so `provide(ep, ...)` on an imported identity routes
here regardless of local export spelling):

- `add(initial, changes)` → `applyRemoteContribution`
- `changes()` → `subscribe(ep)` (aggregated snapshots)
- carries `COLLECTION_SURFACE_MARKER`

### Remote provide

`provide(ep, signal)` on a remote identity (`defSpecifier` differs from the current worker)
routes to `remoteProvideImpl(specifier, name, initial, changesOf(signal))`: it acquires the
defining worker's collection surface via the named stub and calls `add(initial, changes)`.
`changesOf(signal)` is an `AsyncIterable` of the signal's subsequent changes (initial excluded;
it travels as the separate initial argument); `return()` stops the effect.

`applyRemoteContribution` registers a local signal with the initial value, then runs a
continuous pull loop over the change stream, updating the signal as values arrive (`undefined`
drops the contribution from snapshots via the existing collection semantics). The pull loop
must run independently of any consumer so the producer's values stream continuously; a failed
or ended stream keeps the last value.

### Key bug found and fixed: double decode of AsyncIterable args

The change stream initially arrived at the defining worker as `{}` — `changes[Symbol.asyncIterator]
is not a function`. Root cause: `serveRefOwner` decoded `frame.args` once, then handed the
decoded array to `makeRpcHandler`, which **decodes again**. Plain values survive a second
decode, but a codec value like an AsyncIterable placeholder becomes a plain object on the
second pass: the placeholder is already a real object, so the walker's `isPlainObject`
branch copies only string keys and drops the symbol-keyed `[Symbol.asyncIterator]`.

Fix: `serveRefOwner` passes the original `frame.args` (still encoded) to `makeRpcHandler` and
lets it decode once — matching the library's own `ref_codec` example, which decodes once and
invokes the function directly.

### Tests (interop_test.ts)

- Cross-worker single provider: provider contributes `[1]`, definer's snapshot reports it;
  a signal change streams `[2]`; `undefined` drops the contribution.
- Cross-worker multiple providers: two provider workers contribute, definer aggregates both.
- Backward compatibility: the existing actor-interop tests (depActor over an ordinary actor,
  default-import redirect, jsr:-prefixed specifier, plain-module pass-through, reactive
  subscribe across workers) all stay green.

The tests hand-build a remote identity (`{ name, owner, defSpecifier, brand }`) because stub
identity replacement is not yet implemented; see below.

## Remaining work

1. **Stub identity replacement.** The load-hook stub currently serves a default acquire
   surface; `import { ep } from "@contract/main"` does not yet yield the real identity value
   with `defSpecifier` filled. The hand-built identity in the tests must become automatic: the
   stub should provide an identity substitute carrying `{ name, owner, defSpecifier, brand }`
   for contract-module exports. `flattenSurface` cannot currently reach constant/identity
   exports, so this needs either a stub-side export table or a marker-based pass-through.
2. **Remote `snapshot`.** A remote `snapshot(ep)` is a request to the defining worker; add it
   to the collection surface (currently only `add` and `changes`).
3. **Remote `unprovide` / lifecycle.** Provider worker exit should drop its contribution
   (worker-actor liveness or an explicit `unprovide` round-trip). Currently a dead provider's
   value lingers in the definer's collection.
4. **`defSpecifier` provenance.** The hand-built test identity sets `owner` to a contract URL
   that does not match the defining worker's actual module URL. The real stub replacement will
   carry the true owner; the tests should then assert on the real identity rather than the
   substitute.

## Verification baseline

- Current worktree: deno workspace tests 55 passed / 0 failed (SDK + host), REPL 10 passed —
  no regressions. `deno fmt --check` clean, `deno check` clean for SDK and host.
- Feature gate: the two new cross-worker aggregation tests are green.
- Full repository acceptance still to run: `dotnet test Maieutics.slnx`,
  `dotnet build Maieutics.slnx --no-restore -warnaserror`, `git diff --check`.
