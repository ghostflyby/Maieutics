# ADR 0002: Agent Sessions, Runs, and Content

Status: Accepted

Date: 2026-07-16

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
    ValueTask<IAgentRun> StartTurnAsync(
        AgentTurn turn,
        CancellationToken cancellationToken = default);

    AgentTranscript GetTranscriptSnapshot();
}

public interface IAgentRun
{
    AgentTurnId Id { get; }
    IAsyncEnumerable<AgentEvent> Events { get; }
    Task<AgentTurnResult> Completion { get; }
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}
```

Starting a run reserves the session immediately. Event enumeration does not control whether the run has started. A
session owns at most one mutating run unless a later design explicitly introduces branches or isolated child runs.

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

## Transactional transcript

- The current user turn and final assistant result are committed together only after successful model and tool-loop
  completion.
- Cancellation, provider failure, tool failure that aborts the turn, output limits, and worker failure do not commit a
  partial turn.
- Observable partial events and notebook outputs may remain visible even when the transcript transaction rolls back.
- History eviction and future compaction operate on complete turns or explicit summary checkpoints.
- Provider checkpoint metadata is committed atomically with the transcript state it describes.

## Identity and correlation

Strong value types are required for at least:

- `AgentSessionId`;
- `AgentTurnId`;
- `AgentRunId`;
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
- Shutdown stops new runs, cancels the active run, interrupts active execution targets, drains terminal events where
  possible, and then disposes child sessions and connections.

The current executable may continue registering one session as a singleton because it hosts one kernel, but the domain
contract must describe kernel-session scope rather than global process state.

## Consequences

- Starting and cancelling work no longer depends on whether a caller begins enumerating an async stream.
- Tool loops and distributed operations have an owner and terminal completion task.
- Multimodal and structured provider responses do not require replacing message APIs later.
- The first-stage string-only records require a deliberate breaking migration before the next feature stage.

