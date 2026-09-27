# ADR 0035: Service-Worker Lifecycle for Plugin Workers

Status: Accepted

Date: 2026-09-26

Related: [ADR 0016](0016-script-plugins-and-extension-points.md),
[ADR 0020](0020-repl-actor-boundary.md), [ADR 0033](0033-plugin-scoped-mcp-data-file.md),
[ADR 0034](0034-mcp-adjustment-chain.md)

## Context

Workers were started eagerly at host boot in dependency waves and ran forever — one
resident Deno worker per declared entrypoint, regardless of whether anything ever
called it. The lifecycle machinery that exists (per-worker start/stop, dependency
cascades, provider-dead notifications, reload) already models "a worker can go away
and come back"; the only missing piece was a policy for when to stop and a wake path
for when to start.

## Decision

All workers get a Service-Worker lifecycle: **install at boot, reference-counted
reclamation, and wake-on-demand.**

- **Install**: workers still spawn once at boot in dependency waves, register their
  extension points, and settle — unchanged.
- **References** (anything that keeps a worker alive):
  1. in-flight extension-point invocations;
  2. live actor serve channels routed to the worker's specifier (`__ref-released`
     frames from the SDK decrement);
  3. a running dependent — a running worker pins its declared dependencies
     transitively (derived, not explicit).
- **Reclamation**: zero references for the whole idle grace (30s default,
  `MAIEUTICS_PLUGIN_IDLE_GRACE_MS` / config `idleGraceMs`) → cooperative stop through
  `#stopWorker` on the serialized lifecycle tail — the provider-dead notification
  fires and dependents drop contributions, exactly as when a worker dies.
- **Wake**: an invoke or an actor acquire targeting a stopped worker starts its
  transitive dependency closure first (a new requirements-direction closure; the
  existing `#dependencyClosure` walks dependents) and then the worker. A deliberate
  wake resets the crash counter.

Key invariant: **a stopped worker keeps its registry entries and contract
identities** — a Service-Worker registration survives termination. The kernel's
registry therefore keeps routing to stopped workers, MCP server generations and
adjustment chains survive stops, `%status` reports stopped workers as a normal lazy
state, and the only kernel-side change needed is the invoke timeout (30s) covering a
cold spawn.

## Implementation notes

- The reclaimer runs on a host-side monitor (tick = grace/4, clamped 50ms–1s);
  reclamation is cooperative and serialized on the lifecycle tail.
- The acquire router counts open served channels; the SDK posts `__ref-released`
  when a stub is disposed or a contribution is withdrawn, so consumers pin their
  providers exactly while subscribed.
- `__maieuticsProviderDead` now also invalidates cached dependency stubs and pending
  acquires for the dead provider, so the next dependency call re-acquires from the
  restarted worker.
- Crash cascades are serialized through the lifecycle tail (they previously
  interleaved with reloads — amplified by frequent stop/start cycles).
- Storage pools outlive worker stops by design (WAL recovery on reopen); only host
  shutdown closes them.

## Consequences

- Idle workers cost nothing: a long tail of installed plugins no longer keeps
  resident workers.
- Worker authors must treat in-memory state as reconstructible — contributions and
  signals die with a reclaimed worker and are rebuilt on the next start (the same
  contract as a crash, now routine).
- The first invocation after reclamation pays worker startup latency within the 30s
  invoke budget.
- Registrations, MCP server generations, adjustment chains, capability grants, and
  storage directories all survive reclamation; nothing kernel-side needs to change
  on a stop.
