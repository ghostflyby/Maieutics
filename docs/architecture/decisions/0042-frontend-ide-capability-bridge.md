# ADR 0042: Frontend IDE Capability Bridge — Problems and Language Services Through a Generalized Reverse Request

Status: Draft

Date: 2026-10-13

Related: ADR 0023 (custom web frontend protocol — the input
request/answer loop), ADR 0029 (MCP client reverse requests — attached
elicitation to that loop and named the kernel-side pending-input
registry), ADR 0038 (kernel capability advertisement on
`GET /v1/agent/capabilities`; comm ownership), ADR 0032 (instruction
surface gates the write path must re-enter), ADR 0037 (plugin
capability fingerprints), ADR 0024 (comm plane — the rejected
transport), ADR 0040 (contribution framework — why this is not a
contribution kind), `docs/web-frontend-protocol.md` (events-stream
section gains the additions). Revised through a two-lens subagent
review (codebase fact-check + adversarial design).

## Context

The agent runs in the kernel, but the only process that can reach VS
Code's Problems list (`vscode.languages.getDiagnostics`) and its language
services (`vscode.executeDefinitionProvider` and the other built-in
provider commands) is the extension host (`deno/maieutics-vscode`).
Notebook renderers hold only `acquireVsCodeApi().postMessage`; the plugin
host is a separate Deno process with no connection to VS Code. Any IDE
capability the kernel offers is therefore a reverse request to a
connected frontend.

The protocol has exactly one request/reply shape where the kernel asks
the frontend: the `input.request` frame answered by
`POST /v1/agent/inputs/{requestId}`. ADR 0029 named the kernel-side
pending-input registry; in code the endpoint dispatches by `action` to
the REPL presentation sinks' and the elicitation presenter's own
pending tables — no shared registry type exists yet. The events stream
is half-duplex (server→frontend frames only; the client sends nothing
but close), and the kernel's capability advertisements (`comm`, `ui`)
are server-side on `GET /v1/agent/capabilities` — there is no
frontend→kernel announcement channel. The comm plane (ADR 0024/0038) is
a state-sync surface for live models, not RPC. Nothing today exposes
diagnostics or navigation to the model, the REPL, or plugins.

Text addressing is split three ways: `read_text` takes a 1-based line
window, `search_text` returns 1-based line/column matches, and
`apply_patch` addresses edits by codex-V4A anchor context lines
(`ApplyPatchChangeSection`). Git unified diffs can be generated
(`UnifiedDiff.Create`) but not parsed.

## Decision

1. **`ide.request` is a second instance of the input reverse-request
   pattern, broadcast to the run stream — not a new transport and not
   the comm plane.** The kernel emits the `ide.request` payload as an
   `application/json` **`tool.progress` content item** on the
   requesting run's event stream (stage-A implementation finding: the
   run-local sequence allocator lives inside the Agent runtime, so
   riding the progress stream gives sequencing and replay for free
   instead of standing up a competing kernel-side frame counter) —
   the content's JSON value is the wrapped form
   `{"ideRequest": {"requestId", "method", "params"}}`, matching the
   protocol document's sample and the extension's extractor — and the
   frontend answers `POST /v1/agent/ide/{requestId}`
   with `{ value }` or `{ error: { code, message } }`; late or
   duplicate answers get 404, matching the inputs endpoint. Delivery is
   **broadcast to every subscriber of the requesting run's run stream,
   first answer wins**: a session may hold several events sockets (the
   run hub hands each run to exactly one waiting socket and per-run
   sockets may subscribe besides), so targeting "the" socket is
   unimplementable, and the answer endpoint's 404 idempotence already
   tolerates multiple answers. Unlike the flat, non-sequenced
   `input.request` presentation frame, the `ide.request` progress item
   carries a run-local
   sequence and is **replayable**: a frontend reconnecting mid-request
   receives the frame again and its answer can still complete the
   pending call, and a re-answer of an already-completed request gets
   404 — which the frontend **must absorb silently** (replay noise,
   not an error). Pending entries are owned by the initiating tool
   call's cancellation token (the elicitation ownership model): run
   cancellation or cooperative cancel removes the entry immediately,
   and later answers 404; a shared pending-request registry component is
   introduced for these entries, while the inputs endpoint and its two
   clients keep their existing dispatch untouched (wire compatibility).
   Per-request timeout defaults to 30 s; a per-method override knob is
   a later-stage addition (a loading language server can hang a
   provider command), and the wait counts against the turn duration
   budget like any other tool.

