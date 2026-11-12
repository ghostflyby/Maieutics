# Maieutics for Visual Studio Code

Notebook-native frontend for the [Maieutics](../../) agent. The extension speaks the custom web
protocol (`docs/web-frontend-protocol.md`) over HTTP + WebSocket to the `maieutics` executable — no
Jupyter kernel involved (ADR 0023).

## Features

- `.maieuticsnb` notebooks: one ordinary cell is one submitted Agent turn.
- Streaming assistant markdown, tool activity, and typed turn failures.
- REPL rich displays: `Deno.jupyter.display` inside a REPL tool call renders into the same cell
  output and updates in place, tracked by display id.
- REPL input: `prompt()` / `securePrompt()` surface as VS Code input boxes and the answer flows back
  to the running REPL.
- Command cells (`%status`, `%session`, `%model`, `%workspace`) answer inline.
- Session commands: new session, resume a stored session, show status, rename a session
  (`title →
  first prompt → id prefix` is the display-name fallback everywhere).
- Multi-session servers (`capabilities.multiSession`): every session-addressed route is served
  directly, so each notebook simply targets its pinned session — an unpinned notebook creates its
  own instead of competing for the foreground, and two notebooks run turns concurrently.
- Sessions tree: grouped by the workspace each session was created in (current workspace first and
  expanded), with a current-workspace-only filter toggle in the view title. Fork branches render
  directly under their root (lineage adjacency, branch badge in the description). Context menus
  offer rename and copy-id; the view title also exposes object GC (`%session gc`) and object-view
  repair (`%session repair`) without typing commands.
- Sessions as files: **Maieutics: Mount Sessions as Workspace Folder** (also the folder button in
  the sessions tree title) mounts one of three lens views — All Sessions (`maieutics:/sessions`),
  This Workspace (`maieutics:/workspace`), or Recent (`maieutics:/recent`, 20 entries) — so stored
  sessions appear in the Explorer as ordinary `<session-id>.maieuticsnb` notebooks that can be
  opened, searched, and diffed like any file. The mount is a snapshot view, not a live feed: the
  server transcript stays authoritative, listings refresh on mount or window reload, content
  materializes when a file is opened (opening — or full-text searching — a stored session's view
  lazily resumes it on the server, since transcripts are served per session), and deleting a file
  there only discards the local view until the next sync re-materializes it.
- Agent output follows one **Timeline**. Every message, permitted reasoning summary, tool invocation
  and child run is a separate notebook output block, in first-appearance order. Assistant replies
  fill their output block, always expanded. They show native Markdown immediately, then use shared
  passive SSR from an attached kernel when supported, including containers, code groups, code
  titles, line numbers, line highlights, and Shiki diff/focus/error annotations. Code groups switch
  through native controls; selections survive text updates when the group, tab position, and title
  still match. Activity nodes remain compact and collapsed by default. Click a node to expand its
  original content; the expanded state survives streaming updates. Native Markdown remains the
  offline and error fallback; unbound snapshots never start a kernel solely for rendering. While
  assistant text streams, completed passive SSR prefixes can commit before the newest snapshot
  finishes, so rendering does not stall until the model stops. Partial Vue/HTML tags stay inert
  without consuming adjacent Markdown; rewrites and completion invalidate older prefixes. On kernels
  advertising progressive templates, complete setup/template prefixes can mount before completion;
  later templates preserve the same refs/computed state and existing compatible DOM. Script rewrites
  establish a new scope. Incomplete tails retain the last usable component; invalid sealed updates
  report a local diagnostic. Older kernels wait for successful completion. Model-supplied HTML stays
  escaped. See the [SSR demo](demo/README.md) for the current subset and limitations. Tool nodes
  retain structured arguments, latest progress and results, capped at 20,000 characters per detail
  and 256 tools per run, with an explicit truncation note. Saving rejoins blocks into one portable
  turn snapshot; reopening restores expanded replies and collapsed activity nodes. History tied to a
  session/run reconnects the kernel and regains Vue rendering and interaction after exact source
  verification against the committed transcript. Component-local state restarts from its initial
  value. Edited, unbound, unavailable and offline history remains passive source; a file cannot
  grant execution authority by storing a live marker or compiled code. Cancellation and failure
  preserve the same timeline. Older snapshots use grouped activity; server-history restoration
  reconstructs model/tool order, but display-only reasoning summaries require saved notebook output
  or event replay.
