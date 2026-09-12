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
- A concurrent turn on a session is rejected with `409` + `agent_busy`. Turns can also be queued
  explicitly on the session's server-owned turn queue (below); `POST /turns` itself never queues.

## Session turn queue (server-owned)

Each session carries a bounded, server-owned queue of agent turn items. Queued items run in
arrival order under the session's single-run gate; when an item starts, its run behaves exactly
like a `POST /turns` run (same frames, same cancel endpoint). Command text (`%`-commands) and
empty text are never queued. The queue is versionless full state: every snapshot below replaces
the client's view wholesale.

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/agent/sessions/{sid}/queue` | Full-state snapshot (items in run order, with their texts) |
| POST | `/v1/agent/sessions/{sid}/queue` | Enqueue turn items → `202 {"items":[{"id","position"}…]}` |
| DELETE | `/v1/agent/sessions/{sid}/queue/{itemId}` | Remove one waiting item → `204` |
| DELETE | `/v1/agent/sessions/{sid}/queue` | Clear every waiting item (running item untouched) → `204` |

- `GET` answers `200 {"sessionId","running":{"itemId","runId"}|null,"items":[{"id","text",
  "enqueuedAt"}…],"capacity"}`; `items` are the waiting items in run order and `text` appears
  only here (frames carry ids alone).
- `POST` body is `{"items":[{"text":"…"}…]}` (one item per text). Errors: `400 invalid_request`
  for command text, empty text, or oversized payloads; `409 queue_full` when the batch would
  exceed `capacity`; `404 not_found` for an unknown session.
- `DELETE …/{itemId}` answers `204`, `409 item_running` once the item started (cancel the run
  instead), or `404 not_found`.
- Every queue mutation emits one event frame on the session events socket (never on `?runId=`
  per-run sockets):

```json
{"type": "queue.updated", "queue": {"sessionId": "…", "running": {"itemId": "…", "runId": "…"},
 "items": [{"id": "…"}], "capacity": 8}}
```

The frame carries no `sequence` number and is an idempotent full-state replacement; a client that
misses frames self-heals on the next mutation, and a (re)connecting client reads `GET /queue`
once after the hello to rebuild its view.

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
  `application/vnd.jupyter.widget-view+json`:
  `{"model_id": "…", "version_major": 2, "version_minor": 0}` — the model's
  state travels on the comm channel, not in the bundle.
- Everything else about a widget travels on the comm WebSocket below.

### `GET /v1/agent/sessions/{sid}/comms?sinceSeq=<n>&token=<hex>`

Full-duplex WebSocket, session-scoped (ADR 0024). `token` is accepted as a
query parameter like the events endpoint; an unknown session is
`404 not_found` (legacy single-active servers answered
`404 session_not_active` for a non-active one). The server's first frame is JSON text:

```json
{"live": [{"commId": "…", "targetName": "…"}], "replayed": false, "truncated": false}
```

`live` is the registry of currently open comms (identities survive replay
eviction); `replayed` is true when a non-zero `sinceSeq` was requested (the
replayed set may still be empty); `truncated` reports that the client's `sinceSeq` precedes
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
reconnects with its last observed sequence. Sequences are dense per session. Planes are retained per session (least
recently used, a small fixed window) rather than torn down eagerly; widget
state lives in the session's REPL process, so a plane created for a
switched-back session reflects only comms that REPL re-announces. Frames are
re-sent verbatim on replay; widget state merges are last-wins, so replay is
idempotent.

`GET /v1/agent/capabilities` advertises the feature as
`"comm": {"version": 1, "maxMessageBytes": 16777216}`; clients that do not
implement comms ignore it and never open the endpoint.

## REST endpoints (frontend → executable)

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/agent/capabilities` | Protocol version, server version, workspace root, feature flags |
| GET | `/v1/agent/session` | The foreground session (compatibility alias; id, turn count, persistence state, title) |
| POST | `/v1/agent/sessions` | Start a new session and make it active |
| GET | `/v1/agent/sessions` | List stored sessions with display metadata (persistence disabled → empty) |
| POST | `/v1/agent/sessions/{sid}/resume` | Resume a stored session and make it active |
| POST | `/v1/agent/sessions/{sid}/rename` | Set or clear one stored session's title |
| POST | `/v1/agent/sessions/{sid}/fork` | Fork a stored session at a turn and make the fork active |
| POST | `/v1/agent/sessions/{sid}/gc?graceHours=24` | Prune unreferenced objects |
| POST | `/v1/agent/sessions/{sid}/repair` | Rebuild the derived object view |
| POST | `/v1/agent/sessions/{sid}/turns` | Submit one Agent turn → `202 {runId}` |
| GET | `/v1/agent/sessions/{sid}/transcript` | Authoritative history snapshot |
| POST | `/v1/agent/runs/{runId}/cancel` | Cooperative cancel; waits for termination |
| POST | `/v1/agent/commands` | Execute a `%`-command cell → `{markdown}` |
| POST | `/v1/agent/complete` | Command completion for the current cell text |
| GET | `/v1/model/profiles` | Selectable model profiles (id, provider, model, selected) |
| GET | `/v1/status` | Status snapshot as markdown |
| GET | `/v1/objects/{sha256}` | Immutable binary object stream (content-addressed) |