2. **The frontend announces IDE capability on the events upgrade
   request, not in hello.** The events stream is half-duplex and the
   `comm`/`ui` advertisements are server-side — there is no hello field
   for a frontend to write and no generic frontend→kernel channel to
   open. The upgrade request gains a query parameter beside `token`:
   `?ide=<version>`. The announcement is **per socket** — cleared on
   disconnect, no REST lifecycle to drift — and the version selects the
   method catalog of decision 3 (no per-frontend method lists in v1).

3. **The method catalog is a fixed, versioned allowlist in two groups —
   read and write — plus a `maieutics/` extension namespace; never
   arbitrary command execution.** Read group: `textDocument/hover|
   definition|typeDefinition|declaration|implementation|references|
   documentHighlight|documentSymbol|signatureHelp|foldingRange|inlayHint|
   selectionRange|semanticTokens/full`, `workspace/symbol`,
   `callHierarchy/prepare|incomingCalls|outgoingCalls`,
   `typeHierarchy/prepare|supertypes|subtypes` (hierarchy traversal is
   pure data — cataloguing `prepare` without its continuation requests
   would sell half a ladder), and `maieutics/problems` (workspace-wide
   or per-document, severity-filtered, from
   `vscode.languages.getDiagnostics`). Write group: `textDocument/rename`,
   `textDocument/codeAction`, and the `textDocument/formatting` family —
   constrained by decision 6 (they return data; application is a
   separate step) and reachable only through the stage-C named tools.
   Stage E adds `maieutics/fs/list|read` to the read group (the escape
   hatch reaches them accordingly); a future `maieutics/fs/write`
   joins the write group.
   The kernel sends only catalog methods at the announced version;
   everything else fails `ide_method_unsupported` without a round trip,
   and the escape-hatch tool (decision 8) exposes the **read group
   only** in v1. Forwarding raw `vscode.commands.executeCommand`
   strings is a red line: it would make the bridge an
   arbitrary-command surface.

4. **The wire contract is LSP-shaped JSON, the agent-side URI domain is
   exclusively `workspace://local`, and the workspace's logical↔physical
   mapping is the translation authority.** Raw VS Code SDK objects never
   cross the boundary (the invariant-11 discipline applied to the IDE
   adapter): `Position`/`Range`/`Location`/`Hover`/`DocumentSymbol`/
   `Diagnostic` are structurally near 1:1 with LSP (both 0-based
   `{line, character}`); `Hover.contents` normalizes to
   `MarkupContent`; `LocationLink` is kept as-is; code actions map
   Command-shaped results to LSP `CodeAction` with their
   `WorkspaceEdit` carried as data. URIs translate both directions
   through the workspace's logical↔physical mapping, **computed
   kernel-side in the tools** (stage-A implementation finding: only the
   kernel holds the mapping, so the tools resolve outgoing
   `workspace://local` URIs to physical `file://` addresses, and map
   returned `file://` URIs back; the extension host passes physical
   addresses through to VS Code unchanged):
   registered
   project links are link-aware (a link addressed by logical
   `workspace://local/projects/x/...` path must map back from the
   physical target `file://` URI, or every linked project's IDE results
   would be rejected as out-of-workspace). Untranslatable entries
   split two ways: the diagnostics aggregation path drops unmappable
   documents whole with a count (`unmappedFiles`), the other read
   tools pass untranslatable entries through verbatim, unmapped (the
   model has no read tool that can follow them) — `untitled:` and
   virtual-document URIs among them; only the diagnostics path
   actively excludes them, and `maieutics/problems` filters to
   workspace-contained documents before reading. The kernel re-checks containment on every returned
   URI (defense in depth): the reachable domain of the language-service
   tools equals the kernel read domain (decision 11's virtual-file family
   is the stated exception), and `file://` stays a closed reserved scheme.

