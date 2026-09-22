# ADR 0030: Tool-Scoped Subagent Runs on the Task Plane

Status: Draft

Date: 2026-09-22

## Context

The runtime has no subagent support: a turn is one model/tool loop inside one mutating run,
and the only inter-agent surface is the tool-call contract. The ecosystem offers three families
to borrow from: in-process tool-style subagents (an isolated child run that returns one report —
Claude Code's Task tool, OpenAI Agents SDK), shared-state graph orchestration (LangGraph, Agent
Framework Workflows), and externalized delegation protocols (A2A). The repository already defers
the Agent Framework family and A2A, and two invariants pick the tool-style family on their own:
canonical history commits atomically per turn, and tool results stay structured until an output
adapter renders them. A shared blackboard or a mid-turn control transfer has no committed
boundary to land on.

One gap remains once the tool-style family is chosen. The tool envelope
(`AgentToolStarted` → bounded `AgentToolProgress` → one `AgentToolFinished`) is a complete
lifecycle only for a blocking call. A subagent that outlives its spawn call — spawned early,
awaited later — needs completion, waiting, and cancellation semantics that the envelope cannot
carry, and ADR 0028 deliberately excluded agent runs from the task plane because "the model
cannot usefully poll the run it is executing inside". That argument holds for top-level runs;
it does not hold for a child run, which the model is not executing inside.

While bringing child runs onto the plane, the plane itself had a one-sided contract: ADR 0028
made `task://` a read plane and left interruption with each subsystem's typed tools. A spawned
child that the model can read but neither wait on nor cancel is half a task. Uniform control
is what makes the plane worth addressing generically.

## Decision

1. **Subagents are tool-style child runs, in process.** A subagent is a complete
   provider-neutral run — its own profile lease, bounded event sequence, limits, and tool
   registry — created and owned by exactly one tool call of the parent run. Rejected: shared-state
   graph orchestration and control-transfer handoffs (both contradict atomic per-turn commit and
   the deferred-frameworks boundary), and externalized delegation (A2A family, deferred).

2. **The inter-agent channel is the tool-call contract; there is no general agent-to-agent
   channel.** Parent→child is the composed turn input; child→parent is bounded progress plus
   exactly one structured result; cancellation is a cooperative cascade. A general channel
   (addressing, cross-agent ordering, lateral messaging) is rejected: the per-run sequence model
   deliberately refuses cross-run ordering, and lateral channels defeat deny-wins permission
   trimming — a network-denied child could exfiltrate through a sibling. A second topology, if
   ever needed, is a new ADR that solves exfiltration first.

3. **Spawn permissions are requests, not authority.** The spawn tool accepts child tool and
   permission hints as arguments; the permission layer clamps them. The child's effective policy
   is one more narrowing layer over the parent's scope under the deny-wins overlay (invariants
   19–21): spawn arguments can only narrow, never widen, and the clamp happens server-side at the
   permission layer, not by trusting model output. The child's tool registry is a validated
   subset of the parent's under the existing unique-name and object-schema registration rules.

4. **Child-run lifecycle is task semantics; this amends ADR 0028 decision 5 for child runs.**
   A running child is addressable as `task://agent/{agentSessionId}/{subagentRunId}`, served by an
   `AgentTaskResourceSource` in the executable's Execution domain, mirroring
   `TerminalTaskResourceSource`. `Maieutics.Agent` exposes a provider-neutral subagent registry
   seam (snapshot, wait, cancel) and never sees the `task://` scheme: the executable renders task
   URIs through an address renderer it injects into the host, so the Agent assembly stays
   host-neutral. Snapshots are bounded — lifecycle status in the ADR 0028 vocabulary
   (`working | complete | fail | cancel`), a digest, usage, artifact links — and never carry the
   child transcript. Top-level runs remain non-addressable for 0028's original reason.

5. **The task plane's entry contract is read, wait, and cancel; sources lacking any of the three
   may not register an authority. This amends ADR 0028 decision 4.** `ITaskResourceSource` gains
   `WaitTaskAsync` (await a terminal status — `complete | fail | cancel` — returning the terminal
   snapshot, bounded by the caller's timeout) and `CancelTaskAsync` (request cooperative
   cancellation and settle to a terminal status). There is no `task_read`: snapshots ride the
   existing resource-read tools. Uniform model tools `task_wait` and `task_cancel` route by the
   registry's claim specificity to the owning source; the owning subsystems keep their richer
   typed tools (`terminal_interrupt`, `terminal_close`) alongside. Cancel addresses only tasks of
   the calling session; wait keeps read-plane visibility. Terminal one-shots satisfy the contract
   with existing machinery: wait resolves on process exit or session close, cancel drives the
   close path.

