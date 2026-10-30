# ADR 0046: Task title policies and session display metadata

Status: Accepted

## Context

A notebook task is an Agent session. Identity, canonical transcript, portable
snapshot, execution status, and display title have different lifetimes. Naming
must not delay a completed answer or require a dedicated model request per turn.

## Decision

The executable owns title metadata. `Maieutics:Agent:Title:Mode` selects the startup
default: `auto` generates once and exposes `set_task_title` for material scope
changes; `initial` generates once without a mutation tool; `manual` uses the request
preview until named by a user. A persisted session override takes precedence.
Without the new setting, legacy `AutoTitle=false` selects `manual`; otherwise the
default is `auto`. Explicit new configuration wins over the legacy switch. The
startup default requires restart; invalid modes fail configuration validation.

A manual rename locks the title in every mode. Clearing releases the lock and
resets initial eligibility; the selected mode still applies. Mode changes keep the
name and never unlock it. Regenerate is user-only, for unlocked `auto`/`initial`
sessions: admit one new attempt using committed history, or leave eligibility for
the next successful turn when history is empty.

`Maieutics.Commands` owns policy and a product session adapter, independently of
frontend connections. The adapter reserves its session through metadata commit
and admission, so later runs cannot publish older proposals out of order. Agent
and canonical transcript formats do not change. Concrete runtime access for
subagent composition goes through the manager's explicit seam.

Initial naming captures a separate selected-profile lease at turn startup. A
successful run captures bounded request/answer excerpts and admits one job to a
host-owned coordinator; it does not await the job. The worker runs one request at
a time, with at most 64 waiting jobs, no tools, 96 output tokens, and an eight-second
cooperative cancellation budget. Excerpts exclude tools, binary content and
reasoning. Failure, timeout or exhausted admission retains the preview/name and
settles eligibility; later turns do not retry. Failed/cancelled main runs do not
admit initial work. Automatic titles are one line, at most 80 UTF-16 units without
splitting a surrogate pair; manual names retain the existing 200-unit cap.

The coordinator owns its completion task, cancellation sources, queued leases,
and session pins. Disposal cancels and joins the worker and drains resources
before stores/providers are disposed. Superseded jobs are cancelled; queued
resources release when dequeued or drained. A non-cooperative in-process provider
can outlive its cancellation budget. Admission failures are logged and settled.
Metadata failures log session and failure type, never prompts, model output or
secrets, and do not turn a committed answer into a failed Agent turn.

`set_task_title({title})` is a statically reachable, source-generated `AIFunction`.
Its only model argument is the title; session/run identity comes exclusively from
`AgentToolContext`. It is offered only in unlocked `auto` profiles with function
capability, with the current title quoted as untrusted display context. Calls
inherited by another session fail typed instead of creating orphan proposals.
The function stages one proposal per run (last valid proposal wins) and truthfully
returns `staged`; only successful runs commit it. Failure/cancellation discards it,
even if a truncated turn records the staged tool result. Runtime checks preserve
concurrent user renames and mode changes. Prompts forbid renaming for continuation,
clarification, review, progress, retries or completion. A tool call can add an
ordinary continuation request; there is no dedicated per-turn title check.

Title state has a visible revision, user-control revision, source, mode override,
and initial-attempt state (`eligible`, `pending`, `settled`). CAS writes are atomic.
User actions invalidate older jobs/proposals, including rename then clear. A
successful tool proposal settles/cancels initial work. Initial jobs cannot overwrite
tool names; continuation alone does not invalidate initial naming. Forks inherit
the resolved mode and an unlocked derived title eligible on their first own turn;
explicit fork names are locked. Volatile sessions use the same rules in memory.

SQLite schema v6 extends v5 ownership/revision fields. Migration preserves old
non-null titles as manual (pre-v5) or settled (v5); opening/listing a named session
does not rename it. Pending attempts abandoned on exit become settled on store
reopen, without automatic retry. The existing newer-schema guard prevents older
executables opening v6; there is no downgrade. Canonical transcript and portable
`.maieuticsnb` formats are unchanged.

`session.updated` is a full session-state notification with no `runId` or run-local
`sequence`. It travels on general session sockets independently of active/idle
runs. Each handler owns a metadata pump and serializes writes through its existing
socket send gate. Capturing the hello revision before waiting prevents a handshake
gap. Full-state notifications may coalesce; this is not an audit stream. Run-only
replay excludes titles. Reconnect hello and addressed REST remain authoritative;
monotonic `titleRevision` prevents stale metadata replacing newer names.

Authenticated `GET /v1/agent/sessions/{sid}` reads metadata without activation;
`POST .../{sid}/title-policy` accepts `mode` or `regenerate`. Metadata exposes
`titleMode`, `titleLocked`, and `titleSource`. Manual and automatic changes share
one notification path. Unknown fields remain tolerated.

VSCode consumes metadata independently of answer output. The tree, pickers and
notebook status bar display titles. Task Title Settings offers rename, clear/unlock,
mode selection and regeneration. Names never rename files, change notebook URIs or
dirty snapshots. Offline display never launches a kernel solely for metadata; no
polling is introduced.
