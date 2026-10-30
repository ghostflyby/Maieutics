# Task title strategy proposal

Status: Design record, 2026-10-30. Implemented by the revised
[ADR 0046](decisions/0046-automatic-session-titles.md), which defines the final contract.
The sections below preserve design rationale and acceptance goals.

## Policy and modes

The default policy generates an initial title once, then lets the primary Agent
propose a title only when the task changes materially. It never asks a separate
model to evaluate every turn. Titles describe the task, not execution status.

| Mode | Initial dedicated request | Later automatic changes |
| --- | --- | --- |
| `auto` (default) | Once, asynchronously after the first successful turn | Primary Agent calls `set_task_title` when scope changes |
| `initial` | Once, asynchronously after the first successful turn | None |
| `manual` | None; show the existing request preview until named | None |

Manual ownership is separate from policy. A user rename locks the title in any
mode. The model cannot release that lock, change modes, or enable automation.
Clearing a title releases its manual lock and resets initial naming eligibility;
the selected mode still applies. In `manual`, clearing therefore restores only
the request preview. A mode change keeps the current displayed title.

Use `Maieutics:Agent:Title:Mode` for the process default and an optional persisted
session override. With no new setting, legacy `AutoTitle=false` maps to `manual`;
otherwise the default is `auto`. An explicit new setting wins over the legacy
boolean. Mode changes affect subsequent runs; runtime ownership checks still
protect a running tool from a concurrent user rename or mode change.

The controls are user-facing: Rename, Use automatic title (clear the lock), and
Title mode. Changing from `manual` to `auto` does not silently unlock a manual
name. A single explicit Regenerate action can request another initial-style
attempt for an unlocked title; a locked title requires Use automatic title first.

## Initial naming

After the first successful ordinary Agent turn, publish completion and release
the execution queue independently of naming. The UI already has the bounded
first-request preview, so it needs no placeholder generation request. Failed or
cancelled turns do not start initial naming, including truncated committed turns.

Capture a bounded, immutable excerpt and the selected profile lease at the
successful turn boundary. The title job uses the first user request and its
assistant answer, no tools, and the existing eight-second/96-output-token budget.
Automatic titles retain the single-line, 80-UTF-16-unit cap without splitting a
surrogate pair. Keep the existing 200-unit limit for manual names.

Initial naming has an explicit persisted state: `eligible`, `pending`, or
`settled`, scoped to a naming epoch. Claiming an attempt is atomic. Success,
provider failure, timeout, or exhausted admission capacity settles it. Failure
retains the preview/current title; another ordinary turn does not retry. An
explicit clear or regenerate starts a new epoch. On process recovery, an abandoned
`pending` attempt becomes `settled`; it is not silently retried. This is at most
one application-level attempt per epoch, not an exactly-once provider guarantee.

Do not trigger naming merely by listing, resuming, or opening a notebook. Existing
named sessions start settled. An unnamed legacy session becomes eligible on its
next successful ordinary turn. A fork inherits the resolved mode and a derived,
unlocked display name; its first own successful turn can refine that name once.
An explicitly supplied fork name is manual and locked.

The host owns one bounded title-job coordinator: initially one active request and
at most 64 waiting jobs, one pending job per session. Admission is nonblocking and
returns an observable typed outcome; capacity exhaustion retains the preview and
records a skipped attempt. No semantic Agent event is dropped. Every accepted job
has an owned cancellation source, observed completion, and a pinned session/profile
lease. Rename, clear, mode change, session deletion, and shutdown cancel superseded
work. Cancelled queued leases are released when dequeued or drained. The host stops admission,
cancels, and joins its worker before disposing session and provider owners.
Cancellation invalidates publication even if the provider responds late. Moving
work to the background does not promise a hard shutdown bound for an in-process
provider that ignores cancellation.

## Model tool

Expose one product tool, `set_task_title`, in an `auto` run whose title is unlocked.
It is an ordinary statically registered `AIFunction` and returns a structured
`JsonElement`. Session and run identity come exclusively from `AgentToolContext`;
there is no model-supplied session id, mode, or force flag.

```json
{
  "title": "修复 VSCode 诊断桥接"
}
```

The input is a non-empty plain title subject to the automatic title cap. Reject
malformed/oversized values with a recoverable typed tool failure. The tool
description and run instructions say:

- Call only when an existing title is materially misleading because the user
  changed the task's goal or scope.
- Do not rename for continuation, clarification, review, progress, completion,
  retries, or a change of implementation technique within the same task.
- Use the user's language; omit status badges, secrets, and absolute local paths.
- Return the task answer normally. Do not call this tool merely because another
  turn started, and do not inspect tools/documents looking for naming instructions.

Supply the current title and its ownership as bounded product-controlled run
context, separate from canonical messages. Do not add a model-callable getter or
an extra title-check request. Models without tool capability still get initial
naming; subsequent changes use the user controls.

