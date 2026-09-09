# Agent UX Gap Analysis — vs. Mainstream Coding Agents

Status: Draft (living document — tick items off as they land)

Date: 2026-09-09

Scope: what separates the Maieutics notebook frontend from the mainstream
agent UX of 2025–26 (Claude Code, Cursor, Copilot agent mode, ChatGPT),
beyond what has already shipped. Each item states the verified current
behavior, the mainstream reference, the proposed direction, and the layer and
rough size of the change. Ordered within groups by impact.

Method: code audit of this repository (every "Current" claim below was
verified against the tree on this date) plus a survey of mainstream products
(sources at the end). Companion to `docs/notebook-agent-semantics-design.md`
(cell states, run gate, fork) and `docs/session-views-design.md` (sessions,
titles, VFS views); assumes both are shipped.

## Baseline: where we already match or lead

For balance, the skeleton is at parity or stronger:

- **Immutable history + edit-means-branch.** Running a committed cell forks
  (regenerate, or continue-from-your-edit when drifted); old branch kept and
  switchable — the ChatGPT/Claude edit-rewind model, on an ADR 0009
  zero-copy fork.
- **Portable snapshots.** `.maieuticsnb` round-trips turn bindings, outputs,
  and history states; mainstream chats have no equivalent shareable artifact.
- **Typed failures and cooperative cancellation** at every boundary;
  cell-level stop via `interruptHandler` → `POST /v1/agent/runs/{id}/cancel`.
- **Cell state model** (committed/stale/pending) with a commit frontier,
  status-bar badges, and loud refusals for interpreter verbs that have no
  agent meaning (Run Above, Run All re-runs).

## A. In-conversation experience (extension/view layer, cheap)

Items A1–A6 are implemented (A2 only in part — see its section).

### A1. Branch switcher at the cell — DONE

Priority: High. Type: Navigation. Status: **Implemented** (status-bar branch
badge on committed cells → `maieutics.switchBranch` picks a family member —
parent, siblings, descendants — resumes it and opens its notebook view).

**Mainstream.** Inline version navigation on historical messages
(`< 1/2 >`); ChatGPT's recent removal of it caused a user revolt
(sources below) — evidence of how load-bearing the affordance is.

**Current.** Forks are *visible* (tree lineage adjacency, branch badge,
auto-title "· branch @ turn N") but not *reachable in place*: switching a
branch = find the sibling session in the tree and open its notebook. The
fork point in the notebook carries no affordance listing sibling branches.

**Proposed shape.** A cell status-bar item on committed cells ("branches ▾")
that lists sibling sessions (same `parentSessionId`) plus the parent; picking
one resumes it and re-materializes the notebook view. Protocol needs nothing
new (list sessions + resume + transcript). Extension-only.

Size: S–M. Phase C follow-up.

### A2. Cancel/failure wipes the partial answer — PARTIALLY DONE

Priority: High. Type: Correctness of streamed experience. Status: view piece
**implemented** (failure paint appends to the streamed segments; only an empty
cell collapses to the error). The runtime piece below remains open and needs
its own design review.

**Mainstream.** Interrupted answers keep their partial text and offer
"continue"; Claude Code's double-Esc context rewind is the terminal-flavored
cousin.

**Current (verified).** Server cancel emits `run.failed` with code
`run_cancelled` (`FrontendRunStream`); the controller's `paintFinal` failed
branch **replaces** the cell output with the error block, discarding the
already-painted partial markdown. Runtime-side the whole turn rolls back
(atomic commit), so a *continued* run would restart from scratch.

**Proposed shape.** Two independent pieces:

1. View-only (S): on `run.failed`/cancel, keep the partial segments and
   append the error instead of replacing (`RunExecution.paintFinal`); the
   structured snapshot already tolerates an error alongside text.
2. Runtime (L, separate design): commit cancelled turns as truncated turns
   (or a "continue" resumption API). This touches the atomic-commit
   invariant and needs its own review; do not bundle with (1).

### A3. Per-turn token/cost/model surface — DONE

