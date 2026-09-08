# ADR 0024: Frontend Comm Plane for Interactive Widgets

Status: Draft

Date: 2026-09-08

Amends: ADR 0023 (frontend protocol v1 gains an additive comm feature family)

## Context

ADR 0023 retired the executable's Jupyter kernel, and with it the only path that
carried bidirectional comm traffic (anywidget-style widget models) to a user
frontend. The child-side machinery survived intact: the REPL exposes
`maieutics.comm` (`open`/`msg`/`close`/`on`) and `Deno.jupyter.broadcast`
comm frames with metadata and binary buffers; the host relays them over the
dedicated `/comm` WebSocket using a fixed binary codec with native buffers
(16 MiB ceiling). Two seams were left deliberately unwired: the host's
`commFrontendSink` delegate (REPL→frontend downlink) and
`PushCommMessageAsync` (frontend→REPL uplink). The frontend protocol had no
comm surface, so widget displays rendered their initial state and then sat
inert.

The migration-gap ledger (item 6) recorded the product decision: interactive
outputs are supported, designed as a native web-API channel rather than a
port of the Jupyter comm wire.

## Decision

1. **One new endpoint: a session-scoped, full-duplex WebSocket** at
   `GET /v1/agent/sessions/{sid}/comms?sinceSeq=<n>&token=<hex>`. It does not
   ride the events stream because widget traffic is session-lifetime (state
   pushes routinely happen outside any run), carries binary both directions
   (invariant 26), and deserves its own backpressure story (invariant 27).
   Bearer auth matches the events endpoint, including the query-token
   exception for browser-standard WebSocket clients.

2. **The frame format is the existing comm codec on both hops.** After a JSON
   text hello, every application frame is binary. The frontend hop prepends an
   8-byte big-endian envelope sequence to each codec frame so clients can
   resume with `sinceSeq`; the child hop keeps the raw codec. The codec and its
   frame reader move from `Control` to the `DenoRepl` namespace beside
   `ReplCommMessage`, because both consumers (control host, frontend host) are
   translating the same transport-neutral shape.

3. **Hello carries the live-comm snapshot.** The server's first text frame is
   `{"live": [{"commId", "targetName"}...], "replayed": <bool>, "truncated":
   <bool>}`. `live` is the relay's registry of currently open comms, so a late
   or resuming client learns identities whose `comm.open` has fallen out of the
   bounded replay buffer; `truncated` marks the gap case explicitly instead of
   leaving the client to guess. Since sequences are dense per session,
   `truncated` is derivable (`sinceSeq + 1 < first retained sequence`).

4. **Direction discipline.** `comm.open` is REPL-originated only; an uplink
   open is a typed protocol error and closes the socket. `Message` and `Close`
   flow both ways. An uplink addressing an unregistered comm id gets a
   `comm.error` text frame with code `comm_not_found` and the socket stays
   open; uplink while the session's REPL is detached gets `repl_unavailable`.

5. **The relay is the composition-root wiring of the two existing seams.**
   `FrontendCommRouter` (a `Maieutics.Frontend` singleton) receives the
   downlink through the `commFrontendSink` delegate (now carrying the session
   id), maintains the registry, and publishes into `FrontendCommStream` — a
   session-scoped replica of the run stream's structured-concurrency skeleton:
   bounded replay buffer (4096), bounded per-subscriber queues (1024),
   overflow closes the socket (1011 `backpressure`) and the client reconnects
   with `sinceSeq`. Uplink flows from the endpoint's receive loop into
   `ReplControlHost.PushCommMessageAsync`. Neither the events stream nor the
   run registry changes.

6. **Session scoping follows REPL ownership.** Each Agent session owns one
   REPL process; widget state lives in that process and does not survive
   session switches or restarts. The comm stream (registry, replay buffer,
   sequences) is therefore reset when the active session changes, and the
   endpoint serves the active session only (`404 session_not_active`
   otherwise), like the events endpoint.

7. **Widget views are a display mime, not a comm frame.** A widget renders
   through `Deno.jupyter.display` as an
   `application/vnd.maieutics.widget-view+json` bundle member
   (`{commId, version, esm?, css?, state}`); the comm channel carries the
   model's subsequent life. Large `esm`/`css` payloads travel as
   content-addressed `$object` references served by the existing objects
   endpoint (invariant 26). Replay re-sends frames verbatim; widget state
   merges are last-wins by design, so replay is idempotent. A coalescing
   `comm.state` frame family is an explicit non-goal for v1.

8. **Capability negotiation is additive.** The capabilities response gains a
   nullable `comm` object (`{"version": 1, "maxMessageBytes": 16777216}`);
   clients that do not understand comm frames ignore them under the existing
   unknown-frame tolerance (invariant 17). The discovery file and protocol
   version are unchanged.

## Consequences

- Non-goals, recorded deliberately: frontend-originated `comm.open` (no
  target registry exists); any plugin reverse path into the REPL (ADR 0020
  invariant 25 — extensions still have no comm surface); server-side state
  coalescing; comm persistence in the transcript or notebook snapshot (a
  snapshot keeps only the final rendered view).
- The extension buffers comm frames for not-yet-rendered widget views; the
  display-announces-comm-lives pairing is by `commId`, so no cross-socket
  ordering guarantee is required.
- The legacy `/ws` JSON comm envelope (`comm.open/comm.msg/comm.close/ack`)
  remains superseded; this ADR covers the frontend hop, not a revival of it.
