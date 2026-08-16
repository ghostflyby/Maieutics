---
name: maieutics-agent-runtime
description: Use when changing Maieutics Agent sessions, runs, events, transcripts, Microsoft.Extensions.AI function orchestration, tools, model capabilities, provider profiles, provider switching, canonical history, or Agent-to-Jupyter behavior across Maieutics.Agent and the Maieutics executable.
---

# Maieutics Agent Runtime

## Boundary Model

- `Maieutics.Agent` owns provider-neutral sessions, runs, contents, events, transcripts, tools, capabilities, limits, and cancellation semantics.
- `Microsoft.Extensions.AI.IChatClient` is the only model-provider boundary. Do not add a parallel model-client abstraction.
- `AIFunction` is the provider-visible function definition and invocation contract. Do not add a parallel tool descriptor, arguments, or outcome hierarchy.
- `FunctionInvokingChatClient` and `RecordingChatClient` are internal orchestration. Provider SDK, authentication, and response types must not cross Maieutics contracts.
- The executable owns provider construction, configuration, DI, hosting, and Agent-to-Jupyter adaptation.
- The Agent runtime must not depend on Jupyter. Only the executable adapter understands both domains.

## Run And Transcript Semantics

- `StartTurnAsync` reserves the session before returning and starts work independently of event enumeration.
- Only one mutating run is active per session.
- `IAgentRun.Events` is bounded and single-consumer. Producers wait for capacity; callers that stop consuming must cancel or dispose the run.
- `Completion` is the terminal boundary and must not complete before the session reservation and profile lease are released.
- Each run captures one immutable `AgentRunProfile` lease. Provider, model, prompt, limits, and tools do not change inside a model/tool loop.
- The local provider-neutral transcript is canonical. Provider conversation IDs and storage never replace it.
- Build provider requests from committed canonical messages plus the current user message. Reject non-empty provider conversation IDs.
- Record every provider iteration and commit one complete `AgentTranscriptTurn` atomically only after the final response passes validation.
- Cancellation, provider failure, unsupported content, capacity failure, and aborting tool failure discard the prepared turn while retaining already emitted partial events.
- History eviction removes complete turns only.

## Tools And Providers

- Keep an immutable `AIFunction` registry. Validate unique provider-visible names and object-valued JSON schemas at session construction.
- Attach `AgentToolContext` through `AIFunctionArguments.Context[typeof(AgentToolContext)]` for session, run, call identity, and bounded progress. It is runtime state, not schema input.
- Invoke tool calls serially unless a separate concurrency and transcript design is approved.
- Require function results to be `JsonElement` or `null`. Wrap successes as `{"status":"ok","value":...}`.
- Convert expected `AgentToolException(code, safeMessage)` into `{"status":"error","code":"...","message":"..."}` recoverable model input. Unknown tools, malformed calls, limit violations, and unexpected exceptions terminate and roll back the turn.
- Publish `AgentToolStarted`, zero or more bounded `AgentToolProgress` events, and one `AgentToolFinished` envelope. Keep results structured until an output adapter renders them.
- Keep `RecordingChatClient` directly inside `FunctionInvokingChatClient`; the function decorator alone is not the canonical transcript recorder.
- For NativeAOT, create application functions from statically reachable delegates with source-generated `JsonSerializerOptions`. Do not scan assemblies or discover methods by name.
- Model configuration uses named case-insensitive Sources and Profiles. A run acquires one profile generation; retired clients live until their final lease ends.
- Capability-check streaming text and function calling before the first provider request.
- Profile switching affects the next run, never an active run, and does not reset canonical history.

## Agent-To-Jupyter Mapping

- Map semantic Agent events to ordered Jupyter messages without exposing ZeroMQ implementation types or wire envelopes to Agent code.
- Preserve partial display output after cancellation or failure, but do not commit the failed turn.
- `%maieutics` control cells do not call a model or enter the transcript.
- Never emit private chain-of-thought. Only explicitly permitted provider reasoning summaries may be exposed.

## Workflow

1. Identify whether the change belongs to Agent contracts, Microsoft.Extensions.AI function orchestration, provider construction, configuration, or the Jupyter adapter.
2. Preserve run-local leases, complete iteration recording, atomic commit, and typed error boundaries.
3. Add deterministic Agent tests; add Jupyter integration coverage only for adapter-visible behavior.
4. Use `maieutics-structured-concurrency` for lifetime changes and `maieutics-dotnet-testing` for verification, including a real published-process function invocation for NativeAOT-affecting changes.
