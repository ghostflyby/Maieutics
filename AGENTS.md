# AGENTS.md

## Project purpose

This repository is building a notebook-native LLM agent exposed as a Jupyter kernel.

The intended user model is:

- one executable notebook cell is one submitted agent turn;
- the kernel owns the authoritative live conversation state;
- assistant text, tool activity, rich values, errors, and input requests are emitted through Jupyter messages;
- existing notebook frontends provide editing, execution, history, and rich rendering;
- an `.ipynb` file is a portable snapshot of the interaction, not the runtime database.

The generic Jupyter implementation must remain reusable independently of the agent runtime. Agent-specific behavior must
be composed above the Jupyter protocol and kernel-host layers.

## Current solution

The repository currently contains:

- `Maieutics.Jupyter.Shared`
- `Maieutics.Jupyter.Client`
- `Maieutics.Jupyter.Kernel`
- `Maieutics.Jupyter.Tests`
- `Maieutics.Agent`
- `Maieutics.Agent.Tests`
- `Maieutics`

The first three projects are reusable Jupyter libraries. `Maieutics.Jupyter.Tests` owns their automated tests and
cross-layer Agent/Jupyter integration tests. `Maieutics.Agent` owns the Jupyter-independent Agent facade and runtime;
`Maieutics.Agent.Tests` owns its unit tests. `Maieutics` is the executable composition root and contains the
product-specific Agent-to-Jupyter adapter and model-provider wiring.

`Maieutics.Agent` is currently an internal product assembly and is not packed as a supported Agent SDK. Its boundary is
kept provider- and host-neutral so that a future independent consumer can validate whether it should become one.

`Maieutics.Providers.OpenAI` is an internal namespace in the executable that wires OpenAI Responses and Chat
Completions into `IChatClient`. `Maieutics.Jupyter` is an executable-owned namespace that adapts Agent runs to the
user-facing kernel. These are logical product modules, not independently published assemblies. Responses is the default
OpenAI flavor. Both flavors currently send `store: false`; provider-side conversation IDs are not used as canonical
session state.

Do not assume that future Provider, Tool, Notebook, Execution, Worker, Extension, or Persistence projects already exist.
The boundaries described below are target architectural boundaries. They may initially be represented by namespaces and
internal abstractions, but dependency direction must be preserved from the first implementation.

Do not place reusable protocol, runtime, tool, or persistence logic in `Maieutics`. The executable may contain
product-specific provider construction, Agent-to-Jupyter adaptation, configuration, dependency registration, startup,
shutdown, and process-level hosting. Extract an adapter into a project only when it has an independent consumer,
publication target, deployment boundary, target framework, or dependency boundary that cannot be expressed safely as
an executable-owned namespace.

## Dependency direction

The current hard dependency rule is:

```text
Maieutics.Jupyter.Shared
    ^
    |-- Maieutics.Jupyter.Client
    `-- Maieutics.Jupyter.Kernel
```

`Client` and `Kernel` must never reference each other.

The target Agent dependency direction is conceptually:

```text
IChatClient provider adapters
    |
    v
Maieutics.Agent internal Microsoft Agent Framework orchestration
    |
    v
Maieutics.Agent public facade
    ^
    |-- tool adapters
    `-- persistence adapters

Maieutics executable composition root
    |-- product-specific IChatClient provider wiring
    `-- Agent-to-Jupyter adapter
            |-- Maieutics.Agent
            `-- Maieutics.Jupyter.Kernel
```

The exact future project names are not prescribed by this document. The boundaries are.

The agent runtime must not depend on Jupyter. The Jupyter protocol layer must not depend on agent concepts. The adapter
that hosts the runtime behind `JupyterKernelHost` is the only place that should understand both domains.

Microsoft Agent Framework is an internal implementation dependency of `Maieutics.Agent`. Framework types must not cross
into Jupyter libraries, notebook presentation contracts, Deno extension IPC, worker protocols, or persisted Maieutics
formats.

Do not solve a boundary problem by adding a reverse project reference. Prefer a small interface in the lower-level
owning layer.

## Maieutics.Jupyter.Shared

`Maieutics.Jupyter.Shared` is the transport-independent Jupyter wire-protocol foundation.

Allowed responsibilities:

- classic Jupyter connection-file models and validation;
- channel names;
- message headers and parent headers;
- wire envelopes and routing identities;
- multipart frame encoding and decoding;
- HMAC signing and verification;
- protocol message DTOs;
- MIME bundles and binary buffers;
- JSON serialization contracts;
- message identifiers and correlation primitives;
- protocol exceptions and compatibility helpers.

