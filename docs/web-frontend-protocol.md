# Maieutics Frontend Protocol (v1)

Draft — implemented by the `Maieutics.Frontend` domain in the executable and
consumed by the VSCode extension (`deno/maieutics-vscode`). Companion to
ADR 0023.

The protocol is an internal web application API surface (invariant 27): every
endpoint has an explicit direction, a bounded payload, and a version prefix.
All JSON uses camelCase and is serialized with source-generated contexts on
the NativeAOT path. No Jupyter type and no Microsoft.Extensions.AI type
crosses the wire (invariant 11).

## Passive Markdown rendering demo

Capabilities may advertise `rendering: {"version":1}`. `POST /v1/render/markdown` is an authenticated,
stateless request/reply endpoint for derived presentation, independently of run event sequencing:

```json
{"version":1,"documentId":"message1","source":"# Result\n\n**Ready**"}
```

The response is `{"version":1,"documentId":"message1","html":"…","css":"…"}`. HTML is passive
server-rendered content; CSS is the renderer's host-scoped stylesheet. A document ID begins with an
ASCII letter and contains at most 64 ASCII letters, digits, underscores, or hyphens. The source limit
is 200,000 UTF-16 units; request bodies are capped at 2 MiB, and HTML at 4,000,000 UTF-16 units. The
kernel admits at most eight rendering requests and gives each a 30-second total budget. Unknown JSON
fields are tolerated. Invalid requests return `400 invalid_request`, saturated admission returns
`429 render_busy`, and renderer failure returns `503 render_unavailable`; clients retain the original
Markdown. A cancelled request does not cancel its Agent run or change canonical history.

Clients correlate answers with their own source revision and ignore superseded results. This demo
returns complete snapshots, with no patch or server-owned document revision contract. It does not
execute document scripts, Vue expressions, includes, or filesystem snippets. Product resource links
are preserved; frontend resource delivery and interactive components remain separate work.

`POST /v1/render/ready` is an internal child-to-kernel readiness notification on the shared host. It
requires the frontend bearer plus the child scope's nonce and accepts `{version:1,port,nonce}`. An
accepted notification returns `204`; forged or repeated notifications return `409`. It is not a
frontend registration API. The owned child serves authenticated, bounded request/reply
`POST /v1/markdown` on an ephemeral loopback port using its separate scope credential.

The VS Code extension uses its already attached connection for rendering; opening an offline
snapshot does not start a kernel. Its renderer messaging bridge assigns stable document IDs and
local request IDs, coalesces pending source updates, and rejects obsolete replies. Native Markdown
appears on first presentation and remains the fallback. On unchanged-layout updates, committed SSR
stays attached until the next matching response or a fully prepared latest native fallback commits;
intermediate native renders do not replace it. Identical committed source/mode/bindings need no new
request. This is a frontend presentation policy; server requests remain stateless. A local `applied`
acknowledgement follows DOM application and confirms the committed frontend engine; it is not a
frontend event frame or an Agent completion signal.
SSR HTML is derived presentation and is not saved into notebook v1 snapshots.
Live assistant text nodes and restored snapshots carry `application/vnd.maieutics.turn+json` so they
enter this renderer; native Markdown fallback happens inside it. Legacy Markdown-node metadata
remains readable when saving older in-memory outputs.

The passive content subset also includes `[[toc]]` (top-level H2/H3 headings), heading-only `{#id}`
attributes, inline Markdown container titles, and quoted GitHub Alerts (TIP/NOTE/INFO/IMPORTANT/
WARNING/CAUTION/DANGER). Heading IDs and local links are document-scoped, including TOC targets.
Explicit IDs accept `[\p{L}_][\p{L}\p{N}_.:-]{0,127}`; invalid IDs fall back to generated slugs.
Duplicate IDs and collisions with code controls receive deterministic numeric suffixes in source
order. Full-source rendering updates earlier TOCs and container-title references when later source
arrives. Attribute handlers/styles are rejected; alert titles are escaped literal text. VS Code owns
local-fragment navigation within the rendered Shadow DOM and removes its handlers on replacement
and disposal. This content expansion does not change rendering API version 1 or the notebook format.

The passive content subset includes `::: code-group` with direct fenced-code children, literal fence
titles, line numbers and selected-line highlighting, and VitePress-style Shiki comment notations for
diff/focus/highlight/error/warning. Code groups use document-scoped native radio controls and the
returned stylesheet; they require no executable document code. Mixed groups retain their normal
visible Markdown. Frontends may preserve tab choices locally; neither choice nor derived HTML enters
canonical history or notebook v1 persistence. The rendering API shapes and version remain unchanged.

## Discovery and authentication

The frontend spawns the executable and passes the discovery file location:

```
maieutics --frontend-discovery <path>
```

Once Kestrel has bound the frontend listener, the executable writes the file
atomically; the file's appearance is the readiness signal. Shape
(`maieutics-frontend-discovery` version 1):

```json
{
  "version": 1,
  "url": "http://127.0.0.1:51234",
  "token": "<64 hex chars>",
  "pid": 12345
}
```

Every frontend request must carry `Authorization: Bearer <token>`, compared in
constant time. The token is generated per process start. The frontend listener
is loopback TCP on every platform (the Deno WebSocket client cannot ride a
Unix domain socket), so bearer auth is the only gate; the file lives in
user-owned state with default-restrictive permissions.

## Conventions

- Turn submissions and WS frames reference sessions and runs by their
  identifiers (`AgentSessionId`/`AgentRunId` as 32-char lowercase `N` GUID
  strings).
- Every run event frame carries `sequence`: the strictly increasing run-local
  number from `AgentEvent.Sequence`.
- Errors are JSON bodies with a stable `code` and a human-readable `message`.
  Frontend-relevant codes mirror the agent typed failures (`agent_busy`,
  `agent_provider_error`, `agent_tool_error`, `agent_input_too_large`,
  `agent_response_too_large`, `agent_turn_in_progress`→`agent_busy`, …) plus
  protocol codes (`not_found`, `invalid_request`, `unauthorized`,
  `command_error`).
- A concurrent turn on a session is rejected with `409` + `agent_busy`. Turns can also be queued
  explicitly on the session's server-owned turn queue (below); `POST /turns` itself never queues.

### Model provider failures

Model request failures use the existing `run.failed` frame's `code` and `message` fields.
Provider adapters classify upstream failures before they enter the provider-neutral Agent runtime;
frontends display the supplied message and preserve any output received before failure.

| Code | Meaning |
|---|---|
| `agent_provider_authentication` | Credentials were rejected; check the API key. |
| `agent_provider_permission_denied` | The account cannot access the requested resource. |
| `agent_provider_invalid_request` | Request parameters, input, or tools were rejected. |
| `agent_provider_not_found` | The model or endpoint was not found. |
| `agent_provider_context_limit` | The request exceeds the model's context window. |
| `agent_provider_rate_limited` | Request or token throughput is limited. |
| `agent_provider_quota_exceeded` | Account quota, credits, or billing allowance are exhausted. |
| `agent_provider_timeout` | A provider request timed out. |
| `agent_provider_connection` | The connection could not be established or was interrupted. |
| `agent_provider_unavailable` | The provider is unavailable or overloaded. |
| `agent_provider_invalid_response` | The response is malformed or incomplete. |
| `agent_provider_error` | No recognized failure category is available (compatibility fallback). |

Messages contain a category-specific explanation and suggested action, plus the upstream HTTP
failure status and an allowlisted provider error code when available. Arbitrary provider messages,
unknown error codes, response bodies, credentials, and request content are not copied to the wire.
Non-JSON proxy errors still classify by HTTP status. Streaming error events classify
without an HTTP failure status because the stream was already accepted. OpenAI Chat error
objects and Responses `error` / `response.failed` events terminate the run with a classified
failure; they are not treated as successful empty responses or displayed as raw error content.
Caller cancellation remains cancellation; expiration of the Agent turn budget remains
`agent_turn_duration_exceeded`. Neither is reclassified as a provider timeout.
These codes are additive: clients must continue to accept unknown error codes and display their
message. The frontend protocol version and frame shape are unchanged.

## Session turn queue (server-owned)

Each session carries a bounded, server-owned queue of agent turn items. Queued items run in
arrival order under the session's single-run gate; when an item starts, its run behaves exactly
like a `POST /turns` run (same frames, same cancel endpoint). Command text (`%`-commands) and
empty text are never queued. The queue is versionless full state: every snapshot below replaces
the client's view wholesale.

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/agent/sessions/{sid}/queue` | Full-state snapshot (items in run order, with their texts) |
| POST | `/v1/agent/sessions/{sid}/queue` | Enqueue turn items → `202 {"items":[{"id","position"}…]}` |
| DELETE | `/v1/agent/sessions/{sid}/queue/{itemId}` | Remove one waiting item → `204` |
| DELETE | `/v1/agent/sessions/{sid}/queue` | Clear every waiting item (running item untouched) → `204` |

- `GET` answers `200 {"sessionId","running":{"itemId","runId"}|null,"items":[{"id","text",
  "enqueuedAt"}…],"capacity"}`; `items` are the waiting items in run order and `text` appears
  only here (frames carry ids alone).
- `POST` body is `{"items":[{"text":"…"}…]}` (one item per text). Errors: `400 invalid_request`
  for command text, empty text, or oversized payloads; `409 queue_full` when the batch would
  exceed `capacity`; `404 not_found` for an unknown session.
- `DELETE …/{itemId}` answers `204`, `409 item_running` once the item started (cancel the run
  instead), or `404 not_found`.
- Every queue mutation emits one event frame on the session events socket (never on `?runId=`
  per-run sockets):

```json
{"type": "queue.updated", "queue": {"sessionId": "…", "running": {"itemId": "…", "runId": "…"},
 "items": [{"id": "…"}], "capacity": 8}}
