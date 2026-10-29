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
  fill their output block and use VS Code’s default Markdown renderer directly, always expanded.
  Activity nodes remain compact and collapsed by default. Click a node to expand its original
  content; the expanded state survives streaming updates. Answer Markdown uses VS Code's native
  renderer (including tables, nested lists, code and Markdown extensions); model-supplied HTML stays
  escaped. Tool nodes retain structured arguments, latest progress and results, capped at 20,000
  characters per detail and 256 tools per run, with an explicit truncation note. Saving rejoins
  blocks into one portable turn snapshot; reopening restores expanded replies and collapsed activity
  nodes. Cancellation and failure preserve the same timeline. Older snapshots use grouped activity;
  server-history restoration reconstructs model/tool order, but display-only reasoning summaries
  require saved notebook output or event replay.
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
links.

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