The comms channel above is the one full-duplex WebSocket; the REST table
stays request/reply only.

Every session-addressed route is served for its addressed session: a live
session is used as-is, a stored one is lazily resumed, and an unknown id is
`404 not_found`. Addressing a session never moves the foreground. This is
advertised as `multiSession: true` on `/v1/agent/capabilities`; the process
keeps an arbitrary number of live sessions (bounded; lazily resumable ones
are evicted least-recently-used first), and the model-profile override
(`%model use`, fork `profileId`) is per session — the configured default
stays process-level. The legacy gate no longer occurs on this build; older
single-active servers still rejected a session other than their single
active one (`409 session_not_active` from turns, `404 session_not_active`
from events/comms).

`POST /v1/agent/sessions/{sid}/turns` body: `{"text": "..."}`. Empty text is
`400`. `%`-command text is executed as a command (same semantics as the
Jupyter adapter) and answered with `200 {markdown, sessionId}` instead of
starting a run. `sessionId` is the addressed session unless the command moved
the foreground (`%session new` / `resume` / `fork`), in which case it is the
new foreground — a notebook frontend re-pins only when it differs from its
pinned session. Session-aware commands (`%session current`, `%model
use/current/reset`) are scoped to the addressed session.

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

## Session display metadata

Stored sessions carry bounded display metadata so frontends can name and
group them without loading transcripts (schema v4 of the transcript store):

- `GET /v1/agent/sessions` items are
  `{id, turns, createdAt, lastActivityAt, title?, preview?, workspaceRoot?, parentSessionId?, forkPointSeq?}`.
  `title` is the user-set name (absent when never renamed); `preview` is the
  first committed user message, collapsed to one line and capped at 200
  chars (absent before the first committed turn); `workspaceRoot` is the
  workspace root stamped when the session's row was created (absent for rows
  written before the column existed). `parentSessionId` and `forkPointSeq`
  describe a fork head: its visible history is the parent's first
  `forkPointSeq` turns followed by its own (both absent on root sessions).
- `GET /v1/agent/session` and the `session` inside `/v1/agent/capabilities`
  carry the **foreground** session (most recently activated: start / resume /
  fork move it; per-session addressing does not). Prefer the
  session-addressed routes; the singular endpoint is a compatibility alias.
- `/v1/agent/capabilities` carries `workspaceRoot?` — the process's current
  `Maieutics:Workspace:Root`, live across `%workspace use` switches — so a
  frontend can detect a server serving another workspace.
- `POST /v1/agent/sessions/{sid}/rename` body `{"title": "..."}` sets the
  title; empty or whitespace-only clears it. The answer is
  `{"id": "…", "title": "…"}` with the stored, normalized title (nulls
  omitted). Renaming is **not** active-session-gated and works for any stored
  session; renaming a session with no stored row creates one (zero turns), so
  a named session is listed before its first committed turn. Errors:
  `400 invalid_request` for a malformed id or a title over 200 chars. The
  command surface equivalent is `%session rename <id-prefix> <title...>`.
- `POST /v1/agent/sessions/{sid}/fork` body is exactly one of
  `{"runId": "…"}` (a committed turn of the source; the fork keeps the turns
  before it and re-runs it as the fork's first turn) or `{"seq": n}` (the
  number of the source's committed turns the fork keeps, `0..turns`). The
  fork is a new head in the source's root family database (ADR 0009: turns
  are referenced, never copied), it is auto-titled from the source's
  title/preview plus `" · branch @ turn N"`, and it becomes the
  foreground session. The answer is `200 {"id": "…", "title": "…"}` (nulls omitted).
  Forking is **not** active-session-gated and fork of a fork chains through
  the parent link. An optional `profileId` switches the model profile first,
  so the fork's first turn runs on it (regenerate-with-model); an unknown
  profile is `400 invalid_request`. Other errors: `404 not_found` for an
  unknown session or a run id that never committed on it, `400
  invalid_request` for a malformed body, an out-of-range `seq`, or
  persistence disabled. The command surface equivalent is
  `%session fork <id-prefix> <runId|seq>`.

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
{"type": "run.completed", "runId": "…", "truncated": false,
 "model": {"profileId": "…", "provider": "openai", "model": "…"},
 "usage": {"inputTokens": 11, "outputTokens": 7, "totalTokens": 18}}
{"type": "run.failed", "runId": "…", "code": "agent_provider_error", "message": "…"}
{"type": "repl.display", "displayId": "…", "mime": "text/markdown", "data": "…"}
{"type": "repl.updateDisplay", "displayId": "…", "mime": "text/markdown", "data": "…"}
{"type": "run.status", "state": "busy" | "idle"}
{"type": "input.request", "requestId": "input-<unique>-1", "prompt": "Name:", "password": false}
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
- `run.completed` carries the run's model identity and the provider-reported
  token usage (additive; absent on older servers, on failures, and when the
  provider reports nothing). The chat-completions wire flavor only returns
  usage when the client asks for it — the executable's OpenAI adapter asks on
  the Responses flavor, which always reports it.

## Notebook snapshot (frontend-owned)

`.maieuticsnb` is a frontend-owned portable interaction snapshot; the server
never reads or writes it. Save/load must not mutate the live session
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
