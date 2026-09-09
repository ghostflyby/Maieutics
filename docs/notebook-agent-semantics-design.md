# Notebook Agent Semantics: Cells as Conversation History (Design)

Implemented — Phases A–C shipped on the `feat/notebook-agent-semantics`
branch (cell states + run gate + interrupt; server fork; tree lineage).
Companion to `docs/session-views-design.md`
(sessions, titles, and VFS views), ADR 0009 (immutable turns, fork model),
and `docs/agent-ux-gap-analysis.md` (what still separates this UX from
mainstream coding agents).
Research question: what should the notebook's *interpreter-shaped* UI mean for
a conversation engine, and how much of the mainstream agent UX — edit a
message and continue from there, regenerate, branch — can a notebook carry?

## Problem

Today every executed cell submits its text as a **new turn appended to the
pinned session**. Running an old cell neither replays nor rewinds — it re-asks
history as a new question. Nothing binds a cell to the turn it produced: the
structured turn snapshot (`application/vnd.maieutics.turn+json`) carries no
`runId`, the serializer keeps no per-cell metadata, and the committed-history
boundary is invisible. The notebook *looks* like a mutable worksheet while the
server treats it as an append-only conversation.

North star: mainstream agent UX. The conversation is a linear immutable log
with a composer at the bottom; editing an earlier message rewinds to that
point and continues from the edit (old branch preserved); regenerate re-asks
from a chosen point. This design maps each notebook UI operation onto that
model instead of kernel-interpreter semantics.

## Principles

1. **History is immutable** (ADR 0009). Changing "what the conversation says"
   is always a fork to a new head; nothing is ever truncated or rewritten.
2. **One cell is one turn** (invariant 2) — and after this design, provably:
   every executed cell carries the identity of the turn it created.
3. **View ≠ history.** The notebook document and `.maieuticsnb` snapshots are
   local views (invariant 13); ops like clear-output or undo never touch the
   conversation, and conversation ops (fork) are explicit server actions.
4. **Interpreter verbs that have no agent meaning fail loudly with the agent
   alternative offered**, never silently do the wrong thing (current running
   an old cell does the wrong thing silently).

## Cell state model

A Code cell is in exactly one of these states, derivable locally:

| State | Meaning | Derived from |
|---|---|---|
| `committed` | The cell created a turn that is part of the pinned session's history | Cell carries a turn binding and the text matches the binding's snapshot |
| `stale` | A committed cell whose text was edited after the run | Binding exists, text differs from the snapshot's input text |
| `pending` | Never executed | No binding |

The **commit frontier** is the last committed cell; everything below it is the
composer region. Bindings are stored per cell and survive save/reopen:

- The turn snapshot written to the answer output gains `runId` (additive;
  `TurnView` already knows it) and the submitted input text (`input`), so a
  binding needs no extra fetches.
- `CellSnapshot` gains an optional `turn?: { runId: string; input: string }`
  persisted next to the output — the input rides along because stale
  detection must work after save/reopen, not just live. Note: the codec's
  header comment claims unknown fields are preserved, but
  `parseNotebook`/`parseCell` actually drop them — the binding must be an
  explicit field, and the comment gets fixed in the same change. Live cells
  keep the same binding in cell metadata (`maieuticsTurn`), which is the
  authoritative copy: it survives clear-output, so the frontier never
  depends on outputs being present.
- Notebook metadata keeps only the pinned session id (`maieuticsSessionId`),
  unchanged.

## Operation semantics table

The core of this design: every operation the VSCode notebook UI already
offers, with its agent meaning, enforcement point, and phase. Enforcement
points: `executeHandler` (the single choke point all Run variants funnel
through), cell status bar (`registerNotebookCellStatusBarItemProvider`),
document events (`onDidChangeNotebookDocument`), and typed refusals.

