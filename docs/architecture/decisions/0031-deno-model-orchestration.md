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
   resource-read endpoints. v1 scope is subagent orchestration — the model-run primitive.
   Sessions and their turns stay owned by the user and the single-run gate; an orchestration
   spawn never submits a turn to an existing session.

2. **Deno-spawned children are detached (session-scoped); this designs the deferred detach.**
   The child runs on its own session — the single-run gate is per session, so nothing
   collides — owned by the calling Deno process's owning Agent session, not by any run. There
   is no join: the child's lifetime is the process or an explicit cancel. Its transcript stays
   in memory only (the phase 3 policy), its report is the waiter's product, its events flow to
   the display-plane sink, and it is addressable on the task plane like every other child.
   `MaxDetachedChildren` bounds the session's retained state; settled children stay
   addressable and count toward the cap.

3. **Permission scope inherits.** A Deno-spawned child registers under the owning Agent
   session in the permission override registry (ADR 0030 decision 3), so the acquisition
   overlay composes the owning session's override for everything the child launches. Deno
   orchestration cannot widen a session's policy.

4. **ADR 0020 is amended for this surface and only this surface.** Extensions and REPL code
   may drive model capabilities through the control channel's orchestration endpoints; they
   still cannot call REPL or host internals, spawn processes outside the permission module, or
   reach any endpoint the control channel does not deliberately map. The one-way rule keeps its
   force everywhere else.

5. **The Deno SDK exposes orchestration as a first-class section** of the REPL client
   (`model.spawnSubagent` / `model.waitForSubagent` / `model.cancelSubagent`), speaking the
   endpoints above over the client's existing transport and credential handling — the same
   pattern as `tools.start`/`tools.invoke`.

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
