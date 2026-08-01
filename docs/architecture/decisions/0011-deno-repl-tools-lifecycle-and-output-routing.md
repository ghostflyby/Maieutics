# ADR 0011: Deno REPL Tool Lifecycle and Output Routing

Status: Accepted

Date: 2026-08-01

Refines: ADR 0003 and ADR 0005

## Context

ADR 0003 selected a real `deno jupyter` kernel for the stateful TypeScript REPL, while ADR 0005 reserved a future
worker boundary. The first product implementation also needs deterministic Agent tools, interpreter ownership, output
audiences, cancellation, and a local deployment policy.

## Decision

The executable exposes `repl_execute`, `repl_create`, `repl_list`, `repl_restart`, and `repl_close` as ordinary
Microsoft.Extensions.AI functions. Omitting `sessionId` from execute, restart, or close selects a reserved default
session. The default session starts lazily; explicitly created sessions start immediately.

Each logical REPL generation owns one `deno jupyter` operating-system process. Sessions are scoped to an Agent session,
capture the selected workspace root as their working directory at creation, and never share one Deno process. Restart
preserves the logical session ID, increments its generation, and clears Deno and display state.

The first implementation is local. It does not expose execution-target, worker, or isolation arguments. A future worker
selects and owns the complete stateful session rather than distributing individual executions between workers.

## Output audiences

Jupyter message types determine the audience:

| Deno output | Agent tool result | User notebook |
|---|---:|---:|
| stdout stream | yes | no |
| execute result | yes | no |
| display, display update, clear | counts only | yes |
| stderr stream | yes | yes |
| execution error | yes | yes |
| input request | no | yes, with the reply returned to Deno |
| execute input and status | no | no |

The model chooses whether data is user-visible by generating standard Deno code that calls `Deno.jupyter.display`.
Maieutics does not inject Deno globals or add audience flags. Display MIME data and metadata are never copied into the
model-facing tool result.

The active user-facing Jupyter execution is attached before the Agent run starts. `AgentToolStarted` is the presentation
barrier for the matching tool call. Normal presentation writes complete before the tool returns. Late display, update,
clear, stderr, and error events may be routed only while an Agent-run presentation sink remains active; late model-only
values never modify a completed tool result.

## Failure and security

Agent cancellation and execution timeout request an explicit Jupyter interrupt. If the execution does not finish within
the interrupt grace period, Maieutics shuts down and ultimately terminates the process tree and marks the session
faulted. Recovery is explicit; code is never retried or replayed automatically.

Non-critical malformed presentation messages do not terminate the Jupyter Client or fault the REPL. In particular, a
display update without a usable `transient.display_id` remains ordered with its parent execution, is counted as skipped,
and is not published to the notebook. Malformed reply, status, and input messages remain terminal because request
correlation, completion, or user interaction cannot proceed safely without them.

`deno jupyter` remains privileged code execution rather than an untrusted-code sandbox. The child starts with an
allowlisted environment that excludes model-provider credentials. Output, event, session, and timeout limits are fixed
at process startup.

## Consequences

- Agent Core remains independent of Jupyter and Deno.
- The executable becomes a production consumer of both reusable Jupyter Client and Kernel libraries without adding a
  reference between those libraries.
- Standard Jupyter display behavior is the first-stage Deno presentation API.
- Remote workers, sandbox permissions, extension IPC, artifacts, and component APIs remain separate future work.
