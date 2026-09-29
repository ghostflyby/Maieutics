# ADR 0034: The MCP Adjustment Chain

Status: Accepted

Date: 2026-09-26

Related: [ADR 0016](0016-script-plugins-and-extension-points.md) (`mcp.discover`),
[ADR 0029](0029-mcp-client-reverse-requests.md), [ADR 0033](0033-plugin-scoped-mcp-data-file.md),
`docs/declarative-extensions-design.md`, `docs/plugin-import-resolution.md`

## Context

Plugins can now contribute MCP servers (ADR 0033), but a plugin that composes on top of
another plugin's servers — hiding an internal tool, renaming one for its own consumers,
rewriting a description — has no mechanism. Tool surfaces also only exist per connection
(`ListToolsAsync` materializes them after the server connects), so any composition-time
adjustment must be expressed against declarations and applied when listings materialize.

Hard constraint: the adjustment channel carries **declarations only** — names,
descriptions, and input schemas. It is never on the tool-call path; every call routes to
the owning server. An adjuster structurally cannot observe call-time input or output
(the `ToolPreInvoke`/`ToolPostInvoke` hooks are a separate, already-authorized surface).

## Decision

A new worker extension point, `McpAdjust`, invoked twice in the lifecycle of the
composed view:

1. **Composition** (per registry revision, in dependency-topological order — the
   responsibility chain): the handler receives `{reason: "composition", servers}` — the
   in-scope server definitions, filtered to the owners of the adjuster's declared
   `dependencies` — and may drop servers (`{id, drop: true}`). Upstream output is
   downstream input; the folded view is global (every session sees the composed
   surface).
2. **Tool listing** (when a server's listing materializes or changes):
   `{reason: "tools", server, tools}` — names, descriptions, input schemas. The handler
   returns the adjusted listing; entries map an input identity via `aliasOf` (rename,
   alias — both names may be exposed — or re-describe; `inputSchema` may be
   overridden). Omission removes the tool. Entries referencing unknown identities are
   rejected: there is no way to route a tool no server declared, so **add** means
   re-expose or alias, never a fabricated implementation.

Authorization rides the existing `dependencies` access-grant precedent: the kernel
filters the payload before the handler runs; out-of-scope servers are invisible.

Semantics:

- **Sticky last-good everywhere.** A failed composition invocation keeps the adjuster's
  previous drops; a failed tools invocation keeps the server's last adjusted listing (a
  server never successfully adjusted exposes nothing — fail-closed). Removed tools never
  resurrect because an adjuster hiccups.
- **Projection, not connection state.** Adjustment results never enter the generation
  key. A chain change re-projects the tool surface over the live connection (refresh
  signal) without reconnecting. Exposed-name duplicates across the whole MCP union are
  resolved at acquisition (later server id loses, warning logged) — the one point where
  the full union is visible.
- **No new entry model.** The adjuster is a plain worker exporting the extension point
  (`defineExtensionPoint("McpAdjust", …)`); the long-lived actor assumption is
  untouched — extension-point calls were already one-shot request/response. A separate
  "composition function" entry kind would add spawn/exit machinery to save one idle
  worker.

## Consequences

- Plugin composition can now curate the MCP surface without touching the provider
  plugin; the provider keeps its full surface, and each consumer chain shapes its view.
- The kernel-side projection (`MaieuticsMcpToolInfo` remote/exposed split finally
  diverges) means `%mcp list` shows exposed names and the Agent registry keys by exposed
  names — invocations still address the remote tool.
- Extensions land in stages, each independently shippable: per-consumer views and
  transitive direct targeting extend the same chain fold (per-consumer snapshots keyed
  by consumer, and chain edges derived from indirect references respectively); tools
  with their own implementations grow the contract into a routed surface, with call
  traffic to the owning worker and the declarations-only guarantee scoped to the
  declaration plane.
