# AGENTS.md

## Repository purpose

Maieutics is a notebook-native LLM agent exposed as a Jupyter kernel.

The user model is:

- one executable notebook cell submits one Agent turn;
- the kernel owns the authoritative live conversation state;
- assistant text, tool activity, rich values, errors, and input requests are emitted through Jupyter messages;
- notebook frontends provide editing, execution, history, and rich rendering;
- an `.ipynb` file is a portable interaction snapshot, not the runtime database.

The reusable Jupyter implementation must remain independent of the Agent runtime. Agent-specific behavior is composed
above the protocol and kernel-host layers.

## Solution structure

| Path | Role |
|---|---|
| `Maieutics.Jupyter.Shared` | Reusable, transport-independent Jupyter wire models and serialization |
| `Maieutics.Jupyter.Client` | Reusable Jupyter client, protocol session, NetMQ transport, and local kernel manager |
| `Maieutics.Jupyter.Kernel` | Reusable server-side Jupyter host and NetMQ transport |
| `Maieutics.Jupyter.Tests` | Jupyter unit, transport, interoperability, and product integration tests |
| `Maieutics.Agent` | Jupyter-independent Agent facade, run lifecycle, transcript, and tool runtime |
| `Maieutics.Agent.Tests` | Agent runtime unit tests |
| `Maieutics` | NativeAOT executable composition root, configuration, providers, and Agent-to-Jupyter adapter |

`Maieutics.Agent` is currently an internal, non-packable product assembly rather than a supported Agent SDK. Its public
boundary stays provider- and host-neutral so independent use can be evaluated later. Product-specific provider and
Jupyter adapter namespaces remain in the executable until they have an independent consumer, publication target,
deployment boundary, target framework, or dependency boundary.

Do not assume that future Provider, Tool, Notebook, Execution, Worker, Extension, or Persistence projects already
exist. Preserve those conceptual boundaries without creating assemblies merely to represent namespaces.

## Dependency direction

The reusable Jupyter dependency rule is:

```text
Maieutics.Jupyter.Shared
    ^
    |-- Maieutics.Jupyter.Client
    `-- Maieutics.Jupyter.Kernel
```

`Client` and `Kernel` must never reference each other.

The Agent composition direction is:

```text
IChatClient provider adapters
    |
    v
Maieutics.Agent internal orchestration
    |
    v
Maieutics.Agent facade
    ^
    |-- tool adapters
    `-- persistence adapters

Maieutics executable
    |-- product-specific provider wiring
    `-- Agent-to-Jupyter adapter
            |-- Maieutics.Agent
            `-- Maieutics.Jupyter.Kernel
```

The Agent runtime must not depend on Jupyter. Jupyter libraries must not depend on Agent concepts.
Microsoft.Extensions.AI function orchestration is an internal implementation dependency of `Maieutics.Agent`;
provider-specific types must not cross into Jupyter, provider-neutral public contracts, Deno IPC, worker protocols, or
persisted formats.

Do not solve a boundary problem with a reverse project reference. Put a small interface in the lower-level owning layer.

## Global invariants

Every change must preserve these invariants:

1. The kernel owns the authoritative live conversation history.
2. One ordinary executable cell corresponds to one submitted Agent turn.
3. Jupyter requests are correlated by message ID, channel, and expected reply type where applicable.
4. Shell execution is serialized unless explicit parallel semantics are designed and tested.
5. Control interrupt and shutdown remain responsive during shell execution.
6. IOPub output retains causal parent IDs and wire order.
7. Busy precedes parented output; reply precedes idle; idle is emitted from a `finally` path.
8. Completion follows protocol state, never fixed delays or guessed ordering.
9. Cancellation is cooperative through all layers and may escalate to owned child-process termination.
10. Raw NetMQ frames do not cross transport boundaries.
11. Provider SDK objects do not cross provider adapters.
12. Tool results remain structured until an output adapter renders them.
13. Notebook snapshot creation does not mutate the active session.
14. Binary data remains binary until its target Jupyter representation requires encoding.
15. Disposal stops owned loops, closes sockets, completes streams, and fails pending operations exactly once.
16. Backpressure never silently drops protocol messages or Agent events.
17. Unknown protocol messages cannot crash long-running receive loops.
18. Expected tool failures remain typed and recoverable within a turn unless the runtime itself is unusable.

Use structured concurrency. Every long-lived loop or child process needs an owner, cancellation source, completion task,
and deterministic disposal path. Observe background exceptions. Do not hold locks while awaiting provider streams, tool
calls, stdin, bounded queues, transport sends, or process exit. Multi-stage shutdown uses one total timeout budget.

## Compatibility boundaries

The Jupyter target is classic messaging protocol 5.5 and classic connection files. Readers tolerate unknown optional
fields and compatible older protocol announcements; writers avoid unsupported fields. Unsupported capabilities return
protocol-valid errors or fallback statuses. CurveZMQ and unsupported signature schemes fail explicitly.

Unless explicitly requested, do not add history, comm, debug, subshell, Jupyter 5.6 registration, automatic reconnect,
remote provisioners, or another ZeroMQ implementation.

The canonical Agent transcript is local and provider-neutral. Provider-side conversation state must not silently replace
it. Disable provider storage where supported. Do not introduce Agent Framework Workflows, A2A, AG-UI, Durable Task, MCP,
shell tools, or persistence providers without a concrete requirement and architecture review.