5. **Agent-facing coordinates are 1-based (the `read_text`/`search_text`
   convention); the LSP facade is 0-based; the kernel is the single
   translation point.** All agent tools and the selector grammar
   below use 1-based lines and columns on both input and output, so the
   model never mixes conventions with the existing workspace tools.
   Columns are **UTF-16 code units** — the unit VS Code and LSP use —
   declared here once because the write path is the kernel's first
   consumer of character offsets.

6. **IDE computes, kernel writes — in two phases, pre-validated as a
   whole.** Write-group methods always return data: `WorkspaceEdit`
   (LSP-shaped: `changes`, `documentChanges` incl.
   `RenameFile`/`DeleteFile`/`CreateFile`) or formatting `TextEdit`s.
   Application is a separate explicit tool call (`ide_apply_edit`),
   which converts the edit to the internal representation and applies
   it through the existing gated path (`WorkspaceEditFunctions`), so
   instruction-surface gates (ADR 0032), unified-diff results, and
   editor-reload semantics are exactly those of every other kernel
   edit. Before any mutation, every target is pre-validated —
   containment, decodable text, size bounds, and **not dirty**
   (decision 7); any failure rejects the whole edit as typed
   `ide_edit_rejected` listing the failing targets, because an LSP edit
   is one semantic unit and the first-failure-stops apply path is never
   entered half-validated. A mid-apply I/O failure after pre-validation
   still stops at the first file (no transactional filesystem is
   introduced) and the result reports exactly which files changed. The
   extension host never calls `workspace.applyEdit` on the agent's
   behalf. Command-only code actions (no edit) are listed but not
   executed — executing arbitrary VS Code commands is out of scope by
   decision 3.

7. **Dirty documents are a typed rejection, and buffer/disk divergence
   is declared semantics.** VS Code's language services compute on
   in-memory buffers (unsaved changes included) while the kernel
   writes disk bytes; an edit computed against a dirty buffer and
   applied to the file would misplace. A write-group method targeting a
   dirty document fails typed `ide_document_dirty` (message: save or
   revert first); `untitled:` and virtual documents are rejected at
   translation (decision 4). Read-side tools answer the buffer state
   and `read_text` answers the disk state; on a dirty document the two
   may legitimately disagree — stated here so it is semantics, not a
   bug.