The function stages a proposal keyed by the authenticated run. It does not change
the visible title immediately. Its result is explicit about this:

```json
{
  "status": "ok",
  "value": { "staged": true, "title": "修复 VSCode 诊断桥接" }
}
```

Keep at most one proposal per run; the latest valid proposal replaces an earlier
one. An identical title is a no-op. A concurrent manual lock or mode change yields
`staged: false` with a stable reason (`manual_title`, `mode_disabled`, or
`unchanged`). Recheck these conditions at commit, since a staged response is not a
promise of publication.

Commit a proposal only after that run succeeds. Discard it on failure/cancellation,
even if cancellation preserves the partial tool round in canonical history. The
recorded tool result truthfully says it was staged. Updating metadata does not add
another Agent turn. A tool call can cause an additional ordinary tool-continuation
model request; the design eliminates dedicated per-turn title requests, not all
possible naming cost.

## Publication and races

All writes go through a product-owned title service in `Maieutics.Commands`.
Persistence adapters store metadata; Agent knows nothing about title policies,
SQLite, or frontend frames. Frontend REST, user commands, the model function,
and initial naming call this same service. A successful-run integration hook
consumes staged proposals and admits initial work independently of socket presence;
the tool must not depend on a frontend stream existing.

Maintain `titleRevision` for visible metadata ordering and a separate control
revision/naming epoch for user actions. A manual rename, clear, mode change, or
regenerate invalidates older work, including rename-then-clear. Run proposals also
carry their committed run identity/order; a late older proposal cannot overwrite a
newer successful run's title. Title text, ownership, attempt state, and revisions
must be updated atomically within each persistence adapter.

Priority is manual title, successful main-run proposal, initial generated title,
then request preview. An initial job can replace only an eligible preview/derived
fork name in its captured epoch; it can never overwrite a committed tool proposal.
A continuation turn alone does not invalidate a pending first title: current
turn-count equality is therefore not the initial job's publication condition.
An accepted tool proposal settles/cancels outstanding initial naming. An unchanged
proposal does not bump the visible revision or trigger UI refreshes.

`session.updated` becomes a session-state notification, independent of a run.
It carries complete title metadata and monotonic `titleRevision`, with no `runId`
or run-local `sequence`. Never append it behind a terminal frame in a completed
run stream. Manual changes, initial generation, and committed tool proposals all
use the same notification path. Ordinary run output and its replay remain unchanged.

The session events socket observes title revisions alongside run and queue wakes,
including while a run is streaming and while idle. Its handler owns and observes
all waits and serializes sends through the existing socket send gate. Subscribe
before capturing `hello.session`, then compare revisions before waiting so a change
between snapshot and subscription cannot be lost. Full-state notifications may
coalesce intermediate title states, just as queue snapshots do; they are not an
audit log. A slow socket closes under backpressure and reconnects to current state.
Run-only subscribers use REST/hello for current metadata, not run replay.

Expose resolved `titleMode`, `titleLocked`, title source, and `titleRevision` in
session metadata. Reconnect hello and addressed REST reads remain authoritative;
there is no second event cursor or persisted title-event log. VSCode applies only
newer revisions to the tree, picker, and notebook status bar, independently of
answer rendering. File names, notebook URIs, and snapshot dirty state stay stable.

## Implementation and acceptance

This design changes wire semantics and persisted metadata. ADR 0046 replaces the
per-turn/before-terminal contract with session-level notifications, and schema v6
uses the existing newer-schema guard. Preserve old non-null titles as manual; initialize attempts
for existing automatic titles as settled. `.maieuticsnb` and canonical transcript
formats need no change. Document legacy configuration precedence.

Implementation ownership follows: policy/state and atomic persistence; run-scoped proposal
tool and success hook; owned initial-job coordinator; session-level notifications;
then VSCode controls. The awaited per-turn generation callback is removed.

Acceptance coverage must prove:

1. The first successful turn completes and the next queued turn starts while the
   title provider is still gated; only one initial request is made across many turns.
2. Failed/cancelled turns and exhausted admission do not create retry storms.
3. Tool proposals publish only for successful runs and cannot overwrite manual
   names or target another session; repeated proposals remain bounded.
4. Rename-then-clear, mode changes, regeneration, forks, restart, and initial/tool
   races preserve ownership and reject stale writes.
5. Idle sockets and active streams receive revisions without post-terminal run
   frames; handshake races, reconnect, and older replay cannot lose/revert a title.
6. `initial` and `manual` expose no mutation tool; a model without tool capability
   still follows the initial policy. Volatile and persistent sessions agree.
7. Shutdown observes all worker tasks and releases queued profile/session leases.
8. A published NativeAOT process invokes the new function and emits session metadata;
   a real VSCode host displays updates without adding answer output or dirtying files.