```

The frame carries no `sequence` number and is an idempotent full-state replacement; a client that
misses frames self-heals on the next mutation, and a (re)connecting client reads `GET /queue`
once after the hello to rebuild its view.

## Display objects (binary rich values)

Binary mime values in `repl.display` bundles are stored as immutable,
content-addressed objects. The bundle carries a structured reference —
never base64 (invariant 26):

```json
{"image/png": {"$object": "/v1/objects/<sha256>", "byteLength": 12345}}
```

The reference URL is relative to the discovery URL, served with
`Cache-Control: public, max-age=31536000, immutable`, and the bytes travel
natively in the HTTP body. Because the URL is content-addressed (same bytes
= same URL forever), clients should cache unconditionally and may share
cached entries across displays, runs, and notebooks. Binary mime values are
always object references — never base64, regardless of payload size
(invariant 26); a value under a binary mime that is not a reference carries
no renderable data and clients fall back to the bundle's other mimes.

## Attachments (object references inside turn text)

A frontend attaches a file to a turn by ingesting the bytes first and then
referencing the content address inside the turn text. Upload:

```
POST /v1/agent/sessions/{sid}/objects?name=<display name>
Content-Type: <media type>
<body: raw bytes>
→ 200 {"sha256": "<64 lowercase hex>", "byteLength": 12345}
```

The bytes travel natively in the request body — never base64 (invariant 26).
`name` is optional display metadata. The object is content-addressed: the
answer's `sha256` is the path of the same `GET /v1/objects/{sha256}` stream
the display-bundle references above use, and re-ingesting identical bytes is
idempotent. Typed failures: unknown session `404 not_found`; a payload over
the server's ingest bound `400 invalid_request` (the queue's oversized-payload
precedent); malformed answers are client-side `protocol_error`. There is
deliberately no capability flag: a server without the endpoint answers
`404`, which frontends surface as "attachments unsupported".

The turn references the object with a **marker** in the turn text (the same
text `POST /turns` and `POST /queue` already carry — no wire-shape change):

```
[[maieutics:object sha256=<64 lowercase hex> mime=<type/subtype> name="<escaped>"]]
```

The server parses markers out of the submitted text into the turn's
structured data parts (`application/vnd.maieutics.blob+json`) and composes
the turn from the remaining text plus those references. The grammar is
strict, and both sides must agree exactly:

- fields are fixed-order (`sha256`, `mime`, `name`), each preceded by exactly
  one space; `name` is double-quoted and may contain any printable character
  except `"` and `\`, which travel escaped as `\"` and `\\` (control
  characters never appear — emitters replace them).
- only the exact canonical form is a marker. Anything else — uppercase sha,
  reordered or missing fields, stray whitespace, a bad mime token, an
  unterminated quote — is ordinary text and stays literal, so a user-written
  look-alike can never become a reference and a **client newer than its
  server degrades safely**: the markers reach the model as plain text and the
  feature is inert, never a crash.

Markers ride unchanged through the queue (items carry `text`, so attached
turns enqueue like any other), the transcript (the user message keeps the
text part plus one data part per marker), and notebook turn bindings.
Object lifetime is the object store's: an upload becomes referenced when its
turn commits, and unreferenced objects are pruned by `%session gc` after the
grace period — a marker whose object was pruned fails typed (`not_found`),
not silently.

## Skill references (user-selected skills inside turn text)

A frontend lets the user select a skill (its catalog comes from
`GET /v1/skills` below) and references it inside the turn text with a
**standard markdown link over the custom protocol**:

```
[<display text>](skill://<name>)
```

`<name>` is the catalog name (`[a-z0-9][a-z0-9-]{0,63}` — the same charset
the `skill://` host uses) and is the reference identity; the link text is
display metadata. The grammar is strict, and both sides must agree exactly:
only the exact form is a reference — an uppercase letter, a path or query on
the URL, a title, or stray whitespace is ordinary text and stays literal, so
a hand-written look-alike degrades instead of injecting a skill body. A
**backslash directly before the opening bracket** (`\[text](skill://name)`)
suppresses recognition and the escape is consumed: that is the *mention*
form, how a frontend encodes link-shaped text that is NOT a user selection.
Frontends that track selections in an editor-side model use the mention form
for link-shaped text the user did not select.