8. **One tool family with one selector grammar and one read-only escape
   hatch.** Stage-annotated: `ide_diagnostics(uri?, minSeverity?)`,
   `ide_hover(uri, select)`, `ide_navigate(kind, uri, select)` with
   kind ∈ definition | type_definition | declaration | implementation |
   references | highlights, `ide_document_symbols(uri)`,
   `ide_workspace_symbols(query)` (stage A–B, read); the escape hatch
   `ide_request(method, parameters)` (read group only, per decision 3);
   `ide_code_actions(uri, select)` and `ide_apply_edit(edit)` (stage C);
   `ide_format_document(uri)` (stage C — computes via the formatting
   method, applies through decision 6); the stage-E virtual-file
   tools (decision 11) extend this family. `ide_diagnostics` defaults
   to an **aggregate workspace view** (severity and per-file counts,
   top-N files) with per-file detail behind the `uri` drill-down — a
   thousand-problem workspace must not become an 8 MiB object paged
   back at 64 KiB — and an empty result carries the
   `analyzedDocuments` count, and when that count is zero (no provider
   has produced anything) an explicit no-active-source note
   (no `sources` field): **empty means "no diagnostics reported",
   never "no problems"**, and the tool description says so. Every position-taking tool shares one selector:

   ```text
   select :=
     | { line, column?, endLine?, endColumn? }   # coordinates (1-based); stage A
     | { anchor: [context lines], occurrence? }  # unique-match locator; stage B
     | { patch: string }                          # V4A or git unified diff; stage B
   ```

   The anchor form is a **new unique-match locator, generalized from —
   not extracted from — the apply-path matcher**: multi-line
   consecutive anchors, ordinal line equality with CR normalization,
   no whitespace drift, `occurrence` disambiguation, typed ambiguity
   failure listing bounded candidate positions, and scan/candidate
   budgets at `search_text` scale. The apply path (`apply_patch` and
   the git-diff conversion below) deliberately **keeps its existing
   first-match-after-seek semantics** — tightening it would change
   `apply_patch` behavior — and the divergence is intentional:
   addressing a hover must not silently pick the first of several
   lookalike lines, while patch-application compatibility is a wire
   promise. The patch form addresses through hunk context lines and
   makes a **git unified diff parser a new bounded component** (parse →
   convert to the internal ops representation → reuse the existing
   gated apply path; `UnifiedDiff` remains the generator). Anchors are
   the preferred addressing mode once stage B lands: the model has just
   read the text, and exact context lines beat coordinates under line
   drift.

9. **The tool family is gated per run on a live ide-announcing socket,
   through a consumer-owned seam.** `MaieuticsRuntimeConfiguration`
   includes the family only when any live events socket announced
   `ide` (process-wide at profile assembly; re-checked per session at
   invocation) — dynamic connection state, unlike the Shell gate's
   static profile capability. The seam follows the
   `IMcpElicitationPresenter`/deferred-presenter precedent: the
   interface lives at a layer the consumer (Configuration) may
   reference, and the composition root binds frontend connection state
   to it — **not** the `IFrontendPluginSurface` shape, whose direction
   (Plugins→Frontend) is the reverse of what Configuration may hold.
   The family joins `ReservedToolNames` (the terminal-tools precedent)
   so the hidden tools still reserve their names against MCP/plugin
   collisions. Mid-run socket loss or timeout fails that tool call as a
   typed, turn-recoverable error (`ide_unavailable`, `ide_timeout` —
   invariants 18/9); results are untrusted model-facing data, bounded
   by `MaxToolResultBytes` with the object-store spillover, like every
   tool.

10. **The same bridge is the TS API surface; no new consumption path is
    built.** The family registers in the composition root's
    script-tools list (beside workspace/edit/resource/terminal), so the
    REPL calls it through the control plane as
    `tools.invoke("ide_diagnostics", …)`; the `ide.*`-shaped TS facade
    that accepts LSP-native 0-based params and returns LSP shapes —
    reading as `sendRequest("textDocument/definition", {...})` — is the
    stage-D wrapper over those calls. A plugin capability `ide.query`
    joins `PluginCapabilityCatalog` (deny-by-default,
    fingerprint-covered by the existing manifest `capabilities` domain —
    ADR 0037 applies verbatim, no new fingerprint machinery); the
    worker→host→kernel relay is reused unchanged, and the kernel-side
    capability handler is new code.

