# ADR 0025: Server-Owned Session Turn Queue

Status: Draft

Date: 2026-09-12

## Context

ADR 0023 pinned the opposite choice on queuing: the frontend protocol rejects a
concurrent turn with `409 agent_busy` and leaves any notebook-side queue a client
chore (the VSCode extension marks queued cells and drains them itself, one
`POST /turns` at a time). That choice is now under pressure from three directions:

1. **A second frontend consumer.** Queuing in one client does not exist for
   another: a second window, a tree-view runner, or a third-party consumer sees
   no queue and races the same session with direct submissions.
2. **Frontend-as-projection.** The live session, its runs, and their event
   streams are already server state that survives client disconnects
   (`sinceSequence` resume, retained replay buffers). The queue is the one piece
   of interaction state still trapped in a client.
3. **Persistence and resume.** A client reload loses its pending cells' run
   order exactly when the run order matters (each queued turn is built on the
   transcript the previous turn committed). Server state would keep draining the
   queue across a disconnect or reload.

Meanwhile the server already owns the only serialization point that matters
(`AgentSession` reserves one mutating run per session — invariant 4), the turn
entry point is a single reusable path (`FrontendSessionService.StartTurnAsync`:
resolve, validate, start run, wire the run stream), and the events socket already
fans out session-scoped non-sequenced frames (presentation, status). Queuing on
the server is therefore composition, not a new concurrency regime.

## Decision

1. **A per-session turn queue is server state, scoped to Agent turn items.**
   `FrontendTurnQueue` (in `Maieutics.Frontend`) keeps a bounded FIFO per session
   (64 items) plus the running item, drained by at most one worker loop per
   session with queued work (structured concurrency: owner, linked cancellation,
   observed completion, deterministic disposal). `%`-command cells stay
   client-orchestrated on `POST /turns`; the queue rejects command text with a
   typed 400, because commands can move the foreground, switch profiles, or
   answer inline and are not turns.
2. **Direct `POST /turns` semantics are unchanged.** A direct submission while
   the single-run gate is held is still `409 agent_busy`, never queued. The
   worker submits through the same `StartTurnAsync` path; when a direct
   submission races the worker's dequeue, the worker waits the in-flight run out
   (bounded) and retries a few times before failing the item. The gate remains
   the only serialization point (invariant 4); the queue is scheduling on top of
   it, not a second gate.
3. **REST shapes.** `GET /queue` returns the full state (running item with run
   id, pending items with texts in run order, capacity); `POST /queue` enqueues
   1–64 texts atomically (all-or-nothing, `409 queue_full`) and answers
   1-based positions in run order; `DELETE /queue/{itemId}` removes a queued
   item (`409 item_running` for the running one — cancel the run instead);
   `DELETE /queue` clears the pending items and leaves the running item
   untouched. Text validation mirrors turn submission (empty, command, input
   limit).
4. **A full-state `queue.updated` event frame.** Session-scoped, no sequence
   number, idempotent full replacement, item ids only. Every queue transition
   wakes every connected session socket, and each wake writes the queue's
   current full state — a socket that is busy serving a run stream observes the
   newest state when it resumes, which a full-state frame makes safe. The
   events socket's run wait races a queue-change wait (the queue wait first, so
   a socket that opens while queue state exists writes the current state
   immediately after `hello`); a queue wake never touches the run-serving
   bookkeeping, so a run is still served exactly once. Per-run sockets
   (`?runId=`) never receive queue frames.
5. **A queued session pins its live registration.** The queue holds one
   cooperative eviction-pin lease (`MaieuticsAgentSessionManager.TryPinSession`)
   per session with queue work; eviction skips pinned sessions at candidacy and
   re-check. Eviction would be survivable via lazy-resume, but queued work is
   active interest and lazy-resume mid-queue would churn transcripts for no
   benefit. The lease is capacity management, not a lifetime guarantee.
6. **Phase 1 drops pending items on session shutdown or queue disposal** and
   logs it; a run already submitted keeps executing through its own run stream.
   **Phase 2 — SQLite durability across server restarts (persisted queue rows
   replayed on resume) — is explicitly deferred** until a consumer needs
   restart-survivable queues; the wire shape above is chosen so a durable queue
   is additive to it.

### Wire shape (summary; see docs/web-frontend-protocol.md)

- REST: `GET/POST /v1/agent/sessions/{sid}/queue`,
  `DELETE /v1/agent/sessions/{sid}/queue/{itemId}`,
  `DELETE /v1/agent/sessions/{sid}/queue`.
- Frames: `{"type": "queue.updated", "queue": {sessionId, running?, items[],
  capacity}}`.

## Consequences

- The single-run gate stays authoritative: turns are still serialized by the
  session, and the queue can never create a second concurrent run. Direct
  submissions keep their typed busy error, so existing clients are unaffected.
- A queued session's run order is observable and reconcilable by any number of
  clients: the queue is reachable through the snapshot endpoint and the
  idempotent frame, so a second frontend or a reloading notebook re-syncs
  without protocol changes.
- The queue adds one worker loop per busy session (not per session ever seen) —
  idle sessions cost a small state record — and one pin lease while work is
  queued.
- Failed queue items surface as removal (the next frame shows the queue without
  them) plus server logs; per-item typed failure events are deferred until a
  consumer needs them.
- Phase 1 is in-memory: a server restart loses queued items by design until the
  deferred Phase 2 lands.

## Verification evidence

- Frontend API integration tests (`FrontendTurnQueueIntegrationTests`): serial
  in-order draining, direct busy rejection with an active queue, snapshot shape,
  delete/clear semantics, `queue_full`, command/empty text rejection,
  `queue.updated` frames to one and later-opened sockets, and the
  direct-submission race.
- Session manager tests (`MaieuticsAgentSessionManagerTests`): pinned sessions
  survive LRU eviction; releasing the pin un-pins.
- `dotnet build Maieutics.slnx -warnaserror`, filtered test runs, and
  `git diff --check`.
