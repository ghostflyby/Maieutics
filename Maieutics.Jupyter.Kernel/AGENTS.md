# Maieutics.Jupyter.Kernel instructions

Use `.agents/skills/maieutics-jupyter-protocol/SKILL.md` and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for cross-project behavior.

## Ownership

This project is the reusable server-side Jupyter host. Kernel authors implement application capabilities without
touching ZmqSharp or wire envelopes.

- `Transport` owns ROUTER shell/control/stdin, XPUB IOPub, REP heartbeat, framing, routing identities, bounded queues,
  asynchronous pumps, terminal state, and deterministic disposal.
- `JupyterKernelHost` owns request dispatch, execution count, state publication, active execution cancellation,
  interrupt, shutdown, and conversion between application outcomes and protocol replies.
- Application interfaces and `JupyterExecutionContext` expose domain behavior and ordered output operations only.

## Forbidden dependencies

- Reference only `Maieutics.Jupyter.Shared` for Jupyter contracts.
- Never reference `Maieutics.Jupyter.Client`, Agent, provider, or executable concepts.
- ZmqSharp types and raw frames must not escape `Transport`.
- Application capability implementations must not create frames, access sockets, or manage routing identities.

## Lifecycle and ordering

- A single transport owner creates and disposes every socket, observes every long-lived asynchronous pump, and
  propagates one terminal cause. Dispose received `ZMessage` values after copying their frames across the boundary.
- Shell requests execute serially. Control handling remains independently responsive during long execution.
- Preserve `busy -> handler and parented output -> reply -> idle`; publish idle from `finally` after busy.
- Publish `status:starting` once per host lifecycle and `iopub_welcome` according to XPUB subscription semantics.
- Interrupt cancels the host-owned active execution token. Shutdown sends its reply before stopping the host.
- `silent` suppresses IOPub execution output while preserving application execution semantics.
- `JupyterExecutionContext` preserves call order on the IOPub wire and does not maintain frontend display state.
- Missing optional language providers return protocol-valid `NotSupported` or `unknown` responses as specified by the
  message type.
- Terminal transport transitions complete once and fail all dependent operations consistently.
