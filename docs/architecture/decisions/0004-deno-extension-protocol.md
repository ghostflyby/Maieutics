# ADR 0004: Out-of-Process Deno Extensions and Lifecycle Hooks

Status: Accepted

Date: 2026-07-16

## Context

Maieutics needs independently deployable Deno scripts that can extend the TypeScript REPL or react to kernel, Agent,
turn, and REPL lifecycle events. Some extensions may resemble host hooks: they observe an event, contribute setup or
cleanup behavior, validate or transform an operation, or publish diagnostics and notebook presentation events.

These extensions are not model tools. Their APIs are not advertised in model tool schemas and the LLM does not invoke
them directly. MCP therefore does not describe the intended ownership or invocation model.

## Decision

Out-of-process Deno extensions use a dedicated, versioned Maieutics extension protocol over an owned IPC connection.
The protocol is independent of Agent tool calling, MCP, Jupyter wire messages, and the distributed worker protocol.

The first local transport should use child-process stdio. Unix domain sockets, named pipes, or worker-proxied transports
may be added behind the same protocol. The exact encoding and framing are deferred, but protocol semantics are fixed by
this decision.

## Invocation model

The host invokes extensions in response to registered lifecycle events or extension points. An extension cannot enqueue
an arbitrary Agent tool call merely because it is connected.

The protocol must distinguish three forms of participation:

- Notification: observe an event without changing the operation.
- Hook: return a bounded decision such as continue, replace, reject, or contribute additional data.
- Contribution: register a stable capability such as REPL initialization content, module resolution, output processing,
  diagnostics, or cleanup behavior.

Hooks that may modify behavior are explicitly declared and ordered. Observation does not imply mutation authority.

## Initial extension points

The architecture reserves names and correlation semantics for at least:

```text
Host
    host.starting
    host.started
    host.stopping

Agent session
    session.created
    session.disposing

Agent turn
    turn.starting
    turn.completed
    turn.failed

Deno REPL
    repl.starting
    repl.started
    repl.before_execute
    repl.after_execute
    repl.restarting
    repl.stopping

Notebook presentation
    presentation.before_publish
    presentation.published
```

This list establishes event categories rather than a promise that every hook is implemented in the first extension
release. New events are versioned additions and unknown events are ignored unless declared required by the extension.

## REPL extension capabilities

An extension may contribute behavior such as:

- initialization or preload TypeScript executed when a Deno REPL starts;
- import-map, module-resolution, or library registration information;
- setup and teardown associated with a REPL session;
- validation or transformation before a REPL execution;
- inspection or normalization of a REPL result;
- bounded diagnostics, artifacts, or notebook presentation events;
- restart and failure cleanup.

An extension does not receive direct access to Jupyter sockets. Rich output is expressed through the shared notebook
presentation contract and is mapped to Jupyter messages by the user-facing adapter.

## Separation from Agent tools

Extension registration and Agent tool registration are independent registries.

- Extension APIs are not included in provider tool definitions.
- Extension hook names are not valid model tool names.
- Extension results do not automatically become model-visible content.
- Agent Core does not depend on extension protocol DTOs or extension process objects.
- A capability becomes model-callable only when a separate adapter explicitly exposes it as an Agent tool under normal
  schema, validation, policy, and approval rules.

This explicit adapter is the only supported bridge between the two systems.

## Protocol semantics

The extension protocol must represent:

- initialization, protocol version, extension identity, and capability negotiation;
- hook registration and contribution registration;
- invocation request, response, notification, and stream event;
- cancellation and deadline;
- typed failure and process-level terminal failure;
- correlation with kernel session, Agent run, REPL session, execution, and notebook display identity;
- bounded artifact and notebook presentation references.

The wire representation must not serialize arbitrary .NET runtime types. Human-readable output is not parsed as protocol
data.

## Ordering and failure policy

Hook ordering is deterministic. The host defines an order using configured priority followed by extension identity as a
stable tie-breaker. Parallel callback execution is opt-in and only valid for observation hooks that cannot mutate shared
state.

Each hook registration declares or inherits:

- timeout;
- whether it is observational or mutating;
- whether failure is ignored, fails the current operation, or disables the extension;
- whether retry is permitted;
- whether it may emit presentation events or artifacts.

Recursive re-entry into the same hook chain is rejected unless the extension point explicitly permits it. Extension
failure must not silently change operation behavior.

## Extension manifest

Process launch and security information lives in a Maieutics extension manifest:

```text
Extension ID and version
Required extension protocol range
Command and arguments
Registered hook and contribution capabilities
Deno permission grants
Environment-variable allowlist
Working-directory and execution-target policy
Startup, callback, and shutdown deadlines
Failure and restart policy
Resource and output limits
```

The manifest is declarative. Command arguments are passed without shell interpolation.

## Process and security rules

- stdout is reserved for extension protocol messages.
- stderr is captured as bounded diagnostic logging.
- The host owns process startup, handshake, health, cancellation, shutdown, forced termination, and cleanup.
- Environment variables are allowlisted; provider credentials and Jupyter connection secrets are not inherited by
  default.
- Deno permissions are explicit and minimal. Extensions do not inherit the `deno jupyter` all-permissions model.
- Hook inputs are minimized to the declared capability and do not include the complete transcript by default.
- Extension output is treated as untrusted.

## Distributed execution

An extension that modifies or observes a target-local Deno REPL normally runs on the same execution target as that REPL.
The worker may proxy the versioned extension protocol and lifecycle events, but worker messages and extension messages
remain distinct contracts.

Local control-plane hooks may run locally. Target placement is explicit in the extension manifest and is not inferred
from a filesystem path.

## Consequences

- Extensions can enhance the Deno REPL and host lifecycle without becoming LLM-callable tools.
- Hook ordering, mutation authority, cancellation, and failure policy are explicit.
- Extension authors do not need to implement MCP or Jupyter protocols.
- A future tool adapter may intentionally expose one extension capability without coupling the registries.
- Extension transport and encoding can evolve without changing Agent Core or hook semantics.

## References

- Deno permissions: https://docs.deno.com/runtime/reference/permissions/

