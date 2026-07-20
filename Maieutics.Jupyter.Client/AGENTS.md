# Maieutics.Jupyter.Client instructions

Use `.agents/skills/maieutics-jupyter-protocol/SKILL.md` and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for cross-project behavior.

## Ownership

This project is a reusable client for arbitrary Jupyter kernels. It contains three internal layers:

- `Transport`: NetMQ sockets, wire serialization, bounded queues, thread affinity, terminal state, and disposal.
- `Protocol`: request correlation, pending operations, execution aggregation, stdin, late output, and event fan-out.
- public facade and local manager: stable .NET APIs, kernelspec parsing, child-process startup, interrupt, restart, and
  cleanup.

It must remain usable without `Maieutics.Agent`, the product executable, or `Maieutics.Jupyter.Kernel`.

## Forbidden dependencies

- Reference only `Maieutics.Jupyter.Shared` for Jupyter contracts.
- Never reference `Maieutics.Jupyter.Kernel` or Agent/product concepts.
- NetMQ types and raw frames must not escape `Transport`.
- Transport must not own request/reply semantics, execution aggregation, or notebook UI reduction.

## Transport lifecycle

- The dedicated I/O thread creates, polls, uses, and disposes shell, control, stdin, IOPub, and heartbeat sockets.
- Shell and stdin share an identity; control has its own identity.
- External producers communicate through bounded command queues. Queue saturation terminates the connection with a
  typed backpressure failure; never drop protocol messages.
- Startup cancellation, owner-thread failure, disconnect, backpressure, and concurrent disposal converge on one
  terminal cause and promptly fail pending sends and pings.
- Use an explicit I/O-loop owner object to dispose related NetMQ resources together. Do not scatter closure-captured
  `using` locals across `RunIoThread`.

## Protocol and API constraints

- Match replies by parent message ID, channel, and expected reply type. Never correlate by execution count.
- An execution completes only after its reply and parented idle both arrive, in either order.
- Preserve `input_request` headers for `input_reply`. Late parented output remains observable as late output.
- Unknown messages become controlled events or errors and never crash the receive loop.
- Local cancellation removes local waiting; interrupt is an explicit control/manager operation.
- Request/reply APIs use `Task<T>`. Execution output is single-consumer `IAsyncEnumerable<T>`; event subscriptions are
  independent streams. Do not expose writable `Channel<T>` values.
- Asynchronous connection starts through a factory, not constructor side effects.
- `LocalJupyterKernelManager` owns process, temporary connection file, client, and cleanup. Shutdown uses one total
  timeout budget and kills the process tree when graceful shutdown cannot finish.
