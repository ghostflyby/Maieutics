# ADR 0006: Selective Microsoft Agent Framework Adoption

Status: Superseded by ADR 0010

Date: 2026-07-16

Superseded: 2026-07-29

## Context

Maieutics currently implements streaming model invocation, transactional text history, one-active-turn enforcement,
limits, and provider error normalization directly over `IChatClient`. The next stages require richer content, tool
loops, context providers, session serialization, middleware, approvals, and observability.

Microsoft Agent Framework provides `AIAgent`, `ChatClientAgent`, `AgentSession`, `ChatHistoryProvider`, context providers,
streaming response updates, middleware, tool invocation facilities, and optional workflow and hosting packages. Reusing
the composable Agent and session facilities can avoid rebuilding common orchestration behavior. Adopting framework types
as Maieutics public or distributed contracts would instead couple the notebook kernel, Deno integration, extensions, and
workers to a dependency that evolves independently.

## Decision

Maieutics selectively adopts `Microsoft.Agents.AI` as an internal Agent orchestration implementation.

```text
Maieutics public Agent API
    IAgentSession / IAgentRun / AgentEvent / transcript
        |
        `-- MaieuticsAgentSession
                one-run gate, limits, transaction, event normalization
                    |
                    `-- AIAgent / ChatClientAgent
                            |
                            `-- IChatClient
```

Framework types do not cross these boundaries:

- the public Maieutics session, run, event, transcript, tool, and capability contracts;
- the Agent-to-Jupyter adapter;
- notebook presentation and artifact contracts;
- Deno extension IPC;
- distributed worker protocols;
- persisted Maieutics formats.

The composition root may reference framework registration and builder APIs. Reusable Jupyter projects must not reference
Microsoft Agent Framework.

## Run ownership

Maieutics remains the owner of externally observable run semantics:

- `StartTurnAsync` reserves a session immediately and returns an owned run object;
- one mutating run per session is enforced outside Agent Framework;
- input, response, retained-history, event, and artifact limits are checked by Maieutics;
- cancellation and interrupt propagate through the run owner;
- partial Agent events and notebook output may remain visible after failure;
- only a validated successful run commits the canonical transcript.

`AgentResponseUpdate` values are internal inputs to Maieutics event normalization. Receiving or streaming an update does
not itself commit a Maieutics turn.

Each run acquires one provider-neutral `AgentRunProfile` lease containing its `IChatClient` and immutable
`AgentSessionOptions`. The Framework agent, staging history provider, and Framework session are constructed for that
run. Configuration changes may affect the next run, but they cannot replace the model client, instructions, or limits
inside an active provider/tool continuation loop. The lease and Maieutics session reservation are released before the
run completion task becomes terminal.

## Transactional history

The default framework history behavior stores messages after a successfully completed framework stream, which is close
to but not identical to the Maieutics transaction boundary. Maieutics may still reject a completed provider stream for
an empty response, unsupported content, output limits, policy, or a later tool-loop failure.

Therefore the runtime uses a Maieutics-owned staging `ChatHistoryProvider` rather than treating
`InMemoryChatHistoryProvider` as the canonical store:

1. Invocation reads only committed transcript messages.
2. Successful framework completion stages the request and response messages for the active run.
3. The outer run validates the final semantic result and all required tool operations.
4. Commit atomically promotes the complete staged turn and provider checkpoint metadata.
5. Cancellation or failure discards the staged candidate.

History reduction and compaction operate on committed turns or explicit summary checkpoints. Staging state is never
serialized as committed conversation history.

## Model-client pipeline

`IChatClient` remains the provider boundary defined by ADR 0001. `ChatClientAgent` receives an explicitly constructed
client pipeline.

Set `UseProvidedChatClientAsIs` and compose required decorators deliberately. Configure provider-history conflicts to
fail rather than clearing the local history provider. This prevents default function
invocation, approval, message injection, or history persistence behavior from changing ownership without an explicit
Maieutics design and test.

Model-callable tools use an explicitly constructed, per-run `FunctionInvokingChatClient`:

```text
Agent Framework function-call representation
    -> Maieutics tool adapter
        -> limits, typed events, structured result envelope, and dispatch
            -> IAgentTool
