# Maieutics Frontend Protocol (v1)

Draft — implemented by the `Maieutics.Frontend` domain in the executable and
consumed by the VSCode extension (`deno/maieutics-vscode`). Companion to
ADR 0023.

The protocol is an internal web application API surface (invariant 27): every
endpoint has an explicit direction, a bounded payload, and a version prefix.
All JSON uses camelCase and is serialized with source-generated contexts on
the NativeAOT path. No Jupyter type and no Microsoft.Extensions.AI type
crosses the wire (invariant 11).

## Discovery and authentication

The frontend spawns the executable and passes the discovery file location:

```
maieutics --frontend-discovery <path>
```

Once Kestrel has bound the frontend listener, the executable writes the file
atomically; the file's appearance is the readiness signal. Shape
(`maieutics-frontend-discovery` version 1):

```json
{
  "version": 1,
  "url": "http://127.0.0.1:51234",
  "token": "<64 hex chars>",
  "pid": 12345
}
```

Every frontend request must carry `Authorization: Bearer <token>`, compared in
constant time. The token is generated per process start. The frontend listener
is loopback TCP on every platform (the Deno WebSocket client cannot ride a
Unix domain socket), so bearer auth is the only gate; the file lives in
user-owned state with default-restrictive permissions.

## Conventions

- Turn submissions and WS frames reference sessions and runs by their
  identifiers (`AgentSessionId`/`AgentRunId` as 32-char lowercase `N` GUID
  strings).
- Every run event frame carries `sequence`: the strictly increasing run-local
  number from `AgentEvent.Sequence`.
- Errors are JSON bodies with a stable `code` and a human-readable `message`.
  Frontend-relevant codes mirror the agent typed failures (`agent_busy`,
  `agent_provider_error`, `agent_tool_error`, `agent_input_too_large`,
  `agent_response_too_large`, `agent_turn_in_progress`→`agent_busy`, …) plus
  protocol codes (`not_found`, `invalid_request`, `unauthorized`,
  `command_error`).
- A concurrent turn on a session is rejected with `409` + `agent_busy`; the
  protocol does not queue turns. Queuing semantics would be an explicit v2
  design (AGENTS.md invariant 4).