Forbidden responsibilities:

- NetMQ socket creation or lifecycle;
- client request orchestration;
- kernel request dispatch;
- process launching;
- agent execution;
- provider, tool, or persistence concepts;
- notebook UI state reduction.

All Jupyter wire-format types belong here. Preserve routing identities in the wire envelope rather than mixing them into
semantic message content.

Use source-generated `System.Text.Json` metadata for protocol DTOs. Avoid reflection-heavy serialization in protocol hot
paths.

Prefer immutable records and explicit field names. Preserve unknown fields where forward compatibility or round-tripping
requires it. Keep binary buffers outside JSON.

Validate required fields, frame counts, delimiters, signature schemes, and HMAC at the wire boundary. Unsupported
CurveZMQ or signature schemes must fail explicitly rather than degrade silently.

## Maieutics.Jupyter.Client

`Maieutics.Jupyter.Client` is a reusable client for connecting to Jupyter kernels. It must remain usable without the
agent runtime or `Maieutics.Jupyter.Kernel`.

The project has three internal layers.

### Client transport

The transport layer owns raw connection and socket behavior:

- load validated connection information;
- create shell, control, stdin, IOPub, and heartbeat sockets;
- serialize and deserialize `JupyterWireMessage` values;
- send outgoing wire messages;
- expose one read-only incoming stream;
- own socket thread affinity, polling, shutdown, and disposal;
- surface backpressure and transport failure.

All NetMQ sockets must be created, polled, used, and disposed by their owning I/O thread. NetMQ-specific types must not
escape the transport implementation.

The transport does not own request/reply semantics, execution aggregation, or UI concepts.

### Client protocol

The protocol session owns Jupyter behavior above the raw transport:

- message ID generation;
- pending request registration and completion;
- reply matching by parent message ID, channel, and expected reply type;
- routing IOPub output to the corresponding execution;
- execution completion from protocol state, including reply and parented idle;
- stdin request tracking and parent-header preservation;
- late-output publication;
- cancellation, disconnect, and terminal-error propagation;
- global event fan-out.

Never correlate an execution by execution count. Execution count is display/history metadata, not a request identity.

Unknown messages must become controlled protocol events or errors according to their context. They must not crash the
receive loop.

### Public client API

The public API exposes stable .NET semantics:

- request/reply operations use `Task<TReply>`;
- streaming execution output uses `IAsyncEnumerable<T>`;
- broadcast event subscriptions use independent `IAsyncEnumerable<T>` streams;
- high-frequency internal sends may use `ValueTask` when justified;
- connection starts through an asynchronous factory, not constructor side effects.

`Channel<T>` is an internal implementation detail. Never expose a writable channel from a public API.

Events are allowed only in optional UI or traditional .NET adapter layers. They are not the core asynchronous
abstraction.

Execution output streams are single-consumer unless an API explicitly promises broadcast semantics. Global event
subscribers must not consume each other's events.

Local cancellation cancels local waiting. Kernel interruption is a separate, explicit manager or control-channel
operation.

## Maieutics.Jupyter.Kernel

`Maieutics.Jupyter.Kernel` is the reusable server-side Jupyter host. Kernel authors should implement application
capabilities without touching NetMQ or wire envelopes.

The kernel transport owns:

- ROUTER shell, control, and stdin channels;
- XPUB IOPub publication;
- REP heartbeat handling;
- routing identities and multipart framing;
- socket polling, thread affinity, and deterministic disposal;
- bounded incoming and outgoing transport queues.

`JupyterKernelHost` owns:

- request dispatch;
- shell serialization;
- independently responsive control handling;
- execution count;
- busy, idle, starting, and welcome publication;
- execution cancellation ownership;
- interrupt and shutdown behavior;
- conversion of application results and failures into Jupyter replies and outputs.

Application capability interfaces own only domain behavior such as execution, completion, inspection, and completeness
checks. They must not construct raw Jupyter frames or manage socket state.

For each shell request, preserve the externally observable order:

```text
busy -> handler and parented output -> reply -> idle
```

`idle` must be sent from a `finally` path when a request entered the busy state. Control requests must remain responsive
while a long shell execution is running.

Interrupt cooperatively cancels the active execution through an owned `CancellationTokenSource`. Child runtimes may be
terminated when cooperative cancellation exceeds the configured deadline.

Shutdown sends its reply before stopping the host.

## Jupyter compatibility