6. **Completion, waiting, and cancellation keep two consumers with different mechanisms.** The
   model side is pull-only: there is no push of child data into a running turn, ever. Blocking
   mode waits implicitly on the child's `Completion` inside the spawn call; async mode pulls via
   `task://` snapshot reads or `task_wait`. The frontend side keeps the existing terminal frames:
   child run frames under the child runId plus the spawn tool's envelope. Cancellation is uniform
   — the run cancel endpoint accepts child runIds (child runs register under the session's run
   routing), `task_cancel` drives the same cooperative CTS, and both are one-way parent→child.
   Session `run.status` stays bound to the top-level run; children never flip idle.

7. **Two in-turn modes; no cross-turn survival.** Blocking (default): the parent's tool loop
   awaits the child. Async: spawn returns early with the task URI in its `tool.finished` result;
   the parent continues other tools and pulls or waits later. In both modes the parent run's
   `Completion` joins every spawned child — a turn cannot commit while a child is unsettled;
   pending children are cancelled or reaped by budget and deadline first. Rationale: atomic turn
   commit, honest busy/idle, single-run-gate soundness. The parent's tool calls remain serial;
   children spawned by successive spawn calls may overlap as independent runs — this decision is
   the approved child-run concurrency design: children share no mutable state (each owns its
   transcript and registry) and are bounded by the depth and budget caps in decision 10.
   Detached subagents that survive their turn are deferred, with their costs named: ownership
   transfer to session scope, busy/idle redefinition, cross-turn transcript ownership.

8. **Data plane and display plane are separate.** Child activity is visible to the frontend only:
   child events flow through the existing event pipeline under the child runId namespace
   (run-local sequence, bounded replay buffer, `sinceSequence` resume). On `run.missing` the
   frontend degrades to the spawn tool's persisted result — the child transcript is not
   refetchable. Model-visible data exists only at boundaries: the spawn/wait tool results and the
   canonical history of subsequent turns. Artifacts travel through the object store as resource
   URLs, not through event payloads.

9. **Output discipline for children.** (a) Normal output is visible as child-run frames;
   children are never black boxes. (b) Interactive output is mechanically excluded: tools that
   elicit input never enter a child registry, and MCP elicitation requests arriving during child
   runs are answered with a typed decline (a client may refuse). A child that lacks information
   fails with a typed recoverable error and the parent re-spawns; there is no agent-to-agent
   interactive channel — the interaction counterparty is the user. (c) GUI output splits in two:
   live display routing (`repl.display`, the displayId → output mapping) is denied to children,
   while rich content is data — written to the object store and returned as `workspace://` /
   `task://` URLs in the structured result, rendered at the parent's tool result. Invariants 14
   and 26 hold unchanged; no new display channel exists.

10. **Transcript, snapshots, and budgets.** The parent canonical transcript records the spawn
    tool call and its structured result; child conversation history never enters it. Child
    committed turns may persist under the child's own identity for audit and debugging, behind
    the normal transcript store and persistence-policy decision at implementation time. Notebook
    snapshots are unaffected: they carry reports, never live child state. Child runs inherit
    per-run limits and add recursion dimensions — a depth cap and a per-parent-turn total child
    budget. Budget exhaustion and `task_wait` timeouts are typed recoverable errors to the parent
    model, not turn rollback; hung children remain inspectable as cancelled/fail snapshots,
    following the timed-out terminal one-shot precedent. Child failure does not kill the parent
    turn unless the runtime itself is unusable (invariant 18); parent cancellation kills all
    children.

## Consequences

- Parallel fan-out within a turn needs no new wire channel: the display plane reuses the event
  pipeline, the data plane reuses tool-result boundaries, and the lifecycle reuses `task://`
  reads, waits, and cancels. Frame additions are additive types and fields under the
  tolerate-unknown-fields rule, documented in the frontend protocol.
- All model-facing tools are thin executable adapters over the `Maieutics.Agent` host seam
  (`agent_spawn`, `task_wait`, `task_cancel`); the Agent assembly gains the registry and
  lifecycle machinery but no model tools. `task_wait`/`task_cancel` address the whole plane, so
  they arrive with the plane contract (Phase 2), not with the Agent internals.
- The plane routing gains a caller context: wait and cancel pass the calling session identity
  from the tool context so sources can enforce same-session cancellation.
- Seam work: `Maieutics.Agent` gains a provider-neutral subagent registry (snapshot, wait,
  cancel, XML-documented); the executable gains `AgentTaskResourceSource`, the spawn/wait/cancel
  tool adapters, and the terminal source's wait/cancel implementation. No assembly is added;
  subagent orchestration stays inside `Maieutics.Agent` internal composition.
- Deliberately out of scope for v1, each requiring its own design: a general or lateral
  inter-agent channel; rich-value backflow into the parent turn (children return text and
  structured data; rich values stay in the child activity stream); aggregation of child usage
  into the parent's `run.completed` (per-run usage stays separate until the billing story is
  decided); materialization of a cancelled child's partial report; detached cross-turn children.
