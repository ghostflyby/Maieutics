---
name: maieutics-structured-concurrency
description: Use when changing Maieutics background loops, channels, ZeroMQ transport ownership, cancellation, backpressure, run lifetimes, provider generations, process supervision, interrupt, shutdown, concurrent disposal, or timeout behavior in any project.
---

# Maieutics Structured Concurrency

## Ownership Checklist

Every long-lived operation must have:

- one explicit owner;
- an owned cancellation source or inherited lifetime token;
- an observed completion task;
- a deterministic disposal path;
- one terminal cause propagated to all dependents.

Do not create fire-and-forget tasks. Constructors remain side-effect free when startup is asynchronous.

## Awaiting And Synchronization

- Do not hold locks while awaiting provider streams, tools, stdin, channel capacity, transport sends, or child-process exit.
- Reserve state in a short critical section, perform asynchronous work outside it, then commit in another short critical section.
- Use bounded queues where producers can outrun consumers. Wait for capacity for semantic event streams; fail the connection for protocol backpressure.
- A single-consumer stream must reject or prevent a second consumer explicitly.
- Terminal transitions such as startup cancellation, owner failure, disconnect, backpressure, and concurrent dispose complete at most once.

## Resource Lifetimes

- Group asynchronous socket resources under one owner, stop and observe their pumps, then dispose them deterministically.
- Do not null constructor-initialized disposable fields merely to signal disposal; the owner object's lifetime is the boundary.
- Reference-count provider generations. Publish a replacement only after successful construction; retire the old generation after its last lease.
- `CancelAsync` requests cancellation idempotently and observes termination. A caller token cancels waiting, not the already-issued cancellation request.
- `DisposeAsync` is idempotent, cancels owned work, observes background tasks, completes streams, and releases leases.

## Shutdown And Processes

- Use one total timeout budget across request, graceful exit, forced termination, and cleanup. Do not reset the full timeout per stage.
- Send Jupyter shutdown replies before stopping the host.
- Keep control interrupt and heartbeat independent from serialized shell execution.
- Escalate cooperative child cancellation to process-tree termination only after the configured budget.
- Delete owned temporary connection files even when shutdown fails.

## Failure Tests

Test startup cancellation, active cancellation, queue saturation, owner-thread failure, disconnect, repeated cancellation, concurrent disposal, graceful shutdown, forced termination, and cleanup. Use deadlines and protocol signals rather than fixed sleeps.