11. **Virtual file systems are reached through `workspace.fs`, scoped to
    the frontend's workspace folders, and addressed by a new `idefs://`
    domain — the one capability the kernel cannot replicate, because the
    connection itself lives in the extension host.** A remote workspace
    (vscode-remote SSH/WSL/container, web) and extension-registered
    schemes exist only behind the frontend's authority: the kernel's
    filesystem tools see nothing to link (`projects/` links need local
    paths), and duplicating the remote connection kernel-side is exactly
    the second-implementation the git ruling rejects. The agent-facing
    domain is `idefs:///<folderId>/<relative-path>` — an **empty
    authority** (the `file:` scheme's standard local form: there is
    nothing constant to put in an authority, and arbitrary data there
    forfeits control of parsing to generic URI rules — case folding,
    host:port splitting, forbidden code points), with every component
    in path segments under one percent-escaping discipline. The
    `<folderId>` is an **opaque ASCII token assigned by the frontend**,
    never the workspace folder's display name: names are labels, not
    identities (duplicate basenames are legal and `.code-workspace`
    assigns arbitrary ones),
    and the ruling is that **display names appear in neither the API
    nor the implementation** — the root listing identifies folders by
    id plus their resolved URI (the platform's real identity, for
    audit and orientation) and by nothing else. The id is
    `<nonce><counter>`: the nonce is a one-time 8-character
    `[a-z0-9]` random string generated at extension-host activation,
    the counter is a base-36 count owned by that activation, and
    "frontend session" means one extension-host activation — ids are
    stable across socket reconnects and every id retires when the
    activation ends. Three allocation rules follow: the counter
    belongs to the activation and is assigned synchronously inside
    the extension host's single-threaded event loop; an id is bound
    to its folder for life at first listing, so insertions and
    deletions never renumber; and a retired id is never reused. The
    nonce is what makes different frontends' and different
    activations' id string spaces genuinely disjoint: under a bare
    counter every activation emits the same strings, and under
    decision 1's broadcast and first-answer delivery another frontend
    could win the race with an identically named id for a different
    folder — a silent wrong read the earlier "monotonic per frontend
    session" and "disjoint id spaces" argument did not actually
    prevent. Addressing: an
    `ide_fs_list` root call returns `[{id, uri}]`; the model addresses
    `idefs:///<id>/<segments>`, the relative segments follow the same
    segment syntax as `workspace://local` (whether the workspace-level
    denied names — `.git`, the state directory — carry over to idefs
    relative segments is decided at implementation); validation runs
    in full — id charset plus segment grammar — at the kernel tool
    entry before the bridge is sent, and the frontend re-validates
    immediately before `Uri.joinPath` as defense in depth, because
    `joinPath` normalizes dot segments under posix semantics and an
    unvalidated `..` would escape the folder root; the frontend maps
    id → its `workspaceFolders` entry → `Uri.joinPath` per call
    (no cache), and retired ids fail typed `fs_no_folder` with the
    fresh listing — all served through `vscode.workspace.fs`, which
    makes every scheme with a FileSystemProvider work identically.
    **One prefix fronts all providers by construction**: VS Code has no
    single VFS protocol, only unboundedly many per-scheme
    FileSystemProviders (the `vscode-remote` authorities plus any
    extension-registered scheme), and the scheme never crosses the
    boundary — id resolution dissolves it at the frontend, so the
    prefix count follows bridge-owned domains, not provider count; the
    scheme joins the kernel's reserved-scheme table with a null owner
    (the `file://` treatment — no provider may claim it) when
    implemented, which is what makes its uniqueness governed rather
    than assumed. Opaque ids also repair the multi-frontend hazard:
    name addressing lets two announcing frontends resolve one name to
    different folders silently, while disjoint per-frontend id spaces
    turn a foreign answer into a typed `fs_no_folder` instead of a
    wrong-file read; root listings stay per answering frontend and the
    kernel never merges them.
    Editor-virtual documents (`untitled:`, output) are not file systems
    and stay excluded, as in stage A. **This decision amends decision 4's
    domain sentence for this family: the reachable domain is the
    frontend's workspace folders — a domain the kernel cannot otherwise
    read — with structural containment as the compensating control.**
    v1 tools mirror the local shapes the model already knows:
    `ide_fs_list(uri?, cursor?, pageSize?)` (a root call lists the
    folders) and `ide_fs_read(uri, startLine?, maxLines?)` (the
    `read_text` 1-based line window); the catalog methods
    `maieutics/fs/list` and `maieutics/fs/read` carry `idefs://` URIs
    both ways — the frontend resolves the id internally — and return
    entries or the full decoded text under a 1 MiB bridge cap
    (`fs_too_large`), with kernel-side line windowing doing the rest.
    The fs family bypasses the stage-A translation layer entirely:
    `idefs` is already the agent domain and needs no translation, and
    fs paths do not reuse `TranslateIncoming`. "No VS Code scheme
    crosses the wire" rules on addressing; the root listing's
    resolved folder URI is the single deliberate metadata exception
    (audit and orientation — the deliberate contrast with the
    language-service family's containment discipline) and must not be
    pushed through the `file://`→`workspace://` mapping. The 1 MiB is
    deliberately not `read_text`'s budget: the local tool is a 64 KiB
    output window over an 8 MiB scan, while the bridge caps the whole
    text (remote providers offer no range reads; sinking
    `startLine`/`maxLines` into the method is a later optimization),
    and the bound is measured on the response body after JSON
    escaping. Reads answer the saved provider
    state — decision 7's buffer/disk divergence declaration extends
    verbatim. Text is the v1 plane: the frontend decodes with a fatal
    UTF-8 decoder and invalid bytes fail typed `fs_not_text`; binary
    rides the existing object plane in a later sub-stage (the frontend
    ingests through the attachment endpoint and answers with the content
    address — invariant 26 keeps binary out of JSON), and the returned
    content address is recorded as a session object reference when the
    tool result commits (the attachment-marker `blob_refs` path), so
    `%session gc` cannot reclaim it mid-session. Typed failures:
    `fs_no_folder` (listing the available names), `fs_not_found`,
    `fs_scheme_unreadable` (no FileSystemProvider for the scheme),
    `fs_not_text`, `fs_too_large`, beside the family's `ide_*` codes;
    frontend `FileSystemError`s map onto them — `NoPermissions` →
    `fs_access_denied`, `FileIsADirectory` → `fs_is_directory`,
    `Unavailable` and anything unrecognized → `fs_failed` — and
    provider error messages are stripped of physical URIs before
    crossing the wire. Pagination: root calls do not paginate (the
    folder count is bounded; exceeding the ceiling fails typed),
    folder listings page under a keyset cursor (the `list_directory`
    precedent), and the id is resolved before the listing so a folder
    removed in between fails `fs_no_folder`. The kernel's duty ends at
    syntax: it validates the grammar but never resolves an id and
    never caches a listing; `fs_no_folder` is the frontend's quick
    answer about its own `workspaceFolders`, not a wait under the
    request timeout.
    Pull-only (watch deferred, the diagnostics precedent). **Writes to
    virtual file systems are ruled kernel-gated and frontend-executed,
    staged after C**: only the frontend can perform the I/O, so the gate
    moves, not the authority — the kernel validates (permission
    overlays on `idefs://` patterns, instruction-surface rules, bounds)
    and computes the unified diff from before/after reads, the frontend
    executes `workspace.fs.writeFile`. Permission patterns anchor on
    the resolved folder URI — the id is not a policy key, it is a
    session-scoped token — idefs writes default-deny at the baseline
    until an overlay grants them, and a write request carries the
    before content hash as a precondition, failing typed
    `fs_conflict` on a mismatch instead of silently last-write-wins.
    This inverts decision 6's execution point while keeping its
    spirit — gates, diffs, and typed failures stay kernel-side — and
    it is the only write path whose execution point cannot be the
    kernel.

## Later stages

- **A — channel + read tools. — IMPLEMENTED 2026-10-13.** Upgrade-parameter
  announcement (`?ide=<version>` beside `token`), the `ide.request` progress
  payload, `POST /v1/agent/ide/{requestId}`, the pending-request registry,
  kernel-side URI/position translation, the extension-host bridge (fixed read
  catalog, hover normalization, workspace-filtered problems, silent 404
  absorption), `ide_diagnostics`/`ide_navigate`/`ide_hover`/symbol
  tools/escape hatch, and the announcement gate (`IIdeFrontendGate` +
  `IdeToolFamily`; names reserved even when hidden); the selector accepts the
  coordinate form only. Verified with tool-level round trips, the endpoint
  contract, and the announcement lifecycle against the composed host
  (`FrontendIdeBridgeTests`), plus pure-module bridge tests
  (`deno/maieutics-vscode/ide_test.ts`), and an end-to-end frame round
  trip (kernel progress frame → wire → answer endpoint).
- **B — addressing. — IMPLEMENTED 2026-10-15.** The unique-match anchor
  locator (`IdeSelector`: ordinal line equality, CRLF-normalized, no
  whitespace drift, `occurrence` disambiguation with 0 = unique-required,
  typed ambiguity/no-match failures listing bounded candidates, scan
  bounded at the `search_text` 8 MiB scale) wired into
  `ide_hover`/`ide_navigate`; the patch selector form locates through
  pre-image context lines — a V4A change section (`ApplyPatchParser`) or
  a git hunk's first hunk (`GitDiffParser`, the new bounded unified-diff
  parser in Execution, `a/`/`b/`-stripped paths, `/dev/null` sides,
  rename/binary/no-newline tolerance, typed malformed/too-large bounds) —
  with path-mismatch rejection so context is never borrowed from an
  unrelated patch. `apply_patch`'s first-match-after-seek semantics are
  untouched.