## REST endpoints (frontend → executable)

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/agent/capabilities` | Protocol version, server version, feature flags |
| GET | `/v1/agent/session` | The active session (id, turn count, persistence state) |
| POST | `/v1/agent/sessions` | Start a new session and make it active |
| GET | `/v1/agent/sessions` | List stored sessions (persistence disabled → empty) |
| POST | `/v1/agent/sessions/{sid}/resume` | Resume a stored session and make it active |
| POST | `/v1/agent/sessions/{sid}/gc?graceHours=24` | Prune unreferenced objects |
| POST | `/v1/agent/sessions/{sid}/repair` | Rebuild the derived object view |
| POST | `/v1/agent/sessions/{sid}/turns` | Submit one Agent turn → `202 {runId}` |
| GET | `/v1/agent/sessions/{sid}/transcript` | Authoritative history snapshot |
| POST | `/v1/agent/runs/{runId}/cancel` | Cooperative cancel; waits for termination |
| POST | `/v1/agent/commands` | Execute a `%`-command cell → `{markdown}` |
| POST | `/v1/agent/complete` | Command completion for the current cell text |
| GET | `/v1/status` | Status snapshot as markdown |
| GET | `/v1/objects/{id}` | Binary object stream (object store bypass) |

Turn requests are limited to the active session; a turn addressed to another
session id is `409` + `session_not_active`. This keeps "the kernel owns the
authoritative live conversation" (invariant 1) while the path shape stays
forward-compatible with multi-session.

`POST /v1/agent/sessions/{sid}/turns` body: `{"text": "..."}`. Empty text is
`400`. `%`-command text is executed as a command (same semantics as the
Jupyter adapter) and answered with `200 {markdown}` instead of starting a run.

`GET /v1/agent/sessions/{sid}/transcript` returns the committed public
transcript rendered provider-neutrally:

```json
{
  "sessionId": "…",
  "version": 3,
  "turns": [
    {
      "runId": "…",
      "truncated": false,
      "model": {"profileId": "default", "provider": "openai", "model": "…"},
      "messages": [
        {"role": "user", "parts": [{"kind": "text", "text": "…"}]},
        {"role": "assistant", "parts": [{"kind": "text", "text": "…"}]}
      ]
    }
  ]
}
```

`POST /v1/agent/complete` body: `{"text": "...", "cursor": 12}` where `cursor`
is a UTF-16 code-unit offset (no Jupyter code-point conversion). Response:
`{"matches": ["…"], "tokenStart": 0, "tokenEnd": 8}`.

## WebSocket event stream (executable → frontend, half-duplex)

`GET /v1/agent/sessions/{sid}/events?sinceSequence=<n>` upgrades to a
WebSocket. The endpoint carries server→frontend frames only; the client sends
nothing except the close. Because the browser-standard WebSocket API cannot set
headers, this endpoint additionally accepts the bearer token as a `token` query
parameter; every other endpoint requires the `Authorization` header. Frames are
JSON text:

```json
{"type": "hello", "session": {"id": "…", "turns": 2}, "replayed": false}
{"type": "run.started", "runId": "…"}
{"type": "text.delta", "runId": "…", "sequence": 4, "messageId": "…", "text": "…"}
{"type": "message.completed", "runId": "…", "sequence": 5, "messageId": "…",
 "agentMessage": {"role": "assistant", "parts": [{"kind": "text", "text": "…"}]}}
{"type": "tool.started", "runId": "…", "sequence": 6, "callId": "…",
 "tool": "workspace_read", "arguments": {"…": "…"}}
{"type": "tool.progress", "runId": "…", "sequence": 7, "callId": "…",
 "content": {"kind": "text", "text": "…"}}
{"type": "tool.finished", "runId": "…", "sequence": 8, "callId": "…",
 "result": {"status": "ok", "value": {"…": "…"}}}
{"type": "turn.truncated", "runId": "…", "sequence": 9}
{"type": "run.completed", "runId": "…", "truncated": false}
{"type": "run.failed", "runId": "…", "code": "agent_provider_error", "message": "…"}
{"type": "repl.display", "displayId": "…", "mime": "text/markdown", "data": "…"}
{"type": "repl.updateDisplay", "displayId": "…", "mime": "text/markdown", "data": "…"}
{"type": "run.status", "state": "busy" | "idle"}
```

Rules:

- `sinceSequence` is per run; a reconnecting client passes the last sequence
  it observed for the run(s) it still renders and the server replays from the
  retained buffer. Frames older than the buffer produce `{"type":
  "run.missing", "runId": "…"}` so the client can refetch from the transcript
  endpoint instead of rendering a gap.
- The replay buffer is bounded per run; events are never silently dropped
  (invariant 16). If a consumer's send queue overflows, the server closes the
  socket (`1011`, reason `backpressure`) and the client reconnects with
  `sinceSequence`.
- Runs started before any connection existed are buffered regardless of
  connections, so a late client can still catch up.
- Tool activity, presentation, and status frames route by `runId`; REPL
  presentation frames use the display id as the tracking key (the extension
  maps it onto one updatable notebook output).

## Notebook snapshot (frontend-owned)

`.maieuticsnb` is a frontend-owned portable interaction snapshot; the server
never reads or writes it. Save/load must not mutate the active session
(invariant 13). Shape (frontend-side schema, informative here):

```json
{
  "kind": "maieutics-notebook",
  "version": 1,
  "session": {"serverSessionId": "…", "createdAt": "…"},
  "cells": [
    {
      "kind": "agent" | "markdown",
      "text": "…",
      "output": {"kind": "agent", "turn": {"text": "…", "tools": ["…"]}}
    }
  ]
}
```
