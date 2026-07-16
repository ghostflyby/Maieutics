# ADR 0005: Distributed Execution Control and Worker Planes

Status: Accepted

Date: 2026-07-16

## Context

Model API requests, credentials, conversation state, and notebook interaction may run locally while filesystem, shell,
Deno REPL, build, and other tools run over SSH or inside a container. Agent Core must not assume that a path, process,
or artifact exists on the same machine as the model client.

## Decision

The system is divided into a local control plane and one or more execution planes.

The control plane owns:

- provider credentials and model requests;
- canonical transcript and Agent run state;
- tool policy and approval decisions;
- execution-target selection;
- notebook presentation routing;
- artifact indexing and access policy.

An execution worker owns:

- target-local filesystem operations;
- shell, process, and future PTY operations;
- target-local Deno Jupyter kernels;
- target-local Deno extension and hook processes;
- artifact reading, writing, and transfer;
- operation cancellation and cleanup.

## Execution target abstraction

Tools depend on a transport-neutral execution target, conceptually:

```csharp
public interface IExecutionTarget
{
    ExecutionTargetId Id { get; }
    ExecutionCapabilities Capabilities { get; }

    ValueTask<IExecutionOperation> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default);
}
```

Initial implementations may include in-process, local worker, SSH stdio, and container targets. A later network service
may use gRPC or another bidirectional transport without changing tool contracts.

## Worker protocol

The worker wire protocol is separately versioned and independent of its transport. It must define:

- initialization, protocol version, target identity, and capability negotiation;
- operation request, accepted, event, result, cancellation, and terminal failure;
- process and session ownership;
- bounded streaming and backpressure;
- deadlines and cancellation acknowledgement;
- artifact upload, download, metadata, integrity, and size limits;
- health and graceful shutdown.

Exact message encoding is deferred. The semantic protocol must not depend on .NET runtime type serialization. SSH may
launch a worker in stdio mode, while local containers may use stdio or a Unix socket and network workers may use a
different transport.

## Paths and workspaces

Plain local absolute paths are not valid cross-target identities. Agent, tool, transcript, and worker contracts use
target-scoped workspace URIs, for example:

```text
workspace://dev-container/src/app.ts
workspace://ssh-build-host/home/project/README.md
artifact://sha256/0123456789abcdef...
```

Path resolution, normalization, symlink policy, allowed roots, case sensitivity, and filesystem operations are enforced
by the selected execution target. Human-facing display may show a friendly target-relative path, but the stable identity
retains its target.

## Artifacts

Artifacts are immutable references with media type, size, integrity, provenance, and access policy. Binary and large
results are transferred once and referenced by ID across model, tool, extension, and notebook boundaries.

The control plane decides whether an artifact may be:

- summarized for the model;
- sent to a multimodal model;
- rendered in the notebook;
- downloaded from or uploaded to a worker;
- persisted beyond the current kernel session.

The artifact store implementation is deferred and may initially be local.

## Routing and policy

Each tool declares execution requirements. The router selects an explicit target based on capabilities and policy; tools
do not silently fall back from a remote target to the local machine.

Model providers never choose an unrestricted transport address or raw host path. Model-produced tool arguments are
validated against registered tools, selected targets, workspace roots, and approval policy before dispatch.

Provider API keys remain local. A worker receives only credentials explicitly required for its execution policy, scoped
to the operation where possible.

## Failure and cancellation

- Every remote operation has a stable operation ID and terminal state.
- Agent cancellation sends an explicit worker cancellation request and still observes worker acknowledgement or
  disconnect.
- Worker disconnect fails owned operations with a typed execution-target failure.
- The control plane does not infer success from a lost connection.
- Retry is opt-in and only allowed for operations declared safe to retry; shell commands are not assumed idempotent.
- Partial presentation events may remain visible even when the operation fails, while transcript commit remains
  transactional.

## Consequences

- Local and remote tools share one domain contract.
- SSH and containers are deployment choices rather than Agent Core concepts.
- Paths, artifacts, cancellation, and output correlation are correct before remote execution is implemented.
- Worker protocol and Deno extension IPC remain separate but composable boundaries.
- Distributed execution can be introduced incrementally without moving model credentials or transcript authority away
  from the local control plane.