The intended TypeScript runtime is a supervised real `deno jupyter` child connected through the reusable Client.
Independent Deno extensions use a separate versioned IPC protocol; they are not Agent tools or MCP servers and do not
reuse the Jupyter connection merely for permissions. Treat Deno execution as privileged, allowlist its environment, and
do not describe it as an untrusted-code sandbox.

The live in-memory session is authoritative. Notebook snapshots and optional runtime state are separate persistence
forms. Version persisted runtime and IPC formats, tolerate unknown fields, and document migrations for breaking changes.

## Security and errors

- Verify HMAC signatures whenever a connection key is configured.
- Never log provider secrets, authorization headers, HMAC keys, or complete connection-file credentials.
- Treat notebook input, model output, tool arguments, and tool output as untrusted.
- Execute cell text only through an explicitly selected runtime or tool.
- Validate tool arguments and enforce filesystem, workspace, network, process, and environment policy inside tools.
- Allowlist child-process environment variables.
- Bound payloads, queues, retained logs, history, and persisted data.
- Redact secrets before persistence where practical.
- Never treat untrusted HTML as trusted merely because it appears in a MIME bundle.
- Use typed failures at subsystem boundaries. Expected request failures must not kill receive loops; fatal failures must
  cancel the owner, fail dependents consistently, emit protocol-valid errors where possible, and exit non-zero when the
  executable cannot recover.
- Never swallow exceptions from background tasks.

## Public API and coding style

- Enable nullable reference types.
- Prefer immutable records for protocol values, messages, events, and results.
- Prefer `Task` for request/reply and `IAsyncEnumerable<T>` for streams; use `ValueTask` only when justified.
- Accept `CancellationToken` on potentially blocking asynchronous operations.
- Keep constructors side-effect free; use asynchronous factories for asynchronous startup.
- Avoid global mutable state and service locators.
- Keep dependency injection in the executable composition root.
- Use explicit registries for dynamic providers and tools instead of mutating DI registrations at runtime.
- Public APIs require XML documentation.
- Use explicit ownership for every disposable resource.
- Use source-generated serialization on NativeAOT and protocol paths.
- Keep `JsonElement` as a compatibility or structured-data escape hatch, not the primary domain model.
- Comment only non-obvious protocol or lifecycle assumptions.
- Keep changes scoped to the owning layer and its tests.

## Scoped instructions

Rules apply cumulatively from this file down to the nearest child `AGENTS.md`.

| Scope | Local instructions |
|---|---|
| Jupyter wire types | `Maieutics.Jupyter.Shared/AGENTS.md` |
| Jupyter client | `Maieutics.Jupyter.Client/AGENTS.md` |
| Jupyter kernel host | `Maieutics.Jupyter.Kernel/AGENTS.md` |
| Agent runtime | `Maieutics.Agent/AGENTS.md` |
| Executable composition | `Maieutics/AGENTS.md` |
| Runtime configuration | `Maieutics/Configuration/AGENTS.md` |
| Provider adapters | `Maieutics/Providers/AGENTS.md` |
| Agent-to-Jupyter adapter | `Maieutics/Jupyter/AGENTS.md` |
| Agent tests | `Maieutics.Agent.Tests/AGENTS.md` |
| Jupyter and product integration tests | `Maieutics.Jupyter.Tests/AGENTS.md` |

Project-local reusable guidance belongs under `.agents/skills/<skill-name>/SKILL.md`. Skills describe domain practices
that apply across multiple project folders; `AGENTS.md` files describe ownership and constraints for their directory.
When a scoped file references a skill, follow both. Do not duplicate a skill verbatim into multiple `AGENTS.md` files.

| Skill | Use for |
|---|---|
| `.agents/skills/maieutics-jupyter-protocol/SKILL.md` | Wire DTOs, Client/Kernel protocol behavior, NetMQ channels, output ordering, cursors, and Deno interoperability |
| `.agents/skills/maieutics-agent-runtime/SKILL.md` | Sessions, runs, transcripts, tools, providers, profiles, capabilities, and Agent-to-Jupyter semantics |
| `.agents/skills/maieutics-structured-concurrency/SKILL.md` | Cancellation, channels, backpressure, owner loops, disposal, processes, and shutdown |
| `.agents/skills/maieutics-dotnet-testing/SKILL.md` | xUnit v3, FluentAssertions, deterministic integration tests, Deno, process tests, and NativeAOT verification |

## Change workflow

Before editing:

1. Identify the owning layer and read its nearest `AGENTS.md`.
2. Verify dependency direction.
3. Locate existing tests and compatibility fixtures.
4. Determine whether public API, wire, IPC, or persisted-format compatibility changes.

When adding behavior:

1. Change the smallest owning abstraction.
2. Implement behind the correct boundary.
3. Adapt at integration boundaries without leaking lower-level types.
4. Add focused unit tests and externally visible integration or conformance coverage where appropriate.
5. Document non-obvious lifecycle or compatibility decisions.

Do not mix unrelated formatting, dependency upgrades, protocol refactors, provider behavior, persistence migrations, and
tool additions. Do not revert unrelated user changes in a dirty worktree.

## Verification

Standard repository acceptance:

```bash
dotnet test Maieutics.slnx
dotnet build Maieutics.slnx --no-restore -warnaserror
git diff --check
```

For transport, process lifetime, cancellation, or timing changes, run focused tests first. For wire, IPC, or persisted
formats, update and inspect representative fixtures. NativeAOT-affecting executable or provider changes also require the
supported RID publish check. Never report completion without naming relevant checks that were skipped.
