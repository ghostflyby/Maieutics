# Session Titles, Workspace Metadata, and Session Views (Design)

Draft — pre-implementation design for review; **the v1 scope is now
implemented** (store schema v3, rename endpoint + `%session rename`, workspace
grouping with the current-only filter, VFS lenses with lazy materialization,
gc/repair view actions). Titled VFS filenames and the model/MCP GUI surfaces
remain phase-2 items. Covers the VSCode extension's session tree
(`deno/maieutics-vscode`), the sessions virtual filesystem, and the smallest
server-side metadata additions that make both hierarchical. Companion to the
frontend protocol (`docs/web-frontend-protocol.md`), ADR 0009 (durable storage
shape), `docs/notebook-agent-semantics-design.md` (cell/turn bindings,
fork-on-old-cell, and agent semantics for notebook UI operations), and
`docs/agent-ux-gap-analysis.md` (remaining gaps vs. mainstream agent UX).

## Problem

1. The sessions tree and the `maieutics:` VFS are flat, single-root lists.
   With many sessions across projects the list is unusable: nothing indicates
   which project a session belongs to.
2. Sessions have no human-readable identity — the tree, the quick pick, the
   `%session list` table, and VFS filenames all show a UUID prefix
   (`id.slice(0, 12)`).
3. Browsing "sessions of the workspace I have open right now" is impossible:
   the persisted session row records nothing about where the session was
   created.

## Current state (verified)

| Area | Today |
|---|---|
| Session store | One SQLite db per fork family: `families/<familyId>/history.db`, schema `user_version=2`, `sessions(id, created_at, last_activity_at, turn_count)` (`Maieutics/Persistence/SqliteTranscriptStore.cs`) |
| Store location | Global user data root (`ApplicationPaths.AgentFamiliesRoot`), shared by all workspaces; one process serves one workspace, but the workspace is not persisted |
| Listing | `MaieuticsAgentSessionManager.ListStoredSessions()` scans every family db; sessions that never committed a turn have no row and are not listed |
| REST | `GET /v1/agent/sessions` → `{id, turns, createdAt, lastActivityAt}`; `POST /v1/agent/sessions` (new); `POST .../resume` — no title, no workspace, no rename |
| Tree | Flat: `New session` + `active` + stored sessions, label = 12-char id prefix (`sessionsTree.ts`) |
| VFS | Fixed shape: only `/sessions/<id>.maieuticsnb`; mount materializes **every** stored session eagerly, one transcript fetch per session (`vfs.ts`, `extension.ts syncSessions`) |
| Command language | `%session current|list|new|resume|gc|repair` — no rename |
| Workspace | `--workspace` sets `Maieutics:Workspace:Root`; `%workspace` can switch it at runtime; it feeds tool policy only |

## Goals

- Sessions carry a **title** (user-set), a **preview** (derived from the first
  committed user message), and a **workspace root** (stamped at creation).
- The tree is hierarchical: grouped by workspace, current workspace first and
  expanded, with an optional current-workspace-only filter.
- Titles show everywhere sessions are named: tree, quick pick, VFS-adjacent
  UI, `%session list`.
- The VFS becomes a set of **lenses** — derived directories with grouping
  semantics — instead of one flat folder, and stops fetching every transcript
  on mount.

## Non-goals (v1)

- Deleting/archiving stored sessions end-to-end (needs family-db removal +
  object GC); the VFS "delete discards the local view" semantics stay, but the
  context menu labels it "Forget Local View" so it stops looking like a
  server-side delete.
- Automatic titles from model summarization; renaming is manual.
- Storing titles in `.maieuticsnb` snapshots (the format tolerates unknown
  fields, so this can be added later without a version bump).
- Multi-root workspace sessions (one session records the workspace root the
  process was launched with; VSCode multi-root uses the first file folder, as
  the launcher does today).

## Design

### 1. Store: session metadata (schema version 3)

`sessions` gains three nullable columns:

```sql
ALTER TABLE sessions ADD COLUMN title TEXT;
ALTER TABLE sessions ADD COLUMN preview TEXT;
ALTER TABLE sessions ADD COLUMN workspace_root TEXT;
PRAGMA user_version=3;
```