The current protocol target is the classic Jupyter messaging protocol 5.5 and classic connection files.

Compatibility rules:

- the local kernel host advertises the protocol version it actually implements;
- the client may accept older protocol versions announced by real kernels when the used messages are compatible;
- protocol compatibility behavior must have an automated test;
- readers should tolerate unknown optional fields;
- writers should avoid unstable or unsupported fields;
- unknown message types must not terminate transport receive loops;
- unsupported features must return protocol-valid errors or fallback statuses.

The current core includes execution, IOPub output, stdin, heartbeat, interrupt, shutdown, language services, and display
update semantics. Do not claim support for optional Jupyter features until their full Client/Shared/Kernel behavior is
tested.

Unless a task explicitly expands scope, do not introduce history, comm, debug, subshell, Jupyter 5.6 registration,
automatic reconnect, remote provisioners, or CurveZMQ as incidental work.

## Core invariants

Every change must preserve these invariants:

1. The kernel owns the authoritative live conversation history.
2. One executable cell corresponds to one submitted agent turn.
3. Requests are correlated by Jupyter message ID, channel, and reply type where applicable.
4. Shell execution is serialized unless explicit parallel semantics are designed and tested.
5. Control interrupt and shutdown do not wait behind shell execution.
6. IOPub output retains the causal parent message ID and wire order.
7. Busy is published before turn output; idle is published after completed turn output.
8. Completion is based on protocol state, never fixed delays or guessed ordering.
9. Cancellation is cooperative through all layers and may escalate to child-process termination.
10. Raw NetMQ frames do not cross the transport boundary.
11. Provider-specific objects do not cross provider adapters.
12. Tool results stay structured until an output adapter renders them.
13. Notebook snapshot creation does not mutate the active session.
14. Binary data remains binary until the target Jupyter representation requires encoding.
15. Disposal stops owned loops, closes sockets, completes streams, and fails pending operations once.
16. Backpressure never silently drops protocol messages.
17. Unknown protocol messages cannot crash long-running receive loops.
18. A tool failure remains a typed failure within the current turn unless the runtime itself is unusable.

## Concurrency and lifetime

Use structured concurrency. Every long-lived loop or child process must have a clear owner, cancellation source,
completion task, and disposal path.

The kernel host owns channel receive loops, shell dispatch, control dispatch, IOPub publication, heartbeat, and
child-runtime supervision.

The client transport owns its socket thread and polling lifetime. The client protocol session owns routing and pending
operation lifetimes.

Avoid fire-and-forget tasks. Background task exceptions must be observed and propagated through the owning component's
terminal state.

Use bounded queues whenever producers can outrun consumers. Define queue capacity and overflow behavior. A full protocol
queue must terminate the affected connection with a typed backpressure failure; it must not discard messages.

Do not hold locks while awaiting:

- provider streams;
- tool invocation;
- stdin replies;
- bounded queue capacity;
- transport sends;
- child-process exit.

Terminal transitions such as startup cancellation, socket-owner failure, backpressure, disconnect, and concurrent
disposal must complete at most once. All pending sends, pings, requests, executions, and streams must observe the same
terminal cause promptly.

Use one total timeout budget for multi-stage shutdown flows. Do not reset the full timeout independently for request,
process exit, forced termination, and cleanup.

## Agent runtime target

The agent runtime is independent of Jupyter and owns:

- authoritative conversation history;
- conversion of cell input into a turn request;
- context construction and compaction;
- provider selection and capability negotiation;
- tool registration and invocation;
- provider tool-call continuation;
- cancellation and turn state;
- normalized semantic output events;
- replay and snapshot models.

`Microsoft.Extensions.AI.IChatClient` is the primary model-provider abstraction. OpenAI Chat Completions, OpenAI
Responses, Anthropic, Google, and future providers should be integrated as dedicated `IChatClient` implementations or
adapters. Do not introduce a parallel `IModelClient` that merely mirrors `IChatClient`.

`Microsoft.Agents.AI.AIAgent`, `ChatClientAgent`, `AgentSession`, response updates, context providers, and history
providers may be used internally. The stable Maieutics boundary remains its own `IAgentSession`, `IAgentRun`, events,
transcript, tool, and capability contracts.

Maieutics, not Agent Framework, owns these externally observable semantics:

- starting a run reserves the session immediately;
- one mutating run is active per session;
- input, response, history, event, and artifact limits;
- cancellation and terminal completion;
- normalized event types and correlation IDs;
- validation and atomic commit of the canonical transcript.

