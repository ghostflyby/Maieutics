# ADR 0022: Plugin Web Storage and Platform Application Directories

Status: Draft

Date: 2026-08-29 (revised 2026-08-29: persistence switched from per-plugin JSON
to per-plugin SQLite via `node:sqlite` before any release; no migration path
kept)

## Context

Plugin code expects the Web Storage surface — `localStorage` and
`sessionStorage` — but Deno does not provide it usefully in our topology:
main-thread `localStorage` needs `--location`, and inside a Worker it fails.
Beyond storage, plugins run as permission-scoped Workers inside one shared
host process, so "each plugin has its own storage space" has no API-level
meaning today: nothing keys any state by plugin, and every realm of a plugin
dies with its worker.

Two decisions were needed:

1. How to give every plugin a private, synchronous, browser-shaped storage
   API without granting filesystem permissions.
2. Where per-plugin persistent data lives on disk, and who derives that path.

The synchronous requirement is the crux: `localStorage.getItem` must return
inline, but the authoritative store must live in one place — the plugin
host's main isolate — so that every worker of a plugin (each entrypoint and
every nested worker) shares it like a browser origin, and so exactly one
writer persists. The codebase already had a proven pattern for exactly this:
the REPL's blocking input mailbox and the plugin HTTP admission handshake
(ADR 0021 decision 9) both park a worker thread on a SharedArrayBuffer while
a free event loop answers.

## Decision

### 1. Authoritative per-plugin store in the host main isolate

Each plugin owns one store, keyed by plugin identity inside the host process.
`localStorage` is synchronous for the plugin and consistent across all of its
realms; `sessionStorage` is a plain per-realm in-memory map (browser-tab
semantics, zero IPC).

The transport (`deno/maieutics-runtime/storage_channel.ts`) mirrors the
admission handshake:

- request direction: a bare `postMessage` frame
  (`{ type: "maieutics-storage", sab }`) — the receiving side's event loop is
  free;
- reply direction: a per-realm SharedArrayBuffer mailbox
  (state + request/response lengths + 1 MiB payload region) written by the
  host, which then `Atomics.notify`s;
- the requesting realm parks in a bounded `Atomics.wait` (10 s). A timed-out
  op poisons the mailbox (a late reply could corrupt a reused payload
  region), so later ops fail loudly instead of wedging module evaluation.

Routing never trusts a client-declared identity. The host maps the sending
worker to its owning plugin (the same worker→plugin resolution the acquire
router uses) and binds each mailbox to that plugin on first sight; a mailbox
that reappears under a different plugin is rejected with a typed error, so a
mailbox handed across plugins through an actor port cannot borrow another
plugin's store.

Relay topology: a realm's own ops post to its parent (the host for a root
plugin worker, the creating plugin worker for a nested realm). A child's
frames land on the child Worker object in the parent realm — never on the
parent's global scope — so `worker_patch.ts` gained a creation hook
(`onControlledWorkerCreated`) and the storage client attaches its relay to
each created worker. Known, accepted consequence: internal storage frames are
visible on the parent-side message surface of nested workers, exactly like
the admission frames; plugin code that inspects child messages must skip
frames by `type`.

Plugin workers need **zero** Deno permissions for storage: the mailbox is
allocated inside the requesting realm, and only the trusted host touches the
filesystem. The host persists each plugin to one SQLite database
(`local-storage.db`, `node:sqlite` `DatabaseSync`) in the kernel-assigned data
directory, and the database IS the store: no in-memory shadow copy, no flush
step. Ops are synchronous indexed point queries and WAL appends
(`synchronous=NORMAL`, no per-commit fsync), so a parked plugin's op completes
in microseconds-to-sub-millisecond on the main isolate and every committed
write survives host crashes. The schema is
`kv(key TEXT PRIMARY KEY, value TEXT NOT NULL, ordinal INTEGER NOT NULL)
WITHOUT ROWID` plus an index on `ordinal`; the ordinal is a monotonic counter
assigned only on INSERT and never reused, because `ORDER BY rowid` breaks
after deletions (SQLite reuses freed rowids) and `keyAt(i)` must keep browser
insertion-order semantics. Schema changes are versioned through
`PRAGMA user_version`; a store written by a newer host fails with a typed
error. Quota is 5 MiB (UTF-16 code units, keys + values) per store, hydrated
from the rows at open, and a single request must fit the 1 MiB mailbox
payload; overflows surface as `QuotaExceededError`. Hot reload replaces the
worker, not the database. Deleting a plugin keeps its data directory on disk,
like an uninstaller that preserves user data.

### 2. The kernel derives storage paths; the Deno side never does

`Maieutics/ApplicationPaths.cs` (composition root) resolves the persistent
plugin-data root on the platform application-data location:

- Windows: `%LOCALAPPDATA%\Maieutics\plugin-data` (local, not roaming)
- macOS: `~/Library/Application Support/Maieutics/plugin-data`
- Linux: `$XDG_DATA_HOME/Maieutics/plugin-data` (absolute paths only),
  defaulting to `~/.local/share/Maieutics/plugin-data`