- **C — write path. — IMPLEMENTED 2026-10-15.** The write-group compute
  methods (`textDocument/rename|codeAction|formatting`) answer through the
  same ide.request channel and return data only; the named tools are
  `ide_rename`/`ide_code_actions` (compute) and `ide_format_document`
  (compute+apply folded), with `ide_apply_edit` as the only writer. The
  apply core pre-validates the whole edit (containment, decode, range
  table, computed size) before mutating, then one `maieutics/dirty` query
  gates everything on unsaved buffers (`ide_document_dirty`), then each
  file flows through the gated write seam
  (`WorkspaceEditFunctions.ApplyComputedContentAsync` — the instruction
  gate, size bounds, and unified diff of write_text); a mid-apply failure
  is reported with what changed and what is pending. The extension host
  gained write-group dispatch (`executeDocumentRenameProvider` /
  `executeCodeActionProvider` / `executeFormatDocumentProvider`) whose
  answers flatten to plain data — `workspaceEditToWire` notes the public
  API cannot read file-level workspace operations, so structural refactors
  lose those ops. Dirty checking costs one bridge round trip per apply;
  the `TranslateIncoming`/`TranslateOutgoing` deep walk now translates
  object property names too (an LSP changes map is keyed by them).
- **D — TS API faces. — IMPLEMENTED 2026-10-15, with a decision-10
  amendment.** Implementing the script-tools mount exposed that a
  script/plugin invocation has no run stream to announce on
  (`AgentToolContext.ReportProgressAsync` is agent-run-bound; the
  control-plane path hands tools a `ReplToolProgress` whose frames never
  reach the run events socket), so every scripted IDE call would time
  out. Amended: `FrontendIdeSockets` now registers announcing sockets
  (send delegate + per-socket send gate) and `SendAsync` grows a second
  announcement path — an unordered, standalone `ide.request` frame
  pushed directly to announcers, not replayed, with the answer
  endpoint's 404 idempotence covering the missing replay; agent-run
  announcement is unchanged. The gate degrades for context-less callers
  to the process-level check. Mounted in the script-tools list; the
  wire frame shape is documented in the protocol doc. The LSP-shaped
  TS facade and the `ide.query` plugin capability (which rides this
  channel) — the facade landed as
  `deno/maieutics-repl-client/ide.ts` (hover/navigate/symbols/diagnostics
  plus the request escape hatch over the script tools; LSP 0-based in,
  kernel 1-based conventions translated at the facade). `ide.query`
  landed as a catalogued capability (kernel whitelist
  `PluginHostManager.IdeQueryToolNames` narrows it to the read tools —
  writes are unreachable by construction) with seam tests for the
  whitelist and the grant path.
