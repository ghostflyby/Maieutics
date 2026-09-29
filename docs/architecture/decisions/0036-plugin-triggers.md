# ADR 0036: Plugin Triggers — Runtime-Held Event Sources

Status: Accepted

Date: 2026-09-27

Related: [ADR 0018](0018-declarative-permission-store-and-deno-execution-module.md) (variable
table), [ADR 0021](0021-plugin-http-ui-host-mounted-zero-permission-handlers.md),
[ADR 0033](0033-plugin-scoped-mcp-data-file.md), [ADR 0035](0035-service-worker-lifecycle.md)

## Context

Plugins need to react to the world outside their own files: a local IDE rewriting its
MCP port file, a config file of another tool changing shape, a nightly sync window,
an interval poll. ADR 0035's reference-counted lifecycle deliberately keeps workers
reclaimable — a worker holding its own `watchFs` handle or timer would either pin
itself forever (defeating reclamation) or die with its listener mid-subscription
(losing events). Event sources therefore belong to the runtime, not to workers —
the Service-Worker split: the runtime holds registrations and wakes workers to
deliver events.

## Decision

Plugins declare **triggers** in the manifest. The kernel owns the declaration plane
(validation, variable expansion); the Deno host holds the listeners; delivery rides
ADR 0035's wake machinery.

```json
{
  "triggers": [
    { "name": "idea-port", "kind": "watch",
      "paths": ["${env.HOME}/Library/Caches/JetBrains/**"], "depth": 2,
      "action": { "type": "rediscover" } },
    { "name": "nightly-sync", "kind": "cron", "expression": "0 3 * * *",
      "action": { "type": "event" } },
    { "name": "poll-5m", "kind": "interval", "seconds": 300,
      "action": { "type": "event" } }
  ]
}
```

- **Kinds (first stage)**: `watch` (path set; glob suffix `**`; depth caps
  recursion, default 3; missing paths are tolerated silently so a trigger can
  predate the software it watches), `cron` (5-field local-time expression), and
  `interval` (seconds). The kind catalog is a kernel release surface; later stages
  add `probe` (declarative local queries for state without file traces) and
  `webhook` (inbound, on the plugin HTTP gateway from ADR 0021).
- **Actions**: `event` wakes the owning worker (ADR 0035 `#ensureStarted`) and
  dispatches `{trigger, firedAt, detail}` to its `PluginEvent` extension point;
  `rediscover` sends a `plugin.trigger` control frame to the kernel, which
  republishes that plugin's MCP registration subset — discovery re-runs and the
  coordinator recomposes. `rediscover` needs no worker and no wake.
- **Debounce**: watch events coalesce per trigger (500ms, matching the kernel
  watcher); interval and cron fire at most once per tick.
- **Authority split**: the kernel expands `${env.*}` through the SAME variable
  table as the permission store (single source, no literal path duplication) and
  ships the concrete path set to the host with the plugin config; the host only
  listens on what it was given. A trigger grants no read permission — watching a
  path informs the plugin that it changed, nothing more; the `PluginEvent`
  handler still runs under the plugin's own grants.
- **Re-arming**: triggers are manifest declarations, so a host restart rebuilds
  every listener from the config; handlers treat delivery as at-least-once and
  reconcile state on wake (the ADR 0035 contract).

## Later stages (same delivery pipeline)

- `probe` kind: declarative command/http queries with change detection — covers
  state with no file trace (process tables, service lists).
- `webhook` kind: inbound endpoints on the plugin HTTP gateway with auth.
- Persistence of next-fire times across process restarts, and OS-scheduler
  externalization (launchd/systemd timers) for across-shutdown schedules.
- Broadcast delivery through reactive collections (multiple consumers of one
  stream) and task-plane ingestion (trigger = enqueue work).

## Consequences

- Plugins react to files, schedules, and intervals without holding any runtime
  resource; reclamation stays uniform.
- The host gains one bounded resident resource per declared trigger (a watcher
  or timer), owned by the host process and replaced on config reload.
- `%status` can surface trigger state; the kernel keeps the declaration snapshot
  alongside the other descriptor-held declarations.