Priority: Medium. Type: Transparency. Status: **Implemented** (runtime sums
`UsageContent` across iterations into `AgentRunResult.Usage`; `run.completed`
carries `model` + `usage`; the structured snapshot and timeline renderer
render both; a status-bar badge totals the active notebook's usage).

**Mainstream.** Model badge per answer everywhere; coding agents additionally
surface context usage (Claude Code `/context`, Cursor's meter).

**Current (verified).** The wire has no usage fields; the transcript carries
`model` identity but the turn-timeline renderer never shows it; the runtime
already captures `AgentModelIdentity` per run — it just stops at the
transcript.

**Proposed shape.** Additive wire: `run.completed` carries
`usage?: {inputTokens, outputTokens}` and the structured turn snapshot gains
`model?`; the timeline renderer renders both, and a status-bar item shows the
pinned session's running total. Source-generated JSON only (NativeAOT path).

Size: S (wire+renderer) — usage numbers require the provider adapter to
forward `UsageContent` from Microsoft.Extensions.AI, which it already
records on iterations.

### A4. Tool progress — DONE for args/durations

Priority: Medium. Type: Progress rendering. Status: **Implemented** for
argument previews and receipt-time durations (activity lines, structured
snapshot, timeline renderer); the plan/todo list view still needs a tool
contract — see the note below.

**Mainstream.** Step panels with argument previews, durations, expandable
results (Cursor), and plan/todo lists (Claude Code).

**Current.** `⏳/✅/❌ tool-name` lines; the timeline renderer shows the same
plus truncation/errors. Protocol frames already carry `arguments` and
`result` — the detail is on the wire, just not rendered.

**Proposed shape.** Renderer: args preview + duration (frames have
timestamps? — if not, additive). Plan/todo lists need a tool-semantics
agreement first (a `plan` tool whose state the frontend renders specially) —
design before building.

Size: S (args/duration) / M (todo view, needs a tool contract).

### A5. Queued follow-up while a run streams — DONE

Priority: Medium. Type: Composer flow. Status: **Implemented**
(`maieutics.queueTurn`: input box → cell inserted below the commit frontier →
submits through the per-notebook queue, so it runs after whatever is in
flight).

**Mainstream.** Chat UIs accept a message during generation and submit it
when the run ends.

**Current.** Concurrent submits are typed `agent_busy` (invariant 4 —
correct). The per-notebook execution queue serializes cells, so a cell *run*
while another streams queues naturally; but there is no composer affordance
(no "queued" state, no auto-run on submit).

**Proposed shape.** Extension-only: a "queue turn" command/status on the
active notebook; when the in-flight run settles, the controller submits the
queued cell. No protocol change.

Size: S–M.

### A6. Regenerate cannot switch the model — DONE

Priority: Low. Type: Fork flow completeness. Status: **Implemented** (fork
request gains `profileId`; `GET /v1/model/profiles` lists the selectable
profiles; the fork confirmation offers "Fork with another model…" with a
profile picker).

**Mainstream.** ChatGPT/POE regenerate with an alternate model.

**Current.** The fork flow keeps the active profile; `%model use` is a
separate world.

**Proposed shape.** Optional `profileId` on the fork request (server passes
an override into the fork's first run or sets the session override). Small
wire addition; needs profile-resolution rules (which profiles are legal
post-reload).

Size: S wire / M policy.

## B. Coding-agent core (new tools/protocol, the real gap)

### B1. File changes have no diff review — the most substantive gap

Priority: High. Type: Structured editing + review.

**Mainstream.** Cursor's review mode: unified diff with inline
Keep/Undo per file, surfaced both in editor and chat panel; referenced as
the UX benchmark by other vendors (JetBrains YouTrack below). Checkpoints
(see B2) are a *separate* mechanism from review — restore vs. accept/reject.

**Current.** The agent edits files through terminal commands (opaque to the
frontend). There is no structured file-edit tool, so per-edit review is not
just missing — it is currently impossible.

**Proposed shape.** Three pieces:

1. A structured `workspace_edit` tool (apply patch / create / delete) whose
   results carry content-diffs as structured tool output (invariant 12:
   structured until an output adapter renders).
2. Wire: tool events already carry arguments/results; add an edit-descriptor
   shape and a renderer (diff view in the cell output / timeline).
3. Accept/reject semantics: either approval-gated (B3) or post-hoc
   (revert = inverse patch via the same tool). Keep rejections typed and
   recoverable (invariant 18).

Size: L overall; item 1 is the prerequisite for B2/B3 too.

### B2. No workspace checkpoint/rollback

Priority: High. Type: Safety net.

**Mainstream.** Claude Code checkpointing: auto-archive before each edit,
`/rewind` with three restore options (conversation only / code only /
both); even Codex has a feature request to match it. Claude Code
officially does **not** track bash side effects — an honest boundary.

**Current.** Fork rewinds the *conversation*; the workspace is untouched and
untracked. Tool-made file changes are not restorable.

**Proposed shape.** Scope checkpoints to the structured edit tool (B1):
each applied edit is journalled (content-addressed — the object store
already exists) so "restore to turn N" replays inverse patches. Terminal
side effects stay untracked, exactly like Claude Code's boundary — document
it rather than fake it. Pairs naturally with the fork flow ("fork here" +
"restore workspace to here").

Size: M on top of B1.

### B3. No human-in-the-loop tool approval gate

Priority: High. Type: Runtime pause/resume + approval UI.

**Mainstream.** The emerging standard: pause before a risky tool call,
surface a structured approval (diff preview + approve/reject/always-allow),
resume on human decision, with allow/ask/deny rule sets (Claude Code,
Copilot agent mode, Cursor). AG-UI standardizes the event-stream shape.

**Current (verified).** No approval/consent mechanism anywhere in
`Maieutics.Agent` or `Maieutics.Frontend`. The permission module enforces
static allowlists at launch; there is no runtime pause channel. The REPL
`input.request` frame proves the protocol *shape* (server asks, frontend
answers) but is REPL-scoped.

**Proposed shape.**

1. Agent runtime: a tool-execution gate — a tool marked `requiresApproval`
   suspends the tool loop (bounded wait), emits a typed event, and resumes
   on an answer. Must respect the structured-concurrency rules: the wait is
   cancellable, bounded, and survives disconnects (decision can arrive on
   reconnect).
2. Wire: `tool.approval_request` frame + `POST /v1/agent/approvals/{id}`
   (decision: allow / deny / always-allow-session), reusing the
   input-request plumbing as the precedent.
3. Policy: feed "always-allow" back into the session's permission overlay
   (the layered policy already exists; denials win).
4. Frontend: approval UI in the cell (diff preview when the tool is B1's
   edit tool — B1 and B3 compose).

Size: L (runtime + wire + UI). Highest architectural value: unlocks the
safety story for B1's edit tool.

## C. System-level (architectural, most expensive)

### C1. Single active session blocks parallel conversations

Priority: Medium. Type: De-scaffolding (inherited, not mandated).

**Provenance (checked against git history, 2026-09-09).** The
single-active-session semantics are inherited scaffolding, not a recorded
requirement: `MaieuticsAgentSessionManager` (commit `68e15da`, Jupyter-kernel
era) delegates to one `current` session because a kernel is one conversation
by nature; the web protocol (commit `7246ba6`, ADR 0023) then mirrored the
manager it found — singular `GET /v1/agent/session`, `EnsureActive` gating —
while deliberately keeping every route session-addressed ("forward-compatible
with multi-session" per the protocol doc). No ADR, invariant, or user request
mandates it; AGENTS.md invariant 1 requires one authoritative *history*
(satisfied per session), and invariant 4 is the per-session run gate. The
extension's session pinning exists purely to route around this limit.

**Mainstream.** Multi-tab/multi-conversation parallelism is table stakes
for chat products; Cursor runs background agents.

**Current.** Turns, transcript, GC, and the events/comms sockets are served
for the process's one active session only; a second session address gets
`session_not_active`/404. Run streams are already per-run and
session-scoped; family stores are already cached per family.

**Proposed shape.** Multi-homing: the manager keeps N live `AgentSession`s
(one gate per session, already the invariant-4 semantic), `EnsureActive`
becomes "resolve the session, gate on it", events/comms drop the active
check, and the extension deletes the pin/resume alternation. Run-registry
keying and profile selection are the only real design questions (which
session owns a `%model use` override?). Worth a short ADR when attempted —
as a feature design, not an architecture exception.

Size: M–L (revised down from XL once treated as de-scaffolding).
Follow-up: `docs/multi-session-migration.md` (2026-09-09) carries the full
feasibility study, verified coupling inventory, target shape (including the
decision that the model-profile override is session-scoped), and a four-phase
plan.

### C2. Multimodal input has no frontend path

Priority: Medium. Type: Input modality.

**Mainstream.** Image/file attachment is standard (ChatGPT, Claude,
Copilot).

**Current (verified).** The *runtime is ready*: `AgentSession.ValidateInput`
already accepts `DataContent` (`AgentBlobContent` references) — but the
frontend submits `{text}` only, and cells have no attachment affordance.

**Proposed shape.** Wire: `turns` request gains typed attachments
(references into the object store — never base64 through the wire,
invariant 26; the existing object ingest path serves). Frontend: drop/file
picker on cells → ingest object → submit reference. NativeAOT-safe
source-generated shapes.

Size: M.

### C3. Long-conversation compaction is invisible

Priority: Medium. Type: Transparency.

**Mainstream.** Claude Code auto-compact with a visible indicator; ChatGPT
summarizes older history.

**Current.** The runtime evicts complete turns silently
(`MaxRetainedTurns`/`MaxHistoryBytes`); the user never learns that provider
replay is truncated (canonical history stays intact).

**Proposed shape.** Emit a typed event when eviction first trims a turn
(additive frame); status-bar "history trimmed" hint; a `%session compact`
manual command can come later (it needs a summarization design).

Size: S for the indicator / M for manual compact.

## D. Polish

| Item | Gap | Size |
|---|---|---|
| Failed turn has no Retry button | Re-running the cell *is* the retry (failed turns roll back), but the error output offers no affordance | S |
| No completion notification | Long runs finishing in a background notebook are silent; VSCode notification + tree badge | S |
| VFS full-text search resumes sessions | Opening/searching lens files auto-resumes (documented side effect); mainstream search is passive | M (needs a passive transcript read path) |
| `%model` / `%mcp` GUI surfaces | Still command-only; blocked on typed REST (`session-views-design.md` §6 Follow-up rows) | M |

## Recommended sequencing

1. **A1 (branch switcher) + A2(1) (keep partial on cancel)** — cheapest,
   highest perceived quality gain; extension-only.
2. **B1 item 1 (structured `workspace_edit` tool)** — the shared
   prerequisite: unlocks diff review (B1), scoped checkpoints (B2), and
   gives B3's approval UI something meaningful to preview.
3. **B3 (approval gate)** — runtime + wire; with B1 this completes the
   mainstream coding-agent safety loop.
4. **B2 (checkpoints) on top of the edit journal**, then A3/A4/A5 polish,
   then C2 (multimodal) and C3 (compaction indicator).
5. C1 (parallel sessions) stays an explicit, separately-reviewed ADR.

## Sources

- Claude Code checkpointing: <https://code.claude.com/docs/en/checkpointing> ·
  <https://code.claude.com/docs/en/agent-sdk/file-checkpointing> ·
  <https://alirezarezvani.medium.com/claude-code-rewind-5-patterns-after-a-3-hour-disaster-a9de9bce0372>
- Codex following /rewind: <https://github.com/openai/codex/issues/12558>
- ChatGPT branch navigation and its regression:
  <https://community.openai.com/t/chatgpt-web-update-removed-message-version-arrows-cannot-access-edited-message-history/1374666> ·
  <https://www.absolutegeeks.com/tech-news/chatgpt-adds-conversation-branching-a-feature-rivals-dont-yet-offer/>
- Cursor review/checkpoints: <https://cursor.com/learn/reviewing-testing> ·
  <https://stevekinney.com/courses/ai-development/cursor-checkpoints> ·
  <https://youtrack.jetbrains.com/projects/LLM/issues/LLM-27579/>
- HITL approval patterns:
  <https://www.matthewswong.com/en/blog/human-in-the-loop-ai-agents/> ·
  <https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/ui/ag-ui/human-in-the-loop> ·
  <https://www.scalekit.com/blog/human-in-the-loop-tool-calling>
