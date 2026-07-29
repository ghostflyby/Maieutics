# ADR 0010: Direct Microsoft.Extensions.AI Function Runtime

Status: Accepted

Date: 2026-07-29

Supersedes: ADR 0006 and the tool definition, lifecycle event, and result-shape portions of ADR 0002.

## Context

Maieutics owns the externally observable Agent session, run, event, limit, and canonical transcript semantics. The
selective Microsoft Agent Framework integration from ADR 0006 added `ChatClientAgent`, a Framework session, and a
staging history provider around a model/tool loop that was already implemented by
`Microsoft.Extensions.AI.FunctionInvokingChatClient` and observed by a Maieutics recording client.

That extra orchestration layer did not own the transaction boundary and did not expose enough stable information to
replace Maieutics recording. It therefore duplicated history staging without removing the need for the local canonical
transcript, provider-iteration recording, call correlation, or Maieutics limits.

The executable is also distributed as a NativeAOT binary. `AIFunction` does not itself require runtime code generation,
but `AIFunctionFactory` binds delegates through method and JSON type metadata. A successful publish alone does not prove
that schema creation, argument binding, invocation, and typed-result serialization work after trimming.

## Decision

Maieutics removes Microsoft Agent Framework and uses Microsoft.Extensions.AI directly:

```text
Maieutics AgentSession
    one-run gate, profile lease, limits, events, and transcript transaction
        |
        `-- per-run FunctionInvokingChatClient
                |
                |-- immutable AIFunction registry
                `-- RecordingChatClient
                        |
                        `-- provider IChatClient
```

`IChatClient` remains the only model-provider boundary. `AgentSession` accepts an immutable set of `AIFunction` values,
constructs one explicitly configured `FunctionInvokingChatClient` for each run, and invokes functions serially. It
builds every provider request from the committed canonical messages plus the current user message; the system prompt is
supplied through the captured run options.

The runtime keeps `RecordingChatClient`. It records every provider request and response iteration before function-loop
normalization so the committed turn retains provider call IDs, intermediate assistant function calls, corresponding
tool results, and the final assistant response in provider order. A non-empty provider `ConversationId` is rejected;
provider-side conversation state cannot replace the local canonical transcript.

The existing run ownership remains unchanged:

- `StartTurnAsync` reserves the session before returning;
- each run owns one immutable provider/options profile lease;
- the event stream remains bounded and single-consumer;
- cancellation, backpressure, limit enforcement, and disposal remain structured and deterministic;
- a complete turn commits atomically only after the final response passes validation;
- cancellation, provider failure, malformed calls, aborting tool failure, and validation failure roll back the turn.

## Function contract and invocation

`AIFunction` is the provider-visible function definition and invocation contract. Maieutics does not wrap it in a
parallel `IAgentTool`, descriptor, arguments, or outcome hierarchy.

At registration, Maieutics requires a unique provider-visible name containing 1 through 64 ASCII letters, digits,
underscores, or hyphens and an object-valued JSON input schema. The runtime preserves its own invocation identity and
progress semantics through an `AgentToolContext` stored in
`AIFunctionArguments.Context[typeof(AgentToolContext)]`. This context is runtime data, not part of the function schema.

A successful `AIFunction` invocation returns `JsonElement` or `null`. A factory-created delegate may have a typed
application result only when `AIFunctionFactory` marshals it to `JsonElement` with the supplied serializer metadata
before control returns to the Maieutics invoker. The runtime exposes one complete JSON value to the model and to
`AgentToolFinished`:

```json
{"status":"ok","value":{}}
```

An expected, recoverable `AgentToolException(code, safeMessage)` becomes:

```json
{"status":"error","code":"stable_code","message":"Safe model-visible message."}
```

Any other return type is invalid. Unexpected exceptions publish a generic failure envelope, abort the run, and roll
back its transcript transaction; exception details never become model-visible. Argument, result, progress, function
call, and provider-iteration limits remain Maieutics-owned and are enforced around the function invoker. Result size is
measured over the complete envelope.

The public tool lifecycle is reduced to:

- `AgentToolStarted`, containing the Maieutics call ID, function name, and detached JSON arguments;
- zero or more bounded `AgentToolProgress` events containing provider-neutral `AIContent`;
- `AgentToolFinished`, containing the complete detached model-visible JSON envelope.

There is no separately observable requested/completed/failed event phase. The canonical transcript still stores the
provider function-call and function-result messages needed for replay.

## Workspace functions

The initial workspace implementation uses one `Workspace` owner for startup root, session override, versioning, and
immutable snapshots. Each function captures exactly one snapshot, whose operations own workspace URI validation,
`.git` denial, symbolic-link checks, regular-file checks, verified opening, and bounded reads.

One cohesive `WorkspaceFunctions` implementation creates the three provider-visible functions
`list_directory`, `read_text`, and `search_text`. They remain separate functions with independent scalar schemas rather
than one tagged union with invalid argument combinations. The executable registers the same `Workspace` owner for both
model functions and Jupyter workspace commands.

## NativeAOT constraints

All application-created functions must use statically reachable delegate overloads of `AIFunctionFactory.Create`.
Factory options must receive `JsonSerializerOptions` backed by source-generated metadata, with unknown parameters
rejected where the tool contract requires strict binding. Runtime assembly scanning, name-based method discovery, and
reliance on reflection-only JSON metadata are forbidden.

NativeAOT acceptance requires running a published supported-RID process through a real function continuation. The
smoke must create the schema, bind arguments, invoke at least one `AIFunctionFactory` workspace function, serialize its
typed result, and complete the next model response. New Microsoft.Extensions.AI function-related trimming or AOT
warnings are release blockers; documented unrelated project exceptions remain governed separately.

## Consequences

- The Agent project has one direct Microsoft.Extensions.AI package dependency and no Microsoft Agent Framework
  dependency.
- The model/tool loop has fewer ownership layers while retaining canonical history and transactional guarantees.
- `RecordingChatClient` remains necessary infrastructure rather than removable framework glue.
- Tool authors use the standard `AIFunction` schema and invocation surface plus a small Maieutics runtime context and
  recoverable exception type.
- Workspace state, safe access, and functions are more cohesive without combining distinct provider-visible
  operations.
- Framework workflows, approvals, hosting, MCP integration, remote tools, and persistence remain outside this decision.

## References

- ADR 0001: Provider-neutral model boundary
- ADR 0002: Agent sessions, runs, and content
- ADR 0006: Selective Microsoft Agent Framework adoption (superseded)