- **E — virtual file systems.** The `idefs://` domain and the two
  `maieutics/fs/*` methods (decision 11): `ide_fs_list`/`ide_fs_read`
  text plane, nonce-scoped folder-id resolution (`<nonce><counter>`)
  under the empty-authority URI grammar (display names never cross
  the API), structural containment, the fs family's bypass of the
  stage-A translation layer, typed failures. Later in the stage:
  binary via the
  object-store round trip,
  `maieutics/fs/search` over `workspace.findTextInFiles` (the remote
  grep — the search gap this family otherwise leaves), and — after C —
  the kernel-gated, frontend-executed write path.

## Consequences

- All protocol additions are additive within v1: an upgrade query
  parameter, one progress payload shape on `tool.progress` (no new
  frame type), one REST endpoint; old frontends ignore the payload
  (invariant 17). The events-stream section of
  `docs/web-frontend-protocol.md` gains the parameter and the payload
  shape — sequenced and replayable, deliberately unlike the flat,
  non-sequenced `input.request` presentation frame.
- The agent's tool surface varies with the connected frontend: a
  headless run silently lacks `ide_*` tools, a mid-run disconnect
  produces typed failures rather than hangs, and replayed requests
  recompute and absorb the 404 silently.
- The ide reachable domain equals the kernel read domain for the
  language-service tools; the virtual-file family is the stated
  exception (decision 11): it reads the frontend's workspace folders,
  structurally contained. Out-of-workspace entries split two ways —
  the diagnostics aggregation path reports them as `unmappedFiles`
  counts, the other read tools pass them through verbatim, unmapped
  (the model has no read tool that can follow them) —
  `untitled:` and virtual documents never surface as entries at all,
  and the kernel re-checks every returned URI.