At submission the server parses references out of the text (after attachment
markers) and expands each distinct name once, in order of first appearance:
the body is read fresh through the same `skill://` plane the model reads,
bounded per marker, and appended as framed text parts after the turn's text
and attachment parts — framed as user-selected instructions, so the model
receives the selected body as a guarantee, not a pointer it may choose not
to follow. Typed submission failures: a name not in the current catalog
`404 skill_unknown` (pruned, renamed, inert, or disabled); the per-turn
body budget (8 MiB across all references, 4 MiB per marker) exceeded
`413 skill_budget_exceeded`. A host without a skill catalog answers
`409 agent_configuration_error`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/skills` | The live skill catalog: `{skills: [{name, description, source}], diagnostics}` — active skills only; catalog freshness is the frontend's cache policy |

## Prompt references (user-selected MCP prompts inside turn text)

A frontend lets the user select an MCP server prompt (the catalog comes
from `GET /v1/prompts` above) and references it inside the turn text
with a markdown link over the dedicated scheme (ADR 0041):

```
[<display text>](mcp-prompt://<serverId>/<name>?<arguments>)
```

The `serverId` segment is an **opaque token** — deliberately not a valid
generic URI authority — and the operative invariant is that a prompt
reference never enters a generic URI parser: the kernel's regex IS the
parser (literal `mcp-prompt://` prefix; the name split is from the
right). Prompt names fit `^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$` (the MCP
spec's own example is snake_case); servers or prompts whose id segments
cannot carry the grammar list with `referencable: false`. Only the exact
form is a reference; a backslash directly before the opening bracket
suppresses recognition and is consumed (the mention form). Arguments
travel as URL query: keys and values percent-encoded with the RFC 3986
unreserved set literal (encoders must escape `!'()*` explicitly —
`encodeURIComponent` leaves them raw); duplicate keys, missing required
arguments, and arguments the server did not declare all fail typed.

At submission the server expands each distinct `(serverId, name,
canonicalized query)` once, in first-appearance order, **after** skill
parts (fixed order, position in the cell does not reorder the shared
budget): the kernel resolves the server's generation, validates
arguments against the declared set, calls `prompts/get` under the
server's request timeout, and injects the result as framed user-side
content with kernel-forged role headers (role-shaped body text is
escaped — template content cannot forge framing). Text blocks are kept;
image, audio, and embedded-resource blocks drop with diagnostics. Typed
submission failures: unknown server or prompt `404 mcp_prompt_unknown`;
reconnecting server or timeout `409 mcp_prompt_unavailable` (the message
names the reason); argument problems `400 mcp_prompt_argument_invalid`
(listing each missing argument's name and description); a structurally
unusable result `400 mcp_prompt_result_invalid`; the shared
reference-content budget exceeded (8 MiB across skill bodies and prompt
expansions; 4 MiB per prompt) `413 skill_budget_exceeded`. A host with
no MCP servers answers `409 agent_configuration_error`. Generic markdown
renderers may mishandle the opaque href — an accepted limitation; the
reference's arbiter is the kernel grammar, not the renderer.

## Input requests (REPL stdin, MCP elicitation, and tool approval)

A REPL `prompt()` surfaces as an `input.request` frame. The frontend answers
with:

```
POST /v1/agent/inputs/{requestId}   body: {"value": "..."}   → 200 {} | 404
```

The answer completes the pending request; a second answer for the same id is
`404`.

Three consumers share this frame and endpoint. The request id's prefix selects
the owner before any lookup, so an id can never cross-wire:

| Prefix | Producer | Answer |
|---|---|---|
| `input-` | REPL stdin | `value`; no `action` |
| `elicit-` | MCP elicitation (ADR 0029) | `value` plus `action` |
| `approve-` | Tool approval (ADR 0044) | `value` plus `action` |

The frame carries an explicit `kind` (`input` / `elicitation` / `approval`) so a
frontend picks the right surface instead of inferring it from which optional
fields are present. Older clients that ignore `kind` still answer, because the
action vocabulary is shared.

MCP elicitation requests (ADR 0029) add two optional fields, `schema` (the
server's primitive form schema object) and `serverId` (the configured server that
raised it); older clients ignore them and can still answer. Their answers carry
`action`: omit it (or `"accept"`) with `value` set to a JSON object of field
values for form elicitation, or send `{"value":"", "action":"decline"|"cancel"}`
to refuse.

Tool-approval requests (ADR 0044) add `tool` and `reason` alongside
`kind: "approval"`. The tool's arguments ride the `schema`'s `description` as
read-only context, never as editable fields: the user approves the call the model
made, and editing it here would create a second, unreviewed argument source. The
answer's `action` is the decision:

| Action | Decision |
|---|---|
| `accept` / `allow` / `approve` | Approved for this call |
| `always` / `always-allow` | Approved, and remembered for this session |
| `decline` / `deny` / `reject` | Denied; `value` carries the user's reason |
| `cancel` | Denied |
| absent or unknown | Denied |

A denial is a recoverable tool failure: the model receives
`{"status":"error","code":"tool_denied"}` and the turn continues. The gate never
hangs and never silently executes — a run that ends, a session with no live
stream, a timeout (120 s by default), and a malformed answer all resolve to a
denial.

If the run ends before an answer arrives, the request is cancelled
server-side and any late answer is `404`. A detached presentation scope
cancels REPL stdin and elicitation; a tool-approval request keeps waiting for
its budget (it is bounded by `Maieutics:Agent:ToolApproval:WaitTimeout`, so a
closed notebook denies rather than executing).
Dismissing the input box should post an empty value.

## IDE requests (Problems and language services, ADR 0042)

A frontend that can answer IDE requests — the extension host is the only
process holding `vscode.*` — announces that on the **events upgrade request**:
`GET /v1/agent/sessions/{sid}/events?…&ide=1`. The announcement is per
socket and lapses when the connection closes; a kernel without a live
announcing socket never offers the `ide_*` tools to a run.

The kernel then asks questions by publishing an `ide.request` payload as an
`application/json` **`tool.progress` content** on the requesting run's event
stream — sequenced and replayable with the run's own events, so a reconnecting
frontend receives the request again:

```json
{"type": "tool.progress", "runId": "…", "sequence": 7, "callId": "…",
 "content": {"kind": "json", "value": {"ideRequest": {
   "requestId": "ide-…", "method": "textDocument/hover",
   "params": {"uri": "file:///…", "position": {"line": 41, "character": 6}}}}}}
```

Agent runs announce the request as a sequenced `tool.progress` content
item (replayed with the run). Script/plugin calls (REPL `tools.invoke`,
plugin `capability.invoke` — ADR 0042 stage D) have no run to ride, so
the kernel instead pushes a standalone, unordered frame to every
announcing events socket; it is not replayed, and the answer endpoint's
404 idempotence covers the missing replay:

```json
{"type": "ide.request", "data": {"requestId": "ide-…", "method": "textDocument/hover",
 "params": {"uri": "file:///…", "position": {"line": 41, "character": 6}}}}
```

The frontend answers:

```
POST /v1/agent/ide/{requestId}
  body: {"value": <any JSON>}   or   {"error": {"code": "…", "message": "…"}}
  → 200 {} | 400 | 404
```

Exactly one of `value` or `error` must be present. The kernel's tool call
awaits the answer under its own timeout; a request that is no longer pending —
already answered, timed out, or cancelled with the run — is a typed `404`
that the frontend **absorbs silently**: replayed requests re-answer and land
here, and that is not an error surface. The params travel kernel-translated:
URIs are physical `file://` addresses and positions LSP 0-based
`{line, character}`; results may use the same shapes and the kernel maps URIs
back to the workspace domain and re-checks containment. The `method` values
come from a fixed read catalog (`textDocument/hover`, `…/definition`,
`…/references`, `…/documentSymbol`, `workspace/symbol`,
`maieutics/problems`, and the rest of ADR 0042 decision 3); the kernel never
sends outside it and there is no arbitrary-command passthrough. Write-group
language operations return data only and are applied kernel-side through the
gated edit path (later stage).

### Workspace file-system forwarding (`maieutics/fs/local/*`, ADR 0042 decision 12)

While the calling session has a live announcing frontend, the kernel serves
the workspace repository file system through the bridge instead of its local
walk, so reads answer the IDE's text state (dirty buffers included) and
writes land through `workspace.fs`. These requests are **targeted**: the
kernel elects one authoritative announcer per session (the earliest live
announcement; re-elected deterministically on disconnect) and pushes the
frame only to that socket — other announcers never see it, so the
first-answer-wins broadcast cannot answer a read with a different window's
buffer. An operation that loses its target mid-call fails typed
`ide_unavailable`. The methods:

- `maieutics/fs/local/read` — `{uri}` (physical `file://`) →
  `{content}`: the dirty document's buffer text, else a fatal-UTF-8 decode
  of the file bytes (`fs_not_text` on binary), capped at 8 MiB
  (`fs_too_large`).
- `maieutics/fs/local/list` — `{uri, maximumEntries}` →
  `{entries: [{name, kind, sizeBytes?}], truncated}` with `kind` one of
  `file|directory|symbolic_link|other`; names sorted, kernel pages locally.
- `maieutics/fs/local/write` — `{uri, content, expectedHash?}` →
  `{}`: the frontend hashes its current state and answers
  `fs_conflict` on a mismatch (no silent last-write-wins), then
  `workspace.fs.writeFile` (parents created).
- `maieutics/fs/local/delete` — `{uri}` → `{}` via `workspace.fs.delete`.

- `maieutics/fs/local/search` — `{uri, query, regex?, caseSensitive?,
  maximumMatches?, include?, exclude?, respectIgnore?}` →
  `{matches: [{path, line, column, preview}], truncated, scannedBytes}`:
  a bounded scan of the tree under the kernel-translated `file://`
  root — literal or regex (pattern ≤ 512 chars), 1-based lines/columns,
  `path` relative to the root with posix separators. Local-disk roots
  are served by ripgrep when available (the install's bundled rg, then
  the PATH; grep as the universal fallback, columns always 1), with the
  default posture respecting VCS ignore files and including hidden
  entries except `.git`; the globs are rg-style (`**`, `*`, `?`; a
  pattern without a separator names a segment at any depth) and the
  provider scan judges the same globs itself. The kernel maps paths onto the workspace
  domain and applies its own match bound (200). The same scan serves
  the `idefs://` family as `maieutics/fs/search`
  (`{folderId, segments, …}`, matches relative to the searched folder).

### Debug bridge (`maieutics/debug/*`, ADR 0042 decision 13)

The kernel drives the user's debugger through a bounded slice: list launch
configurations (`maieutics/debug/configurations` — launch.json parsed
extension-side, JSONC tolerated), start by name or inline configuration
(`…/start`, optional `stopOnEntry`; **the session is visible in the debug
UI** — the stable API has no headless mode), wait for a transition from
running (`…/state`, `waitSeconds` ≤ 20), inspect a paused session
(`…/stack`, `…/variables` — a frame's scopes flattened, or a
`variablesReference` expansion; `…/evaluate` in `repl` context), set or
clear a file's breakpoints (`…/breakpoints`, kernel-translated
`file://` source), and step/continue/terminate (`…/control`). State
transitions ride the debug events; every request is one ide.request /
answer pair, never a subscription.

Kernel-side gates (instruction surface, bounds, the dirty gate over
`maieutics/dirty`, unified diff) run before the bridge exactly as for the
`idefs://` family. **Coverage**: the kernel forwards only paths under the
elected frontend's workspace folders (fetched once per election from the
`maieutics/fs/list` root answer and cached); a handler whose window does not
cover the target answers `fs_not_covered`, and the kernel serves the path
locally instead. A frontend predating this family answers
`ide_method_unsupported`; the kernel absorbs that once and serves the
session from local I/O again (the pre-bridge behavior).

### IDE document editing (`maieutics/documents/*`)

The IDE edit tools use current editor documents, not the filesystem forwarding
family above. These additive methods use the existing authenticated IDE request
and answer endpoints (IDE capability version 1); unsupported methods fail typed
and never degrade to disk writes. One call pins the owning session's elected
frontend registration, including the provider computation. Requests are targeted
standalone `ide.request` frames and are not broadcast or replayed through the run
stream. Disconnection fails the call instead of switching editors mid-transaction.

- `maieutics/documents/read` — `{uris: [physicalFileUri, ...]}` →
  `{documents: [{uri, content, version}, ...]}`. Opens `TextDocument`s without
  showing editor tabs; unsaved text is authoritative.
- `maieutics/documents/apply` — `{documents: [{uri, content, version,
  expectedHash}, ...]}` → `{applied: true}`. `content` is the kernel-validated
  final document text; `version` and SHA-256 `expectedHash` refer to the snapshot
  before editing. All targets must pass validation before one text-only
  `workspace.applyEdit` transaction. Changes enter the editor undo stack and
  remain unsaved. Version/content conflicts fail as `ide_document_conflict`;
  editor rejection fails as `ide_edit_rejected`.

