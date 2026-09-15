---
name: maieutics-dotnet-testing
description: Use when writing, reviewing, or running Maieutics tests with xUnit v3, FluentAssertions, sockets, fake model providers, Deno execution, external processes, configuration reload, or NativeAOT publishing.
---

# Maieutics .NET Testing

## Test Placement

- `Maieutics.Agent.Tests` owns Agent runtime unit tests.
- `Maieutics.Product.Tests` owns executable and product integration tests: frontend web API, provider conformance, configuration reload, permissions, Deno execution/REPL, plugins, control plane, persistence, and process-level smoke tests.
- Use deterministic fake providers and tools. Default tests must not call external model services.

## xUnit And Assertions

- Use xUnit v3 and flow `TestContext.Current.CancellationToken` into cancellable APIs.
- **Every asynchronous test must carry a declarative xUnit `Timeout`** (`[Fact(Timeout = 30_000)]` / `[Theory(Timeout = ...)]`). Do not rely on an internal deadline alone; the attribute is the outer safety net.
- **The internal deadline must be strictly smaller than the declared xUnit `Timeout`.** Default pairing: 20s internal deadline under 30s Timeout; heavier integration 50s under 60s; process-level 90s under 120s. When raising an internal deadline, raise the Timeout to match.
- For asynchronous exception assertions, invoke through FluentAssertions `Awaiting(...)`.
- Do not create a temporary `async` delegate solely for `delegate.Should().ThrowAsync(...)`.
- When `Awaiting` overloads are ambiguous, return an explicit `Task`/`ValueTask`, or use a named helper with a concrete return type.
- Assert protocol stages and typed failures, not incidental exception text unless the text is the public contract.

## Determinism

- **Never synchronize by polling or by relying on incidental timing.** This is a hard rule:
  - No busy-wait loops (`while (condition) { await Task.Yield(); }`), no `Thread.Sleep`, no `SpinWait`, no wall-clock polling (`while (DateTime.UtcNow < deadline) { ... await Task.Delay(...); }`).
  - No fixed sleeps used as synchronization (`await Task.Delay(200, ...)` to "give it time to start"). A `Task.Delay` is only acceptable as a genuine timeout with an explicit purpose, never to wait for an event.
- **Every wait must be signal-driven and awaitable:**
  - `TaskCompletionSource` (always `TaskCreationOptions.RunContinuationsAsynchronously`) awaited via `.WaitAsync(deadlineToken)`.
  - Bounded/unbounded `Channel<T>` readers (`await foreach (... .WithCancellation(token))`) for streaming or event sequences.
  - `task.WaitAsync(deadline)` for terminal conditions; `Task.WhenAll`/`WhenAny` for concurrency.
  - If the production code offers no signal for an event a test must await (e.g. a configuration reload, a plugin registry update), expose an `internal` seam (channel, TCS, or completion task) visible to the test assembly via `InternalsVisibleTo` rather than polling a counter.
- Do not use fixed TCP ports. Allocate loopback ports dynamically.
- Keep real socket integration tests isolated when they share process-level state.
- Use task completion signals, protocol messages, readiness probes, or bounded awaiting with a deadline.
- Integration failures should identify the failed stage: process start, readiness, heartbeat, send, reply, output, shutdown, or cleanup.

## Coverage By Boundary

- Agent (`Maieutics.Agent.Tests`): run reservation, event backpressure, provider-iteration recording, transcript commit, tools, limits, capabilities, provider switching, and rollback.
- Frontend and product (`Maieutics.Product.Tests`): discovery, auth, turn lifecycle, replay/resume, cancel, commands, sessions/forks, comm plane, and the published-executable smoke tests.
- Providers, configuration, permissions, and processes: deterministic fake HTTP/SSE servers, layered overlays, variable interpolation, reload, and process environment allowlists.
- Deno execution/REPL, plugins, and control plane: eval/output/control IPC, comm channels, host derive, policy broker, plugin lifecycle, and persistence stores.

## Verification Sequence

Run focused tests first for transport, lifetime, timing, or process changes. Then run:

```bash
dotnet test Maieutics.slnx
dotnet build Maieutics.slnx --no-restore -warnaserror
git diff --check
```

For executable, provider, or function-runtime changes, also publish the supported NativeAOT RID and run the relevant process smoke test. The smoke must exercise a real published-process function continuation when `AIFunctionFactory` behavior is affected. Do not suppress trimming or dynamic-code warnings broadly.