- Composer conveniences: **Maieutics: Queue Follow-up Turn** submits a message that joins the
  session's server-owned turn queue behind anything already queued or running (queued cells show
  `queued #N` markers with per-cell dequeue and clear-queue); a status-bar badge totals the provider
  token usage of the notebook's committed turns; a cancelled or failed run keeps its partial answer
  and appends the failure instead of wiping the cell; running a committed cell offers "Fork with
  another model" (profile picker) alongside the plain fork; a committed cell's branch badge
  (**Maieutics: Switch Branch**) lists the conversation's other branches and opens the picked one.
- Cells as conversation history (see `docs/notebook-agent-semantics-design.md`): every committed
  cell carries its turn binding (`runId` + the submitted input, in cell metadata and in the
  structured snapshot), so the notebook distinguishes committed, edited-history (`stale`), and
  pending cells. Run Below / Run All submit only pending cells; Run Above explains that history is
  immutable instead of running; running a committed cell forks the session at that point —
  regenerate when unchanged, continue-from-your-edit when drifted (confirmation dialog, skippable
  via `maieutics.confirmFork`) — and re-pins the notebook to the new branch, keeping the original
  switchable in the sessions tree. The stop button cancels the in-flight run cooperatively
  (`NotebookController.interruptHandler` → `POST /v1/agent/runs/{id}/cancel`). Status bar badges
  mark the commit frontier and edited history; deleting a committed cell warns that the conversation
  keeps its turns.

## Connection

By default the extension launches the `maieutics` executable for the first workspace folder
(`maieutics.executablePath`) and reads the discovery file it publishes. To attach to an externally
launched instance instead, set `maieutics.discoveryFile` to the discovery file the instance was
started with (`maieutics --frontend-discovery <path>`).

## Development

Browser output rendering is shared with
[`maieutics-output-client`](../maieutics-output-client/README.md), an independently importable ESM.
`media/turnRenderer.js` is the notebook adapter, and `media/outputClient.js` is a copied, bundled
artifact containing the shared DOM/Vue implementation. Run `deno task build:media` after changing
browser sources; CI checks the shared artifact's freshness. Extension-host code stays independent of
browser module sources.