Requests contain at most 64 distinct local workspace documents, with an 8 MiB
combined UTF-8 text budget (before plus after for apply). Kernel per-file write
bounds, instruction-surface gates, write policy and range/overlap validation
still apply. The apply request is not exposed through the read-only `ide_request`
escape hatch. No create/delete/rename resource operations are accepted.
Computed rename/code-action edit envelopes add a `versions` map keyed by URI;
the kernel translates its keys and preserves the versions when applying edits.
Formatting records the document version before computation.

Workspace saving is an explicit, separate barrier:

- `maieutics/documents/dirty` — `{}` → `{documents: [{uri, version, kind}, ...]}`.
  `kind` is `text` or `notebook`. Enumerates dirty file-backed documents across
  every local project of this frontend's multi-root workspace, including
  pre-existing user edits. Unrelated and untitled documents are excluded.
- `maieutics/documents/save` — `{documents: [{uri, version, kind}, ...]}` →
  `{status: "saved", saved: [uri, ...]}` or `{status: "failed", saved: [...],
  failedUri, reason: "conflict" | "save_failed", pending: [...]}`. The kernel
  authorizes every target against workspace containment, instruction and write
  policy before requesting any save. No contents or binary notebook data are
  transferred. At most 256 save targets are accepted, with no silent truncation.

Saving uses `TextDocument.save()` / `NotebookDocument.save()`, preserving native
save participants and file-conflict handling. A changed version or newly dirty
document rejects the barrier; changes introduced by a document's own successful
save participants are permitted. The final workspace check must find no dirty
in-scope documents. Saves are sequential, so earlier successful saves remain
persisted after a later failure or cancellation. The kernel maps failure to
`ide_save_failed` / `ide_document_conflict`, including a bounded failed target
and count of successful saves. It never reports a partial save as success.

The agent tool is `ide_save_all`. `terminal_run`, `terminal_input` and
`terminal_paste` accept additive `saveWorkspace: true` to enforce this barrier
before allocating a terminal or delivering input; failures/cancellation prevent
the dependent operation. Tool instructions require this flag for IDE-backed
builds/tests. The default is false for compatibility and non-build operations;
shell command text is not parsed to guess intent. One elected frontend remains
pinned throughout each barrier, and requests are never broadcast or replayed.

## Comm channels and declarative forms

New plugin component data/action transport uses the plugin HTTP gateway (ADR 0051), rather than
expanding form events into general RPC. The bearer-authenticated `/v1/plugins` response retains its
existing `pageUrl` field, now populated only for an approved live HTTP mount with a plugin-scoped
credential. URLs lacking a current mount or scoped credential are omitted. Changing the plugin ID
in such a URL does not grant access to another plugin; worker replacement invalidates the old
credential. This is additive to the internal host registry's optional mount `accessToken` field;
older registry frames remain readable without exposing the host-wide gateway token in a page URL.
Scoped requests support credentialless CORS after mount/token validation; native HTTP response
bodies carry binary and streaming data. Existing comm framing and form capabilities below are
unchanged. The Vue client and remaining component binding are described in
[ADR 0051](architecture/decisions/0051-plugin-scoped-http-fetch.md).

The generic session comm transport carries owned producer state and actions. Built-in forms use
`application/vnd.maieutics.form+json`:

```json
{"version":1,"modelId":"opaque-comm-id","state":{"title":"Approve","fields":[],"values":{}}}
```

`comm_open.target_name` is `maieutics.form`, and data is `{state}`. Downlink messages use
`{method:"update",state:{...}}`; the form adapter merges received state into its current snapshot.
Uplink actions use `{method:"event",name:"submit",payload:{values:{...}}}` or `name:"cancel"`.
This is a data/action contract, not a component registry or component-source envelope.
Kernel elicitation, approved plugin workers/declarative plugin entries and REPL forms use the same
contract; uplink routes to the registered kernel or worker owner.

UI capability version 2 advertises `{version:2,formVersion:1}`. The former version-one `families`
contract, old view MIME, bundled-source execution, widget authoring and widget renderer are removed
(ADR 0050). Generic comm framing remains version 1. Older clients retain `text/plain` fallback.

The standalone Vue client owns normal form components and reactive state. Hosts supply transport.
Only confirmed live state enables actions, and a mounted view identity scopes the frontend bridge.
Closing a view releases its attachment, not another view's model; closing the model or losing its
transport leaves retained output readonly. Saving/reading a form adds `snapshot:true` and retains
only version/modelId/state; that presentation never attaches, emits actions or accepts live state.
Historical form announcements are decoded into readonly data. Other retired component output
becomes JSON/text and never executes its embedded source.

### `GET /v1/agent/sessions/{sid}/comms?sinceSeq=<n>&token=<hex>`

Full-duplex WebSocket, session-scoped (ADR 0024). `token` is accepted as a
query parameter like the events endpoint; an unknown session is
`404 not_found` (legacy single-active servers answered
`404 session_not_active` for a non-active one). The server's first frame is JSON text:

```json
{"live": [{"commId": "…", "targetName": "…"}], "replayed": false, "truncated": false}
```

`live` is the registry of currently open comms (identities survive replay
eviction); `replayed` is true when a non-zero `sinceSeq` was requested (the
replayed set may still be empty); `truncated` reports that the client's `sinceSeq` precedes
the retained buffer, so state must be treated as unknown-until-refresh.

After the hello, every application frame is **binary**:
`[sequence:8 big-endian][comm frame]` where the comm frame is the fixed binary
comm encoding shared with the child hop —
`[kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][metadataLen:4][metadata][bufferCount:2][bufLen:4][buf]...`.
`data` and `metadata` are UTF-8 JSON (empty length = absent); buffers are
native bytes, never base64 (invariant 26). Direction rules:

- `kind 0` (`open`) is REPL-originated only. The frontend never sends it; an
  uplink open is a protocol violation and closes the socket.
- `kind 1` (`message`) and `kind 2` (`close`) flow both ways. On the downlink
  the server stamps the envelope sequence; on the uplink the client sends
  sequence `0` (the server assigns ordering).
- An uplink message or close for an unregistered `commId` is answered with a
  JSON text frame `{"type": "comm.error", "code": "comm_not_found", "commId":
  "…"}` and the socket stays open. Uplink while the session's REPL is
  detached yields `code: "repl_unavailable"`. A text application frame is a
  policy violation and closes the socket.
- Per-message ceiling: 16 MiB (buffers included). Exceeding it closes the
  socket.

Backpressure and resume follow the events stream: bounded per-subscriber
queues; on overflow the server closes with `1011 backpressure` and the client
reconnects with its last observed sequence. Sequences are dense per session. Planes are retained per session (least
recently used, a small fixed window) rather than torn down eagerly. Producer state lives in its owning REPL or plugin worker; the
relay does not own a component engine. Frames are re-sent verbatim on replay. Form state merges
are last-wins; missing bootstrap state keeps a view readonly rather than guessing that it is live.

`GET /v1/agent/capabilities` advertises the feature as
`"comm": {"version": 1, "maxMessageBytes": 16777216}`; clients that do not
implement comms ignore it and never open the endpoint. Declarative forms advertise
`"ui": {"version": 2, "formVersion": 1}`. New component/model contracts must version their own
capabilities; opaque comm targets do not themselves grant component execution.

