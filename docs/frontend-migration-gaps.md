# Frontend Migration Gaps

Status: Draft (living document — tick items off as they land)

Date: 2026-09-05

Scope: what remains to migrate from the retired Jupyter frontend to the
custom web protocol + VSCode extension (ADR 0023), beyond what has already
shipped. Each item states the current behavior, the gap, the proposed shape,
and acceptance criteria. Ordered by recommended execution sequence.

Items 1, 2, 3, and 5 are implemented (see git history; acceptance criteria
met by the integration and unit tests noted in each section).

Shipped already (for context, not re-planned here): v1 protocol (discovery,
bearer auth, sessions/turns/cancel/transcript/commands/completion/status),
event streams with replay and backpressure disconnect, command execution via
the shared `Maieutics.Commands` domain, the VSCode extension (serializer,
controller, streaming, tool activity, REPL displays, input requests, session
pinning, turn-timeline renderer, stable output segments), process smoke
coverage, and the four-gate Deno CI job.

## 1. Command cells fight the session pin (bug) — DONE

Priority: High. Type: Correctness.

**Current.** `%session new` / `%session resume <id>` command cells switch the
server's active session. But the notebook's pinned session id (notebook
metadata, written by `resolveSessionPin`) is not updated by command
execution. On the next execution batch, the controller resumes the *pinned*
session — silently undoing the command the user just ran. Two entry points
for the same state, fighting each other.

**Proposed shape.**

- Command execution returns the (possibly new) active session id alongside
  the markdown — either by extending the command response body with
  `{"markdown": "...", "session": {"id": "..."}}`, or by having the
  controller re-read `GET /v1/agent/session` after a command turn.
- The controller updates the notebook metadata pin whenever the active
  session differs from the pin — the same `writeSessionId` path the pin
  logic already uses.

**Acceptance.** An integration test runs `%session new` as a command cell in
a notebook with a pinned id and asserts the notebook metadata ends up
pointing at the new session, and the following turn does not resume the old
one. Extension unit test covers the metadata update path.

## 2. Binary rich values (object bypass) — DONE

Priority: High. Type: Feature (largest visible gap).

**Current.** The REPL collector decodes binary mimes into base64 inside the
display bundle (the encoding the Jupyter wire required); the frontend sink
then replaces them with `[binary image/png display omitted]` placeholders
(invariant 26 forbids base64 on the frontend wire). Images, audio, PDFs from
`Deno.jupyter.display` are invisible in the notebook. The
`GET /v1/objects/{id}` endpoint exists but nothing routes objects through
it.

**Proposed shape.**

- Collector: binary mime values above a small inline threshold (e.g. 8 KiB)
  are ingested into the `ObjectStore` (content-addressed, already used for
  agent tool objects); the bundle value becomes an object *reference*
  (`{"$object": "<sha256>", "byteLength": n}` placeholder object, not a
  base64 string).
- Frontend sink: passes object references through verbatim (they are
  structured JSON, so invariant 26 is respected — no binary in text).
- Protocol: `repl.display` frames may carry `{"$object": ...}` values;
  document that a reference is dereferenced via
  `GET /v1/objects/{sha256}` (streaming bytes, `application/octet-stream`).
- Extension: `bundleItems` maps an object reference to
  `NotebookCellOutputItem.bytes(new Uint8Array(await response.arrayBuffer()), mime)`.
  Display ids keep the update-in-place behavior; a display whose items
  change from bytes to bytes replaces items as today.

**Acceptance.** An integration test drives a REPL display with a binary mime
through the full path and asserts the extension-side item carries the exact
bytes; the digest/model path keeps seeing only media types (no size
explosion); a second display with identical bytes reuses the same object
(content addressing).

## 3. Cell completion provider — DONE

Priority: Medium. Type: Feature (small).

**Current.** The backend implements `POST /v1/agent/complete` (UTF-16
cursor, profile-aware command completion) and the extension client already
has a `complete()` method. Nothing registers a VSCode completion provider,
so cells have no `%`-command completion.

**Proposed shape.**
`vscode.languages.registerCompletionItemProvider` for the notebook cell
language (`markdown`), scheme-filtered to `maieutics-notebook` documents,
trigger characters `%`/`/`, querying the client and mapping matches to
`CompletionItem`s with the token replacement range from the response
(`tokenStart`/`tokenEnd` are UTF-16 offsets — direct VSCode `Range`
semantics). One in-flight request guard; failures degrade silently (no
completions), never error toasts.

