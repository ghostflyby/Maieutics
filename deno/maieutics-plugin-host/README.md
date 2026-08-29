# Maieutics plugin host

Out-of-process host for Maieutics script plugins. The kernel spawns this process with the control
channel address and a plugin configuration file; the host creates one permission-scoped worker per
plugin export subpath, scans each worker for extension point marker symbols, and routes extension
point calls over the spawned worker-actor surfaces.

The plugin SDK lives in `../maieutics-plugin-sdk`; the SDK and host are embedded in the kernel and
materialized per process, so versions are lockstep.

The host process runs with a materialized root `deno.json` whose `imports` entry maps
`@ghostflyby/worker-actor` to the pinned JSR package and whose `links` entry maps
`jsr:@maieutics/plugin-sdk` to the local materialized SDK package. Plugins therefore import the SDK
with a stable specifier (`import "@maieutics/plugin-sdk"` or
`import "jsr:@maieutics/plugin-sdk@^0.1"`) and Deno resolves it to the kernel-provided local copy;
this keeps JSR-distributed plugins working without a registry round trip.

## Deno permissions

The host process launches with **every** Deno permission kind granted (`--allow-read`,
`--allow-write`, `--allow-net`, `--allow-env`, `--allow-run`, `--allow-ffi`, `--allow-sys`,
`--allow-import`) and `--no-prompt`. It is trusted orchestration code that only spawns and
supervises workers; it never runs plugin code itself. No permission broker is attached to the host,
and no per-plugin grant union is computed — the host process is the ceiling, and each worker is the
actual isolation boundary.

Each worker's `deno.permissions` is mapped from the plugin's positive grants with the plugin
directory, SDK module, and worker entry paths injected into read access. Worker grants can never
exceed the host process grants (Deno rejects escalation at spawn). A kind the plugin does not
declare is denied inside the worker, so two plugins sharing a host cannot read each other's paths
unless each declares them.

### Plugin storage (ADR 0022)

Each plugin gets its own `localStorage` (persistent) and `sessionStorage` (per realm), available
with **zero** Deno permissions — the storage channel never touches the worker's grant surface. The
authoritative store lives in this host's main isolate, keyed by plugin identity; every worker of a
plugin (each entrypoint and every nested worker) shares its plugin's `localStorage` like a browser
origin, while `sessionStorage` stays per realm. The kernel assigns each plugin a data directory
under the platform application-data root and ships it in the plugin config; the host persists
per-plugin storage there as a versioned JSON document with debounced atomic writes (temp file +
rename) and a bounded flush on shutdown. The per-store quota is 5 MiB (UTF-16 code units) and a
single request must fit the 1 MiB mailbox payload; overflows surface as `QuotaExceededError`.

The transport follows the admission handshake pattern (request direction `postMessage`, reply
direction a per-realm SharedArrayBuffer mailbox, bounded `Atomics.wait`). Routing never trusts a
client-declared identity: frames are mapped to the sending worker's owning plugin and each mailbox
is bound to that plugin on first sight, so a mailbox handed across plugins through an actor port is
rejected. Internal storage frames (`type: "maieutics-storage"`) are visible on a nested worker's
parent-side message surface, like the admission frames; plugin code that inspects child messages
must skip them by frame type.

### Broker forwarding for derived REPL processes

When the kernel runs with a permission broker, it hands the host the broker address under the
`MAIEUTICS_PERMISSION_BROKER` environment variable. The host itself never consults the broker — it
launches with every permission kind granted and no policy is ever registered for its pid, so it must
not carry `DENO_PERMISSION_BROKER_PATH` itself. When the host derives a REPL process
(`host.repl.derive` / `ReplManager.spawnRepl`), it appends that address to the child's environment
as `DENO_PERMISSION_BROKER_PATH`; the kernel already registered the child's effective policy with
the broker for the reported pid (ADR 0020 decision 1), so the REPL's permission checks resolve
against the kernel authority rather than the host's full grants. A host launched without a broker
carries no `MAIEUTICS_PERMISSION_BROKER` and forwards nothing; the derived child then runs against
its static permission shell only.

Permission, manifest, or source changes under the plugins root are detected by a kernel-side
`FileSystemWatcher`; the kernel ships the owning plugin's full replacement config over the
`plugin.reload` control message and the host rebuilds that worker (and its transitive dependents)
with the new grants. No host-process restart is needed for a permission change.

Validation: `deno task check` and `deno task test`.