`Migrate()` becomes version-stepped: one transaction converges a fresh
database and any older version (v0/v1/v2) identically to v3 — the idempotent
`CREATE ... IF NOT EXISTS` script runs first, then the three `ALTER`s (not
idempotent themselves, but guaranteed to run at most once by the version
gate), then `user_version=3`. A database newer than the build is still
refused.

Column semantics:

- `title` — user-set, bounded (≤ 200 chars). `NULL` until renamed. Empty
  string means "cleared" (display falls back to preview).
- `preview` — derived, write-once: the first user text part of the turn that
  creates the row, truncated to 200 chars. Filled by the `INSERT INTO
  sessions` path in `AppendTurn`; never updated afterwards.
- `workspace_root` — stamped at row creation from a workspace-root accessor
  passed to the store. Never updated afterwards.

Workspace stamping does **not** change `IAgentTranscriptStore.AppendTurn`:
the workspace is a property of the process, not of a turn, so the store takes
an optional `Func<string?>` accessor in its constructor (default `null`). The
composition root's store factory captures `Workspace.RootPath`. Stamping at
row creation (not at `StartNew`) is deliberate: a session that never commits
keeps no row (unchanged listing semantics), and a `%workspace` switch mid
process stamps later sessions with the then-current root, which is the honest
answer to "where was this session created".

**Rename.** The store gains one method, `SetTitle(AgentSessionId, string?)`:

- Upserts the row: `INSERT ... ON CONFLICT(id) DO UPDATE SET title = $title`.
  The insert branch creates a row with `created_at = last_activity_at = now`,
  `turn_count = 0`, `preview`/`workspace_root` from the same accessors used by
  `AppendTurn`.
- Creating the row on rename is deliberate: a session the user explicitly
  named must survive and be listed even before its first committed turn. The
  "sessions that never committed a turn have no row" comment stays true for
  sessions that were never renamed; zero-turn titled sessions appear in
  listings (they render like fresh sessions).
- Renaming works for **any** stored session — active or not — by resolving the
  family like `Resume` does.

`AgentSessionDescriptor` (`Maieutics.Agent/IAgentTranscriptStore.cs`) gains
`Title`, `Preview`, `WorkspaceRoot` (all nullable). `ListSessions` maps them.
Fake stores in tests gain trivial implementations.

### 2. Frontend protocol: additive changes (v1 stays v1)

All changes are additive fields/endpoints; the protocol tolerates unknown
fields, so an old extension against a new server and vice versa degrade to
today's behavior.

