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

Default execution applies only to assistant text from a successful completed live run. The VS Code
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
state from it; restoring reconstructs passive MIME. Opening a file never reproduces execution
provenance. Explicit authenticated live replay can recreate derived component state; rendering never
runs an external producer. No Agent contract, canonical transcript or notebook version changes.

Use the public frontmatter plugin with explicit uncached, non-evaluating parsers: bounded YAML/YML/
JSON header bodies are omitted and their values currently ignored. Executable engines are rejected.
Common VitePress content color variables map to host theme variables. Neither addition installs a
site runtime, filesystem imports, page resources or metadata bindings.

## Consequences

A complete Vue-in-Markdown counter can render directly from the assistant's response. A tool remains
available for explicit rich displays and producer-driven updates. Components appear after successful
turn completion; progressive component compilation and late producer subscription require the later
document runtime. Saved answers remain readable source snapshots, and existing passive clients and
APIs retain their behavior. The design, normative protocol and browser/controller regressions own
the compatibility and lifecycle details.