`StartTurnAsync` starts work independently of event enumeration. `IAgentRun.Events` is a bounded single-consumer stream;
producers wait for capacity, and a caller that stops consuming must cancel or dispose the run. `Completion` is the only
terminal success or failure boundary and must not complete before the session reservation is released.

Framework history completion is only a staging point. Use a Maieutics-owned staging `ChatHistoryProvider`: load committed
history before invocation, stage request and response messages after framework completion, and promote them only after
the outer run validates the complete result. Empty or unsupported responses, policy rejection, output limits,
cancellation, provider failure, and aborting tool failure must discard the staged turn.

Pass an explicitly composed `IChatClient` pipeline to `ChatClientAgent`. Initially use `UseProvidedChatClientAsIs` so
default function invocation, approval, message injection, or per-service-call history behavior cannot acquire ownership
without an explicit design and tests.

The runtime should emit semantic events such as assistant text deltas, completed messages, permitted reasoning
summaries, tool-call lifecycle events, rich values, warnings, typed failures, and input requests.

The Agent-to-Jupyter adapter maps these semantic events to Jupyter messages. Runtime code must not know about NetMQ,
routing identities, multipart frames, or notebook frontend state.

`IChatClient` provider adapters must:

- translate `ChatMessage`, `AIContent`, tools, and options to provider APIs;
- preserve streaming content needed by the Agent runtime;
- preserve opaque provider identifiers needed for continuation;
- report capabilities and token usage;
- map provider failures into typed runtime errors.

Provider response objects, SDK types, and authentication types must not leak into the conversation model, tool runtime,
or Jupyter host.

Use a Maieutics-owned capability descriptor. Do not assume every provider supports tools, reasoning summaries,
multimodal inputs, continuation IDs, server-side history, or identical streaming behavior.

The canonical transcript is local and provider-neutral. The default model profile must not allow a returned provider
conversation ID to silently disable local history. Disable provider-side conversation storage where supported. Any
future provider-managed acceleration mode must independently retain the canonical transcript and define replay, expiry,
provider switching, and recovery.

Do not adopt Agent Framework Workflows, hosting, A2A, AG-UI, Durable Task, MCP, shell tools, or persistence providers
without a concrete requirement and an independent architecture review.

## Tool runtime target

Tools are invoked by the agent runtime, not directly by the notebook frontend.

The tool subsystem owns:

- descriptors and argument schemas;
- argument validation;
- invocation dispatch;
- cancellation;
- structured and MIME results;
- bounded execution logs;
- approval and policy hooks where required.

Do not flatten a typed or rich result to plain text before the Jupyter output boundary. Preserve structured values and
attach a useful `text/plain` fallback when rendering custom MIME.

Treat tool input and output as untrusted. Apply explicit filesystem, workspace, network, process, and environment
policies in the tool implementation.

## Deno runtime target

The primary stateful TypeScript REPL is a real `deno jupyter` kernel. The .NET process remains the user-facing Jupyter
kernel and connects to Deno through `Maieutics.Jupyter.Client`. One Agent session owns one Deno REPL session by default.

The Deno adapter owns process startup, portable kernelspec resolution, connection-file lifecycle, readiness, heartbeat,
execution, stdin, interrupt, shutdown, forced termination, and cleanup. It maps Deno Client outputs into Maieutics model
content and ordered notebook presentation events. Raw Deno Jupyter headers, identities, and wire messages never cross the
adapter.

Independent Deno script extensions use a separate versioned IPC protocol for REPL contributions and lifecycle hooks.
They are not Agent tools, are not MCP servers, and do not share the Deno Jupyter connection merely to reuse permissions.

Pass only explicitly allowlisted environment variables to Deno processes. Do not inherit the complete kernel
environment by default. Treat `deno jupyter` as privileged execution, not an untrusted-code sandbox.

The existing real Deno Jupyter kernel test remains an interoperability test for the reusable Client. Its kernelspec is a
test asset at `Maieutics.Jupyter.Tests/TestData/kernels/deno/kernel.json`, resolved from the test output directory.
Tests must not depend on a user-specific kernelspec path or absolute Deno executable path.

## Persistence target

The in-memory agent session is authoritative while the kernel is alive.

Persistence has two distinct forms:

1. Notebook snapshot: source cells, visible outputs, execution metadata, and portable session metadata.
2. Optional runtime state: provider continuation identifiers, compacted context, tool state, and data unsuitable for
   notebook storage.

