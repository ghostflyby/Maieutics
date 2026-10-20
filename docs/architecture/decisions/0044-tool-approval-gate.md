# ADR 0044: The Tool-Approval Gate

Status: Draft

Date: 2026-10-20

Related: ADR 0043 (model-callable plugin tools — the annotations this gate consumes),
ADR 0029 (MCP elicitation — the suspend/resume channel this gate reuses),
ADR 0037 (plugin declaration approval — a different, static mechanism),
ADR 0042 decision 9 (connection-gated tool families),
`docs/web-frontend-protocol.md` ("Input requests"),
`docs/agent-ux-gap-analysis.md` (B3, the gap this closes)

## Context

A run executes every tool the model asks for. The permission module enforces
static allowlists at launch, but there is no runtime pause: nothing stands
between "the model asked" and "the tool ran" for a destructive action.

Two things changed that make a gate cheap now. ADR 0043 gave plugin tools a
declared `destructive`/`readOnly` classification, which is exactly the evidence
a gate needs to decide. And the frontend protocol already carries a complete
suspend/resume channel — `input.request` plus `POST /v1/agent/inputs/{requestId}`
— with two working consumers (REPL stdin and MCP elicitation), including
`TaskCompletionSource`-based pending registries, cancellation-to-decline mapping,
and 404 idempotence for late answers. A third consumer is a composition, not a
new protocol.

One existing mechanism is *not* usable, and the reason matters.
`Microsoft.Extensions.AI` ships `ApprovalRequiredAIFunction` and
`ToolApprovalRequestContent`/`ToolApprovalResponseContent`, and the transcript
codec already validates those content types. That SDK path is not unreachable —
its `AnyToolsRequireApproval` check runs in the client's response methods,
independent of the custom `FunctionInvoker` — but nothing here opts into it:
`AgentSession` never wraps a tool in `ApprovalRequiredAIFunction` and never
constructs approval content, and adopting it would require the approval decision
to be represented as message content routed back through the provider loop. The
gate is instead built into the Agent's tool loop, at the one point where the
model has asked and no side effect has happened.

## Decision

1. **The gate is a narrow Agent-side seam, not a content type.**
   `IAgentToolApprovalGate.RequestApprovalAsync(request, cancellationToken)`
   returns a terminal `AgentToolApprovalDecision`. `Maieutics.Agent` gains no
   frontend, transport, or MCP type. `AgentSessionOptions.ToolApproval` carries
   the gate, policy, and memory with the run profile, so a spawned child inherits
   its parent's gate the same way it inherits limits. A null value is the default
   and preserves the pre-gate behavior exactly.

2. **One suspension point, after `AgentToolStarted` and before invocation.** The
   model has committed to the call, the frontend can render the pending tool, and
   nothing has happened yet. The gate runs outside every lock and owns its own
   bounded wait.

3. **A denial is a recoverable tool failure.** The model receives
   `{"status":"error","code":"tool_denied","message":…}`, one `AgentToolFinished`
   event is published, and the turn continues. Denials do not roll back the turn
   and do not throw. This follows invariant 18 and is the difference between "the
   user said no" and "the runtime is broken".

4. **Every non-answer path denies.** No live stream, a run that ends while
   waiting, a timeout (120 s by default, `Maieutics:Agent:ToolApproval:WaitTimeout`
   to change it), a malformed answer, and a gate that throws all resolve to a
   denial with a typed reason. Denying is the only safe default for an action the
   user did not affirmatively approve, and a broken approver must never be able to
   turn into silent execution. A gate exception is caught in the tool loop and
   converted, so it can never escape as a turn failure.

   One qualification on the cancellation paths: when the *turn* is cancelled
   (its duration budget expires, or the user cancels the run) while the gate is
   waiting, the gate returns a denial but the run is already unwinding — the
   resolved event cannot be written and no `tool_denied` result is committed.
   The turn rolls back, which is the correct outcome for a cancelled turn and is
   what every other tool does. `tool_denied` reaches the model only when the run
   continues: a user decline, a timeout, or a gate fault. `AgentToolApprovalResolved`
   is therefore absent, not merely negative, on a cancelled run.

