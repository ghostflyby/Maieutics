# ADR 0048: Default Vue rendering for completed live assistant answers

Status: Accepted

## Context

ADR 0047 established working isolated Vue SSR and browser hydration through an explicit REPL rich
MIME. Normal assistant replies still used passive Markdown, so a model could output a valid
VitePress counter and show its script, buttons and interpolation as literal text. The user selected
direct compilation/rendering for live assistant answers. Requiring a tool call for a self-contained
presentation component no longer matches that intended default.

## Decision

Add authenticated stateless `/v1/render/assistant` and optional `rendering.assistantVersion:1` to
frontend protocol v1. The request uses the existing version/document/source shape; the response is
the existing snapshot with optional compiled Vue metadata. The kernel recognizes top-level authored
scripts through the public Markdown/SFC pipeline after frontmatter removal. Fenced, inline, escaped,
quoted and metadata examples do not select execution. Plain content retains passive SSR. Standalone
SFC input remains available through the existing explicit format selection.

Default execution applies to assistant text from a successful completed live run, and (as amended
on 2026-11-10) canonical historical assistant text verified against the connected kernel.
Session/run-bound history reconnects the kernel when needed; unbound files remain passive. The VS Code
controller marks that output with local MIME `application/vnd.maieutics.live-assistant+json`. Its
stream-to-complete cache transition must produce an update even if final text bytes are unchanged.
Streaming partials, failures, cancellation, tool results, reasoning and child reports do not acquire
that marker. The frontend waits for the full source/context before evaluating scripts; it does not
guess incomplete syntax or future bindings. This is a conservative completion boundary, not the
revisioned streaming document runtime.

Reuse ADR 0047's compiler, permissionless owned SSR worker, private result channel, five-second
deadline, compiled factory, browser runtime and disposal rules. The privileged compiler parent never
executes authored modules. Browser execution uses the existing live bundled-view posture, not a
browser sandbox. Setup runs for SSR and hydration; external effects/producers remain authorized tool
work. Compiled metadata is mounted only for a current explicitly selected component/assistant render
request. Unsupported kernels fall back to passive SSR.

The local live MIME is not a notebook format capability. Saving extracts ordinary source/timeline
state from it; the pure codec reconstructs passive MIME. A file cannot reproduce execution
provenance on its own. The connected restoration adapter can renew it only after verifying the
session, run ID, submitted input and exact assistant source against a committed non-truncated turn
in the authenticated canonical transcript. It reconstructs only the verified text node; altered,
unbound, unavailable or offline snapshots remain passive. This applies to both portable files and
virtual session views and restores already existing history. Setup reruns with initial reactive
state; browser-local interaction state is not persisted. Explicit authenticated live replay can also
recreate derived component state. Recovery never submits an Agent turn, calls a model, runs tools
or starts external producers. No Agent contract, canonical transcript or notebook version changes.

Use the public frontmatter plugin with explicit uncached, non-evaluating parsers: bounded YAML/YML/
JSON header bodies were initially omitted with their values ignored. The 2026-11-10 frontmatter
increment now exposes bounded JSON-shaped data through `$frontmatter`, with data-path interpolation
before heading extraction and identical readonly data in SSR/browser Vue. Metadata text cannot
inject template source; executable engines remain rejected. `useData()` is not implemented. Site/head/layout configuration is intentionally outside this
output-document renderer, not a pending compatibility requirement; its enclosing document belongs
to the host frontend. See the current rendering design for limits.
Common VitePress content color variables map to host theme variables. Neither addition installs a
site runtime, filesystem imports or page resources.

## Consequences

A complete Vue-in-Markdown counter can render directly from the assistant's response. A tool remains
available for explicit rich displays and producer-driven updates. Components appear after successful
turn completion; progressive component compilation and late producer subscription require the later
document runtime. Unavailable history remains readable source; verified history regains Vue UI and
interactivity. Existing passive clients and APIs retain their behavior. The design, normative protocol and browser/controller regressions own
the compatibility and lifecycle details.

## Progressive admission amendment (2026-11-10)

The user selected streaming component rendering with shared JavaScript state. On frontends using
`progressiveVersion:1`, complete synchronous setup/template prefixes may now execute before the
run terminal frame. The first prefix uses SSR; later render-only updates preserve the mounted
client scope and do not repeat server setup. A script rewrite creates a new scope. Failed or
cancelled terminal delivery stops further execution and unmounts its live scope; it cannot undo
presentation code admitted before the outcome. The earlier completed-only streaming restriction
is superseded for this optional lane. Persisted markers still grant no authority.

This increment owns one frontend-local scope per assistant text output, without backend JS closure
retention, cross-cell sharing or automatic producer execution. See the streaming design and
frontend protocol for prefix limits, binding checks, epoch rules and recovery semantics.
