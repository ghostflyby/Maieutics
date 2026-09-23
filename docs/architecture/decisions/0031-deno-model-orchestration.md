# ADR 0031: The Deno Model-Orchestration Surface

Status: Draft

Date: 2026-09-22

## Context

ADR 0020 fixed the call direction between the REPL and extensions: the REPL calls extension
points through actor capabilities, and extensions cannot call into the REPL. Model work —
agent runs, and since ADR 0030 subagents — could only be started by the model inside a turn
or by the user through the frontend. The Deno side observed (resource reads, presentation)
but never orchestrated.

The product direction changes here, by decision: orchestration authority is open. Every model
capability must be orchestratable from Deno — the model itself, from a REPL cell, and host
extensions, from their own code, must be able to start, await, and cancel model work. The
subagent (ADR 0030) is the primitive that makes this coherent: a subagent is a complete
provider-neutral run with its own context, tools, limits, and task-plane lifecycle, and it
already has wait/cancel semantics, a display plane, and a permission scope.

Two hard problems had to be designed rather than skipped:

1. **Ownership.** ADR 0030 deferred detached children — a child with no parent run cannot be
   joined at a turn boundary, and its lifetime, budget, and transcript ownership were named as
   the open costs. A Deno-initiated child has no parent run by definition.
2. **Direction.** Opening model orchestration to Deno is a deliberate exception to ADR 0020's
   one-way rule. The exception must be a designed surface, not a hole: peer-authenticated
   endpoints on the control channel (invariant 27), scoped to the calling Deno process's
   owning Agent session, and still forbidding extensions from reaching REPL or host internals.

## Decision

1. **The control channel gains a model-orchestration surface.** `POST /v1/model/subagents`
   spawns a child run; `GET /v1/model/subagents/{runId}?timeoutMs=` waits (bounded) and
   returns the terminal snapshot; `POST /v1/model/subagents/{runId}/cancel` cancels and waits.
   The same peer-authenticated middleware guards them as the existing tool-invoke and
   resource-read endpoints; they are ordinary invariant-27 endpoints, not an ADR 0020
   exception. v1 scope is subagent orchestration — the model-run primitive. Sessions and
   their turns stay owned by the user and the single-run gate; an orchestration spawn never
   submits a turn to an existing session.

2. **Ownership resolves to the calling context, run first.** The REPL carries an agent
   identity: the control channel resolves the calling Deno process to its owning Agent
   session, and the primary flow — the model orchestrating from a REPL cell — executes inside
   that session's live run (the eval is a tool call on the model's tool loop). A spawn made
   from that context is a **run-owned child of the live run**: join-before-complete holds
   (the cell cannot outlive the turn that hosts it), the per-turn budget applies, and the
   full ADR 0030 child semantics are inherited with no new machinery. Only when no run is in
   flight — host extension code outside any turn — does the child fall back to **detached
   (session-scoped)** ownership as designed below: own session, no join, process lifetime or
   explicit cancel, in-memory transcript, task-plane addressability, and
   `MaxDetachedChildren` as the retained-state bound (settled children stay addressable and
   count toward the cap).

3. **Permission scope inherits.** A Deno-spawned child registers under the owning Agent
   session in the permission override registry (ADR 0030 decision 3), so the acquisition
   overlay composes the owning session's override for everything the child launches. Deno
   orchestration cannot widen a session's policy.

4. **ADR 0020 stands unchanged.** Its one-way rule governs the REPL/extension actor boundary
   and never constrained the kernel's web endpoints. Extensions and REPL code may drive model
   capabilities through the orchestration endpoints exactly as they already drive tool
   invocation and resource reads; they still cannot call REPL or host internals, spawn
   processes outside the permission module, or reach any endpoint the control channel does not
   deliberately map.

5. **The Deno SDK exposes orchestration as a first-class section** of the REPL client
   (`model.spawnSubagent` / `model.waitForSubagent` / `model.cancelSubagent`), speaking the
   endpoints above over the client's existing transport and credential handling — the same
   pattern as `tools.start`/`tools.invoke`.

## Surface Extension (2026-09-22, second slice)

The surface is a common contract with additive semantics, and the client
treats every task as one object shape:

- **Kernel**: the generic endpoints `GET /v1/tasks?uri=&timeoutMs=` (bounded
  wait, terminal snapshot) and `POST /v1/tasks/cancel` (idempotent, session-
  owned) address *any* `task://` authority through the task plane's read +
  wait + cancel contract; per-kind semantics live in the authority's snapshot
  detail (additive fields; the client tolerates unknown kinds). The object
  store joins the resource plane as the read-only `objects://{sha256}`
  provider — content addresses are not live tasks, so no wait/cancel and no
  catalog entries.
- **Client**: every task is a `TaskRef` — `uri`, `kind`, `status`, an
  `AbortController` whose abort initiates cancellation, and PromiseLike
  resolution to the terminal snapshot (a bounded long-poll chain). Spawn
  sites return the reference: `model.spawnSubagent` yields a
  `SubagentTaskRef` (await = the report, abort = cancel, `uri` = the plane
  address). Kind-specific capabilities wrap a reference (parse the uri) rather
  than extending the base interface.

## Consequences

- The model, from a REPL cell, can fan out parallel tool-using model runs and orchestrate
  them — the capability the orchestration decision exists for.
- Detached children are process-lifetime state: the registry retains their records (bounded by
  the cap) so late waits succeed, and a restart loses them (the report lives with the process,
  consistent with the in-memory child-transcript policy).
- Orchestration endpoints must resolve the calling Deno process to its owning Agent session
  (control session registry → REPL registry) and refuse unresolvable callers; spawns are
  always scoped, never process-anonymous.
- A future surface for sessions and turns (driving the user's own sessions from Deno) would
  collide with the single-run gate and frontend ownership; it stays out of scope until a
  concrete requirement arrives.
