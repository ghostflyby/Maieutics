# Multi-Session Migration Research

Status: Implemented (all four phases) on `feat/multi-active-session` —
registry with lazy resolve and soft-cap eviction, per-session run hubs,
per-session profile overrides, command addressing, extension de-pinning via
`capabilities.multiSession`. Deviation from the draft: live-set eviction is
capacity-based only (no idle timer), and unresumable sessions (zero-turn,
persistence-less) are never evicted.

Date: 2026-09-09

Mandate (user decisions, 2026-09-09):

1. A running process supports an **arbitrary number of live sessions**.
2. `%model use` (the model-profile override) is **session-scoped** — it
   affects the session it was addressed to, not the process.

Companion: `docs/agent-ux-gap-analysis.md` §C1 (provenance of the
single-active semantics: inherited Jupyter-kernel scaffolding, not a
requirement), `docs/web-frontend-protocol.md`, ADR 0009 (storage), ADR 0023
(frontend protocol). Every "Current" claim below was verified against the
tree on this date.

## Why this is de-scaffolding, not an architecture change

The single-active semantics came from the Jupyter-kernel era (one kernel =
one conversation) and were mirrored into the web protocol. The load-bearing
design was done correctly back then: **every route, run stream, comm plane,
presentation sink, and control registry is already keyed by session id**.
The only single-active artifacts are a `current` field in the session
manager, an `EnsureActive` guard in the frontend service, one shared
run-announcement channel, and the extension's pin/resume workaround. AGENTS.md
invariant 1 ("the executable owns the authoritative live conversation
history") is satisfied per session; invariant 4 (single-run gate) is a
per-session serialization semantic and stays exactly as it is.

## Inventory

### Already multi-session-ready (no work)

| Surface | Evidence |
|---|---|
| REST route shapes | `/v1/agent/sessions/{sid}/…` everywhere |
| Run streams | `FrontendRunStream` is per-run, carries its session id; `FrontendRunRegistry` keys by run id |
| Comm planes | `FrontendComm.FrontendCommRouter` keeps `Dictionary<string, FrontendCommStream> planes` |
| Presentation sinks | `FrontendDenoReplPresentationRouter` keeps per-session run state |
| Control plane | `ReplControlSessionRegistry` maps each REPL child pid to one session id — N REPL children already map to N sessions |
| Storage | one family database per fork family; nothing process-shaped |
| Agent runtime | `AgentSession` instances are independent; the single-run gate is per instance |
| GC / object view | family-keyed scans |

### Single-active keyed (the migration surface)

| # | Surface | Current (verified) | Multi-home shape |
|---|---|---|---|
| S1 | `MaieuticsAgentSessionManager` | one `current` field; implements `IAgentSession` by delegation; `Resume`/`StartNew`/`Fork` swap `current` | A registry of live sessions: `AgentSessionId → live AgentSession`, created lazily (`Resume` on first touch), plus `StartNew`. The manager stops implementing `IAgentSession`; callers resolve sessions by id |
| S2 | `FrontendSessionService` | `EnsureActive(sessionId)` on turns/transcript/gc/repair/events/comms (16 call sites); `DescribeSession()` without argument | `ResolveSession(sessionId)`: live → use; stored → lazy-resume then use; else typed 404. Describe becomes `DescribeSession(sessionId)` |
| S3 | Run announcements | one `latestRun` + one unbounded `runAnnouncements` channel + `ResetRunAnnouncements()` shared by the process | A per-session hub (`latestRun` + announcements channel) in a dictionary; events endpoints subscribe to the addressed session's hub; reset becomes per-hub (on fork-activation it is unnecessary anyway — runs are per session already) |
| S4 | Events/comms endpoints | 404 `session_not_active` when `sid ≠ active` | Drop the active check; serve the addressed session's stream/plane |
| S5 | `GET /v1/agent/session` + `capabilities.session` | singular "the active session" | Keep as a compatibility alias returning the **foreground session** (most recently used live session); document as deprecated. Additive: `capabilities.multiSession: true` so the extension can feature-detect |
| S6 | Command cells | `HandleTurn` executes commands session-blind; the answer's `sessionId` (re-pin hint) is `DescribeSession().Id` | Command execution gains a session context (the addressing session id); `%session new`/`resume` answers still carry the target id so notebooks re-pin; `%session current` means the addressed session |
| S7 | `MaieuticsCommandExecutor` | all session ops via the manager's `current`; `%session gc`/`repair` target active | Addressed via the command context (S6); `gc`/`repair`/`fork`/`resume` take the id they already parse |
| S8 | Model profile override | `MaieuticsRuntimeConfiguration.sessionOverride` is process-level; the runtime configuration is the `IAgentRunProfileProvider` handed to **every** `AgentSession` | Per-session `SessionProfileProvider` wrapper: holds the session's override (profile id), resolves profiles through `IMaieuticsModelProfileController`, falls back to the configured default (which stays process-level — the config file is process-owned). `%model use`/`reset` mutate the addressed session's wrapper; `Fork(profileId)` seeds the fork's wrapper. Retirement/lease semantics in the runtime configuration are unchanged |
| S9 | `MaieuticsStatusProvider(IAgentSession)` | renders the manager's current session | Process status renders the foreground session's line (or drops it); session-level facts move to the session-addressed transcript endpoint |
| S10 | Extension session pin | `sessionPin.ts` resumes the pinned session before every batch because the server serves one active session; unpinned notebooks steal the active session | With `capabilities.multiSession`: a pinned notebook verifies existence only (no resume); an unpinned notebook **creates its own session** (two windows stop fighting); tree "resume" becomes "open" (lazy resolve server-side); the pin fallback branch survives for older servers |
| S11 | DI registration | `builder.Services.AddSingleton<IAgentSession>(manager)` | Register the manager (registry) instead; `MaieuticsStatusProvider` switches to it (S9) |

## Target semantics

- **Live set.** The manager keeps an `AgentSessionId → AgentSession` map.
  Sessions enter it by `StartNew`, explicit resume, or **lazy resolve** (a
  turn/transcript addressed to a stored session resumes it first — this is
  what makes `EnsureActive`'s removal safe). The live set is bounded
  (AGENTS: bound retained state): an LRU cap (e.g. 8) plus idle eviction
  (e.g. 30 min after the session's last run reaches a terminal state).
  Eviction is safe by construction — the transcript store is the durable
  form and `AgentSession.Resume` reconstructs.
- **Gates.** One mutating run per session (invariant 4) is unchanged and
  now meaningfully per session: two notebooks on two sessions submit
  concurrently without `agent_busy`; two notebooks sharing one pinned
  session keep the existing busy/queue behavior.
- **Profiles.** Configured default (config file + reload) stays
  process-level; the session override becomes per-session state inside the
  wrapper (S8). `%model use` from a cell affects the session that cell was
  addressed to; the fork flow's `profileId` seeds the fork. A session's
  next run acquires its profile at turn start exactly as today.
- **Foreground.** Only two places need a "which session, unspecified"
  answer: the compatibility alias (S5) and process status (S9). Foreground =
  most recently used live session; it is presentation state, not authority.
- **Wire.** Protocol stays version 1: all changes are additive or behavior
  behind existing session-addressed routes. The singular session endpoint
  remains (alias), `multiSession` is additive, event frames unchanged.

## Concurrency and lifetime rules (unchanged invariants)

- One mutating run per session; concurrent submits get typed `agent_busy`.
- Runs in flight keep executing against their own session (already true).
- Profile leases per run unchanged; retired clients live to their lease end.
- No locks across awaits in the new hub/wrapper code; per-session state
  guarded by `System.Threading.Lock`; bounded channels/replay unchanged.
- Disposal: the manager's store cache disposal is unchanged; evicted live
  sessions are only dropped after their runs are terminal.

## Phased plan

1. **Server core (S1–S5, S8).** Registry + lazy resolve + per-session run
   hubs + drop endpoint active checks + profile wrappers + foreground alias
   + `capabilities.multiSession`. Tests: two sessions alternate turns
   concurrently over REST; events stream per session simultaneously;
   transcript/GC addressed to non-foreground sessions; per-session profile
   isolation (`%model`-equivalent via wrapper, fork `profileId` seeds only
   the fork); eviction of an idle session then lazy re-resume.
2. **Command context (S6, S7, S9).** `MaieuticsCommandContext` (session id)
   threaded from `HandleTurn`; `%session current/new/resume/gc/repair/fork`
   addressed; `%model use/reset` per session; status foreground line.
   Tests: command cells on two sessions do not cross.
3. **Extension de-pinning (S10, S11).** Feature-detect `multiSession`;
   pinned notebooks stop resuming per batch; unpinned notebooks create their
   own session; tree/quick-pick "resume" becomes "open"; keep the legacy
   fallback for older servers. Tests: sessionPin decision matrix for the
   multi-home case; two notebooks, zero resume calls.
4. **Docs.** Protocol doc (alias semantics, `multiSession`), README,
   AGENTS.md invariant 1 wording check (no "active session" language to
   change — verified), gap-analysis C1 closure.

Estimated size: phase 1 M, phase 2 S–M, phase 3 M, phase 4 S.

## Risks and open questions

- **Foreground drift.** Anything still reading `GET /v1/agent/session`
  (scripts, the old VFS materializer) sees the foreground session; acceptable
  for a compatibility alias but worth a deprecation note.
- **`%session resume` meaning.** In a cell it re-pins that notebook (via the
  answer's `sessionId`) and lazily loads the target — the "switch the
  process" meaning disappears; documented in the command surface.
- **Config reload with per-session overrides.** A reload retires profile
  generations; sessions holding overrides that no longer resolve fall back
  to the new default at their next acquire (typed failure if none) — needs a
  test.
- **REPL ownership.** Each REPL child is registered to one session; with
  multiple live sessions the question "which session does a new REPL attach
  to" is answered by whichever session the control peer handshake names —
  unchanged mechanics, worth one integration test.
- **Extension/server skew.** Old extension + new server keeps working (the
  resume-per-batch degrades to a no-op-ish verify); new extension + old
  server keeps the pin fallback — the `multiSession` capability flag is what
  makes both directions explicit.
