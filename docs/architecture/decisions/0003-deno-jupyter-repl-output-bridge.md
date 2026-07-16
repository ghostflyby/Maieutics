# ADR 0003: Deno Jupyter REPL and Notebook Output Bridge

Status: Accepted

Date: 2026-07-16

Supersedes: The `AGENTS.md` default that the primary Deno runtime is a custom IPC child rather than a Jupyter stack.

## Context

The Agent needs a stateful TypeScript REPL with access to Deno built-ins and Deno's Jupyter APIs. It must execute code,
observe rich Jupyter output, and forward text, media, display updates, and future components to the notebook used by the
human.

The repository already has independent reusable Jupyter Client and Kernel libraries and real interoperability coverage
against `deno jupyter`.

## Decision

The primary TypeScript REPL is a real `deno jupyter` kernel. Maieutics is simultaneously:

- the user-facing Jupyter kernel through `Maieutics.Jupyter.Kernel`; and
- a Jupyter client connected to the Deno kernel through `Maieutics.Jupyter.Client`.

The two reusable Jupyter libraries remain independent. The executable composition root connects their adapters through
provider-neutral interfaces.

Conceptually:

```csharp
public interface IReplSession : IAsyncDisposable
{
    ValueTask<IReplExecution> ExecuteAsync(
        ReplRequest request,
        CancellationToken cancellationToken = default);
}
```

One Agent session owns one Deno REPL session by default so TypeScript declarations and runtime state persist between
turns. Pooling or sharing one REPL between Agent sessions is forbidden unless a later isolation design explicitly permits
it.

## Output projections

A Deno execution produces two separate projections:

```text
ReplExecutionResult
    ModelContent          Bounded content suitable for continued model reasoning
    PresentationEvents    Ordered rich output forwarded to the user's notebook
```

The model projection may summarize or reference large results. The presentation projection preserves the full user-
visible result. Images, files, and large values use artifact references so they are not copied into the model context or
embedded repeatedly in IPC messages.

## Notebook presentation contract

A shared notebook presentation model, separate from Agent Core and Jupyter wire DTOs, must support ordered events for:

- stdout and stderr;
- display and execute result;
- display update and clear output;
- error;
- artifact;
- component creation, update, event, and disposal;
- future Jupyter comm open, message, and close behavior.

The Jupyter adapter maps presentation events to protocol messages. The Deno adapter maps Deno Client outputs to the same
presentation events. Agent Core never receives raw Jupyter messages.

## Correlation and display identity

- Deno outputs are rebound to the active user-facing cell execution.
- Wire order is preserved for stream, display, update, clear, result, and error events.
- Deno display IDs are mapped into a Maieutics-owned namespace containing the REPL session and execution identity.
- Display updates continue to target the mapped ID across executions when the Deno kernel intentionally performs a
  cross-execution update.
- Unknown MIME data and metadata are preserved at the adapter boundary.
- A useful `text/plain` fallback is retained when rich output is forwarded.

Raw Deno parent headers, routing identities, session IDs, and wire messages are never forwarded directly to the user's
notebook connection.

## Cancellation and lifecycle

Cancelling an Agent run cancels model streaming and requests an explicit interrupt of any active Deno execution. Local
Jupyter Client wait cancellation alone is insufficient because it does not stop the Deno kernel.

The REPL owner is responsible for process startup, connection-file lifecycle, readiness, heartbeat, interrupt, shutdown,
forced termination, and cleanup. Terminal REPL failure fails the current operation and makes the session unavailable
until an explicit restart policy is applied.

## Security

`deno jupyter` is a privileged execution environment and currently runs with all Deno permissions. It must not be treated
as an untrusted-code sandbox. The selected execution target and its surrounding process, container, or remote worker
provide the actual isolation boundary.

Independent Deno script extensions do not run inside this REPL merely to share its permissions. They use the dedicated
hook and extension IPC boundary described by ADR 0004. These extensions are host- or REPL-driven and are not Agent tools.

## Consequences

- Deno language state and rich display behavior reuse a real Jupyter implementation.
- Existing Jupyter Client correlation and output ordering remain reusable.
- Component support requires extending the notebook presentation and Jupyter comm boundaries, not leaking Client wire
  messages into Kernel code.
- The Agent/Deno adapter becomes a production consumer of `Maieutics.Jupyter.Client`; Client is no longer test-only for
  the Agent product.

## References

- Deno Jupyter documentation: https://docs.deno.com/runtime/reference/cli/jupyter/
