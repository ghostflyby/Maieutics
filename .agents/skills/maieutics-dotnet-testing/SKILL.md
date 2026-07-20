---
name: maieutics-dotnet-testing
description: Use when writing, reviewing, or running Maieutics tests with xUnit v3, FluentAssertions, NetMQ sockets, Jupyter Client/Kernel integration, fake model providers, real Deno kernels, external processes, configuration reload, or NativeAOT publishing.
---

# Maieutics .NET Testing

## Test Placement

- `Maieutics.Agent.Tests` owns Jupyter-independent Agent runtime unit tests.
- `Maieutics.Jupyter.Tests` owns Shared, Client, Kernel, executable adapter, self-hosted, real Deno, configuration, and process-level integration tests.
- Use deterministic fake providers and tools. Default tests must not call external model services.

## xUnit And Assertions

- Use xUnit v3 and flow `TestContext.Current.CancellationToken` into cancellable APIs.
- Every asynchronous or process test must have a cancellation deadline or xUnit timeout.
- For asynchronous exception assertions, invoke through FluentAssertions `Awaiting(...)`.
- Do not create a temporary `async` delegate solely for `delegate.Should().ThrowAsync(...)`.
- When `Awaiting` overloads are ambiguous, return an explicit `Task`/`ValueTask`, or use a named helper with a concrete return type.
- Assert protocol stages and typed failures, not incidental exception text unless the text is the public contract.

## Determinism

- Do not use fixed sleeps for synchronization.
- Do not use fixed TCP ports. Allocate loopback ports dynamically.
- Put tests sharing NetMQ process state in the non-parallel socket collection.
- Use task completion signals, protocol messages, readiness probes, or bounded polling with a deadline.
- Integration failures should identify the failed stage: process start, readiness, heartbeat, send, reply, output, shutdown, or cleanup.

## Coverage By Boundary

- Shared: frames, HMAC, JSON names, source-generated round trips, unknown fields, buffers, connection validation, MIME, display IDs, and cursor conversion.
- Client: socket ownership, five channels, correlation, reply/idle ordering, stdin parents, output order, late output, cancellation, disconnect, and backpressure.
- Kernel: busy/reply/idle, shell serialization, control responsiveness, heartbeat, interrupt, shutdown, silent execution, stdin, language services, and display updates.
- Agent: run reservation, event backpressure, transcript staging/commit, tools, limits, capabilities, provider switching, and rollback.
- Integration: self-hosted Client/Kernel plus the portable real Deno kernelspec.

## Verification Sequence

Run focused tests first for transport, lifetime, timing, or process changes. Then run:

```bash
dotnet test Maieutics.slnx
dotnet build Maieutics.slnx --no-restore -warnaserror
git diff --check
```

For executable/provider/Framework changes, also publish the supported NativeAOT RID and run the relevant process smoke test. Do not suppress trimming or dynamic-code warnings broadly.