5. **Approval state is event-sourced and replayable.** `AgentToolApprovalRequested`
   and `AgentToolApprovalResolved` are sequenced `AgentEvent`s published through
   the run's event stream and mapped to `tool.approval_requested` /
   `tool.approval_resolved` frames. The `input.request` frame the user answers is
   unsequenced (it is presentation, not run history), so `FrontendRunStream`'s
   replay filter was widened to include it: without that, a reconnecting client
   receives the run's history but not the pending question, leaving a request that
   is answerable in principle and invisible in practice. The widening is exactly
   "pending requests" — display frames stay unreplayed, since replaying them would
   duplicate rendered output on every reconnect. Replaying an already-answered
   request is harmless at the endpoint (idempotent 404), and every consumer absorbs
   that 404: the approval, elicitation, and REPL answer paths all treat it as replay
   noise rather than a failure, so a replayed dialog the user answers a second time
   does not paint an error over the cell.

6. **The wire is the existing input channel plus one discriminator.**
   `input.request` gains an explicit `kind` (`input` / `elicitation` / `approval`)
   and, for approvals, `tool` and `reason`. The answer reuses the shared `action`
   vocabulary: `accept` approves, `always` approves and remembers the tool for the
   session, `decline`/`cancel`/absent deny. The request-id prefix (`approve-`)
   selects the owning registry before any lookup, so an id can never cross-wire
   between the three consumers.

7. **Arguments are context, not editable fields.** The tool's arguments ride the
   request's schema `description` as read-only text. The user approves the call
   the model made; rendering them as editable form fields would create a second,
   unreviewed argument source that no validation or policy covers.

8. **"Always allow" is session-scoped tool authorization, deliberately separate
   from the permission store.** `AgentToolApprovalMemory` keys on
   `(session, tool)`. It is *not* fed into `PermissionOverrideRegistry`: that store
   composes process and Deno grants (env/net/read/write), and allowing a tool is a
   different domain. Merging them would create the false impression that approving
   a tool changed what a child process may do. There is no session-close signal in
   this process, so the set is bounded by a FIFO (oldest evicted past the cap)
   rather than released, the same discipline `PermissionOverrideRegistry` uses for
   its child-scope registrations.

9. **The policy reads declarations, and hints only ever widen.** A tool its
   producer declared `destructive` requires approval by default. A tool declared
   `readOnly` (and not destructive) is exempt even when its name matches a
   configured pattern. The reverse — treating `readOnly` as a reason to *demand*
   approval — is not offered, because the hint's whole purpose is to let a
   safe tool skip the gate. The name list (`Maieutics:Agent:ToolApproval:Require`,
   glob, case-insensitive) is empty by default, so enabling the gate changes
   nothing for tools that declare nothing.

## Consequences

- The safety story for destructive tools exists: the model asks, the user
  decides, the model learns the answer, and the turn continues either way.
- ADR 0043's annotations now have a consumer. `destructive` is load-bearing
  rather than recorded-but-unused.
- The headless posture is explicit and safe: with no frontend, the publish finds
  no live stream and the gate denies. A headless host never silently executes a
  tool the policy flagged. An operator who wants headless execution must say so
  in policy, not by accident.
- Waiting consumes turn budget: the run's `MaxTurnDuration` clock keeps running
  while a human decides. That is the honest reading of a bounded turn, but it
  means a short `MaxTurnDuration` and an interactive approval policy interact —
  the turn can expire while the dialog is open, and the pending request is then
  cancelled to a denial.
- `ApprovalRequiredAIFunction` and the M.E.AI approval content types remain
  unused. Adopting them is a real alternative (the SDK path is reachable), but it
  would route the decision through message content and the provider loop rather
  than through a gate the host owns; the transcript codec's existing handling of
  those content types stays as forward-compatibility rather than a live path.
- Plugin declaration approval (ADR 0037) and tool-call approval are different
  layers and stay separate: the first decides whether a plugin's declarations
  take effect at all, the second whether one call may execute.
