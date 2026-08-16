# Maieutics.Agent instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for cross-project behavior.

## Ownership

This project owns the Jupyter-independent Agent facade and runtime: session/run lifecycle, canonical transcript,
provider-neutral content and events, model profile contracts, capability checks, limits, tool contracts and invocation,
cancellation, and atomic turn commit.

`Microsoft.Extensions.AI.IChatClient` is the only model-provider boundary. `AIFunction` is the provider-visible function
contract. `FunctionInvokingChatClient` and the recording decorator are internal orchestration details.

## Forbidden dependencies

- Do not reference any Jupyter project, ZeroMQ implementation, the executable, or provider SDK.
- Do not reference Microsoft Agent Framework or expose provider response, authentication, or SDK types from Maieutics
  contracts.
- Do not introduce a parallel model abstraction that duplicates `IChatClient`.
- Do not put executable configuration lookup, DI registration, or notebook rendering here.

## Run and transcript lifecycle

- `StartTurnAsync` validates input, reserves the session atomically, captures one immutable profile lease, and starts
  provider work before event enumeration.
- Permit one mutating run per session. Do not hold locks during provider calls, tool invocation, or event writes.
- `IAgentRun.Events` is bounded and single-consumer. Producers wait for capacity; callers that stop consuming must
  cancel or dispose the run.
- `Completion` is the terminal boundary and cannot complete before the session reservation and profile lease are
  released. Cancellation remains `OperationCanceledException`.
- Active model/tool loops retain their captured client, identity, capabilities, prompt, and limits across reloads.
- The committed transcript is local, immutable, provider-neutral, and organized by complete turns. Successful turns
  record model identity; history replay does not send that identity to providers.
- Build each provider request from committed canonical messages plus the current user message. Record and commit the
  user message, intermediate assistant function calls, function results, and final assistant response atomically after
  complete validation. Cancellation, provider errors, unsupported content, limits, and aborting tool errors roll back
  the entire turn while preserving already emitted events.
- History eviction removes complete turns and obeys both turn and canonical UTF-8 byte limits.

## Provider and tool semantics

- Validate streaming text and function-calling capabilities before sending a provider request.
- Reject non-empty provider conversation IDs. Provider state cannot replace the canonical transcript.
- Use a per-run, explicitly configured `FunctionInvokingChatClient`; keep tool calls serial unless ordering semantics
  are redesigned and tested.
- Keep `RecordingChatClient` directly inside the function loop so every provider iteration, call ID, intermediate
  function call, and function result is available for canonical replay.
- Enforce Maieutics iteration, call, argument, result, response, and progress limits independently of decorator
  counters.
- Validate unique function names and object-valued schemas at registration. Attach runtime-only identity and bounded
  progress through `AIFunctionArguments.Context[typeof(AgentToolContext)]`.
- Function implementations return `JsonElement` or `null`. Wrap success as `{"status":"ok","value":...}` and an expected
  `AgentToolException` as `{"status":"error","code":"...","message":"..."}`.
- Publish `AgentToolStarted`, zero or more `AgentToolProgress` values, and one `AgentToolFinished` envelope. Unknown
  tools, malformed arguments, unexpected exceptions, and runtime limit violations abort and roll back the turn.
- Never leak exception details or private reasoning.
- Application functions created through `AIFunctionFactory` must use statically reachable delegates and source-generated
  `JsonSerializerOptions`; do not scan assemblies or discover methods by name.
- Public APIs require XML documentation and must remain provider- and host-neutral.
