# ADR 0033: Plugin-Scoped MCP Data File

Status: Accepted

Date: 2026-09-26

Supersedes: [ADR 0013](0013-mcp-configuration-file.md)

Related: [ADR 0016](0016-script-plugins-and-extension-points.md) (`mcp.discover`),
[ADR 0029](0029-mcp-client-reverse-requests.md), `docs/declarative-extensions-design.md`

## Context

MCP servers were configured in a kernel-level `mcp.json` beside the active `maieutics.json`
(ADR 0013). The plugin system already had two MCP contribution paths — the manifest's
`extensions.McpDiscover` section and the worker `mcp.discover` extension point — but the
declarative section carried no per-server options (fixed timeouts, no `enabled`, no
`roots`/`elicitation` overrides), so migrating kernel-level configuration to a plugin was a
regression. Keeping the kernel-level file meant two mechanisms for one concern: two id
schemes, two reload channels, two failure models, and validation parity gaps (the plugin
path lacked the HTTPS-unless-loopback rule).

## Decision

`entrypoints` gains one level of hierarchy; the top-level key is the entry KIND, never
a name. The `worker` key holds the worker map exactly as the flat form did (worker name
→ script array, first script starts the worker, the rest are same-worker helpers).
Sibling keys are **data entry points**: `mcp` is the catalogued data name for MCP
servers, its string value is a path (name and location are the plugin's choice) to a
file parsed with the same schema ADR 0013 defined (`mcpServers`/`servers`, per-server
`command`/`args`/`env`/`workingDirectory`/`url`/`headers`/`enabled`, the Maieutics
extension keys, and all four timeouts).

```json
"entrypoints": {
  "worker": {
    "main": ["./mod.ts", "./helper.ts"],
    "echo":  ["./src/echo.ts"]
  },
  "mcp": "config/servers.json"
}
```

The kernel-level `mcp.json` is removed: the plugin data entry is the only MCP
configuration form; a plugin without a declared `mcp` entrypoint simply
contributes no MCP servers, and nothing is hardcoded to a file named `mcp.json`.
An array under an unknown entrypoints key (a pre-hierarchy worker declaration) fails
the manifest with a message pointing at the `worker` wrapper.

- The schema owner moves to `Maieutics.Mcp` (`McpServerFile`); `PluginManifest.TryLoad`
  folds the file into the plugin descriptor. Server ids are `plugin:<pluginId>::<serverKey>`,
  the same scheme the manifest and handler paths use.
- Validation is identical to ADR 0013, closing the plugin path's gaps: HTTPS unless
  loopback, header checks, per-transport allowed keys, positive timeouts. One deliberate
  difference: a relative `workingDirectory` resolves against the **plugin root directory**
  (the kernel-level file resolved against the process start directory).
- A workerless plugin contributes with zero code; the plugins root is itself a plugin, so
  one declaration in its manifest covers the whole workspace without an `imports` entry.
  Unknown data names are
  collected but inert, with a visible diagnostic (same forward-compatibility rule as
  unknown extension kinds). Registry-discovered (jsr/npm) plugins declare no data
  entrypoints, as with extensions (deny-by-default).
- Edit-time feedback: the SDK's deno lint plugin (`@maieutics/plugin-sdk/lint`,
  rule `maieutics/data-entrypoint`) validates every declared data entrypoint —
  existence, JSON, and the `mcp` format shape — while the kernel load-time check
  remains the stricter authority.
- Failure semantics: a data file that cannot be collected (missing, invalid JSON, path
  escaping the plugin root) does **not** fail the plugin load — only the `entrypoints`
  section's own shape is strict (an array value under a catalogued data name is a load
  failure). The plugin stays registered with a typed error marker; discovery for it
  fails, so the coordinator retains the previous contribution (sticky last-good) until
  the file is repaired. `enabled: false` removes a server from the contribution while
  the file stays valid.
- Reload rides the existing plugin watcher: an edit is a declarative-only change and
  replaces contributions whose generation key changed. `MAIEUTICS_PLUGINS_ROOT` relocates
  the plugin workspace for portable setups and test isolation.
- Plugin stdio servers now receive the same capability wiring the kernel-level servers had
  (workspace roots, elicitation presenter); capability still follows handler presence
  (ADR 0029 decision 1).
- Latent binder gap fixed while migrating: the lowercase keys `args` and `env` never bound
  to `Arguments`/`EnvironmentVariables` (whole-word case-insensitive matching); explicit
  alias properties now carry them.

## Migration

Move the file into the plugins root and declare it in the root manifest (the plugins
root is itself a plugin):

```sh
root="$(dirname "$MAIEUTICS_CONFIG")/../Maieutics/plugins"   # default root;
                                                             # MAIEUTICS_PLUGINS_ROOT wins
mv "$(dirname "$MAIEUTICS_CONFIG")/mcp.json" "$root/mcp.json"
printf '{ "entrypoints": { "mcp": "mcp.json" } }\n' > "$root/maieutics.json"
```

Server ids change from the bare key to `plugin:<root-plugin>::<key>`, visible in
`%mcp list` and the status snapshot. A relative `workingDirectory` now resolves against
the plugins root. To split or rename the file later, change the declared path:
`{ "entrypoints": { "mcp": "config/servers.json" } }`.

## Consequences

- One mechanism, one id scheme, one reload channel; per-plugin failure isolation follows
  the existing coordinator semantics.
- MCP configuration no longer participates in the `maieutics.json` configuration reload —
  plugin watcher rules apply (declarative-only edits apply automatically).
- The `RuntimeSnapshot.McpServers` static assembly, its lease acquisition, and
  `McpStartupDirectory` are removed; every run acquires MCP tools through the plugin
  coordinator's leases.