| Change | Shape |
|---|---|
| `GET /v1/agent/sessions` items gain | `"title": string?`, `"preview": string?`, `"workspaceRoot": string?` |
| `GET /v1/agent/session` gains | `"title": string?` (read from the store row; null for an uncommitted, never-renamed active session) |
| `GET /v1/agent/capabilities` gains | `"workspaceRoot": string?` (the process's current `Workspace.RootPath`; lets the extension detect attach-mode servers serving another workspace) |
| New: `POST /v1/agent/sessions/{sid}/rename` | Body `{"title": string}`; empty string clears. → `200 {"id", "title"}`. An unknown-but-well-formed id **creates its row** (zero turns; family = the session itself), so there is no 404 — the REST path has no prefix-resolution safety net, which is the accepted trade-off for renaming non-active sessions; `400 invalid_request` for a malformed id or a title > 200 chars. Not active-session-gated — renaming old sessions is the point |
| Command language | `%session rename <id-prefix> <title...>`; `SessionCommandMatches` gains `rename`; `%session list` table gains a Title column (preview fallback in parentheses) |

`FrontendSessionService.Rename` resolves the family through the session
manager (same path as `Resume`) and never calls `EnsureActive`.

Extension wire types mirror this: `StoredSession` gains `title?`, `preview?`,
`workspaceRoot?`; `SessionInfo` gains `title?`; `Capabilities` gains
`workspaceRoot?`; `FrontendClient` gains `renameSession(id, title)`.

### 3. Display-name resolution (shared rule)

Every surface resolves a session's display name with one fallback chain:

```
title → preview (first ~60 chars, single line) → id.slice(0, 12)
```

- Tree labels, quick-pick labels, and the `%session list` table all use it.
- The active-session node uses `SessionInfo.title` and does not depend on
  `listSessions` having loaded.
- Ids never disappear: the tree item description/tooltip keeps the full id;
  a `Copy Session Id` context action joins the tree menus.

### 4. Sessions tree: workspace grouping

`MaieuticsSessionsProvider` grows one extra element kind and resolves its data
from `listSessions` + `capabilities`:

```ts
type Element =
  | { kind: "new" }
  | { kind: "group"; key: string | null; label: string; current: boolean }
  | { kind: "active"; session: SessionInfo }
  | { kind: "stored"; session: StoredSession };
```

Shape (server unreachable still yields the `New` node):

```
Sessions
├─ ＋ New session
├─ ● <current workspace basename>            ← expanded, sessions of this workspace
│   ├─ ● Refactor vfs lenses                 ← active session (title), "active" badge
│   ├─ Fix flaky kernel test  12 turns · 2h ago
│   └─ a1b2c3d4e5f6  1 turn · yesterday      ← untitled: preview, then id prefix
├─ ▸ ~/repos/other-project                   ← collapsed
└─ ▸ Without workspace                       ← NULL workspace_root (old sessions)
```

Rules:

- Groups sort: current workspace first, then by most recent session activity;
  sessions within a group by `lastActivityAt` descending (server order).
- The active session renders inside its workspace group (not as a separate
  top-level entry); when it has committed no turn it appears even though no
  stored row exists, marked active.
- Group identity is the raw `workspaceRoot` string; the label is its basename.
  Matching against the current workspace compares case-insensitively on
  Windows/macOS and ignores trailing separators; unresolved symlinks are out
  of scope.
- **Current-workspace filter**: a checkable `view/title` toggle
  (`maieutics.filterCurrentWorkspace`, persisted in workspace state). On: only
  the current group renders. Default off. This is the "browse by current
  workspace" option, on top of the default grouping.
- Context menus (`contextValue`): stored sessions gain
  `maieutics.renameSession`; all session items gain copy-id. Rename opens an
  InputBox pre-filled with the current title, empty input clears.

Grouping/label logic lives in a pure module (`sessionGroups.ts`) so it is unit
testable under `deno test` like `sessionPin.ts`; the provider only fetches and
renders.

### 5. VFS: lens directories

The provider keeps one `maieutics:` scheme and one in-memory content map, but
the namespace becomes a small catalog of **lenses** — derived, overlapping
views over the same session set:

| Lens | Contents |
|---|---|
| `/sessions/<id>.maieuticsnb` | Every stored session (today's shape, unchanged — keeps restored mounts working) |
| `/workspace/<id>.maieuticsnb` | Sessions whose `workspaceRoot` matches the extension's current workspace |
| `/recent/<id>.maieuticsnb` | The 20 most recently active sessions (bounded) |

The namespace, concretely (one session shown with a full id, others
abbreviated; current window = workspace A):

```text
maieutics:/                                  ← exactly the three lens directories
├── sessions/                                ← full view: every stored session
│   ├── a1b2c3d4e5f6478890a1b2c3d4e5f6a7.maieuticsnb   (workspace A, active 2 h ago)
│   ├── b2c3d4e5f6a7478890a1b2c3d4e5f6a8b….maieuticsnb (workspace A, yesterday)
│   └── c3d4e5f6a7b8478890a1b2c3d4e5f6a9c….maieuticsnb (workspace B)
├── workspace/                               ← current window's workspace only
│   ├── a1b2c3d4….maieuticsnb
│   └── b2c3d4e5….maieuticsnb
└── recent/                                  ← 20 most recent, across workspaces
    └── a1b2c3d4….maieuticsnb
```

Phase 2 (titled filenames, section 6) changes only the file segment:
`refactor-vfs-lenses--a1b2c3d4.maieuticsnb`; the directories and membership
rules stay as drawn.

Lens rules:

- **The session id is the canonical handle.** Filenames stay
  `<id32>.maieuticsnb`. Validation has two layers: `stat`/`readFile`/
  `writeFile` accept any well-formed session filename whose id is in the
  descriptor cache (entries alias across lenses), while `readDirectory`
  applies the lens predicate. Malformed or unknown-session paths are refused
  with `NoPermissions`, fixing the existing wart where `/sessions/<junk>`
  could be written and never listed. Membership deliberately does not gate
  validity — see "Layout churn and open editors" below.
- Lenses are derived and read-only as *lenses*: `delete` still discards only
  the local view (the server remains authoritative, invariant 1); `rename` is
  refused in v1 (section 6 covers the titled-filename path); `stat` reports
  `mtime = lastActivityAt`, `ctime = createdAt` from the descriptor.
- Membership is computed from a cached descriptor map (`listSessions`
  result), refreshed by the same `syncSessions` path and on `refreshSessions`.
- `readDirectory("/")` lists the three lens directories; `createDirectory`
  accepts only lens roots (unchanged contract, generalized).

**Lazy materialization.** `syncSessions` stops fetching transcripts. It calls
`listSessions` once, caches descriptors, and fires change events;
`readDirectory`/`stat` answer from the cache. Content is materialized on
demand: `readFile` of a known-but-absent session fetches its transcript and
serializes the notebook (the `ensureVfsNotebook` path moves into the provider
via a client callback). Mounting a 200-session store goes from 201 HTTP calls
to 1; opening a notebook costs exactly the transcript it renders.

**One session, one canonical view (link semantics in userland).** VSCode's
`FileSystemProvider` has no link primitives: the interface has no
link/readlink members, `workspace.fs` offers no link operations, and
`FileType.SymbolicLink` is only a reportable bit with no resolution or
creation semantics — so lens entries stay plain `FileType.File` and lenses
cannot be *implemented* as links. But one session appearing under several
lenses is exactly hardlink semantics, so the provider implements it
internally: the content map is keyed by session id (one canonical byte
sequence per session), every lens URI is an alias resolved to those bytes,
materialization and refresh fan `change` events out to all lens URIs of the
session (an editor open on any entry reloads), `delete` evicts the session's
cached content across all lenses at once, and the phase-2 Explorer rename
updates the title once, then renames the session's entry in every lens.
Aliasing lives in content identity only — listings remain computed from the
descriptor cache, which is what links cannot provide here.

**Layout churn and open editors.** The provider event channel has no rename
type (`Created`/`Changed`/`Deleted` only), so a relayout expressed as
delete+create orphans any editor open on the old URI — VS Code marks the
document deleted and the editor never follows. Two rules keep editors stable:

1. *Validity is decoupled from membership.* An entry that drops out of
   `/recent` (or any lens) keeps answering `stat`/`readFile`/`writeFile`
   because validity keys on the known session id, not the current predicate —
   so the open editor keeps working and saving works, including the
   session-pin metadata write the controller performs. Only the listings
   shrink. After a sync the provider fires one `Changed` event per *lens
   directory URI*, which is what makes Explorer re-list that folder.
2. *Every rename routes through a workspace file-rename edit.* For filename
   changes (phase-2 titled filenames) the extension builds one
   `WorkspaceEdit` of `renameFile` edits — one per lens the session appears
   in — and applies it. The workbench performs the renames through
   `provider.rename` and carries open editors to the new URIs, exactly as it
   does for a user's Explorer F2 on disk files. The tree context-menu rename
   uses the same path, so there is one rename route, not two; a window whose
   server was renamed elsewhere (attach mode) diffs filename-per-session at
   sync and repairs open editors with the same edit.

Cross-provider moves need no design: dragging a lens entry onto a real disk
folder is performed by the workbench as read+write into the target scheme
followed by `provider.delete` — which by our semantics only forgets the view.
Drag-out therefore exports the `.maieuticsnb` snapshot, and the entry
re-materializes on the next listing.

**Per-window divergence.** The provider is registered per extension host, so
the `maieutics:` namespace is window-local by construction: whether (and
which lens) the sessions folder is mounted, the `/workspace` membership (each
window has its own root), the materialized content cache, and — in launch
mode — the entire session universe (each window spawns its own executable)
may all differ between windows. What must not differ is the *interpretation*
of a path: the filename grammar and the title→filename projection are
deterministic rules, not window state, so two windows attached to one server
converge on the same canonical name, and the sync-time diff repairs staleness
instead of fighting it. Extension version skew between attached windows could
make two projections disagree and cause rename churn at sync — noisy events,
but no data loss (renames never touch content), and both sides converge once
versions match. The one piece of cross-window shared state that projections
cannot paper over is the server's foreground alias in attach mode on
legacy single-active servers: a
resume in one window switches it for the other. That is the existing session
model (invariant 1), not a filesystem concern.

**Mount UX.** `maieutics.mountSessionsFolder` offers a quick pick: "All
Sessions" (`/sessions`), "This Workspace" (`/workspace`), "Recent"
(`/recent`). The workspace-folder name reflects the choice. Auto-restore at
activation syncs only the lenses of mounted folders (cheap now, so the
existing best-effort activation sync stays).

Filename policy and the mapping of generic Explorer file operations onto
session operations are covered in section 6.

### 6. Command surface versus GUI surfaces

`%`-commands stay the shared control surface (the executor is what every
frontend adapter delegates to, and a command cell is part of the portable
notebook narrative). The design goal is narrower: **no VSCode interaction
should require typing a command**, and each GUI action should target typed
REST rather than proxying command text where a typed endpoint is justified
(invariant 27). Mapping of the current surface:

| Command | VSCode GUI replacement | Backend | Phase |
|---|---|---|---|
| `%session current` | Active node in the sessions tree | `GET /v1/agent/session` (exists) | Done |
| `%session list` | Tree + "Resume Stored Session" quick pick | `GET /v1/agent/sessions` (exists) | Done |
| `%session new` | Tree "New session" button | `POST /v1/agent/sessions` (exists) | Done |
| `%session resume` | Tree click / quick pick | `POST .../resume` (exists) | Done |
| `%status` | `maieutics.showStatus` palette command | `GET /v1/status` (exists) | Done |
| `%session gc [hours]` | View-title action on the sessions tree, InputBox for grace hours | `POST .../gc` (exists; extension client method + UI missing) | This wave |
| `%session repair` | View-title action on the sessions tree | `POST .../repair` (exists; same gap) | This wave |
| `%session rename` (this design) | Tree context menu → InputBox (primary surface) | `POST .../rename` (new, this design) | This wave |
| `%session fork <id> <runId\|seq>` (agent-semantics design) | Running a committed cell in a pinned notebook (primary surface) | `POST .../fork` (new, agent-semantics design) | Done |
| `%model current\|list\|available\|use\|reset` | "Select Model" picker on the view title: profiles as entries, "Reset to default" entry, "Refresh catalog" action | Needs typed REST (`GET /v1/model/profiles`, `POST /v1/model/profiles/{id}/select`, `POST /v1/model/selection/reset`) or `/v1/agent/commands` proxy | Follow-up |
| `%workspace current\|reset\|use` | Status display only (the tree header can show the root); switching stays command-only — it rewrites tool policy mid-process and the frontend launched the process with the original root | Needs REST if ever surfaced | Deferred |
| `%mcp list` | Read-only tree section or quick pick | Needs typed REST (`GET /v1/mcp/servers`) or commands proxy | Follow-up |

Commands are not removed by any of this: a `%model use …` cell recorded in a
`.maieuticsnb` snapshot replays with the document, survives sharing, and is
the only surface available to non-VSCode consumers and scripting
(`POST /v1/agent/commands`). GUI actions are live-state mutations with no
trace in the document — that asymmetry is the criterion for keeping a command.

**Generic Explorer file operations.** Which VFS gestures map onto session
operations:

- **Rename → title.** With `<id32>.maieuticsnb` filenames, Explorer rename is
  meaningless (the id must not change), so it is refused. Under a titled
  convention (`title--<id8>.maieuticsnb`), `rename(old, new)` parses the new
  name and calls the rename endpoint — making generic Explorer rename the
  title editor, and Ctrl+P shows human names. The `<id8>` suffix keeps the
  lookup unique via the descriptor cache. Every rename — Explorer F2, the
  tree context menu, sync-time repair — routes through one workspace
  file-rename edit so open editors follow ("Layout churn and open editors").
  Recommendation: adopt titled filenames in a second phase, exactly when
  Explorer-rename-to-title is wanted; the id remains the parse target either
  way.

  Name rules for the titled phase: the **title is canonical** (server-stored
  verbatim, ≤ 200 chars, any characters); the **filename is its deterministic
  projection** `slug(title)--<id8>.maieuticsnb`. `slug` preserves case and
  non-ASCII letters, replaces filesystem-illegal characters with `-`,
  collapses whitespace runs to one `-`, trims leading/trailing separators and
  dots, and caps the slug at 64 chars; a degenerate slug (all separators)
  falls back to `untitled`. Explorer F2 must keep the `--<id8>` tail — it is
  the session handle, matched against the descriptor cache by splitting on
  the last `--` and validating 8 hex chars; the remainder becomes the new
  title verbatim, and the provider then normalizes the session's entry in
  every lens to the canonical form. So the typed name may differ from the
  final filename (sanitization is expected), but all lenses and windows
  converge on exactly one filename per session. A rename without the id8 tail
  is refused with a typed error; a rename whose result already matches the
  canonical form is a no-op.
- **New File → new session: deliberately not mapped.** `FileSystemProvider.
  writeFile` is synchronous while session creation is a REST call, and an
  unpinned new notebook would ambiguously pin to the active session on first
  execution. The tree "New session" button is the surface; the lens predicate
  change (section 5) refuses junk writes so generic New File fails loudly
  instead of creating an unbacked entry.
- **Delete → "Forget Local View".** Unchanged semantics (server authoritative,
  invariant 1); the context menu label says what it does. A real server-side
  delete remains out of scope (open question 1).
- **Copy → refused.** Under lens validation, pasting to another session id
  would create an unbacked entry and pasting within the same session's other
  lens is a no-op, so `copy` answers with a typed error and Explorer
  copy/paste fails loudly. It is not a fork — durable fork stays with the
  family model (ADR 0009).

## Compatibility and migration

- SQLite: version-stepped migration v2→v3 as above; older builds refuse a v3
  database with the existing "written by a newer build" error. Representative
  fixtures get a v2 database plus expected v3 convergence.
- Wire: additive only; the extension treats missing `title`/`preview`/
  `workspaceRoot` as today's behavior, so
  attach-mode version skew degrades to id-prefix labels and the legacy flat
  grouping (all sessions land in "Without workspace" → rendered as one group).
  When no group matches the current workspace (old server, or a server rooted
  elsewhere), the current-only filter falls back to showing all groups instead
  of an empty tree.
- `.maieuticsnb`: unchanged (version 1, unknown-field tolerant).
- Behavior changes to document: renaming an uncommitted session now creates its
  stored row (zero turns) and lists it; opening a stored session's virtual
  notebook auto-resumes it (see "Layout churn" — the transcript endpoint is
  active-gated), which also means full-text search across a mounted lens reads
  and resumes every session it touches.

## Testing plan

C# (`dotnet test`):

- `SqliteTranscriptStore`: fresh-db-at-v3, v2-fixture migration, title/preview/
  workspace stamping, `SetTitle` upsert incl. zero-turn row creation, preview
  truncation, descriptor mapping.
- `FrontendApiIntegrationTests`: sessions list carries the new fields;
  rename (active, non-active, unknown → row creation, >200 chars → 400, empty
  clears); `capabilities.workspaceRoot`; active-session `title` present and
  cleared. `%session rename` through the command path remains a gap (the
  executor has no direct test harness; its token arithmetic is exercised only
  manually).
- Session manager: rename of a stored session through family resolution.

Deno (`deno test` in `deno/maieutics-vscode`):

- `sessionGroups_test.ts`: grouping, ordering, current-workspace matching,
  label fallback chain, filter.
- VFS lens tests: path validation, membership predicates, lazy materialization
  (fake client), mount-folder sync scope.
- `client_test.ts`: `renameSession`, new field decoding, old-server tolerance.

Verification commands: the standard `dotnet test Maieutics.slnx`,
`dotnet build Maieutics.slnx --no-restore -warnaserror`, `deno test`, and
`git diff --check`; protocol doc (`docs/web-frontend-protocol.md`) and the
extension README updated in the same change.

## Open questions

1. Real session delete/archive (family-db removal + object GC) — worth its own
   ADR; excluded here.
2. A `/by-workspace/<group>/` VFS lens (directories per workspace) needs a
   path-encoding and collision policy; deferred until the tree grouping proves
   insufficient in the Explorer.
3. Client-side pinned/starred sessions — arguably local UI state
   (workspaceState), but a server-side flag would survive across machines;
   undecided, not designed here.
4. Whether `%session rename` should also accept `-c` to clear explicitly
   (empty-string handling in the command grammar is awkward).
