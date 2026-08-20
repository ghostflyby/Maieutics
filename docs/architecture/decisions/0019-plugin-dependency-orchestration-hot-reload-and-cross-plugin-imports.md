# ADR 0019: Plugin Dependency Orchestration, Hot Reload, and Cross-Plugin Imports

Status: Accepted

Date: 2026-08-20

## Context

ADR 0016 delivered out-of-process script plugins (one `deno run` host process, one permission-scoped
worker per plugin export). It left three gaps: plugins could not declare dependencies, the host started
every worker in enumeration order, and there was no hot reload. Plugin authors also had no way for one
plugin to call into another — each worker was isolated by its own module graph.

The orchestration model in `worker-actor/PLUGIN-SYSTEM.md` (lifecycle state machine, cascade teardown
in waves, topological recovery, stop-and-start reload) is mechanism-independent and maps directly onto
Maieutics' worker-per-export model. This ADR applies that model and adds a module-hook view so that
importing another plugin's module specifier yields a remote actor reference with an identical type
shape — the same module script, two contents by context.

## Decision

### 1. Dependency declaration and graph (kernel is the policy authority)

- A plugin's `deno.json` gains `maieutics.dependencies: string[]`; each value is a plugin id (directory
  name).
- `PluginDependencyGraph` (kernel) validates the declared edges: a missing dependency excludes the
  plugin and everything that transitively depends on it (`missing_dependency:<id>`); a cycle excludes
  every member of the cycle and their dependents (`dependency_cycle`). Excluded plugins are logged and
  left out of the host config — degraded, not fatal (ADR 0016's "disable with an explicit log").
- The graph produces the deterministic topological start order written into the host config.
- The host process re-derives ordering and cascade closures at runtime from the same edges because it
  owns the worker handles and crash events.

### 2. Cross-plugin imports (load-hook module view)

The module view follows `PLUGIN-SYSTEM.md` §3.2:

- The canonical specifier of a plugin export is `<deno.json name>/<exports subpath>` (e.g.
  `@maieutics/example/mcp`; the `.` export is the bare name).
- Each plugin worker's entry installs `node:module.registerHooks` for the specifiers of its **declared
  dependencies** before dynamically importing its own module. A declared dependency resolves to a
  synthesized virtual stub (verified in Deno 2.9.5) whose source binds one remote callable per
  top-level export name reported by the owner worker at start; the stub is plain JavaScript.
- Type identity with the real module comes from a kernel-generated `import_map.json` at the plugins
  root, referenced by each plugin's `deno.json` for `deno check` and editors. The runtime host
  configuration never references the import map; the load hook is the only runtime resolver (verified:
  a plugin's own `deno.json` imports do not resolve inside a worker, and a bare specifier is not
  resolvable by Deno without the hook — no conflict between the two views).
- An undeclared cross-plugin import fails at module resolution with `dependency_not_declared` (the
  hook rejects specifiers that match a known plugin root but are not in the redirect table).
- First use lazily acquires a direct `MessageChannel` between the consumer worker and the owner worker
  (`acquire-actor` → host routes → `serve-actor` on the owner + `actor-acquired` on the consumer). The
  channel is a minimal JSON-RPC over `MessagePort` with string-tagged frames (symbol keys are not
  carried by structured clone). Calls and results are structured-cloneable values; a cross-plugin call
  cannot pass or return object references.
- A value export is read as a zero-argument call; every stub binding returns a promise, so plugin code
  uniformly `await`s dependency calls.

### 3. Lifecycle state machine and cascades (host is the execution authority)

- Per-plugin states: `stopped → starting → running → stopping → stopped`, plus `failed` (with reason)
  and `disabled` (crash-restart budget exhausted).
- Topological start: dependencies first, in waves; the host fills each consumer's redirect-table
  export names from the dependency's scan before the consumer starts.
- Disable/reload/crash cascade: compute the transitive dependents, tear down in reverse-topological
  waves (wave nodes in parallel, waves serial), terminate workers with bounded grace, then restart in
  topological order.
- The host records runtime acquire edges (consumer → owner) and merges them with the declaration;
  the declaration must cover all usage.
- Every state change republishes `extension.registry` with per-plugin `states` (new optional field,
  backward compatible).

### 4. Hot reload (two tiers, bounded by the permission broker)

- **Source-only change** (a `.ts`/`.js` edit inside a plugin directory): the kernel watches the
  plugins root with a debounced `FileSystemWatcher`, maps the changed path to its owning plugin, and
  sends `plugin.reload { pluginIds }` over the control channel. The host cascades the plugin and its
  dependents down and back up in topological order. Worker permissions are unchanged, so the host
  process's broker policy remains valid (ADR 0018 invariant: the effective policy is captured once per
  owning scope).
- **Manifest/topology change** (deno.json edit, directory add/remove): the kernel re-scans, rewrites
  the config, and restarts the host process so the new permission policy is captured once.
- Freshness: a new `Worker` re-reads modified local modules (verified), so in-place worker rebuilds
  observe source edits without cache-busting.

### 5. Configuration

New section `Maieutics:Plugins`:

- `watchEnabled` (default true)
- `watchDebounce` (default 500 ms)

The plugins root stays hard-coded at `workspace/.maieutics/plugins`.

## Consequences

- Plugin authors declare dependencies once and `import` dependency modules directly; the type shape is
  identical to a local import, and undeclared imports fail fast.
- A dependency change (source edit, crash, disable, reload) deterministically cascades to its
  dependents; no half-restored graph is left running (degraded recovery: the failed plugin and its
  dependents stay stopped with reasons).
- Hot reload of source is in-process and permission-preserving; manifest changes restart the host.
  Zero-downtime parallel swap (PLUGIN-SYSTEM.md §8.2) remains out of scope; the call path is already
  name-routed, so a rebindable-reference swap can be layered on later.
- Cooperative cancellation on stop is not yet implemented: a stop terminates workers immediately and
  rejects in-flight calls (bounded, not infinite). A worker-side cancellation signal is future work.
- Cross-plugin calls are value-only; object references and callbacks across workers are out of scope
  for now.

## References

- ADR 0004 (extension protocol), ADR 0014 (control channel), ADR 0016 (script plugins), ADR 0018
  (permission store and Deno execution)
- `worker-actor/PLUGIN-SYSTEM.md` (orchestration model, §3–§8)
- Deno `node:module.registerHooks` (verified on Deno 2.9.5)