**Acceptance.** Manual F5: typing `%mo` offers `%model`; typing
`%model use ` offers configured profiles. Unit test maps the wire response
onto completion items (range, labels).

## 4. Frontend E2E coverage for retired kernel tests

Priority: Medium. Type: Test debt.

**Current.** The kernel-driven integration suite was retired with its
transport. Ported to the frontend harness: OpenAI tool loop, reasoning
privacy, configuration reload provider switch. Still missing:

- Anthropic tool loop (the harness's fake Anthropic server was deleted with
  the host integration file; extract it the way `FakeOpenAiServer` was).
- OpenAI↔Anthropic switching with canonical-history assertions.
- MCP tool-loop E2E (stdio test server wired through `mcp.json`).

**Acceptance.** The three scenarios run in the frontend integration suite on
all three CI OSes. No new harness machinery beyond a
`registerBuilderConfiguration`-style hook reuse.

## 5. Notebook close cancels in-flight runs — DONE

Priority: Medium. Type: Lifecycle correctness.

**Current.** Closing a notebook (or the window) disposes the owned process
in launch mode — fine — but in attach mode (`maieutics.discoveryFile`) the
server keeps running and an in-flight turn continues to completion with its
frames going nowhere.

**Proposed shape.** `workspace.onDidCloseNotebookDocument`: for notebooks
with an in-flight run, `POST /v1/agent/runs/{id}/cancel` (best-effort, fire
and forget with logging). Does not wait; the run stream's terminal frame
settles the cell execution if the socket is still open.

**Acceptance.** Unit/integration test: a hanging-provider run is cancelled
when the notebook document closes.

## 6. Comm / interactive widgets (product decision)

Priority: Deferred until decided. Type: Product decision + feature.

**Current.** The Jupyter path relayed bidirectional comm (anywidget clicks
etc.) between frontend and REPL. The executable's Jupyter adapter is gone;
`/comm` remains a child-process-only channel (REPL ↔ host). The frontend
cannot participate in widget interaction; `Deno.jupyter.display` of an
anywidget model renders its initial state but is inert.

**Decision needed.** Either:

- **(a) Design a frontend comm plane**: pair a `comm.open/recv` event frame
  family with a `POST /v1/agent/comms/{commId}` uplink (the input.request
  pattern generalized), route to the REPL's existing comm actor surface.
  Cost: protocol v1 extension (versioned, additive), renderer-side bridge,
  tests. Enables anywidget-style interactive outputs.
- **(b) Declare interactive outputs out of scope**: remove anywidget promises
  from docs, keep displays static. Cost: one documentation pass. Interactive
  outputs would then need the webview-renderer route later (still possible
  without protocol changes).

Either way the migration is *complete* on this axis once the decision is
recorded in an ADR addendum; (a) is a v2 protocol feature, not a v1 gap.

## 7. Extension publishing pipeline

Priority: Low. Type: Release engineering.

**Current.** `deno task package` produces a verified `.vsix` locally. No CI
publish.

**Proposed shape.** A tagged-release workflow job: build → `deno task check`
+ tests → `vsce package` → attach the vsix to a GitHub release (and/or
publish to the Marketplace/Open VSX with repository secrets). Version source
of truth: the extension's `package.json`, bumped via the release PR.

**Acceptance.** Pushing a `vscode-v*` tag produces a release artifact
without local steps.

## 8. Historical documentation sweep

Priority: Low. Type: Documentation hygiene.

**Current.** ADR 0003 (REPL output bridge), `deno-jupyter-compat.md`,
`deno-jupyter-compat-plan.md`, and parts of `docs/README.md` describe the
Jupyter path as the current architecture.

**Proposed shape.** One pass adding "Historical — superseded by ADR 0023"
banners with pointers; no rewriting of the originals (they are records).

**Acceptance.** `docs/README.md` presents the frontend protocol as primary;
the swept files carry the banner.

## Out of scope (recorded deliberately)

- Server-side turn queueing beyond the single-run gate (invariant 4 keeps
  the session as the only serialization point; queueing would be a v2
  protocol design).
- Per-mime diffing of REPL display updates (the display-id addressing plus
  segment reuse covers the visible cost; finer granularity is speculative).
- `.ipynb` import/export (no consumer; the frontend owns `.maieuticsnb`).
- Interactive tool-approval buttons (rides on the webview-renderer route
  after item 6's decision; not a protocol gap).
