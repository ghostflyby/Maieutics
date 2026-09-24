# ADR 0032: Provenance, Content Inspection, and Prompt Assembly Mechanisms

Status: Draft

Date: 2026-09-24

## Context

An agent has to act; safety comes from enforcement — the permission overlay
today, the sandbox later. The kernel's job is to provide the mechanisms those
enforcers consume. On the content path it currently provides none:

1. **Content carries no origin.** Tool results, MCP output, child reports,
   object-store content — everything enters the model context unmarked. The
   permission overlay cannot write rules that reference where content came
   from; the future sandbox cannot grade by origin; the model cannot
   distinguish data from instructions.
2. **Instruction surfaces have no gate.** `AGENTS.md`, `.agents/skills/**`,
   and the system-prompt surface are writable through `write_text`/`edit_text`
   with no permission kind covering them. A run — possibly itself injected —
   can persist instructions into the files future agent sessions treat as
   trusted context.
3. **The content path has no inspection seam.** Tool envelopes and
   `AgentContentTriage` are the only choke points; there is no place to plug a
   scanner, DLP, or classifier. Every future defense would be threaded ad hoc.
4. **There is no prompt-assembly mechanism.** Instructions are one config
   string: no named fragments, no assembly order, no session override, no
   origin declaration. Plugins, users, and the model have no way to compose
   prompts except concatenating text into context.
5. **Derivation has no representation.** A child report or MCP result entering
   the parent context is indistinguishable from directly authored content —
   which task produced it, through which chain, is nowhere recorded.

The workspace instruction conventions (`AGENTS.md`, skills) are consumed by
coding-agent clients rather than the kernel, but that only widens the surface:
Maieutics runs share the workspace and can write the files those clients trust.

## Decision

Mechanisms, not behavior doctrine. Each mechanism below is a facility whose
consumers are the permission overlay (rules, now) and the sandbox (grading,
later). Nothing here forbids a content flow; it makes flows addressable.

1. **M1 — Provenance metadata.** Canonical content carries additive metadata:
   `origin` (`system | user | tool | agent`) plus a derivation chain (the task
   URI / child session identifiers that produced it). Assigned at the two
   choke points (turn ingestion, tool envelope), immutable once written.
   Consumers: overlay predicates ("deny net unless the chain reaches a user
   turn"), sandbox grading, origin-labeled rendering, inspection.
2. **M2 — Instruction-surface gate.** Reserved instruction paths
   (`AGENTS.md`, `.agents/skills/**`, and the system-prompt command surface)
   match a new `instructions` permission kind evaluated by the existing
   overlay. The kind and path matching are the mechanism; the shipped default
   policy (deny model-initiated writes, one allow entry to migrate) is
   configuration.
3. **M3 — Inspection pipeline.** Ordered inspectors observe content at the
   tool envelope: in-process implementations (DI-ordered) and plugin
   inspectors riding the existing `ToolPostInvoke` hook with a
   `content:read-all` grant (install-approved — inspectors see everything).
   Inspectors emit verdicts and tags into the content's inspection metadata;
   they never rewrite or drop content. Enforcement for tags is overlay policy
   (e.g. `flagged ⇒ deny net`). Timeouts and inspector failures fail open with
   an `inspection-skipped` annotation; tool latency stays bounded.
4. **M4 — Prompt assembly.** Named instruction fragments with declared origin,
   a configurable assembly order (system → plugin → user by default), and
   per-session override through the existing overlay. Fragment sources:
   operator config, user-approved plugin declarations (static, manifest
   `instructions` section), and user files. Every assembled fragment carries
   M1 provenance, so composition is orchestratable and policy-addressable at
   once. There is no dynamic third-party prompt API; plugin instruction
   contributions are install-approved declarations.
5. **M5 — Derivation propagation.** Child reports, MCP results, and object
   content enter the transcript with their derivation chain filled from the
   task plane (task URI, child session id) — data that already exists, now
   written into the content metadata so M1 consumers see the chain.

## Consequences

- The canonical transcript schema gains additive origin/derivation metadata —
  a versioned, tolerate-unknown wire and persistence change (migration note
  for stored transcripts).
- `write_text`/`edit_text` acquire the `instructions` overlay check on
  reserved paths; the shipped default policy denies model-initiated writes
  with a one-allow-entry migration, and is configuration, not doctrine.
- Inspector plugins require the `content:read-all` grant at install time and
  are listed in plugin permissions (they are in the sees-everything trust
  tier); timeouts fail open with an `inspection-skipped` annotation, keeping
  tool latency bounded (invariant: bounded payloads; enforcement in policy).
- Prompt assembly gives plugins a legitimate instruction surface under the
  same overlay that gates everything else — composition is orchestratable and
  gated by the same mechanism, with no dynamic third-party prompt API.
- MCP tool descriptions remain third-party in-context content; marking their
  registry entries with M1 provenance is future work once MCP projection
  lands.
- The control channel's tool-hook transport carries the origin context for
  plugin inspectors; payload discipline follows the existing object-reference
  pattern (inline below the triage threshold, object reference above).
