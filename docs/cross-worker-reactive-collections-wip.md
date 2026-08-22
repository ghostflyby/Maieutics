# Cross-Worker Reactive Collections — WIP Design

Status: **In progress.** The minimal end-to-end loop is **closed and verified**:
a provider in one worker contributes a reactive value to an extension point
owned by another worker, the defining worker aggregates it into its local
collection, consumers observe the aggregate, and the contribution can be
withdrawn (explicit `unprovide` or stream end). Stub identity replacement means
`import { ep } from "contract"` yields the real remote identity. Integration
tests cover single/multi-provider aggregation, imported identity shape, and
remote unprovide.

Remaining (designed but not implemented): hard-crash cleanup for dead providers
(no liveness plane today), remote `snapshot`, and duplicate-provide semantics.

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

### Stub identity replacement

The load-hook stub for a contract module synthesizes `createRemoteIdentity(name,
owner, defSpecifier)` exports for each contract identity the dependency worker
reported in its ready frame (host stores them per worker and passes them via
`maieutics-config` `actorEntries[].identities`). `import { ep } from "contract"`
therefore yields the real branded identity with `defSpecifier` filled, and
`provide(ep, signal)` routes to the defining worker's remote collection without
any hand-built substitute.

### Remote unprovide / lifecycle

- The collection surface gained `remove(providerKey?)` (with no key: remove all
  contributions of that extension point).
- `provide` on a remote identity generates a `providerKey` (UUID) carried on the
  registration; `unprovide(registration)` routes `remove(providerKey)` back to
  the defining worker, which stops the pull loop and unregisters the signal.
- `applyRemoteContribution` is idempotent per provider key: a repeated `add`
  with the same key replaces the previous contribution instead of stacking
  duplicates.
- Stream-end (done) or stream-error withdraws the contribution (the provider is
  gone or the channel closed); `disposePlugin` stops every pull loop.
- Hard-crash cleanup (provider process dies without closing its stream) is not
  covered: Maieutics deliberately has no liveness plane, so the definer's pull
  loop cannot observe the death. That remains future work.

### Tests (interop_test.ts)

- Cross-worker single provider: provider contributes `[1]`, definer's snapshot reports it;
  a signal change streams `[2]`; `undefined` drops the contribution.
- Cross-worker multiple providers: two provider workers contribute, definer aggregates both.
- Imported contract identity: `import { ep }` yields `defSpecifier` = the definer's specifier
  and classifies as remote (`isRemoteExtensionPoint`).
- Remote unprovide: `unprovide(registration)` withdraws the contribution.
- Backward compatibility: the existing actor-interop tests (depActor over an ordinary actor,
  default-import redirect, jsr:-prefixed specifier, plain-module pass-through, reactive
  subscribe across workers) all stay green.

## Remaining work

1. **Hard-crash cleanup.** Provider worker exit without an explicit `unprovide` or stream
   close leaves its contribution in the definer's collection (no liveness plane today). Needs
   worker-actor liveness or a host-side cascade that calls `remove` on the definer.
2. **Remote `snapshot`.** A remote `snapshot(ep)` is a request to the defining worker; add it
   to the collection surface (currently `add`, `remove`, `changes`).
3. **Duplicate-provide semantics.** Repeated `provide` of the same signal by the same worker
   currently registers two independent contributions (each with its own provider key); decide
   whether that should coalesce per (specifier, name).

## Verification baseline

- Current worktree: deno workspace tests 57 passed / 0 failed (SDK + host), REPL 10 passed —
  no regressions. `deno fmt --check` clean, `deno check` clean for SDK and host.
- Feature gates: cross-worker aggregation (single/multi provider), imported contract
  identity, and remote unprovide tests are green.
- Full repository acceptance: `dotnet test Maieutics.slnx` and
  `git diff --check` clean (dotnet build with -warnaserror verified on the earlier pass).
