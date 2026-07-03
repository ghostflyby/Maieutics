# AGENTS.md

## Project layout

This repository contains three core libraries:

- `Maieutics.Jupyter.Shared`
- `Maieutics.Jupyter.Client`
- `Maieutics.Jupyter.Kernel`

`Shared` is the protocol foundation. `Client` and `Kernel` must depend on `Shared`, but must not depend on each other.

## Dependency rules

### Maieutics.Jupyter.Shared

Allowed:

- Jupyter protocol data models
- message envelopes
- channel names
- JSON serialization contracts
- MIME bundle models
- protocol exceptions
- shared test fixtures when appropriate

Forbidden:

- NetMQ
- socket lifecycle code
- client behavior
- kernel behavior
- process launching
- execution logic

### Maieutics.Jupyter.Client

Allowed:

- NetMQ socket connection logic
- Jupyter client session handling
- request/reply APIs
- message correlation
- shell/control/iopub/stdin routing
- kernel process connection abstractions

Forbidden:

- kernel-side execution
- kernel dispatch loops
- direct dependency on `Maieutics.Jupyter.Kernel`

### Maieutics.Jupyter.Kernel

Allowed:

- NetMQ socket binding logic
- Jupyter kernel message loop
- kernel-side request dispatch
- execution abstraction
- heartbeat handling
- connection file consumption

Forbidden:

- client-side high-level APIs
- direct dependency on `Maieutics.Jupyter.Client`

## ZeroMQ implementation

Use `NetMQ`.

Do not use clrzmq/libzmq bindings unless there is a documented interoperability requirement that NetMQ cannot satisfy.

## Testing

Use:

- xUnit latest
- Microsoft.NET.Test.Sdk
- Shouldly or FluentAssertions, if assertion readability becomes important

Test categories:

- unit tests for protocol models and serializers
- socket-level tests for client/kernel transport behavior
- integration tests that start an in-process kernel and connect a client to it

Integration tests must use dynamically allocated local TCP ports or isolated IPC-safe endpoints. Tests must not assume fixed ports.

## Protocol boundaries

All Jupyter wire-format types belong in `Shared`.

Transport code must translate between NetMQ frames and `Shared` message types as early as possible.

Do not leak NetMQ-specific types into public protocol APIs unless the API is explicitly transport-level.

## Public API style

Prefer small composable abstractions:

- `IJupyterConnection`
- `IJupyterClient`
- `IJupyterKernel`
- `IJupyterMessageSerializer`
- `IJupyterMessageRouter`

Avoid premature high-level notebook concepts in the core transport libraries.

## Async rules

Prefer `Task` / `ValueTask` APIs for public async operations.

All long-running loops must accept `CancellationToken`.

Socket disposal must be deterministic.

No fire-and-forget background task without an owned lifecycle object.

## Serialization rules

Protocol JSON must be stable and explicit.

Avoid relying on reflection-heavy magic in hot paths.

Any compatibility behavior for real Jupyter clients/kernels must have a test.

## Naming

Use `Jupyter` for protocol-level concepts.

Use `Kernel` only for kernel-side components.

Use `Client` only for client-side components.

Use `Session` only for Jupyter message session identity/correlation, not for arbitrary runtime state.

## Compatibility goal

The initial goal is compatibility with the core Jupyter messaging protocol, not full notebook UI behavior.

When protocol behavior differs from reference Jupyter implementations, document the difference in code comments and tests.