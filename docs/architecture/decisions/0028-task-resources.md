# ADR 0028: Task Resources in the Virtual Resource Plane

Status: Draft

Date: 2026-09-17

## Context

The kernel has no unified addressable notion of "work the agent started and is waiting on".
Each subsystem improvises its own handle: a timed-out terminal one-shot returns its session id
as a pollable handle (`TerminalRegistry.RunOnceAsync`), REPL evals die with their tool call,
and MCP connections expose no request-level identity. The one generic identity infrastructure —
the ADR 0026 resource plane — is a read plane for bodies: `read_text` resolves any registered
scheme, `list_resources` enumerates a live catalog, and the control channel serves the same
URIs to Deno children, but nothing models a *live entity* whose state changes as work
progresses.

Meanwhile the MCP ecosystem converged on a shape for long-running work: the 2026-07-28
`io.modelcontextprotocol/tasks` extension polls a task object (`task/get`), answers its input
requests (`task/update`), and cancels it (`task/cancel`), with a lifecycle of
`working → input-required → terminal(complete | cancel | fail)` and deliberately no `list`.
Adopting that wire protocol today would bind us to a preview SDK package and near-zero client
adoption, but its *shape* is exactly what the agent needs locally: a pollable, bounded,
read-only view of spawned work.

## Decision

1. **`task://` is a reserved built-in scheme in the ADR 0026 plane.** `ResourceRegistry`
   reserves `task` for `ResourceProviderClass.BuiltIn`; custom providers and MCP servers
   cannot claim it (same protection as `workspace`). One `TaskResourceProvider` composes
   `ITaskResourceSource`s; each source owns one authority and its claims carry
   `(task, authority)`, so routing to an owner is the registry's existing claim specificity.

2. **Reading a task returns a fresh snapshot body.** `task://{authority}/...` reads through
   the same single entry (`read_text`, the control-channel `GET /v1/resource`), producing a
   bounded `application/json` snapshot: `uri`, `kind`, `status`
   (`working | complete | fail | cancel`, the MCP tasks vocabulary), and kind-specific detail.
   This is procfs semantics: every read is fresh, the plane stays stateless and read-only,
   and lifecycle progression (events, progress frames) stays on the owning subsystem's
   surfaces. Reading never mutates the task or its registration.

3. **Terminal one-shots are the first source.** A `terminal_run` one-shot that exceeds its
   deadline keeps its existing pollable-handle behavior, and its result additionally carries
   `taskUri` — `task://terminal/{agentSessionId}/{terminalSessionId}` — readable until the
   session closes. Snapshots map the terminal wire states to the status vocabulary
   (`completed` with exit 0 → `complete`, nonzero or `faulted` → `fail`,
   `closing`/`closed` → `cancel`, otherwise `working`). The `terminal_session_not_found`
   lifecycle is unchanged: closing the session removes the resource and later reads fail with
   `resource_not_found`.

4. **Control does not enter the read plane.** Interrupting or closing a task stays with the
   owning subsystem's typed tools (`terminal_interrupt`, `terminal_close`); the fetch bridge
   already rejects non-GET methods on virtual schemes, and no resource write surface exists.
   This keeps the plane's one-way nature enforceable at the transport, not by convention.
   (Amended by ADR 0030: the plane carries a uniform control contract — sources implement wait
   and cancel, exposed as `task_wait`/`task_cancel`, and a source lacking either may not register
   an authority. The typed subsystem tools remain alongside.)

5. **Scope and visibility.** The catalog lists the live one-shots of every Agent session —
   the plane is process-global like the workspace plane, since one user owns every session;
   URIs carry the owning Agent session id so identities stay globally unambiguous and the
   terminal tools remain usable from any snapshot. Agent runs themselves are *not* task
   resources: the model cannot usefully poll the run it is executing inside, and run identity
   stays frontend-facing. There is deliberately no `list`-beyond-the-catalog, mirroring the
   MCP tasks extension's removal of `task/list`.

## Consequences

- `read_text` and `maieutics.resources`/REPL `fetch` gain a uniform way to poll spawned
  work without new model tools; `list_resources` shows running one-shots with kind `task`.
- A timed-out one-shot is now addressable three ways — terminal session id, `taskUri`, and
  the catalog — all backed by one registration and one lifecycle.
- The snapshot schema is versioned implicitly by the protocol's tolerate-unknown-fields
  rule; sources that need richer detail extend their kind-specific record additively.
- Future sources (projected remote MCP tasks, background tools) implement
  `ITaskResourceSource` and register in the composition root; no plane change is required.
- Cross-session task reads are visible to any model in the process, same as workspace files;
  narrowing reads to the requesting Agent session is deferred until the plane carries a
  reading context.