Do not assume replaying visible notebook cells reconstructs an identical provider-side session.

Version serialized runtime state and Deno IPC formats. Readers should tolerate unknown fields. Breaking format changes
require migration notes and updated fixtures.

Persistence adapters must be replaceable without changing the runtime conversation model.

## Output mapping

The Agent-to-Jupyter adapter should use these mappings:

| Agent event                | Jupyter representation                            |
|----------------------------|---------------------------------------------------|
| streamed assistant text    | `stream` or incrementally updated `display_data`  |
| completed assistant answer | final `display_data` or `execute_result`          |
| rich value                 | `display_data`                                    |
| updated rich value         | `update_display_data`                             |
| tool progress              | `stream`, `display_data`, or custom MIME          |
| tool result                | structured MIME with `text/plain` fallback        |
| input request              | `input_request` on stdin                          |
| execution failure          | parented `error` plus an appropriate reply status |
| kernel state               | `status`                                          |
| clear request              | `clear_output`                                    |

Preserve `display_id`, transient metadata, unknown metadata fields, and update ordering. The core Client reports ordered
output events and does not reduce them into final UI state. Notebook frontends own `clear_output.wait` behavior and
cross-execution display reduction.

Never expose private chain-of-thought. Only provider-supported reasoning summaries that are explicitly allowed by
product policy may be emitted or persisted.

## Error model

Use typed failures at subsystem boundaries. At minimum distinguish:

- malformed protocol message;
- invalid signature or unsupported security scheme;
- unsupported message or capability;
- transport disconnect and backpressure;
- provider request or stream failure;
- tool validation or execution failure;
- user cancellation and interrupt;
- kernel shutdown;
- child-runtime failure;
- persistence failure.

Expected request failures must not terminate receive loops. Fatal component failures must cancel the owner lifetime,
complete dependent queues and streams, fail pending operations, publish a protocol-valid error where possible, and make
the executable exit non-zero when recovery is impossible.

Never swallow exceptions from background tasks.

## Security

- Verify HMAC signatures whenever the connection key is configured.
- Never log provider secrets, authorization headers, HMAC keys, or complete connection-file credentials.
- Treat notebook cell input, model output, tool arguments, and tool output as untrusted data.
- Execute cell text only through an explicitly selected runtime or tool.
- Validate tool arguments before invocation.
- Enforce workspace and filesystem boundaries inside tools.
- Allowlist child-process environment variables.
- Bound output sizes, queue capacities, retained logs, and persisted payloads.
- Redact secrets before persistence where practical.
- Do not render untrusted HTML as trusted content merely because it arrived in a MIME bundle.

## Testing

Use xUnit v3 and `Microsoft.NET.Test.Sdk`. Use FluentAssertions or Shouldly when they make protocol assertions clearer.

All tests must be deterministic and bounded by cancellation or a deadline. Do not use fixed sleeps as protocol
synchronization. Do not use fixed TCP ports.

### Shared tests

Cover frame layout, signatures, JSON names, source-generated DTO round trips, unknown fields, binary buffer ordering,
connection-file validation, cursor conversion, MIME bundles, display IDs, and malformed input.

### Client transport tests

Cover socket thread ownership, all five channels, shared and distinct identities as required by the protocol, heartbeat,
bounded queue failure, startup cancellation, disconnect, and concurrent disposal.

### Client protocol tests

Cover parent message correlation, channel and reply-type validation, reply/idle ordering permutations, stdin parent
headers, output ordering, late output, concurrent requests, cancellation, and terminal failure propagation.

### Kernel tests

Cover busy/reply/idle ordering, shell serialization, independently responsive control, heartbeat during long execution,
interrupt, shutdown, silent execution, stdin, language-service providers, display/update/clear output, and exception
conversion.

### Agent tests

Agent runtime unit tests belong in `Maieutics.Agent.Tests`; Jupyter tests retain only adapter, kernel, and process-level
integration coverage.

Use deterministic fake providers and tools. Cover plain and streamed answers, one or multiple tool calls, tool failure,
input requests, cancellation, context compaction, provider capability differences, rich output mapping, and snapshot
creation. Unit tests must not require a real model provider or external network access.

When Microsoft Agent Framework is involved, also cover early stream disposal, staging and commit, empty and unsupported
responses, provider conversation-ID conflicts, one-run enforcement, and framework upgrade compatibility. A partially
consumed or failed run must leave committed history unchanged.

### Integration and conformance tests

