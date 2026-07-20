---
name: maieutics-jupyter-protocol
description: Use when changing Maieutics Jupyter wire DTOs, connection files, serialization, Client transport/protocol behavior, Kernel hosting, output ordering, completion cursors, NetMQ channels, or real Deno kernel interoperability across Maieutics.Jupyter.Shared, Client, Kernel, and their integration tests.
---

# Maieutics Jupyter Protocol

## Choose The Owning Layer

- `Shared` owns transport-independent wire values, connection files, MIME, JSON contracts, frames, HMAC, IDs, and cursor conversion.
- `Client` transport owns DEALER/SUB/REQ sockets and one incoming wire stream. Client protocol owns correlation, executions, stdin, late output, and event fan-out.
- `Kernel` transport owns ROUTER/XPUB/REP sockets. `JupyterKernelHost` owns dispatch, execution count, status, interrupt, shutdown, and error conversion.
- The executable adapter may map Agent events to Jupyter capabilities, but reusable Jupyter projects must not reference Agent types.

Never solve a boundary problem by adding a Client-to-Kernel or Kernel-to-Client reference.

## Preserve Protocol Invariants

- Target classic Jupyter messaging 5.5 and classic connection files unless scope explicitly expands.
- Correlate by parent message ID, channel, and expected reply type. Never correlate by execution count.
- Preserve routing identities in `JupyterWireMessage`, binary buffers outside JSON, and causal parent headers on output.
- Preserve observable shell order: `busy -> handler/output -> reply -> idle`; publish `idle` from a `finally` path.
- Keep control and heartbeat responsive during long shell execution.
- Complete executions from reply plus parented idle, never from delays or guessed ordering.
- Preserve IOPub wire order. The Client reports display/update/clear events and does not reduce final UI state.
- Jupyter cursor offsets are Unicode code-point offsets. Convert with `JupyterCursorPosition`; never slice directly with a protocol offset.
- Unknown optional fields are tolerated where compatible. Unsupported features return protocol-valid errors or fallback statuses.

## Wire And Transport Rules

- Use source-generated `System.Text.Json` metadata for protocol DTOs.
- Validate delimiters, frame counts, signatures, HMAC, required fields, and supported schemes at the boundary.
- Fail explicitly for unsupported CurveZMQ or signature schemes.
- All NetMQ sockets are created, used, polled, and disposed by their owning I/O thread. NetMQ types do not escape transport.
- Use bounded queues. Queue overflow terminates the affected connection with a typed backpressure error; never drop protocol messages.
- Unknown message types must not crash long-running receive loops.

## Deno Interoperability

- Keep the portable kernelspec at `Maieutics.Jupyter.Tests/TestData/kernels/deno/kernel.json`.
- Resolve it from test output and use `deno` from `PATH`; do not introduce user-specific paths.
- Treat real Deno coverage as interoperability testing for the reusable Client, not as a production Agent dependency.

## Verification

Use the `maieutics-dotnet-testing` skill. Add focused Shared, Client, Kernel, self-hosted, or Deno coverage according to the changed boundary before running the solution gates.
