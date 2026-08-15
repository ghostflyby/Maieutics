# ADR 0016: Out-of-Process Script Plugins and Symbol-Identified Extension Points

Status: Accepted

Date: 2026-08-05

## Context

Maieutics exposes a real `deno jupyter` REPL (ADR 0003, ADR 0011) and an out-of-process extension
model (ADR 0004) whose transport is deferred. The control channel (ADR 0014) gives the kernel a
process-bound HTTP/WS channel to each REPL child. Users now need a script plugin system: plugins
that discover MCP servers (without connecting), adjust tool invocation, and observe tool calls.
Plugins must be standard Deno packages with per-domain minimal permissions, support multiple
extension points per plugin, and avoid executing every extension in every context.

## Decision

Script plugins are standard Deno packages executed outside both the kernel and the REPL, inside a
single permission-scoped host process shared by all plugins.

### Package shape

A plugin is a directory containing a `deno.json` with:

- standard `name`, `version`, and `exports` fields, where every non-`.` subpath is one extension
  carrier module;
- the standard `permissions` field (Deno 2.5+ named permission sets) for positive grants;
- a `maieutics` marker field (`{ "version": 1, "isolation": "auto" }`) that identifies the
  directory as a plugin.

The kernel scans `workspace/.maieutics/plugins/*` and parses only the manifest; it never executes
plugin code to learn capabilities.

### Execution model

- The kernel spawns one `deno run --unstable-worker-options` host process per application, with
  permission flags equal to the union of enabled plugin grants plus the control channel
  infrastructure (unix socket read/write/net, the `MAIEUTICS_*` environment allowlist, materialized
  module directory read).
- The host creates one worker per plugin export subpath, passing `deno.permissions` mapped from the
  plugin's positive grants; the plugin directory, SDK module, and worker entry paths are injected
  into read access automatically.
- Workers are long-lived and restarted on crash with a bounded retry count. The host bridges
  `extension.invoke` bus messages to workers over postMessage frames and multiplexes responses by
  correlation id.
- `isolation: process` (or declared `run`/`ffi` permissions) is not implemented yet; such plugins
  are disabled with an explicit log, never silently degraded.

### Extension point identification

Extension points are identified by versioned global symbols under
`maieutics/extensionPoint/v1/<name>`, resolved through `Symbol.for` so every isolate shares the
same identity without module singletons. A carrier module belongs to an extension point when its
top-level export (named or default) carries the marker symbol:

- object form: `{ [symbol]: true, handler(context) }`;
- function form: `fn[symbol] = true`, called directly.

The host scans one level of module exports with `value[symbol] === true`; nested objects are not
scanned. The SDK provides `defineExtensionPoint(name, impl)` that attaches the marker, validates
the shape, and records diagnostics, so forgetting a marker becomes an error instead of silent
absence.

### Channel protocol

Reuses the control channel: the host connects the unix socket WebSocket with a `control.hello`
payload carrying its host id (registered pid to host id in `ReplControlSessionRegistry`). New bus
message types:

- `extension.invoke` (kernel to host): `{ pluginId, exportName, extensionPoint, request }`;
- `extension.result` / `extension.error` (host to kernel), correlated by id;
- `extension.registry` (host to kernel): the scanned extension point snapshot.

### Capabilities in this release

- `mcp.discover` (contribution): returns connection descriptors; the kernel assigns the unique
  server id `plugin:<pluginId>::<module|url>`, de-duplicates, and manages lifecycle through
  `McpServerGeneration`.
- `tools.preInvoke` (hook): responsibility-chain semantics; `continue`, `replace` (signature
  adjustment), or `reject`. Hooks fail the call on error.
- `tools.postInvoke` (notification): observation only; failures are logged, never fatal.

Tool hooks run for script-side invocation through `POST /v1/tool.invoke`. Model-side tool calls
inside the kernel are not hooked in this release.

## Consequences

- Plugins are real Deno packages; `deno check/test/publish/pack` all apply.
- Minimal permissions are enforced per worker through `deno.permissions` positive grants;
  `deny`/`ignore` object forms are only honored under the future process-isolation path.
- The host process carries the union of plugin permissions; a V8 isolate escape could reach that
  union. `isolation: process` is the escape hatch and is disabled until implemented.
- The extension point set is only known at runtime after workers scan their modules; audit and
  diagnostics must read code or the `extension.registry` snapshot.
- Deno worker permission options and config-file permission sets are experimental; the pinned Deno
  version is part of the contract and permission behavior is covered by regression tests.

## References

- ADR 0004 (extension protocol semantics), ADR 0014 (control channel)
- Deno worker permissions and `deno.json` named permission sets
