# ADR 0002: Agent Sessions, Runs, and Content

Status: Accepted

Date: 2026-07-17

Partially superseded by: ADR 0010 replaces this decision's tool definition, tool lifecycle event, and tool result-shape
sections. The session/run ownership and transactional transcript decision remains in force.

## Context

The first-stage `IAgentSession.ExecuteTurnAsync` API combines lazy execution start, streamed events, cancellation, and
completion in one async iterator. Messages contain one string and the public event model primarily represents text.

Tool loops, multimodal content, remote operations, approvals, artifacts, and notebook presentation require explicit run
ownership and richer content without weakening the transactional history rules already implemented.

## Decision

An Agent session starts an explicit Agent run. Conceptually:

```csharp
public interface IAgentSession
{
    AgentSessionId Id { get; }

    Task<IAgentRun> StartTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default);

    AgentTranscript GetTranscriptSnapshot();
}

public interface IAgentRun : IAsyncDisposable
{
    AgentRunId Id { get; }
    AgentSessionId SessionId { get; }
    IAsyncEnumerable<AgentEvent> Events { get; }
    Task<AgentRunResult> Completion { get; }
    Task CancelAsync(CancellationToken cancellationToken = default);
}
```

Starting a run reserves the session immediately. Event enumeration does not control whether the run has started. A
session owns at most one mutating run unless a later design explicitly introduces branches or isolated child runs.

The run owns a bounded single-consumer event stream. Producers wait for capacity rather than dropping events. Callers
that stop consuming before terminal completion must cancel or dispose the run. `Completion` becomes terminal only after
the session reservation is released and the event writer is closed.

The public interfaces above are Maieutics contracts. The runtime may internally use Microsoft.Extensions.AI clients,
functions, messages, and content, but provider response types are not returned to notebook, worker, extension, or
persistence code.

## Content model

Agent messages use Microsoft.Extensions.AI `ChatMessage`, `ChatRole`, and `AIContent` directly. This taxonomy already
represents text, data, media, tool calls and results, usage, annotations, and provider reasoning state. Maieutics does not
duplicate it with `AgentMessage` or `AgentContent` variants.

Maieutics still owns the semantics around those values: which inputs are accepted, tool result policy, event
correlation, transcript commit and rollback, history limits, public redaction, and adaptation to Jupyter or future
worker and persistence boundaries. Raw provider SDK objects remain behind provider adapters.

## Event model

`AgentEvent` is a discriminated event model. It must reserve stable semantics for:

- content delta and completed content;
- tool started, bounded progress, and one finished result envelope;
- approval or input requested;
- artifact produced;
- usage reported;
- warning;
- turn completed or failed.

Notebook-specific output events are not Agent events. A runtime or tool may produce presentation data through the
separate notebook presentation boundary described by ADR 0003.

Microsoft.Extensions.AI response values are normalized at the internal runtime boundary. The Maieutics event model
preserves semantic identity and correlation while carrying policy-cleaned `ChatMessage` or `AIContent` values where a
complete message or content value is part of the event contract. ADR 0010 defines the current three-stage tool event
contract and model-visible JSON result envelopes.

## Transactional transcript

- The canonical transcript is compact UTF-8 JSON containing a versioned Maieutics envelope and complete
  `AgentTranscriptTurn` values. The envelope records its schema and Microsoft.Extensions.AI contract/producer version.
- Every turn begins with the submitted user message and ends with the final assistant message.
- Intermediate assistant tool-call messages and tool-result messages are retained in provider order so a later request
  can be reconstructed without provider-owned conversation state.
- The complete user, assistant, and tool message sequence is committed only after successful model and tool-loop
  completion.
- Cancellation, provider failure, tool failure that aborts the turn, output limits, and worker failure do not commit a
  partial turn.
- Observable partial events and notebook outputs may remain visible even when the transcript transaction rolls back.
- History eviction uses the compact canonical message JSON UTF-8 size and operates on complete turns or explicit summary
  checkpoints.
- Provider checkpoint metadata is committed atomically with the transcript state it describes.
- Canonical private state retains provider reasoning content, including opaque protected reasoning data needed for
  replay. Public transcript snapshots, run results, events, and Jupyter output remove reasoning and provider-private
  metadata.
- Public snapshots are decoded into detached objects. Mutating a returned `ChatMessage`, content value, annotation, or
  additional-property collection cannot mutate canonical state.
- Content that the official Microsoft.Extensions.AI JSON contract cannot serialize rejects the commit and rolls back
  the complete turn.

Provider completion and function-loop completion are preparation boundaries, not transcript commits. The run owner
described by ADR 0010 records each provider iteration and promotes the complete message sequence only after final
validation, so empty or unsupported responses, policy rejection, output limits, and aborting tool failures still roll
back the complete turn.

## Identity and correlation

Strong value types are required for at least:

- `AgentSessionId`;
- `AgentRunId`;
- `AgentMessageId`;
- `AgentToolCallId`;
- `ExecutionTargetId`;
- `ExecutionOperationId`;
- `ArtifactId`;
- notebook display and component IDs.

Stringly typed correlation across model, tool, worker, and notebook boundaries is not allowed.

## Lifetime

- Model clients and connection pools are application singletons.
- Agent session, Deno REPL session, transcript state, and notebook presentation sink are kernel-session scoped.
- Agent runs and tool operations are per-turn owned objects.
- Per-run function-loop decorators are owned by the run. The Maieutics session wrapper remains kernel-session scoped.
- Shutdown stops new runs, cancels the active run, interrupts active execution targets, drains terminal events where
  possible, and then disposes child sessions and connections.

The current executable may continue registering one session as a singleton because it hosts one kernel, but the domain
contract must describe kernel-session scope rather than global process state.

## Consequences

- Starting and cancelling work no longer depends on whether a caller begins enumerating an async stream.
- Tool loops and distributed operations have an owner and terminal completion task.
- Multimodal and structured provider responses do not require replacing message APIs later.
- The first-stage string-only records and Maieutics-owned parallel content hierarchy have been replaced by
  Microsoft.Extensions.AI message/content contracts and explicit Maieutics run and envelope contracts.
- Expected tool failures are committed as structured tool results only when the model subsequently produces a valid
  final answer; unexpected tool exceptions roll back the complete turn.
- Microsoft.Extensions.AI function orchestration can be replaced or upgraded without changing public session and run
  contracts.

## References

- ADR 0010: Direct Microsoft.Extensions.AI function runtime