The toolchain is pure Deno — dependencies live in `deno.json` (`package.json` is only the extension
manifest), and `@vscode/vsce` runs directly under `deno run npm:@vscode/vsce` (the old chalk
incompatibility, deno#26637, is fixed on current Deno).

```sh
deno task check   # type-check shipped sources against ES2023 + @types/node
deno task test    # unit tests (pure modules + protocol client against a mock server)
deno task build   # bundle dist/extension.js (CJS, vscode external)
deno task package # deno run npm:@vscode/vsce package --no-dependencies → .vsix
```

Type-checking uses two configs on purpose: `deno.extension.json` gives shipped sources an ES2023 lib
plus `@types/node` — the runtime the VS Code extension host actually provides — so no phantom Deno
globals slip into the bundle. The member `deno.json` (used by `deno test`) keeps the default Deno
libs because the tests use the Deno CLI runtime (`Deno.test`, `Deno.serve`).

Debug: open this folder in VS Code and use "Run Extension" (F5) after `deno task build` —
`launch.json` points the extension host at `dist/`.

`deno task test:vscode` runs the real editor with isolated settings and a scratch workspace. On
macOS the runner captures the detached editor's stdout/stderr and prints their final output, so test
starts, results, and startup errors remain visible after a timeout. Set
`MAIEUTICS_VSCODE_DIAGNOSTICS_DIR` to enable debug logging and preserve the editor logs and verdict
before termination and scratch cleanup. CI uploads these diagnostics for failed extension jobs and
scheduled runs.

## Permissions

The extension does not turn an editor folder into the Agent home. Launched kernels select their
fixed home from explicit kernel configuration or `<DataRoot>/workspaces`; attached kernels keep
their own home. Multi-root IDE workspaces register all local project folders individually. On
connection and when local editor folders are added, the extension registers each `file:` project
through the kernel's existing `%workspace open` command, yielding
`workspace://local/projects/<name>/...`. The kernel registry owns naming, collisions, identity
validation and physical links. Registration is serialized and joined on connection disposal, without
adding Agent turns or changing permission grants. One failed project does not prevent later projects
from registering. Failures are aggregated as warnings in the Maieutics output channel; they do not
prevent commands or notebook cells from using the kernel. Identical warnings are deduplicated.
Failed projects are retried on later client access or folder changes; successfully registered
projects are not repeated. Connection shutdown still cancels pending registration.

The session tree's current-workspace filter uses the root reported by the kernel. Remote and virtual
folders stay on the IDE filesystem plane; they are not passed off as local disk projects. Closing an
editor folder or disconnecting leaves persisted links intact; use `%workspace close <name>` to
retire one explicitly. Opening a project in VS Code does not create `projects/`, `scratch/` or
`.maieutics/` inside that project. `%workspace current` shows the actual home and registered project
links. Task-title metadata uses the same authenticated frontend connection; it adds no Deno
permission grants.

Development runtime needs `allow-net` (loopback mock server tests) and `allow-read` (discovery
file). The renderer module regression tests also need `allow-write` for temporary bundles and
`allow-run` for the Deno executable: they bundle the renderer sources as CJS and verify that the ESM
gate rejects them. Temporary bundles are removed after the test. The packaged extension talks to the
executable over loopback HTTP/WS only; spawning the executable uses the Node child-process API, not
Deno permissions.

The IDE file-system bridge receives standalone requests on the session socket even when no Agent
turn is active, and serves them through the editor's workspace APIs under the kernel's existing
permission gates. Replies use the authenticated loopback IDE endpoint; this requires no additional
Deno grants. The Maieutics output channel logs request ids, methods, and outcomes, without file
paths, request arguments, or file contents.

IDE edits (`ide_apply_edit`, `ide_format_document`) use current `TextDocument` buffers, including
unsaved changes. The kernel validates targets, permissions, ranges and size bounds, then the
extension applies one `WorkspaceEdit` with document-version and content checks. Conflicts reject the
entire transaction. Edits participate in native undo/redo and do not save automatically. Keep the
`versions` map returned by rename/code actions when applying their edit. Snapshot, computation and
application stay on one elected frontend; edits are never replayed or broadcast to other windows. A
frontend that lacks document editing rejects the request; no filesystem fallback is used. Ordinary
file tools retain their existing filesystem semantics. This adds no Deno permission grants.

`ide_save_all` saves every dirty file-backed text document and notebook in all local workspace
projects, including pre-existing user edits. Unrelated files and untitled documents remain
untouched. The kernel checks the whole target list's permissions before saving, then the extension
uses native document save APIs. Version conflicts, failed saves and newly dirty documents fail the
barrier; earlier successful saves remain on disk. Up to 256 dirty documents are supported without
truncation. Use `saveWorkspace: true` on `terminal_run`, `terminal_input` or `terminal_paste` for
builds/tests: the save barrier runs before any process starts or input is sent, and failure prevents
execution. The flag defaults to false; commands are not inspected to infer intent. These additive
IDE methods use the existing authenticated connection and require no additional Deno grants.

## Task titles

The kernel defaults to `auto`: generate an initial title once after a successful turn, then let the
Agent call `set_task_title` when the task scope changes. Initial naming runs in the background and
never holds up execution. `initial` generates once without later model changes; `manual` uses a
preview until you name the session. Failures do not trigger a naming request on every later turn. In
`auto` and `initial`, the initial naming request sends a bounded request/answer excerpt to the
configured model provider. Select `manual` to disable these dedicated naming requests.

Titles appear in the Sessions tree, session pickers, and active notebook status bar. Click the
status-bar title or run **Maieutics: Task Title Settings** to rename, select a mode, clear/unlock,
or regenerate. Manual names stay locked even if the mode changes. Clearing resets eligibility while
retaining the selected mode; regeneration requires an unlocked `auto` or `initial` title. Notebook
URIs and snapshot dirty state stay unchanged.

Session metadata arrives independently of answer output and run completion; older revisions cannot
overwrite newer titles. Configure the startup default with `Maieutics:Agent:Title:Mode`; legacy
`AutoTitle=false` selects `manual` when no new mode is set. These controls use the existing
authenticated connection and require no additional Deno permissions.

## Vue forms (SDK 0.3 migration)

Form output uses the same standalone Vue ESM as assistant islands, through `media/formRenderer.js`.
The former custom family registry, bundled source engine, widget renderer and widget JSX/TSX
producer API are removed. Discovery UI version 2 advertises the version-one form data contract.
Older clients retain text fallback. Saved forms and migrated historical forms are readonly snapshots
and cannot attach to a live model; other retired views/widgets retain JSON/text.

The renderer adapter uses its owning VS Code context for messaging. Components, local drafts,
reactive updates and cleanup belong to `maieutics-output-client`, which also works without VS Code.
No new extension permissions or automatic browser network access are introduced.
