# ADR 0002: Agent Sessions, Runs, and Content

Status: Accepted

Date: 2026-07-17

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

The public interfaces above are Maieutics contracts. A `MaieuticsAgentSession` may internally own a Microsoft Agent
Framework `AIAgent` and `AgentSession`, but framework session and response types are not returned to notebook, worker,
extension, or persistence code.

## Content model

Messages and tool results use typed content parts rather than one text field. The domain model must be able to represent:

- text;
- image, audio, and other media by artifact reference;
- structured JSON or typed data;
- tool calls and tool results;
- provider-supported reasoning summaries permitted by policy;
- references to notebook presentation or generated artifacts.

Binary payloads are not stored repeatedly in the transcript. They are represented by an `ArtifactRef` carrying identity,
media type, size, integrity metadata, and a resolver-independent URI.

## Event model

`AgentEvent` is a discriminated event model. It must reserve stable semantics for:

- content delta and completed content;
- tool requested, started, progress, result, and failure;
- approval or input requested;
- artifact produced;
- usage reported;
- warning;
- turn completed or failed.

Notebook-specific output events are not Agent events. A runtime or tool may produce presentation data through the
separate notebook presentation boundary described by ADR 0003.

Microsoft Agent Framework `AgentResponseUpdate` and `AIContent` values are normalized at the internal runtime boundary.
The Maieutics event model preserves semantic identity and correlation without exposing provider or framework objects.

## Transactional transcript

- The current user turn and final assistant result are committed together only after successful model and tool-loop
  completion.
- Cancellation, provider failure, tool failure that aborts the turn, output limits, and worker failure do not commit a
  partial turn.
- Observable partial events and notebook outputs may remain visible even when the transcript transaction rolls back.
- History eviction and future compaction operate on complete turns or explicit summary checkpoints.
- Provider checkpoint metadata is committed atomically with the transcript state it describes.

Framework history completion is an internal staging point, not the final transaction commit. The staging provider and
outer run owner described by ADR 0006 ensure that empty or unsupported responses, policy rejection, output limits, and
later tool failures still roll back the complete turn.

## Identity and correlation

Strong value types are required for at least:

- `AgentSessionId`;
- `AgentRunId`;
- `AgentMessageId`;
- `ToolCallId`;
- `ExecutionTargetId`;
- `ExecutionOperationId`;
- `ArtifactId`;
- notebook display and component IDs.

Stringly typed correlation across model, tool, worker, and notebook boundaries is not allowed.

## Lifetime

- Model clients and connection pools are application singletons.
- Agent session, Deno REPL session, transcript state, and notebook presentation sink are kernel-session scoped.
- Agent runs and tool operations are per-turn owned objects.
- The internal framework Agent may be application-scoped when it is stateless across conversations; its framework
  `AgentSession` and Maieutics session wrapper remain kernel-session scoped.
- Shutdown stops new runs, cancels the active run, interrupts active execution targets, drains terminal events where
  possible, and then disposes child sessions and connections.

The current executable may continue registering one session as a singleton because it hosts one kernel, but the domain
contract must describe kernel-session scope rather than global process state.

## Consequences

- Starting and cancelling work no longer depends on whether a caller begins enumerating an async stream.
- Tool loops and distributed operations have an owner and terminal completion task.
- Multimodal and structured provider responses do not require replacing message APIs later.
- The first-stage string-only records have been replaced by Maieutics-owned typed content and explicit run contracts.
- Microsoft Agent Framework can be replaced or upgraded without changing public session and run contracts.

## References

- ADR 0006: Selective Microsoft Agent Framework adoption
