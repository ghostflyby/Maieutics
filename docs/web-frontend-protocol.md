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

## Display objects (binary rich values)

Binary mime values in `repl.display` bundles above a server-defined size
threshold are stored as immutable, content-addressed objects. The bundle
carries a structured reference — never base64 (invariant 26):

```json
{"image/png": {"$object": "/v1/objects/<sha256>", "byteLength": 12345}}
```

The reference URL is relative to the discovery URL, served with
`Cache-Control: public, max-age=31536000, immutable`, and the bytes travel
natively in the HTTP body. Because the URL is content-addressed (same bytes
= same URL forever), clients should cache unconditionally and may share
cached entries across displays, runs, and notebooks. Binary mime values are
always object references — never base64, regardless of payload size
(invariant 26); a value under a binary mime that is not a reference carries
no renderable data and clients fall back to the bundle's other mimes.

## Input requests (REPL stdin)

A REPL `prompt()` surfaces as an `input.request` frame. The frontend answers
with:

```
POST /v1/agent/inputs/{requestId}   body: {"value": "..."}   → 200 {} | 404
```

The answer completes the pending request; a second answer for the same id is
`404`. If the run ends (or the presentation scope detaches) before an answer
arrives, the request is cancelled server-side and any late answer is `404`.
Dismissing the input box should post an empty value.

## Comm channels (interactive widgets)

Interactive widgets (ADR 0024) pair a **display mime** that announces the
widget with a **comm channel** that carries the model's life:

- A REPL display bundle may carry
  `application/vnd.maieutics.widget-view+json`:
  `{"commId": "…", "version": 1, "esm"?: "<js or $object ref>",
  "css"?: "<css or $object ref>", "state": {…}}`. Large `esm`/`css` payloads
  are content-addressed `$object` references like any binary mime.
- Everything else about a widget travels on the comm WebSocket below.

### `GET /v1/agent/sessions/{sid}/comms?sinceSeq=<n>&token=<hex>`

Full-duplex WebSocket, session-scoped (ADR 0024). `token` is accepted as a
query parameter like the events endpoint; a non-active session is
`404 session_not_active`. The server's first frame is JSON text:

```json
{"live": [{"commId": "…", "targetName": "…"}], "replayed": false, "truncated": false}
```

`live` is the registry of currently open comms (identities survive replay
eviction); `replayed` reports whether retained frames were re-sent for a
non-zero `sinceSeq`; `truncated` reports that the client's `sinceSeq` precedes
the retained buffer, so state must be treated as unknown-until-refresh.

After the hello, every application frame is **binary**:
`[sequence:8 big-endian][comm frame]` where the comm frame is the fixed binary
comm encoding shared with the child hop —
`[kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][metadataLen:4][metadata][bufferCount:2][bufLen:4][buf]...`.
`data` and `metadata` are UTF-8 JSON (empty length = absent); buffers are
native bytes, never base64 (invariant 26). Direction rules:

- `kind 0` (`open`) is REPL-originated only. The frontend never sends it; an
  uplink open is a protocol violation and closes the socket.
- `kind 1` (`message`) and `kind 2` (`close`) flow both ways. On the downlink
  the server stamps the envelope sequence; on the uplink the client sends
  sequence `0` (the server assigns ordering).
- An uplink message or close for an unregistered `commId` is answered with a
  JSON text frame `{"type": "comm.error", "code": "comm_not_found", "commId":
  "…"}` and the socket stays open. Uplink while the session's REPL is
  detached yields `code: "repl_unavailable"`. A text application frame is a
  policy violation and closes the socket.
- Per-message ceiling: 16 MiB (buffers included). Exceeding it closes the
  socket.

Backpressure and resume follow the events stream: bounded per-subscriber
queues; on overflow the server closes with `1011 backpressure` and the client
reconnects with its last observed sequence. Sequences are dense per session
and reset when the active session changes (widget state lives in the
session's REPL process and does not survive switches or restarts). Frames are
re-sent verbatim on replay; widget state merges are last-wins, so replay is
idempotent.

`GET /v1/agent/capabilities` advertises the feature as
`"comm": {"version": 1, "maxMessageBytes": 16777216}`; clients that do not
implement comms ignore it and never open the endpoint.

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
| GET | `/v1/objects/{sha256}` | Immutable binary object stream (content-addressed) |

The comms channel above is the one full-duplex WebSocket; the REST table
stays request/reply only.

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
{"type": "input.request", "requestId": "input-1", "prompt": "Name:", "password": false}
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
