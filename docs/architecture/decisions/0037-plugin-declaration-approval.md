# ADR 0037: Plugin Declaration Approval

Status: Accepted

Date: 2026-10-03

Related: [ADR 0016](0016-script-plugins-and-extension-points.md),
[ADR 0018](0018-declarative-permission-store-and-deno-execution-module.md) (decisions 8–9),
[ADR 0033](0033-plugin-scoped-mcp-data-file.md), [ADR 0035](0035-service-worker-lifecycle.md),
[ADR 0036](0036-plugin-triggers.md)

## Context

A plugin's manifest declarations take effect the moment they parse: workers spawn with
their declared grants, the `mcp` data entry spawns child processes, capabilities join the
kernel surface, triggers arm listeners, and `inspections.contentReadAll` routes tool-result
payloads into the plugin. Nothing anywhere asks the user. A plugin installed from jsr/npm
— or copied into the plugins root by any process that can write there — is immediately
trusted with exactly what it declares, and a plugin update that widens its declarations
widens them silently.

The permission store (ADR 0018) governs kernel-side layers (REPL, terminal, MCP children)
but plugin workers deliberately never touch it: their grants come from the manifest alone
(ADR 0018 decision 8 — the host process is the permission ceiling, worker options are the
isolation boundary). That exception stays. What is missing is a consent gate in front of
the declarations themselves.

## Decision

Plugin declarations take effect only under a **persisted user approval** whose
**declaration fingerprint** matches the descriptor exactly. Any change to the fingerprint
revokes the effect until the user re-approves.

1. **Fingerprint scope — the whole security-relevant declaration surface**: all eight
   permission kinds, worker entrypoints, dependencies, isolation, capabilities, extension
   entries (with canonicalized data), data entries (name + canonicalized JSON or error),
   MCP server definitions (id + generation key), triggers (with watch paths in their
   kernel-expanded form — the expanded set is the privileged surface), and
   `inspections.contentReadAll`. Canonicalization follows the `McpServerGeneration` key
   style: fixed field order, Ordinal-sorted lists, length-prefixed UTF-8 into SHA-256,
   and a recursive canonical JSON form for embedded `JsonElement` data so formatting-only
   manifest edits (for example `deno fmt`) do not revoke approval. **Module code is not
   fingerprinted**: an approved plugin's source edits keep their approval — approval
   covers the grant set, not the code identity, exactly like kernel permission profiles.
2. **Exemption**: a descriptor with no workers, no data entries, no capabilities, no
   extensions, no MCP servers, no triggers, no permission grants, and no content
   observation declares nothing privileged and needs no approval (the first-boot plugins
   root skeleton never blocks).
3. **Unapproved state — fail-closed, completely inert**: no worker config ships to the
   host (so no worker spawns and no trigger arms), no MCP registration, no capability
   grants, no extension participation, no content delivery, and extension-point invokes
   are refused with the typed error `plugin_pending_approval`. The plugin stays
   discovered and visible as `pending approval`. **Pending is cascading**: a plugin whose
   transitive dependencies include a pending plugin is itself blocked
   (`blocked-by-dependency`), because its own code cannot honor its declarations without
   the dependency.
4. **Persistence**: `<DataRoot>/plugin-approvals.json`, one record per plugin id —
   fingerprint, name, approved-at, and the approved grant summary for display and diffing.
   The file is versioned (`version: 1`), tolerates unknown fields, rejects newer versions,
   and loads fail-closed: a missing file is an empty store; an invalid file leaves nothing
   approved and surfaces a typed error (the last-known-good rule of ADR 0007 applies to
   reloads of a previously good file). Writes are atomic (temp file + move) and a failed
   persist fails the approval action itself. A revoked record is deleted, not marked:
   absence is the single pending signal.
5. **Change revokes**: on hot reload the re-read descriptor is fingerprinted first. A
   mismatch (or a first sighting) sends the host a **stop** payload per worker — the
   cascade closes the worker and its transitive dependents with no restart — clears the
   plugin's kernel-side declarations, and marks it pending. Re-approval is a fresh
   decision over the new declarations; reverting the manifest to the previously approved
   shape re-matches the still-stored record and reactivates without prompting.
6. **Approval is a command, not a prompt (first stage)**: `%plugin list` renders state,
   requested grants, and the approved-vs-requested diff; `%plugin approve <id>` persists
   the record and activates; `%plugin revoke <id>` removes it and stops the workers.
   Activation ships the full replacement config through `plugin.reload`, which the host
   now treats as an **upsert**: a replacement for an unknown worker creates and starts it
   (and restarts dependents that a stop cascade had closed). The host protocol change is
   additive — a `stop` field on `PluginReloadPayload` and upsert semantics for
   replacement-bearing reloads — under the existing versioned envelope.
7. **Trust model**: the approvals file is a consent gate at the same trust level as
   `permissions.json` — a local attacker who can rewrite it can already rewrite the
   plugin code it authorizes. It is not a boundary against the local user, and it does
   not change the plugin-host exception (ADR 0018 decision 8): approval gates *which
   declarations participate*; the host process's own ceiling and per-worker narrowing are
   unchanged.

## Later stages

- Frontend REST (`GET /v1/plugins`, `POST /v1/plugins/{id}/approve|revoke`) and a VS Code
  approval surface (palette review with a grant diff, startup notification when plugins
  are pending) — the kernel semantics above are already protocol-complete for them.
- Per-kind grant narrowing at approval time (approve a subset of the requested grants)
  would layer onto the same record without a format break.

## Consequences

- A newly installed or updated plugin does nothing until the user approves it; a
  declaration change stops the plugin and its dependents immediately.
- All existing plugin integration tests must seed approvals (the seed writes the real
  file through the real load path; there is no production bypass hook).
- The host reload path gains stop-without-restart and create-on-reload semantics; trigger
  listeners are re-armed from a live plugin registry instead of the boot-time snapshot,
  which also fixes trigger re-arming for ordinary permission reloads.
- `%plugin` joins the command families; `%status` reports pending approvals.