- Until stage E's `maieutics/fs/search` lands, the idefs domain has
  no text-search equivalent — the `ide_fs_*` tool descriptions must
  say so.
- `apply_patch` behavior is unchanged (first-match-after-seek); the
  stricter unique-match semantics live only in the read-side selector
  locator.
- Diagnostics are pull-only in v1 (`maieutics/problems`); a push
  surface on `onDidChangeDiagnostics` would add unsolicited-frame
  semantics and stays deferred.
- The LSP facade is a request-shape-compatible query facade, not a
  `vscode-languageserver-protocol` client: no server lifecycle, no
  custom capabilities, no `didOpen`/`didChange` sync (VS Code owns
  document state), no resolve chains (completion/inlayHint item
  resolution is excluded with them), and no progress. Cancellation is
  not propagated into provider commands — after the kernel timeout
  abandons a request the frontend may still compute it, and the answer
  lands as a harmless 404.
- The MCP route (extension host as MCP server discovered via a plugin)
  was rejected: it detours through plugin approval to reach a process
  the kernel is already connected to, and degrades schema, progress
  attribution, and typed errors.
- The git unified diff parser closes the last asymmetry in the patch
  story (generate-only) and is independently useful for applying
  model-provided git patches through the gated path.
- File history through the bridge was proposed and rejected for local
  workspaces: the `vscode.git` API's log/show/diff/blame are pure git
  reads the kernel's terminal tools already cover with strictly more
  power (`--follow`, arbitrary ranges), and VS Code's local history
  has no public read API — the bridge admits only what the extension
  host uniquely holds. Remote workspaces' git history is a known gap —
  the kernel's terminal cannot see the remote FS; reading it through
  the `vscode.git` API stays deferred until a need is demonstrated.
- Virtual-file writes, when the stage-E write path arrives, invert
  decision 6's execution point under decision 11's own ruling
  (kernel-gated, frontend-executed) — the only family where the kernel
  cannot be the writer; every gate, diff, and typed failure stays
  kernel-side either way.
