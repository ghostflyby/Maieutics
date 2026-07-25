# Maieutics.Agent instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for cross-project behavior.

## Ownership

This project owns the Jupyter-independent Agent facade and runtime: session/run lifecycle, canonical transcript,
provider-neutral content and events, model profile contracts, capability checks, limits, tool contracts and invocation,
staging, cancellation, and atomic turn commit.

`Microsoft.Extensions.AI.IChatClient` is the only model-provider boundary. Microsoft Agent Framework and
`FunctionInvokingChatClient` are internal orchestration details.

## Forbidden dependencies

- Do not reference any Jupyter project, NetMQ, the executable, or provider SDK.
- Do not expose Microsoft Agent Framework, provider response, authentication, or SDK types from Maieutics contracts.
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
- Framework history is staging only. Commit user, intermediate assistant tool calls, tool results, and final assistant
  atomically after complete validation. Cancellation, provider errors, unsupported content, limits, and aborting tool
  errors roll back the entire turn while preserving already emitted events.
- History eviction removes complete turns and obeys both turn and canonical UTF-8 byte limits.

## Provider and tool semantics

- Validate streaming text and function-calling capabilities before sending a provider request.
- Keep the canonical transcript even when a provider returns conversation IDs; provider state cannot replace it.
- Use a per-run, explicitly configured `FunctionInvokingChatClient`; keep tool calls serial unless ordering semantics
  are
  redesigned and tested.
- Enforce Maieutics iteration, call, argument, result, response, and progress limits independently of framework
  counters.
- Tool names and schemas are validated at registration. Arguments are cloned structured JSON and deserialized with
  source-generated `JsonTypeInfo<T>`.
- Expected `AgentToolFailure` is a stable model-visible failure. Unknown tools, malformed arguments, unexpected tool
  exceptions, and runtime limit violations abort and roll back the turn.
- Preserve structured tool results and lifecycle events. Never leak exception details or private reasoning.
- Public APIs require XML documentation and must remain provider- and host-neutral.