```

The decorator runs calls serially and has explicit iteration, error, and unknown-call settings. A custom invoker maps
framework arguments to cloned `AgentToolArguments`, publishes requested/started/progress/completed/failed events, and
returns a source-generated JSON envelope to the model. Expected `AgentToolFailure` values remain recoverable model
inputs. Malformed arguments, unknown tools, limit violations, and unexpected tool exceptions terminate and roll back
the turn.

Framework iteration counters are not treated as the public budget definition. Maieutics counts actual provider
iterations and tool calls and enforces its configured limits before another provider continuation can begin.

The framework may drive the mechanical model/tool continuation loop, but Maieutics owns the immutable tool registry,
policy, future target routing, structured results, cancellation, and notebook presentation. Deno lifecycle extensions
remain outside this tool path.

## Complete interaction recording

Framework history staging is retained as a completion boundary, but it is not assumed to expose every intermediate
message produced inside a function-invocation loop. Each run therefore inserts a non-owning recording client directly
inside `FunctionInvokingChatClient`. It records every provider request and response iteration before framework
normalization.

After the final response, Maieutics reconstructs one complete provider-neutral turn containing the user message,
assistant tool calls, tool results, and final assistant response. Only this validated turn is promoted to the canonical
transcript. The recording client owns no provider connection and never disposes the singleton `IChatClient`.

## Provider history modes

The default model profile uses canonical local replay:

- committed history is supplied through the Maieutics history provider;
- provider-side response or conversation storage is disabled where supported;
- framework `AgentSession.ConversationId` is not the canonical conversation identity;
- opaque provider identifiers are retained only when their lifecycle and fallback behavior are explicit.

Agent Framework treats local chat history and provider-managed conversation IDs as alternative history mechanisms. A
model profile must not silently switch from local canonical history to provider-managed history after receiving an ID.
Any future acceleration mode requires a dedicated adapter that records the complete local transcript independently and
defines expiry, replay, provider switching, and recovery behavior.

## Framework modules

The initial adoption includes only the abstractions and core Agent package needed for `ChatClientAgent` and sessions.

Explicitly deferred:

- Agent Framework Workflows;
- framework ASP.NET, Azure Functions, OpenAI-compatible, A2A, and AG-UI hosting;
- Durable Task integration;
- framework MCP integration;
- framework shell tools;
- framework persistence providers;
- framework types in worker or extension protocols.

These modules may be evaluated independently when a concrete requirement exists. They are not prerequisites for Deno
REPL integration, notebook presentation, or distributed execution.

## NativeAOT and compatibility

NativeAOT remains a release requirement. At decision time, a .NET 10 `osx-arm64` probe using
`Microsoft.Agents.AI 1.13.0` with `Microsoft.Extensions.AI 10.8.0` successfully published with `-warnaserror` and ran the
`ChatClientAgent`, session, streaming, and history path.

This probe does not approve every optional framework package. Each newly adopted module or provider adapter requires:

- trimming and NativeAOT publish coverage for the supported runtime identifier;
- source-generated JSON metadata for application-owned serialized types;
- no broad suppression of trimming or dynamic-code warnings;
- bounded streaming, cancellation, and early-disposal tests;
- a dependency and published-size review.

## Consequences

- Maieutics reuses mature Agent composition facilities without making them public protocol contracts.
- `IChatClient` remains the common provider abstraction for OpenAI, Anthropic, Google, and future providers.
- Existing transactional guarantees remain stronger than the framework's default history completion point.
- The explicit `IAgentRun` API and typed text content are now the public execution boundary; the lazy
  `ExecuteTurnAsync` compatibility surface is intentionally absent.
- Provider or framework replacement remains possible because Jupyter, Deno, workers, and persistence depend on
  Maieutics-owned contracts.
- Framework upgrades require focused conformance and NativeAOT validation rather than broad application rewrites.

## References

- Microsoft Agent Framework overview: https://learn.microsoft.com/agent-framework/overview/agent-framework-overview
- Microsoft Agent Framework repository: https://github.com/microsoft/agent-framework
- Agent Framework agents overview: https://learn.microsoft.com/agent-framework/agents/
- ADR 0001: Provider-neutral model boundary
- ADR 0002: Agent sessions, runs, and content