| Notebook UI operation | Agent semantic | Enforcement | Phase |
|---|---|---|---|
| Run a `pending` cell | Submit one new turn (today's behavior) | — | — |
| Run a `committed`, **unedited** cell | **Regenerate**: fork at this cell and re-run its text as the fork's first turn; old branch kept | `executeHandler` detects binding + unchanged text → fork flow (§Fork) | B |
| Run a `committed`, **edited** (`stale`) cell | **Edit and continue**: fork at this cell with the edited text as the first turn — mainstream edit-rewind | same detection, text differs | B |
| Run a committed cell, Phase A (no fork yet) | Typed refusal toast: "This cell is committed history. New cells continue below; forking arrives in a later update." Never appends a duplicate turn | `executeHandler` | A |
| **Run Above** | Every cell above the frontier is committed → nothing to run; one toast explaining history is immutable and pointing at "Continue here" | `executeHandler` receives exactly those cells → all committed | A |
| **Run Below / Run All** | Sequentially submit the pending cells below/after the frontier as turns (the controller's per-notebook queue already serializes them; committed cells are skipped, not re-run) | `executeHandler` filters to pending | A |
| **Insert cell** between committed cells | Allowed; it is an uncommitted insertion whose position marks a future fork point. Status bar of the neighboring committed cell shows the frontier marker | passive | A |
| **Delete a committed cell** | Refused with explanation (history is not view-owned). The cell reverts on next reopen; v1 keeps the edit visible with a `stale`-like "deleted content" warning instead of fighting the undo stack | warn on document event; snapshot materialization restores truth | A |
| **Move / convert a committed cell** | Same class as delete: warn, never silently accepted as history change | document event | A |
| **Duplicate / copy-paste a committed cell** | Creates a `pending` copy — a re-ask; runs as a normal new turn | — (already correct once bindings mark the original) | A |
| Edit a committed cell (no run) | `stale` badge in the cell status bar + one-time toast: "Edited history — running will continue from here (fork)" | document event + text-vs-snapshot diff | A |
| **Interrupt / stop** | Cancel the in-flight run (cooperative cancel, invariant 9) | set `NotebookController.interruptHandler` — its presence is what adds the stop button; once set, the cell's cancellation token no longer fires, so the handler itself routes to `POST /v1/agent/runs/{runId}/cancel` | A |
| **Restart** (kernel verb) | No agent meaning; surfaces a typed explanation and maps to "abort the in-flight run" when one is active | `interruptHandler`-adjacent; notebook executes nothing | A |
| Clear output / clear all outputs | View-only; allowed. The commit frontier lives in bindings, not outputs, so clearing never demotes a cell | — | A |
| **Undo after a run** | View-level only; one-time toast: "Undo reverts the document, not the conversation. Use Continue-here/fork to change the conversation." | document event | A |
| Save / reopen `.maieuticsnb` | Bindings round-trip; reopen reconstructs committed/stale/pending states without the server | codec extension | A |
| **Continue here** (new affordance) | Pure rewind: fork at this cell without editing — the composer moves to this position; the old branch is preserved and switchable | cell status bar item on every committed cell (click → command) | B |
| **Branch switch** | Switching among a session's branches = session resume; branches render in the tree under their root (lineage section), titled `"<title> · branch @ turn N"` | tree grouping via descriptor lineage | C |

Deleting pending cells, reordering pending cells, markdown cells, and output
rendering stay notebook-native and untouched.

## Fork model (Phase B server work)

True fork — new head referencing existing immutable turns, zero copies — per
ADR 0009. The family layout in `MaieuticsAgentSessionManager` already
anticipates it (`ResolveFamily` scans family databases for member sessions);
the missing pieces are storage and one session-level operation.

**Schema v4** (transcript store): `sessions` gains `parent_session_id TEXT
NULL` and `fork_point_seq INTEGER NULL`. `turns` stays append-only per
session. A fork writes one new row in the **root family's** database (family
id = root ancestor's id — `AppendTurn` and `SetTitle` target the family store
via the same resolution) and `LoadTranscript` walks the parent chain: ancestor
turns `1..fork_point_seq`, then the fork's own turns. Version-stepped
migration as in schema v3.

- `AgentSession.Fork(profileProvider, store, sourceId, forkPointSeq)` — like
  `Resume`, but the restored state ends at the fork point and the session id
  is new. Resuming a zero-turn fork works because the chain supplies the
  prefix (this also fixes the "zero-turn session cannot Resume" gap).
- `MaieuticsAgentSessionManager.Fork(sourceId, forkPointSeq, title?)` —
  resolves the family, creates the fork row, returns the new id. Auto-title:
  source title/preview + `" · branch @ turn N"`.
- REST: `POST /v1/agent/sessions/{sid}/fork` body `{"runId": "…"}` or
  `{"seq": n}` (run ids resolve to seqs server-side; the frontend knows the
  cell's `runId`, not its seq) → `200 {id, title}`. Not active-session-gated,
  like rename. Fork of a fork chains through the parent link.
- Descriptor/wire: `AgentSessionDescriptor` and `FrontendStoredSession` gain
  `parentSessionId?`/`forkPointSeq?` (additive); the sessions tree groups
  branches under their root (Phase C presentation).
- GC: reachability already planned from live session heads (ADR 0009) — a
  fork head pins the shared prefix; nothing extra.
- Command: `%session fork <id-prefix> <runId|seq>`; `%session list` shows
  lineage in the Title column.

A copy-prefix fork (dup turns into a fresh family db) is explicitly rejected:
it contradicts ADR 0009's constant-cost goal and doubles every later GC,
backup, and listing surface. It is the fallback only if v4 must wait
indefinitely.

## Extension flow for Run-on-committed (Phase B)

1. `executeHandler` sees a committed cell; edited → fork-with-new-text,
   unedited → regenerate (fork + same text). A confirmation surfaces once per
   document per session ("Run this cell? It continues from turn N as a new
   branch") — mainstream UX asks nothing for pure-regenerate, so the dialog is
   skippable by setting.
2. `POST fork` → new session id.
3. Re-pin: notebook metadata `maieuticsSessionId` = fork id (the existing
   `writeSessionId` edit path).
4. View rewrite: cells below the fork point are removed from the document and
   a toast notes "Original branch kept as '<title>' — switch in the Sessions
   tree". The forked session's snapshot contains only the prefix; the old
   session stays complete on the server.
5. The edited/first cell executes as the fork's first turn via the normal
   submit path.

Phase A shipped the state model, refusals, Run-Below queueing, the
`interruptHandler` cancellation mapping, the cell status-bar frontier, and the
codec/binding work; Phase B swapped the single-cell refusal for the fork flow
in the same release (the refusal survives only as the Run Above / mixed-run
explanation). Notebook-toolbar menu contributions landed for notebook-level
actions; per-cell fork affordances live in the status bar.

## Customizable surface inventory (verified against @types/vscode 1.99.1)

What the notebook UI actually lets an extension reshape — each surface with
its agent use. This is the evidence base for the table above.

**Extension API (stable):**

| Surface | Gives us | Agent use |
|---|---|---|
| `NotebookController` (`executeHandler`) | Single choke point for Run / Run Above / Run Below / Run All — receives exactly the targeted cells | Refuse/fork committed cells (Phase A refusal, Phase B fork); filter Run Below/All to pending cells |
| `NotebookController.interruptHandler` | Presence adds the toolbar/cell stop button; the cell cancellation token no longer fires when set | Map stop → cooperative run cancel (invariant 9); our current token-based cancel keeps working for programmatic paths |
| `NotebookCellExecution` output writes take a **cell argument** | One execution may rewrite outputs on *other* cells | The Phase B fork rewrite (clear/mark below-cells) can run inside the executing cell's scope, no separate edit race |
| `registerNotebookCellStatusBarItemProvider` (+ `onDidChangeCellStatusBarItems`) | Per-cell computed items with `command` (arguments allowed), tooltip, priority; re-queryable via event | The committed-frontier marker, `stale` badge, and per-cell **Continue here / Regenerate** buttons — the only per-cell-state-aware UI surface |
| Contribution-point menus `notebook/toolbar`, `notebook/cell/title`, `notebook/cell/execute`, `notebook/kernelSource` | Buttons/entries in the notebook toolbar and cell title area, `when`-filtered (e.g. `notebookType == 'maieutics-notebook'`) | Notebook-level actions (New session, branch switcher, GC/repair) on the toolbar; cell-level fallbacks (Continue here) — but menus have **no per-cell state contexts**, so per-cell visibility still belongs to the status bar |
| `NotebookDocumentContentOptions` (`transientOutputs`, `transientCellMetadata`, `transientDocumentMetadata`) on serializer registration | Controls which metadata changes fire change events and appear in diffs | If bindings move from outputs to cell metadata, keep them non-transient (they are persisted by our codec); outputs stay non-transient so snapshots round-trip |
| `onDidChangeNotebookDocument` | Edit/delete/move detection on committed cells | `stale` badge, one-time warnings, (opt-in) revert |
| `NotebookCellExecution` + `onDidChangeNotebookCellExecutionState`-style UI | Streaming paint, status, execution order (already used) | — |
| `workspace.applyEdit(edit, {label, needsConfirmation})` metadata | Labeled workspace edits | The fork view-rewrite becomes an undoable, labeled edit ("Continue from turn N") instead of an opaque mutation — **1.99 reality: only `needsConfirmation` exists on stable `WorkspaceEditMetadata`; the fork rewrite ships unlabeled and the toast carries the story** |
| `createRendererMessaging` + `contributes.notebookRenderer` (preloads, `requiresMessaging`) | Extension ↔ output-renderer channel (already used by widgets) | Turn timeline renderer can gain branch/fork affordances rendered inside outputs |
| `NotebookController.updateNotebookAffinity` / `openNotebookDocument(notebookType, content)` | Kernel-picker preference; programmatic notebook creation | Pre-selecting our controller; creating the forked notebook view programmatically |

**Contribution points already in use:** `notebooks` (type + `*.maieuticsnb`
selector), `notebookRenderer` (turn timeline, widgets), `views`/`menus` for
the sessions tree. New in this design: `notebook/toolbar` and
`notebook/cell/title` entries.

**Explicitly NOT customizable (stable API, 1.99):**

- No per-cell read-only/freeze — long-standing upstream requests
  (microsoft/vscode#237074, #158715, #95662). Editing prevention stays
  advisory (badges + toasts); enforcement lives at `executeHandler` and the
  server (invariant: history is immutable regardless of the view). The
  early-2020 `editable`/`runnable` cell and document metadata (commit
  `64e15885`) is dead: current workbench source has no typed metadata fields
  (`NotebookCellMetadata` is a free-form record), the `notebookCellRunnable`
  context key no longer exists (running is kernel-driven), and
  `notebookCellEditable` now derives purely from the editor-level read-only
  state (`!notebookEditor.isReadOnly`), ignoring cell metadata.
- Built-in notebook commands cannot be removed or overridden — "Run Above"
  stays in the UI; it is made harmless at the choke point instead.
- Menu `when` clauses have no per-cell content state (only execution-state and
  notebook-type contexts), so committed/pending visibility cannot drive menus —
  status bar items are the per-cell surface.
- No stable `NotebookEditor` decoration API; no kernel-picker or variable-view
  customization (proposed APIs only, out of scope for a shipped extension).
- Workbench settings (`notebook.cellToolbarLocation`,
  `notebook.consolidatedRunButton`, …) are user-owned; we adapt, not override.
- Cell editors are ordinary text documents (`vscode-notebook-cell` scheme), so
  language features (completion, hovers, code lenses) apply — already used for
  `%`-command completion; useful later for cell-edge affordances like a
  "continue here" code lens.

## Testing plan

- Extension (`deno test`): cell-state derivation (binding + text diff), run
  filtering (pending-only for Run Below/All, refusal list for Run Above),
  label collapse for stale cells, codec round-trip of `turn` bindings, old-
  snapshot tolerance (missing `runId` degrades to pending).
- Server: migration v3→v4 fixtures; `LoadTranscript` chain walk (fork of
  fork, deep chains); `Fork` zero-turn resume; fork row lands in the root
  family db; descriptor lineage fields; REST fork (active, non-active,
  unknown run → 404, seq out of range → 400); `%session fork` command; GC
  keeps a forked prefix alive.
- Integration: FrontendApi fork happy path; turn snapshot carries `runId`.

## Open questions

1. Should deleting a committed cell (v1 warn) escalate to a v2
   "fork-without-this-cell" affordance, or stay view-only forever?
2. Branch presentation in the tree: nested under the root, or a flat list
   with a lineage badge? (Session-views §4 currently has no lineage section.)
3. Does "Run Below" into a position that crosses a fork intent (insertion
   between committed cells) need a pre-flight fork, or does the insertion
   become the fork's first turn implicitly? (Lean: implicit — the insertion
   is the fork point.)
4. Snapshot portability: a `.maieuticsnb` opened against a different server
   has bindings that reference unknown runs — treat as pending (degrade), or
   offer "re-attach + fork at cell" on first run?
