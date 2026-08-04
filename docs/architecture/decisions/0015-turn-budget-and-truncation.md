# ADR 0015: Turn Budgets and Truncated Turn Commits

Status: Accepted

Date: 2026-08-04

## Context

Agent turns were bounded by counts and sizes only: `MaxModelIterationsPerTurn` (8), `MaxToolCallsPerTurn` (16), input,
response, argument, and result sizes. Exhausting the iteration budget threw
`AgentModelIterationLimitExceededException` and rolled back the whole turn, discarding completed tool rounds even though
their output had already streamed to the notebook. There was no wall-clock budget, so a single slow or hung tool could
hold a cell indefinitely.

Interactive agent applications use a combined budget definition: a per-turn tool-loop cap plus a duration or cost
safety net, with graceful termination instead of hard failure (for example Copilot Agent Mode's per-turn request cap and
its "reached iteration limits" final summary, and Claude Desktop's pause-and-continue sampling loop).

`Microsoft.Extensions.AI.FunctionInvokingChatClient` (version 10.8.0) drives the loop with a zero-based `iteration`
counter. On the last permitted iteration it removes the tool declarations and breaks without throwing, so a turn that
keeps requesting tools is stopped by the Maieutics per-run guard before another provider continuation begins. That
guard throws while the loop is still processing the final tool round, at a clean boundary: every completed round is
recorded and the final round's tool calls were never executed.

## Decision

### Budgets

- `MaxModelIterationsPerTurn` defaults to 24 and `MaxToolCallsPerTurn` defaults to 48, keeping the tool-call budget
  above the iteration budget so iteration remains the primary loop guard for multi-call rounds.
- A new `Maieutics:Agent:MaxTurnDuration` (`TimeSpan`, default `00:00:00` = unlimited) optionally bounds the wall-clock
  duration of one turn. When positive, a linked cancellation source with `CancelAfter` feeds the function loop, so
  expiry cancels an in-flight provider stream or tool cooperatively.

### Iteration truncation

- When the model exhausts the iteration budget while still requesting tools, the run no longer throws. It publishes the
  final recorded assistant message, commits the partial exchange as a truncated turn, emits an `AgentTurnTruncated`
  terminal event, and completes normally with `AgentRunResult.Truncated = true`.
- `AgentTranscriptTurn` gains an optional `Truncated` marker. The transcript is in-memory only, so no persisted format
  changes; the marker defaults to false and older state round-trips unchanged.
- History replay trims truncated turns: trailing assistant messages whose contents are only function calls are removed,
  and a trailing assistant message that mixes text with calls keeps the text. Completed tool rounds before the
  unanswered tail remain in replay, and the next user message follows them, so the sequence stays provider-valid.
- Non-limit termination with unanswered tool calls (for example unknown calls) still throws
  `AgentModelIterationLimitExceededException` and rolls back, preserving the existing typed failure contract.

### Duration expiry

- Expiry maps to a new `AgentTurnDurationExceededException` and does not commit a partial turn: the boundary can fall
  mid-tool, so no consistent partial exchange exists. Already-streamed events remain visible in the notebook, and the
  Jupyter adapter renders a typed `AgentTurnDurationExceeded` error.

### Agent-to-Jupyter presentation

- A truncated run renders as a successful reply followed by a markdown status note instead of an error.

## Consequences

- Long agent-style cells keep partial progress instead of failing wholesale, and the next cell continues with the
  committed partial history.
- Duration budgets are opt-in and backward compatible; the default keeps current behavior of unlimited wall-clock time.
- Replay trimming changes the exact request messages sent to providers after a truncated turn; this is covered by
  deterministic Agent tests that inspect the recorded provider requests.
- The iteration guard now terminates with truncation instead of an exception, so callers that treated
  `AgentModelIterationLimitExceededException` as the only iteration outcome must observe `AgentRunResult.Truncated`.