## REST endpoints (frontend → executable)

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/agent/capabilities` | Protocol version, server version, workspace root, feature flags |
| GET | `/v1/skills` | Live skill catalog for completion and pickers: `{skills: [{name, description, source}], diagnostics}` |
| GET | `/v1/prompts` | Live MCP prompt catalog: `{prompts: [{serverId, state, name, title?, description?, arguments: [{name, description?, required}], referencable}]}` — every live server generation |
| GET | `/v1/agent/session` | The foreground session (compatibility alias; id, turn count, persistence state, title) |
| POST | `/v1/agent/sessions` | Start a new session and make it active |
| GET | `/v1/agent/sessions` | List stored sessions with display metadata (persistence disabled → empty) |
| GET | `/v1/agent/sessions/{sid}` | Addressed session metadata without changing foreground |
| POST | `/v1/agent/sessions/{sid}/resume` | Resume a stored session and make it active |
| POST | `/v1/agent/sessions/{sid}/rename` | Set or clear one session's manual title |
| POST | `/v1/agent/sessions/{sid}/title-policy` | Change title mode or request regeneration |
| POST | `/v1/agent/sessions/{sid}/fork` | Fork a stored session at a turn and make the fork active |
| POST | `/v1/agent/sessions/{sid}/gc?graceHours=24` | Prune unreferenced objects |
| POST | `/v1/agent/sessions/{sid}/repair` | Rebuild the derived object view |
| POST | `/v1/agent/sessions/{sid}/objects` | Ingest one binary object into the session's object library |
| POST | `/v1/agent/sessions/{sid}/turns` | Submit one Agent turn → `202 {runId}` |
| GET | `/v1/agent/sessions/{sid}/transcript` | Authoritative history snapshot |
| POST | `/v1/agent/runs/{runId}/cancel` | Cooperative cancel; waits for termination. A runId matching a live subagent child run of the session cancels that child |
| POST | `/v1/agent/ide/{requestId}` | Answer an `ide.request` payload (ADR 0042) → `200 {}` (`400` invalid body, `404` no pending request) |
| POST | `/v1/agent/commands` | Execute a `%`-command cell → `{markdown}` |
| POST | `/v1/agent/complete` | Command completion for the current cell text |
| GET | `/v1/model/profiles` | Selectable model profiles (id, provider, model, selected) |
| GET | `/v1/status` | Status snapshot as markdown |
| GET | `/v1/objects/{sha256}` | Immutable binary object stream (content-addressed) |

The comms channel above is the one full-duplex WebSocket; the REST table
stays request/reply only.

Every session-addressed route is served for its addressed session: a live
session is used as-is, a stored one is lazily resumed, and an unknown id is
`404 not_found`. Addressing a session never moves the foreground. This is
advertised as `multiSession: true` on `/v1/agent/capabilities`; the process
keeps an arbitrary number of live sessions (bounded; lazily resumable ones
are evicted least-recently-used first), and the model-profile override
(`%model use`, fork `profileId`) is per session — the configured default
stays process-level. The legacy gate no longer occurs on this build; older
single-active servers still rejected a session other than their single
active one (`409 session_not_active` from turns, `404 session_not_active`
from events/comms).

The session listing is a persisted catalog, not the live-session registry: a fresh
zero-turn session may be absent until its first turn is committed or it is renamed.
Clients must not interpret absence from this listing as `not_found`. The foreground
session response or the addressed transcript endpoint (which may return an empty
`turns` array) establishes that a session is usable, with or without persistence.
Only an addressed `404 not_found` invalidates a multi-session notebook's pin;
transient or authentication failures must not replace it with another session.
Virtual notebook files also resolve unknown ids through the addressed transcript
when VS Code requests `stat` before `readFile`.

`POST /v1/agent/sessions/{sid}/turns` body: `{"text": "..."}`. Empty text is
`400`. `%`-command text is executed as a command (the same `Maieutics.Commands`
semantics every frontend shares) and answered with `200 {markdown, sessionId}`
instead of starting a run. `sessionId` is the addressed session unless the
command moved the foreground (`%session new` / `resume` / `fork`), in which
case it is the new foreground — a notebook frontend re-pins only when it
differs from its pinned session. Session-aware commands (`%session current`,
`%model use/current/reset`) are scoped to the addressed session.

Session `title` can be manual or automatic. `titleMode` is `auto`, `initial`, or `manual`;
`titleLocked` indicates manual ownership, and `titleSource` is `preview`, `derived`,
`initial`, `tool`, or `manual`. `auto` performs initial naming once asynchronously after a
successful turn and allows the primary Agent to propose later changes with `set_task_title`.
`initial` generates once; `manual` never generates. A user rename locks the title; clearing
releases the lock and resets initial eligibility without changing the mode. Failure retains
the preview/name without per-turn retries. Generation never delays run completion. See
ADR 0046 for schema-v6 migration and budgets.

`POST /v1/agent/sessions/{sid}/title-policy` accepts `{"mode":"initial"}` or
`{"regenerate":true}` and returns full session metadata. Invalid modes and regeneration of
a locked/manual title return `400 invalid_request`. Regeneration admits one asynchronous
request using committed context; empty history waits for the next successful turn.
The mode is user-owned and cannot be changed by the model.

`SessionInfo.titleRevision` is monotonic (older servers omit it). `session.updated` carries
complete `session` metadata on the general session socket, including while idle; it has no
`runId` or run-local `sequence` and does not enter run replay. Clients render it independently
of answer content and reject older revisions. Intermediate full-state notifications may
coalesce. Reconnect `hello.session` and `GET /v1/agent/sessions/{sid}` recover current metadata;
run-only subscribers use these reads. Manual and automatic changes use the same path.

`GET /v1/agent/sessions/{sid}/transcript` returns the committed public
transcript rendered provider-neutrally:

```json
{
  "sessionId": "…",
  "version": 3,
  "turns": [
    {
      "runId": "…",
      "truncated": false,
      "model": {"profileId": "default", "provider": "openai", "model": "…"},
      "messages": [
        {"role": "user", "parts": [{"kind": "text", "text": "…"}]},
        {"role": "assistant", "parts": [{"kind": "text", "text": "…"}]}
      ]
    }
  ]
}
```

`POST /v1/agent/complete` body: `{"text": "...", "cursor": 12}` where `cursor`
is a UTF-16 code-unit offset (no Jupyter code-point conversion). Response:
`{"matches": ["…"], "tokenStart": 0, "tokenEnd": 8}`.

Built-in completion and command cells do not require a configured model or a
working provider credential. The VS Code adapter uses the returned token range
for replacement, including qualified model selectors. Slash discovery keeps a
slash-prefixed editor filter while inserting the canonical `%` command; spaces
explicitly trigger subcommand completion because Markdown disables quick
suggestions by default.

## Session display metadata

Stored sessions carry bounded display metadata so frontends can name and
group them without loading transcripts (schema v4 of the transcript store):

- `GET /v1/agent/sessions` items are
  `{id, turns, createdAt, lastActivityAt, title?, preview?, workspaceRoot?, parentSessionId?, forkPointSeq?}`.
  `title` is the user-set name (absent when never renamed); `preview` is the
  first committed user message, collapsed to one line and capped at 200
  chars (absent before the first committed turn); `workspaceRoot` is the
  workspace root stamped when the session's row was created (absent for rows
  written before the column existed). `parentSessionId` and `forkPointSeq`
  describe a fork head: its visible history is the parent's first
  `forkPointSeq` turns followed by its own (both absent on root sessions).
- `GET /v1/agent/session` and the `session` inside `/v1/agent/capabilities`
  carry the **foreground** session (most recently activated: start / resume /
  fork move it; per-session addressing does not). Prefer the
  session-addressed routes; the singular endpoint is a compatibility alias.
- `/v1/agent/capabilities` carries `workspaceRoot?` — the process's current
  `Maieutics:Workspace:Root`, live across `%workspace use` switches — so a
  frontend can detect a server serving another workspace.
- `POST /v1/agent/sessions/{sid}/rename` body `{"title": "..."}` sets the
  title; empty or whitespace-only clears it. The answer is
  `{"id": "…", "title": "…"}` with the stored, normalized title (nulls
  omitted). Renaming is **not** active-session-gated and works for any stored
  session; renaming a session with no stored row creates one (zero turns), so
  a named session is listed before its first committed turn. Errors:
  `400 invalid_request` for a malformed id or a title over 200 chars. The
  command surface equivalent is `%session rename <id-prefix> <title...>`.
- `POST /v1/agent/sessions/{sid}/fork` body is exactly one of
  `{"runId": "…"}` (a committed turn of the source; the fork keeps the turns
  before it and re-runs it as the fork's first turn) or `{"seq": n}` (the
  number of the source's committed turns the fork keeps, `0..turns`). The
  fork is a new head in the source's root family database (ADR 0009: turns
  are referenced, never copied), it is auto-titled from the source's
  title/preview plus `" · branch @ turn N"`, and it becomes the
  foreground session. The answer is `200 {"id": "…", "title": "…"}` (nulls omitted).
  Forking is **not** active-session-gated and fork of a fork chains through
  the parent link. An optional `profileId` switches the model profile first,
  so the fork's first turn runs on it (regenerate-with-model); an unknown
  profile is `400 invalid_request`. Other errors: `404 not_found` for an
  unknown session or a run id that never committed on it, `400
  invalid_request` for a malformed body, an out-of-range `seq`, or
  persistence disabled. The command surface equivalent is
  `%session fork <id-prefix> <runId|seq>`.

Queue snapshots also carry a monotonic `version` and a bounded `outcomes` list (the
latest 128 admissions, retaining nonterminal admissions). Each outcome has `itemId`,
`state` (`queued`, `starting`, `running`, `completed`, `failed`, or `cancelled`), an
optional accepted `runId`, and an optional typed `error: {code,message}`. A dequeued
`starting` item still counts as outstanding even when `items` and `running` are empty.
Completed outcomes retain their run id; a startup failure without a run retains its
safe error. Removing or clearing pending items records `cancelled`. These additive v1
members are in-memory, bounded display/recovery state, not persisted conversation history.

Clients reconcile with `GET /queue` after registering the enqueue answer's item ids,
ignore snapshots older than their observed server `version`, and attach outputs through
`events?runId=<accepted-id>` from sequence zero. Session sockets observe queue/activity;
run-scoped sockets own run output. This recovers runs that started or finished before the
HTTP admission response and prevents duplicate rendering. The batch waits for both the
queue's terminal outcomes and the corresponding run replay/terminal events. An admission
whose outcome and run association expired is an explicit `queue_item_expired` display
failure; a failed lookup or stream must never leave an execution silently pending.
`running` is omitted on the wire when null; clients normalize it before use. A client
requiring outcome recovery checks for `outcomes` before admitting work; an older
executable surfaces `queue_recovery_unavailable` with a restart/upgrade instruction,
instead of silently accepting a turn whose output it cannot recover.

After run-scoped replay (or `run.missing`), the server sends a normal WebSocket
close frame before cancelling the receive pump. One bounded close-handshake budget
covers the close write and peer acknowledgement, so terminal output is delivered
before teardown; a peer that does not acknowledge is aborted when that budget ends.

A client-initiated event-socket close also wakes an idle or active stream without requiring a new
run or queue mutation. Closing a subscription does not cancel its session's Agent turn; another
connection can resume the retained output. Control WebSockets are canceled on application stopping
so upgraded requests do not hold Kestrel's shutdown open until hosted services stop.

## WebSocket event stream (executable → frontend, half-duplex)

`GET /v1/agent/sessions/{sid}/events?sinceSequence=<n>` upgrades to a
WebSocket. The endpoint carries server→frontend frames only; the client sends
nothing except the close. Because the browser-standard WebSocket API cannot set
headers, this endpoint additionally accepts the bearer token as a `token` query
parameter; every other endpoint requires the `Authorization` header. The
optional `ide=<version>` query parameter is the frontend's IDE-capability
announcement (ADR 0042): per socket, lapsing with the connection, and gating
whether the kernel publishes `ide.request` payloads to this session. Frames are
JSON text:

```json
{"type": "hello", "session": {"id": "…", "turns": 2}, "replayed": false}
{"type": "run.started", "runId": "…"}
{"type": "reasoning.delta", "runId": "…", "sequence": 3, "messageId": "…", "text": "Displayable summary fragment"}
{"type": "text.delta", "runId": "…", "sequence": 4, "messageId": "…", "text": "…"}
{"type": "message.completed", "runId": "…", "sequence": 5, "messageId": "…",
 "agentMessage": {"role": "assistant", "parts": [{"kind": "text", "text": "…"}]}}
{"type": "tool.started", "runId": "…", "sequence": 6, "callId": "…",
 "tool": "workspace_read", "arguments": {"…": "…"}}
{"type": "tool.progress", "runId": "…", "sequence": 7, "callId": "…",
 "content": {"kind": "text", "text": "…"}}
{"type": "tool.finished", "runId": "…", "sequence": 8, "callId": "…",
 "result": {"status": "ok", "value": {"…": "…"}}}
{"type": "tool.approval_requested", "runId": "…", "sequence": 9, "callId": "…",
 "tool": "remove_file", "reason": "The tool declares that it may destroy data or resources.",
 "arguments": {"…": "…"}}
{"type": "tool.approval_resolved", "runId": "…", "sequence": 10, "callId": "…",
 "approved": true, "reason": "Approved by the user.", "alwaysAllow": false}
{"type": "turn.truncated", "runId": "…", "sequence": 11}
{"type": "run.completed", "runId": "…", "truncated": false,
 "model": {"profileId": "…", "provider": "openai", "model": "…"},
 "usage": {"inputTokens": 11, "outputTokens": 7, "totalTokens": 18}}
{"type": "run.failed", "runId": "…", "code": "agent_provider_error", "message": "…"}
{"type": "repl.display", "displayId": "…", "data": {"text/markdown": "…"}}
{"type": "repl.updateDisplay", "displayId": "…", "data": {"text/markdown": "…"}}
{"type": "run.status", "state": "busy" | "idle"}
{"type": "input.request", "requestId": "input-<unique>-1", "prompt": "Name:", "password": false}
{"type": "input.request", "requestId": "elicit-<unique>-1", "prompt": "Pick:", "password": false, "schema": {"type":"object","properties":{…}}, "serverId": "srv"}
{"type": "input.request", "requestId": "approve-<unique>-1", "prompt": "Allow 'remove_file' to run? It may destroy data.", "password": false, "kind": "approval", "tool": "remove_file", "reason": "…", "schema": {"type":"object","properties":{},"description":"{\"path\":\"/tmp/x\"}"}}
```

`reasoning.delta` is an additive v1 display event for explicitly permitted provider
reasoning summaries. It shares the run-local sequence and replay rules of `text.delta`;
`messageId` groups fragments from one provider message/iteration. It never contains
raw private reasoning, signatures, or encrypted replay state. The first adapter is
OpenAI Responses (`response.reasoning_summary_text.delta`); sources with
`RequestReasoningSummary: true` request summary output. Other adapters must explicitly classify displayable summaries before
emitting them. `message.completed` and the public transcript continue to omit reasoning;
completion does not replace previously streamed summaries. A failed run keeps partial
display summaries without committing its transcript. A cancelled run commits its user input,
completed tool rounds, and received partial assistant content as a `truncated` transcript turn;
unanswered function calls receive `{"status":"cancelled"}` results for subsequent model replay.
Cancellation still terminates with `run.failed` (code `run_cancelled`), after the transcript commit.
Display summaries remain outside the public transcript.

The VS Code frontend renders summaries as collapsed, escaped plain text separately
from the answer, including child runs. Per-run view retention is bounded to 20,000
UTF-16 characters and 64 summary messages. The optional `output.reasoning` member in
notebook v1 snapshots stores `{messageId,text}` entries, including under subagent
reports. Older snapshots remain valid; older readers may ignore this member. These
are display snapshots, never canonical model history. Server transcript restore alone
does not reconstruct them; event replay or a saved notebook supplies them.

VS Code keeps one ordered timeline throughout streaming and completion. Each assistant
message, permitted summary, tool invocation and child run gets an independent notebook
output block in first-appearance order. Assistant reply blocks are always expanded, initially use
VS Code's default Markdown renderer, and replace their text region with shared passive SSR when the
connected kernel supports it. Source revisions reject obsolete responses; failure or offline viewing
retains native Markdown. Activity nodes default to collapsed summaries; expanding one reveals the original content. Expansion
survives output updates. Saving joins node fragments into one portable snapshot; reopening
restores separate collapsed outputs. Tool parameters, latest progress and results retain
up to 20,000 characters per detail and mark truncation explicitly. Notebook v1 snapshots
may also carry `output.timeline`: entries `{kind:"text",messageId,text}`,
`{kind:"reasoning",messageId}`, `{kind:"tool",index}` and `{kind:"subagent",index}`.
Indexes reference the corresponding output arrays. Retention is bounded to 2,048 entries
and 200,000 text characters, plus 256 tool records per run; `timelineTruncated` marks a capped preview. Old files remain
valid and use grouped activity. Public transcript restoration reconstructs message/tool
order without inventing missing display summaries. This adds no frontend wire fields.

Rules:

- `sinceSequence` is per run; a reconnecting client passes the last sequence
  it observed for the run(s) it still renders and the server replays from the
  retained buffer. Frames older than the buffer produce `{"type":
  "run.missing", "runId": "…"}` so the client can refetch from the transcript
  endpoint instead of rendering a gap.
- Frames whose `runId` differs from the serving run's id belong to **subagent
  child runs** the run spawned (ADR 0030): they use the same frame vocabulary
  and the child's own run-local sequence, plus child lifecycle frames
  (`run.started` / `run.completed` / `run.failed` carrying the child's
  `runId`; a cancelled child publishes `run.failed` with code `cancelled`).
  Child frames replay best-effort from the stream's retention — live delivery
  is never dropped — but a client that missed child history refetches the
  report from the spawning `tool.finished` result instead of the frames (the
  child transcript is not on the transcript endpoint).
- The replay buffer is bounded per run; events are never silently dropped
  (invariant 16). If a consumer's send queue overflows, the server closes the
  socket (`1011`, reason `backpressure`) and the client reconnects with
  `sinceSequence`.
- Runs started before any connection existed are buffered regardless of
  connections, so a late client can still catch up.
- Tool activity, presentation, and status frames route by `runId`; REPL
  presentation frames use the display id as the tracking key (the extension
  maps it onto one updatable notebook output).
- `run.completed` carries the run's model identity and the provider-reported
  token usage (additive; absent on older servers, on failures, and when the
  provider reports nothing). The chat-completions wire flavor only returns
  usage when the client asks for it — the executable's OpenAI adapter asks on
  the Responses flavor, which always reports it.

## Notebook snapshot (frontend-owned)

`.maieuticsnb` is a frontend-owned portable interaction snapshot; the server
never reads or writes it. Save/load must not mutate the live session
(invariant 13). Shape (frontend-side schema, informative here):

```json
{
  "kind": "maieutics-notebook",
  "version": 1,
  "session": {"serverSessionId": "…", "createdAt": "…"},
  "cells": [
    {
      "kind": "agent" | "markdown",
      "text": "…",
      "output": {"kind": "agent", "turn": {"text": "…", "tools": ["…"]}}
    }
  ]
}
```

## Renderer process failure and retry

A supervised renderer generation has its own private readiness nonce, readiness task, process and
port. Readiness callbacks authenticate only the current non-retired generation. Unexpected exit is
observed, logged at Warning and invalidates the port; affected work returns `503 render_unavailable`.
Transport/startup failures are recoverable: a subsequent admitted request can start a fresh process.
There is no permanent failure latch and no automatic replay of the failed request. This is derived
presentation recovery, independent of run event sequencing and canonical history.

Caller cancellation does not retire a healthy renderer. Shutdown rejects new admission and cancels/
joins admitted operations before disposing the generation and its transport/resources. No wire shape
or protocol-version change is introduced. A permissionless SSR worker can end its entire Deno
renderer via Deno's allowed self-signal; generation recovery handles that case. Separate kernel PID
signal attempts are denied and covered by a separately owned process smoke test. These limits do not
constitute a complete process-liveness or untrusted-code sandbox.

## Explicit template rendering (v1)

`rendering: {"version":1,"templateVersion":1}` advertises an additional authenticated, stateless
`POST /v1/render/template` request/reply endpoint. The Markdown endpoint remains passive. Request:

```json
{
  "version": 1,
  "documentId": "report1",
  "source": "Total {{ report.total.toFixed(2) }}\n\nLater prose. {{ empty }}",
  "template": {
    "version": 1,
    "sourceSealed": false,
    "bindings": [
      {"name":"report","state":"pending"},
      {"name":"empty","state":"ready","undefined":true}
    ]
  }
}
```

Bindings have unique ASCII identifier names (maximum 64 characters). States are `pending`, `ready`,
`failed`, and `cancelled`; only ready bindings carry `value` or `undefined:true`, exclusively. JSON
null is a real ready value. `undefined`, `constructor`, `prototype`, and `__proto__` are reserved names.
Unknown fields are tolerated; unknown states/versions and invalid data fail with `400 invalid_request`.
Data is JSON-only: finite numbers, strings, booleans, null, arrays and records, with no prototype keys.
Limits: 128 bindings, 8,192 data values, 16 data nesting levels, and 64,000 aggregate UTF-16 units in
string values and object keys; keys are at most 128 UTF-16 units. Existing 2 MiB request, 200,000 source,
4,000,000 HTML, eight-request admission and 30-second total rendering limits apply.

The response extends the Markdown response with `expressions`, each containing `state`,
`dependencies` (names actually read), and an optional fixed `code`. State additionally includes
`incomplete` for unfinished syntax. The zero-based index matches `data-template-index` spans for that
snapshot, including container titles registered during rendering; it is not a stable source identity.
Ready undefined/null produce empty text, false produces `false`, and zero produces `0`; pending
produces an ellipsis. Binding failures/cancellation and invalid expressions produce local diagnostics.
Expression source and producer error details are not echoed in metadata.

Only the data-expression subset in [the design](streaming-output-rendering-design.md#explicit-template-snapshots-implemented-2026-11-05)
is supported. Interpolations are not recognized inside code spans/fences or Markdown escapes. Values
are inserted as escaped text without reparsing. Missing names stay pending until `sourceSealed:true`,
then fail as unresolved; explicitly pending names stay pending. Short-circuit branches depend only on
values they read, and pending never acts as false or undefined. Each snapshot allows 256 interpolations,
2,048 UTF-16 units per expression, 256 interpreter steps and 32 AST traversal levels. Exceeding the
interpolation-count or data limit rejects the request; per-expression failures remain local.

The supervised child exposes authenticated `POST /v1/template` with the same request and HTML/state
response (CSS remains kernel-owned). It uses the existing child lifecycle and permissions and does
not retain documents or call a producer. Requests and answers are correlated by the client request
revision; no server document revisions, publication stream or automatic producer settlement is implied.

A REPL rich display can carry `application/vnd.maieutics.template+json`:

```json
{"version":1,"source":"{{ report.total }}","sourceSealed":true,"bindings":[{"name":"report","state":"ready","value":{"total":42}}]}
```

Pair it with `text/plain` fallback. It travels through existing `repl.display`/`repl.updateDisplay`
frames and notebook-v1 MIME snapshots unchanged. A later update with the same display ID replaces
source and the complete binding list; it never merges partial binding lists. VS Code recognizes this
MIME through the production serializer/renderer and requests the explicit template API when supported;
otherwise it retains native Markdown. The bridge additionally bounds serialized template context to
128,000 UTF-16 units. Persistence includes source/data, not rendered HTML or producer code. Opening or
re-rendering a snapshot never launches a producer. An interrupted producer may leave a frozen pending
snapshot; automatic producer registration, cancellation settlement and late subscriptions are not
part of this stateless version.

## Explicit live Vue rendering (v1)

`rendering: {"version":1,"vueVersion":1}` additionally advertises authenticated, stateless
`POST /v1/render/vue`. This route alone selects component compilation and evaluation:

```json
{"version":1,"documentId":"counter1","source":"<script setup>import { ref } from 'vue'; const count = ref(0)</script>\n\n<button @click=\"count++\">{{ count }}</button>","vue":{"version":1,"format":"markdown"}}
```

`format` is `markdown` (Vue-in-Markdown) or `sfc` (complete Vue single-file component). Version and
format are required. Only static imports from `vue` are supported. Other imports, dynamic imports,
other `import.meta` expressions, external blocks, custom blocks, CSS modules and non-CSS style
preprocessors fail with
`400 invalid_request`. Source must form a complete compilable component. Unknown fields remain
tolerated. Ordinary Markdown and data-template routes do not evaluate component scripts.

The response extends the Markdown shape with
`"vue":{"version":1,"code":"…","scopeId":"data-v-counter1"}`. `html` is initial SSR output;
`code` is a compiled component factory body taking the shipped Vue runtime as its `Vue` argument.
`css` includes the baseline stylesheet and compiled component styles. Source, HTML and document ID
limits remain unchanged; compiled code is capped at 400,000 UTF-16 units and component styles at
48,000. Existing 2 MiB request, eight-request admission and 30-second total budgets apply. SSR runs
in a fresh permissionless worker with a five-second deadline and private reply port. Worker failure
or timeout returns `503 render_unavailable`; authored errors and compiler details are not echoed.
The internal child exposes authenticated `POST /v1/vue` with the same request and component metadata;
its CSS contains component styles only, with the baseline added by the executable.

The live frontend hydrates with the matching bundled Vue runtime and owns/unmounts one app per output.
A changed-source replacement resets component state when it commits. Identical source/mode/binding
redelivery on an unchanged layout keeps the live app and client state. While the next render is
pending, the committed app stays mounted; failed rendering commits the latest native fallback.
Local Vue event/reactivity updates do not generate Agent events or modify canonical history. The client revision guard rejects obsolete responses before
mounting. The current contract verifies synchronous deterministic setup; setup executes in both SSR
and hydration. External operations remain authorized producer work. Async setup/Suspense and resource
hydration are not specified. This is executable live output under ADR 0038/0047, not a browser sandbox.

The corresponding rich-display MIME is `application/vnd.maieutics.vue+json`:

```json
{"version":1,"format":"markdown","source":"<script setup>import { ref } from 'vue'; const count = ref(0)</script>\n\n<button @click=\"count++\">{{ count }}</button>"}
```

Publish through the existing explicitly selected REPL rich-display API and include `text/plain`
fallback. Live output delivery and updates use existing display identities. VS Code selects this MIME
only for live output, requests the explicit endpoint, and hydrates successful SSR. Unsupported or
failed rendering retains native source/fallback. Streaming partials and unverified restored assistant snapshots
do not select execution; the completed-assistant policy below separately covers live and verified
canonical historical answers. The compiled factory is not accepted from a saved notebook or a MIME bundle.
Notebook v1 reading and saving both discard the Vue MIME and preserve its other fallback items;
reading also strips executable `esmSource`/`cssSource` from existing bundled-view MIME. Reopening a
snapshot executes no component; a live producer must redisplay it. Data-template snapshots remain
persistable. No document revision protocol, producer subscription or patch stream is introduced.

## Completed live assistant rendering (v1)

`rendering: {"version":1,"assistantVersion":1}` advertises authenticated, stateless
`POST /v1/render/assistant`. Clients select it for a successfully completed **live assistant** source:

```json
{"version":1,"documentId":"reply1","source":"# Counter\n\n<script setup>import { ref } from 'vue'; const count = ref(0);</script>\n\n<button @click=\"count++\">{{ count }}</button>"}
```

The request has the same bounds and unknown-field tolerance as Markdown v1; no Vue format selector
is required. Response is the existing `{version,documentId,html,css}` snapshot, optionally with
`vue:{version:1,code,scopeId}` under the same component bounds. Plain source uses passive SSR.
Top-level authored script blocks select Vue-in-Markdown compilation using the existing isolated
worker/runtime. Fenced, inline, escaped and quoted examples, and script text inside frontmatter, do
not select component execution. Invalid source is `400 invalid_request`; worker failure/timeout is
`503 render_unavailable`. Existing auth, queue admission, budgets and request-revision rules apply.
The internal child endpoint is authenticated `POST /v1/assistant` and returns component-only CSS;
the executable adds baseline styling. This endpoint does not change Agent history or event order.

The initial completed-only policy emitted local `application/vnd.maieutics.live-assistant+json`
after a successful terminal frame. The optional progressive policy below also admits streaming
text and preserves established setup through completion. Payload remains the existing turn snapshot. Unsupported frontends keep streaming text passive; failed/cancelled/truncated terminal snapshots
use passive MIME. A partial component may already have run before its terminal outcome is known. The final
text cache invalidates even if text bytes match its last streaming delta. The renderer sends the
local bridge request with `assistant:true`; template/Vue selectors cannot be combined with that
flag. The extension selects the endpoint only when advertised; older kernels retain passive SSR.
Optional compiled metadata is accepted only for the current selected assistant/component request.

Notebook v1 saving recognizes this local MIME and extracts ordinary source/timeline data. Restoration
through the pure codec emits passive turn MIME; a stored marker or source text alone cannot select
execution. A VS Code client may renew assistant provenance for session/run-bound history by reconnecting the
kernel and reading the
session-addressed canonical transcript: session ID, run ID, submitted input and the entire
assistant text node must match a committed non-truncated turn. Only that verified text node is
reconstructed as runtime assistant MIME; tool/user text and additional file-authored fields are not
promoted. The transcript `version` is a changing history revision, not a protocol selector.
Missing/unavailable history, changed source and offline files stay passive. This applies
to saved files and virtual session notebooks, including older history; it changes no notebook or
HTTP payload shape. Recovery re-compiles and hydrates the component with initial local state, without
submitting Agent turns, calling models/tools or starting external producers. An explicit live replay
from the authenticated kernel can also recreate presentation state. Component setup
still runs for SSR and hydration and must have deterministic initial state; external producers remain
explicit tools. This version waits for full source/turn completion and introduces no partial-script
execution, automatic binding subscriptions or document patches. The dedicated Vue-rich-display MIME
and explicit SFC format remain available for producer-driven output.

The shared content pipeline recognizes bounded frontmatter headers through the public mdit-vue plugin
with explicit uncached data parsers: YAML/YML/JSON bodies (at most 16,000 UTF-16 units) provide
the bounded `$frontmatter` data described below. JavaScript/CoffeeScript engines are rejected.
An unfinished header remains readable passive source and cannot authorize a component.
CSS additionally provides common
`--vp-c-brand-1`, `--vp-c-brand-soft`, `--vp-c-bg-soft`, `--vp-c-text-1`, `--vp-c-text-2`, and
`--vp-c-divider` host aliases. This adds no site page context or resource loader.

### Frontmatter data (2026-11-10)

Frontmatter headers supply bounded JSON-shaped data. Plain Markdown resolves only `$frontmatter`
data paths in interpolation; other expressions remain literal. Values are escaped and resolved
before heading/TOC extraction. Explicit templates reserve `$frontmatter` for document metadata.
Vue Markdown receives the same readonly data during SSR and hydration; metadata spans cannot inject
Vue syntax. Malformed or excessive headers return `invalid_frontmatter`; executable engines remain
rejected. Unclosed streaming headers remain readable until sealed. No HTTP, IPC or notebook shape
changes; metadata alone never selects Vue execution. `useData()` is not implemented. VitePress site generation, site layouts/navigation and document
`head` management are intentionally out of scope: the host frontend owns its enclosing document.
Frontmatter fields such as `head` remain inert data and cannot change the host document. See the rendering design for data limits and exact subset.

## Progressive assistant templates (optional v1)

`rendering.progressiveVersion:1` enables complete-prefix Vue rendering through the existing
`POST /v1/render/assistant`. Requests add optional boolean `progressive` and `compileOnly`:

```json
{"version":1,"documentId":"message1","source":"…","progressive":true,"compileOnly":false}
```

Initial accepted prefixes return the existing HTML/CSS/full factory, plus `renderedSource` and
`vue.setupKey` (64 hex characters) / `vue.renderCode` (at most 400,000 characters). `renderedSource`
is an exact prefix of the request source; it can stop before incomplete template syntax. A later
request with `compileOnly:true` returns empty HTML and empty `vue.code`, retaining CSS, setup identity
and a render-function factory. This path performs no SSR/setup execution. `compileOnly:true` requires
`progressive:true` and the assistant endpoint; other selectors reject progressive execution.

Clients mount only a full initial factory. Render-only updates require an existing matching setup
identity, document generation and source revision. They update the Vue-owned render function/CSS
while retaining the instance, refs, computed bindings and hooks; full factories from cache recovery
can also update an existing matching scope without re-running setup. Script rewrites start a new
source epoch and require a full initial factory. Template changes invalidate compiler render caches.
Complete synchronous setup is required; arbitrary later declarations and async setup are unsupported.

The notebook bridge enables `compileOnly` only after a correlated local `applied` acknowledgement
confirms a Vue commit for a current progressive result. Message delivery alone is insufficient.
Correlated current fallback commits invalidate that hint, including admission-overload fallbacks;
stale acknowledgements after rewrites, newer commits or
disposal cannot restore it. Receipt retention is bounded, and an evicted hint requires a full initial
result. Receipts store identities, epoch, sequence and accepted prefix length rather than retaining
source snapshots. This is frontend-local coordination; the authenticated stateless HTTP shapes are unchanged.

Supporting VS Code controllers emit the existing runtime assistant MIME during live text streaming.
Model HTML/source text or persisted marker fields cannot opt into this provenance. Clients without
the advertised capability keep streaming passive. Failure/cancellation/truncation return to passive
output and stop owned Vue effects. Successful completion preserves a progressive instance already
mounted. Notebook decoding remains passive; verified canonical history can renew provenance.

The local bridge may deliver accepted prefix snapshots before the newest source finishes. It uses
source epochs, revision ancestry and monotonic commits, rejects obsolete generations and sealed
streaming replies, and clears initialization hints on cancellation/disposal. Pending tails retain
the current component. Invalid sealed updates show a local diagnostic, not a guessed value.

The broader revisioned document transport and external late-binding producer lifecycle remain
proposed. This increment shares state only within one frontend's assistant text output, not across
messages/cells/frontends. Source/HTML/CSS/code, admission, total request and isolated worker limits
remain in force; at most 16 prefix compilation candidates are considered per progressive request.

### Rendering diagnostics and code copy controls

Rendering v1 success snapshots and typed render-input error bodies may include an optional
`diagnostics` array (maximum 32). Each item is `{code,line,column}`, with one-based coordinates in
the original request source. Codes currently include `unclosed_code_fence`,
`component_compile_failed`, `math_invalid`, and `math_limit`. No source snippet or compiler message
is transmitted. Fence positions identify the opening delimiter, math positions the containing
paragraph/block start, and component errors the authored component/script start; this is not a
full compiled-template source map. Clients tolerate absent fields and unknown future fields.
Diagnostics do not alter parsing or independently authorize execution. Display incomplete-stream
warnings only after source sealing. A diagnostic is scoped to its request/source revision.

The internal Deno service uses the same optional shape; the executable validates bounded codes and
source coordinates before forwarding it. The error HTTP status/code contract remains unchanged.
Code-fence copy buttons carry escaped source in `data-code-copy`; copying requires a user click and
frontend clipboard support. The VS Code renderer sends request-correlated `copy` / `copy-result`
messages to its extension host, independently of the rendering HTTP API. Text is capped at 200,000
UTF-16 code units, copied verbatim without synthetic decorations, and never logged. Disposal drops
pending UI references; stale responses do not update detached output.

### Default content theme and CSS bounds

The v1 rendering CSS response includes a scoped, offline VitePress default content theme. The host
retains ownership of its enclosing document and form controls. The baseline and authored component
CSS are each bounded to 48,000 UTF-16 characters; receivers supporting this theme admit their combined
96,000-character CSS response. The component limit and response fields are unchanged. Earlier 64k
receivers continue to render normal responses and may fall back for large combined stylesheets.
The CSS contains no imports or downloaded fonts; its external-link decoration uses an embedded SVG.
Dark hosts can mark the output or an ancestor `.dark`; Shadow DOM adapters also recognize VS Code's
dark/high-contrast contexts. Theme changes do not execute or reinitialize components.

### Client-only regions and SSR constants

Live Vue factories may include a local ClientOnly mount gate: initial SSR/hydration emits a matching
empty placeholder, then the default slot activates on browser mount. Late progressive templates can
use the retained gate and setup bindings without reinitializing state. Normal unmount owns cleanup.
This changes neither passive-content admission nor provenance; the parent setup still executes on
both sides. Script `import.meta.env.SSR` is a compile-time boolean (true in the private SSR target,
false in frontend factories). Other environment access/import policy is unchanged. Template code
uses a declared script binding rather than raw import.meta. The public response never carries the
private SSR factory. Factories remain v1 standard Vue code supported by existing frontend runtimes;
no new message, host service or capability selector is required.
