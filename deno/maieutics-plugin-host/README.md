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

Permission, manifest, or source changes under the plugins root are detected by a kernel-side
`FileSystemWatcher`; the kernel ships the owning plugin's full replacement config over the
`plugin.reload` control message and the host rebuilds that worker (and its transitive dependents)
with the new grants. No host-process restart is needed for a permission change.

Validation: `deno task check` and `deno task test`.
