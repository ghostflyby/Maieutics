# Declarative Extensions Design

Status: Accepted (Phase 1 implemented)
Related: ADR 0020 (REPL extension-host actor boundary), ADR 0021 (plugin HTTP UI),
`docs/plugin-host-redesign.md` §13 (plugin→kernel capability API)

## Problem

The plugin SDK's entry points require code: a plugin declares a worker entrypoint, the
host spawns it, and every extension-point call is a live worker-actor invocation. Plugin
authors who only need static data — an MCP server to expose, a future catalogued data
kind — must ship and run a module for what is effectively configuration. The kernel
already has two declarative precedents (`mcp.json` for kernel-level MCP servers,
`capabilities` in `maieutics.json`), but nothing plugin-scoped.

## Principles

Three orthogonal axes, each with one owner:

| Axis | Owner | Openness |
|---|---|---|
| Kind (what the extension means) | Kernel | Closed catalog — each kind needs a kernel-side interpreter, typed contract, and security review |
| Form (how it is declared) | Plugin, per kind | Code form (live worker extension point) and/or declarative form (manifest JSON) — only kinds whose semantics are data-only get a declarative form |
| Data (the content) | Plugin | Arbitrary JSON, validated by the kind's kernel interpreter |

Consequences:

- Per-call semantics (hooks) can never be declarative — they need live code.
- A plugin can never define a new *kind*: without a kernel interpreter the data is
  inert, and plugin↔plugin data distribution already exists (reactive collections,
  deliberately kernel-invisible). The kernel capability catalog follows the same shape.
- Third-party *runtimes* (registering new kind interpreters) would be a kernel-side
  module event, like the Deno host itself — never a plugin-side one. If ever needed,
  that trust-model upgrade is a separate ADR.

## Design

Declarative entries are **pre-computed extension-point results**: the manifest carries
the same data a handler would return, and the kernel feeds it to the same consumer. No
second consumption pipeline exists per kind.

### Manifest shape

```json
{
  "extensions": {
    "McpDiscover": [
      { "module": "npm:@maieutics/probe-server",
        "transport": { "type": "stdio", "command": "deno" } }
    ]
  }
}
```

- Kind keys use the extension-point dispatch names (`ReplExtensionPointName`).
- Structural failures (section not an object, entries not objects) fail the plugin
  load — maieutics.json strictness.
- Unknown kinds are dropped with a descriptor diagnostic (surfaced as warnings) — a
  newer plugin on an older kernel degrades visibly instead of failing.

### Kernel flow

`PluginManifest.TryLoad` parses the section into `PluginExtensionEntry(Kind, Data)` on
the descriptor (unknown kinds → diagnostics). `PluginHostManager` keeps a
pluginId → entries snapshot (refreshed at start and on manifest reload) and:

- seeds synthetic registrations (`pluginId, "maieutics.json", McpDiscover`) so the
  entries appear in the same registry as worker registrations;
- serves discovery for those registrations straight from the snapshot
  (`DiscoverManifestMcpAsync`), applying the exact same definition rules and
  `plugin:<id>::<module>` id scheme as the handler path — merges, conflicts, and
  generation replacement behave identically;
- refreshes the snapshot and republishes the registry revision on manifest reload —
  a declarative-only change regenerates without any worker rebuild.

A plugin with zero entrypoints and only `extensions` spawns no worker at all (existing
behavior) while still contributing — the hot path pays nothing for declarative data.

### Failure semantics

Entry-level semantic failures (invalid definition) follow the handler path's
sticky-last-good rule: the previous contribution stays active and the failure is
logged. Registry-level conflicts (two plugins contributing the same server id with
different generation keys) fail the whole revision, keeping the previous snapshot —
identical to handler-contributed conflicts.

## Kinds

| Kind | Form | Consumer |
|---|---|---|
| `McpDiscover` | code + declarative | `PluginMcpCoordinator` (dynamic MCP generation) |
| `ToolPreInvoke` / `ToolPostInvoke` | code only | per-call hook chain — no declarative form |

New kinds are kernel release events: an interpreter + a catalog entry + a contract
document. The manifest parse layer (raw kind → entries) never changes per kind.

## Known limits (Phase 1)

- `McpDiscover` is the only catalogued declarative kind.
- Registry-discovered (jsr/npm) plugins cannot declare extensions: their manifests are
  not authored as maieutics projects; they start with no extensions (deny-by-default,
  same as capabilities).
- Declarative entries are kernel-invisible to workers; exposing them read-only to
  workers is a possible Phase 3.
- Reload of declarative sections requires the plugin watcher (manifest changes only
  propagate with reload enabled or an explicit apply).
