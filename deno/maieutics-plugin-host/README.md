# Maieutics plugin host

Out-of-process host for Maieutics script plugins. The kernel spawns this process with the control channel address and a
plugin configuration file; the host creates one permission-scoped worker per plugin export subpath, scans each worker
for extension point marker symbols, and bridges `extension.*` bus messages between the kernel and the workers.

The plugin SDK lives in `../maieutics-plugin-sdk`; the SDK and host are embedded in the kernel and materialized per
process, so versions are lockstep.

The host process runs with a materialized root `deno.json` whose `links` entry maps `jsr:@maieutics/plugin-sdk` to the
local materialized SDK package. Plugins therefore import the SDK with a stable specifier
(`import "@maieutics/plugin-sdk"` or `import "jsr:@maieutics/plugin-sdk@^0.1"`)
and Deno resolves it to the kernel-provided local copy; this keeps JSR-distributed plugins working without a registry
round trip.

## Deno permissions

The kernel derives the host process grants from the union of enabled plugin grants:

- `--allow-env` — the five `MAIEUTICS_*` host variables plus plugin-declared env names.
- `--allow-read` — the plugin configuration file, the materialized module directory, every plugin directory, the control
  channel socket path, and plugin-declared read paths.
- `--allow-write` — the control channel socket path plus plugin-declared write paths (`Deno.createHttpClient` probes
  socket write access).
- `--allow-net` — `localhost` plus `unix:<socket>` for the control channel proxy, plus plugin-declared net domains.
- `--allow-import` — plugin-declared import domains only.

Each worker's `deno.permissions` is mapped from the plugin's positive grants with the plugin directory, SDK module, and
worker entry paths injected into read access. Worker grants can never exceed the host process grants (Deno enforces
this). Any change to environment, network, or filesystem behavior must update this list in the same change.

Validation: `deno task check` and `deno task test`.
