# ADR 0023: Custom Web Frontend Protocol and the VSCode Notebook Frontend

Status: Draft

Date: 2026-09-04

## Context

Maieutics exposes its Agent through the classic Jupyter messaging protocol 5.5.
The pairing has two structural mismatches:

1. **Execution queue modeling.** Jupyter's shell channel imposes a serialized
   request queue with busy/idle status semantics. The Agent runtime already
   owns a stricter serialization point (`AgentSession` reserves one run per
   session), so the protocol-level queue models the wrong owner and forces the
   adapter to translate agent concurrency errors into protocol statuses.
2. **Frontend-blind history.** The Jupyter protocol has no session query or
   replay surface. The kernel owns the authoritative live conversation
   (invariant 1), but every frontend must reconstruct history from its own
   notebook snapshot; a reconnecting or second frontend cannot catch up.

Supporting arbitrary Jupyter frontends also drags the widest adaptation
surface the product has: wire DTOs, ZMQ transports, connection files,
kernelspecs, and the shell/control/iopub state machine — none of which the
agent chat model needs.

Meanwhile the executable already hosts an ASP.NET Core Kestrel
`WebApplication` that carries three HTTP+WebSocket IPC planes (the REPL
control bus, the REPL eval channel, and the REPL output channel), and
invariant 27 already mandates that inter-process communication be designed as
an internal web application API surface. The Agent runtime
(`Maieutics.Agent`) has zero Jupyter dependencies and exposes exactly the
shape a streaming frontend needs (`IAgentSession`, `IAgentRun`,
`IAsyncEnumerable<AgentEvent>` with strictly increasing run-local sequence
numbers, a terminal `Completion` task).

## Decision

1. **A custom web protocol becomes the primary frontend protocol.** HTTP
   request/reply for commands and WS for event streams, every endpoint with an
   explicit direction and a bounded payload (invariant 27). The protocol is
   versioned (`v1`), source-generated JSON on the NativeAOT path, and
   provider-neutral: no Jupyter type and no Microsoft.Extensions.AI type
   crosses the wire (invariant 11). The wire is defined in
   `docs/web-frontend-protocol.md`.
2. **History and reconnect are protocol features, not frontend chores.** The
   server retains a bounded replay buffer per run and the WS endpoint accepts
   `sinceSequence`; the transcript snapshot endpoint exposes the authoritative
   history. Backpressure disconnects instead of dropping (invariant 16); the
   client reconnects and resumes from its last observed sequence.
3. **The first frontend is a VSCode extension using the Notebook API**
   (`NotebookSerializer` + `NotebookController` + custom renderers) with a
   non-Jupyter kernel, storing a custom `.maieuticsnb` snapshot format.
   One ordinary notebook cell remains one submitted Agent turn (invariant 2).
   Snapshot save/load touches only the file; the live session is never
   mutated by snapshotting (invariant 13).
4. **The shared kernel control surface is extracted out of the Jupyter
   adapter.** Command parsing/execution, status capture/rendering, and
   completion move to a `Maieutics.Commands` domain consumed by every
   frontend, so command semantics cannot drift between frontends.
5. **Jupyter support exits in two steps.** First the web API and the extension
   ship while the Jupyter kernel keeps working (coexistence). Then the
   executable's Jupyter wiring is removed (`Maieutics/Jupyter/` adapter,
   `JupyterKernelHostedService`, the kernelspec, SIGINT-as-interrupt); the
   three `Maieutics.Jupyter.*` library projects are retained as reusable
   libraries with their own tests but have no product consumer. The Agent
   runtime never depended on Jupyter and is untouched throughout.
6. **The extension toolchain is pure Deno.** The extension lives in the
   `deno/` workspace; dependencies live in `deno.json` and the manifest
   `package.json` carries none. Shipped sources type-check with an ES2023 lib
   plus `npm:@types/node` — the runtime the extension host actually provides —
   while tests keep the default Deno libs because they run under the Deno CLI.
   `deno bundle` produces the single-file CJS `extension.js` with `vscode`
   external, and `@vscode/vsce` runs directly under `deno run npm:@vscode/vsce`
   (the chalk incompatibility recorded as deno#26637 no longer reproduces on
   the supported Deno; no pnpm fallback is needed).

### Protocol shape (summary; see docs/web-frontend-protocol.md)

- Discovery: the executable writes a discovery file (URL, bearer token, pid,
  protocol version) at a path the frontend chooses; the file appearing
  signals readiness.
- Auth: `Authorization: Bearer <token>` on every frontend request, constant-
  time compared. The frontend listener is loopback TCP on all platforms
  (Deno's WebSocket client cannot ride a Unix socket).
- REST: capabilities, session lifecycle (new/resume/list/gc/repair), turn
  submission (returns a run id; a concurrent turn is a typed `agent_busy`
  error, not a queue), cancel, transcript snapshot, command execution,
  completion, status, binary object streaming.
- WS: one events endpoint per session carrying the six `AgentEvent` kinds as
  provider-neutral frames plus run terminal state and REPL presentation
  frames, each frame carrying the run-local sequence for replay.

## Consequences

- The two protocol mismatches disappear: the session's single-run gate is the
  only serialization point, and any frontend can rebuild its view from the
  transcript endpoint plus sequence-based replay.
- The executable's frontend surface is self-owned and versioned; Jupyter
  interoperability survives only in the retained library projects.
- Command semantics live in one place; the Jupyter adapter and the web API
  both delegate to `Maieutics.Commands`.
- The extension owns the notebook snapshot format; `.ipynb` import/export is
  deferred until there is a consumer.
- Interactive outputs (tool approval buttons, input requests) are not first-
  class VSCode notebook features; they are composed from custom renderers and
  commands, and richer interaction later means a webview renderer for a
  single output, not a protocol change.
- `vsce` remains a Node-side tool; the pnpm footprint in the repo is exactly
  the packaging devDependency, never imported by extension source.

## Verification evidence

- Phase 1: frontend API integration tests (turn streaming, reconnect replay,
  concurrent-turn rejection, cancel, command execution, discovery/auth).
- Phase 2–3: extension protocol client unit tests under `deno test`; manual
  F5 run of the notebook round trip.
- Phase 5: `dotnet test Maieutics.slnx`, `dotnet build -warnaserror`, and the
  supported-RID NativeAOT publish check after Jupyter wiring removal.