Use dynamically allocated loopback TCP ports or isolated IPC-safe endpoints. Socket tests that share NetMQ process state
must be placed in the non-parallel test collection.

Keep both forms of integration coverage:

- self-hosted `Maieutics.Jupyter.Kernel` connected to `Maieutics.Jupyter.Client`;
- real portable Deno kernel connected through the copied test kernelspec and `deno` from `PATH`.

The executable's NativeAOT smoke coverage must exercise the selected Agent Framework core path. Every newly adopted
framework module or provider adapter requires a warning-free publish test for the supported runtime identifier. Do not
work around trimming or dynamic-code failures with broad warning suppression.

Integration failures should identify the failed stage, such as process start, readiness, heartbeat, request send, reply,
output completion, shutdown, or cleanup.

Do not make tests pass by adding protocol timing guesses such as `Task.Delay(250)`.

## Public API and coding style

- Enable nullable reference types.
- Prefer immutable records for protocol values, messages, events, and results.
- Prefer `Task` for request/reply operations and `IAsyncEnumerable<T>` for streams.
- Use `ValueTask` only for measured or clearly high-frequency paths.
- Accept `CancellationToken` on potentially blocking asynchronous operations.
- Keep constructors side-effect free; use async factories for asynchronous startup.
- Avoid global mutable state and service locators.
- Keep dependency-injection registration in the executable composition root.
- Use explicit registries for dynamic providers and tools instead of mutating DI registrations at runtime.
- Do not expose Microsoft Agent Framework types from Maieutics public APIs merely because the runtime uses them
  internally.
- Public APIs require XML documentation.
- Comment non-obvious protocol assumptions with the relevant Jupyter concept.
- Use explicit ownership for every disposable resource.
- Avoid exposing `JsonElement` as the primary domain model; use it as a compatibility escape hatch.
- Avoid inheritance structures that make protocol serialization fragile.
- Keep changes scoped to the owning layer and its tests.

## Change workflow

Before editing:

1. Identify the owning layer.
2. Verify dependency direction.
3. Locate existing tests and compatibility fixtures.
4. Determine whether wire, public API, IPC, or persisted-format compatibility is affected.

When adding behavior:

1. Add or update the smallest owning model or abstraction.
2. Implement the behavior behind the correct boundary.
3. Adapt it at integration boundaries without leaking lower-level types.
4. Add focused unit tests.
5. Add socket, integration, golden, or conformance coverage for externally visible behavior.
6. Document lifecycle or compatibility decisions that are not obvious from code.

Do not mix unrelated formatting, dependency upgrades, protocol refactors, provider behavior, persistence migrations, and
tool additions in one change.

Do not revert unrelated user changes in a dirty worktree.

## Verification

The standard repository acceptance commands are:

```bash
dotnet test Maieutics.slnx
dotnet build Maieutics.slnx --no-restore -warnaserror
git diff --check
```

For transport, process lifetime, cancellation, or timing changes, run the relevant focused tests before the full
solution test. For wire, IPC, or persisted formats, update and review representative fixtures.

Do not report work as complete when a relevant check was skipped without stating exactly what was not run and why.

## Default decisions

When no more specific design has been approved, use these defaults:

- keep the three existing Jupyter assemblies and their dependency boundaries;
- keep Client transport, protocol, and public API as layers inside the Client assembly;
- keep agent runtime concepts independent of Jupyter;
- use `IChatClient` as the provider boundary and keep Microsoft Agent Framework behind the Maieutics Agent facade;
- keep one-run enforcement, limits, cancellation, and final transcript commit owned by Maieutics;
- treat framework history completion as staged data until the outer run commits it;
- keep the executable as a composition root;
- keep product-specific provider wiring and Agent-to-Jupyter adaptation as executable-owned namespaces until they have
  an independent consumer or publication boundary;
- serialize shell execution and keep control independently responsive;
- use NetMQ as the only ZeroMQ implementation;
- use source-generated `System.Text.Json` for protocol serialization;
- use bounded, cancellation-aware asynchronous streams;
- use MIME bundles with a useful text fallback for rich values;
- keep the live kernel session authoritative and `.ipynb` as a portable snapshot;
- run the primary TypeScript REPL as a supervised `deno jupyter` child connected through Jupyter Client;
- keep independent Deno extension hooks behind their own versioned IPC and outside the Agent tool registry;
- use deterministic fakes before real external services;
- preserve real Deno kernel coverage as a Client interoperability test.

If a requested change conflicts with these defaults, make the tradeoff explicit in code, tests, or the change
description.