`PluginHostManager` assigns each plugin
`<pluginDataRoot>/<identity>` via `PluginStoragePaths`: the manifest package
name (the specifier identity, stable across directory renames) falls back to
the scanned directory id; names that needed sanitizing always carry a short
identity hash so the result does not depend on scan order. Two plugins whose
identities resolve to one directory would silently share a store, so the
whole colliding group starts WITHOUT storage (typed runtime errors) and an
error is logged — the resident host must not fail startup over a manifest
name. The resolved `dataDir` ships to the host in the plugin config
(`storage.dataDir`) — including on `plugin.reload` — so the Deno side
receives paths and never derives them.

### 3. Other Deno APIs reviewed for per-plugin semantics

The same review pass covered the rest of the plugin-visible surface:

| API | Observed status on the supported Deno | Decision |
|---|---|---|
| `localStorage` | Unusable in Workers without `--location` | Patched (this ADR) |
| `sessionStorage` | Native, per-isolate | Replaced by a uniform in-memory implementation so behavior does not depend on native availability |
| Nested `Worker` | Wrapper runs before the target module | Wrapper composes plugin storage for nested realms (there is no profile entry module there) |
| `BroadcastChannel` | **Process-scoped and unreliable**: worker→main delivers; main→worker and worker→worker delivery was observed only inconsistently under repeat-traffic probes | Documented gap. Plugins of different plugins must be assumed able to hear same-named channels in some Deno builds. Follow-up: wrap the constructor in plugin workers to namespace channel names per plugin (cheap, test-first). No pin test until Deno's delivery semantics are deterministic. |
| `caches` / CacheStorage, IndexedDB | Not provided by Deno | Not provided; plugins should use the SDK surface instead |
| storage events | Not implemented by Deno | Not implemented; documented divergence from browsers |
| `Deno.makeTempDir` / `makeTempFile` | Writes the shared OS temp directory | Documented; a later change may redirect defaults into the plugin's data directory |
| `Deno.cwd` / `chdir` | Process-wide; cannot be per-worker | Documented as shared |
| `prompt` / `confirm` / `alert`, `Deno.jupyter` | Absent in plugin workers | Kept absent; negative tests exist for the REPL surface |
| `Deno.env` / `net` / `read` / `write` grants | Already manifest-scoped | Unchanged — storage working at zero permissions is the point |

## Consequences

- Plugin authors get standard Web Storage semantics with origin-style sharing
  across their plugin's workers, no permissions, and persistence across
  reloads and host restarts.
- The kernel owns one more path decision (`ApplicationPaths`), consistent
  with "no launch path builds its own grant list": no host-side path
  derivation exists to drift.
- The host process gains a `node:sqlite` (DatabaseSync) dependency — stable
  on the supported Deno, builtin, no package download. DatabaseSync is
  synchronous by design, which is what allows the database itself to be the
  authoritative store without a worker bridging async I/O back to a
  synchronous read path; WAL with `synchronous=NORMAL` keeps per-op cost at
  an unsynchronized append, at the documented cost that a power loss (not an
  app crash) may drop the most recent commits.
- The per-plugin physical boundary is retained (one database file per
  plugin): corruption and quota are per-plugin concerns, and
  `PRAGMA user_version` carries schema evolution.
- Internal `maieutics-storage` frames join `__admit` frames as
  parent-side-visible protocol frames; the README documents skipping them.
- The single outstanding op per realm is a structural property (a parked
  isolate cannot issue a second op), which keeps the mailbox single-slot.
- Embedded-resource lists gained two modules (`storage_channel.ts`,
  `storage_host.ts`); `DenoReplModule` materializes the channel module too
  because the shared wrapper now imports it.

## Verification evidence

- `deno test` (workspace): mailbox protocol round-trips, oversized-rejection
  before posting, timeout + mailbox poisoning, quota (client-side memory and
  host-authoritative, including hydration across a reopen), cross-plugin
  mailbox rejection, unconfigured-storage and newer-schema typed errors,
  durable persistence across close/reopen, insertion order surviving
  removals and restarts, dispose semantics.
- `host_test.ts` (real Deno workers, real host): two-plugin isolation,
  zero-write-grant storage use, reload continuity, nested-worker
  localStorage sharing with per-realm sessionStorage, plain-SQLite readback
  of a plugin write from the kernel-assigned directory, quota as a typed
  plugin-visible error.
- `dotnet test`: platform path resolution (Windows / macOS / XDG with
  relative-`XDG_DATA_HOME` rejection), identity→directory naming
  (verbatim safe names, hashed sanitized names, determinism), wire-format
  camelCase serialization, and tolerance of the pre-storage wire shape.
- BroadcastChannel probes (kept out of the suite because delivery is not
  deterministic): main↔main delivers; worker→main delivers; main→worker and
  worker→worker were observed delivering only inconsistently.
