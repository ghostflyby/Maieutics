# ADR 0032: Content Provenance and the Injection-Defense Pipeline

Status: Draft

Date: 2026-09-24

## Context

The kernel's prompt-injection posture is thin. The security section says tool
output is untrusted; the presentation layer honors that for MIME bundles; the
permission overlay gates side effects. But the content path itself — what
actually enters the model's context, and what it claims to be — carries no
trust information anywhere:

1. **Tool results are unmarked.** Every tool result enters the transcript as a
   `{"status":"ok","value":…}` envelope with no provenance. `read_text` of a
   poisoned file, MCP tool output, or `object_fetch` content lands next to
   user turns and system instructions as undifferentiated text. Nothing
   distinguishes "the user said" from "a file the model was told to read said".
2. **Instruction surfaces are model-writable.** The kernel's system prompt is
   operator config (`Maieutics:Agent:SystemPrompt`), but the workspace's
   instruction conventions — `AGENTS.md`, `.agents/skills/**/SKILL.md` — are
   plain workspace files, and the model's own `write_text`/`edit_text` tools
   are ungated (workspace containment is the root path, nothing more). A run
   that is itself injected can persist instructions into the files that future
   agent sessions treat as trusted context: prompt-injection persistence.
3. **Derived trust is unmarked.** Child reports (ADR 0030) return
   model-generated content that may itself be the product of injected
   instructions; the parent consumes it as an ordinary tool result. Task-plane
   snapshots carried no origin label until this ADR.
4. **There is no extensibility point.** Any future defense (heuristic
   scanner, DLP, LLM classifier) would have to be threaded ad hoc through
   every tool.

What exists already is the right foundation: the permission overlay gates side
effects with deny-wins, the canonical transcript is the single content choke
point, `AgentContentTriage` is the single ingestion point, and child scopes
(ADR 0030 phase 3) already inherit permission structure. What is missing is
the *content-side* twin of the permission system: provenance and inspection.

## Decision

1. **Canonical content carries a `ContentOrigin`.** Every content item entering
   the transcript is tagged `system` (kernel/config instructions), `user`
   (human turn or human-attached attachment), `tool` (observed data from any
   tool or resource read), or `agent` (model-generated content from a child
   run). The tag is assigned at the two existing choke points — turn ingestion
   and the tool envelope — persisted additively in the canonical transcript
   (versioned, tolerate-unknown on the wire), and never mutated afterwards:
   provenance is immutable for the life of the content.
2. **Derived content inherits the lesser trust.** A child report is
   `agent`-origin; content an agent read is `tool`-origin in the child and
   stays `agent`-origin when its report reaches the parent. Provenance never
   upgrades across the run boundary: downstream consumers see the chain's
   least-trusted origin, not the transport's.
3. **Instruction surfaces are protected write targets.** Reserved instruction
   paths — `AGENTS.md`, `.agents/skills/**`, and the config system prompt via
   any command surface — gain a new permission-overlay kind (`instructions`)
   with deny-by-default for model-initiated writes. User and frontend writes
   are the grant path and are unaffected. This closes persistence poisoning by
   construction: injected instructions can still *exist* in tool output but
   cannot *land* where future context is built from. Existing workspaces that
   legitimately have the model maintain these files opt in with one allow
   entry.
4. **A pluggable inspection pipeline observes content at the choke points.**
   Ordered `IContentInspector` implementations run where content enters the
   transcript (tool envelope, triage). v1 semantics: **observe and annotate
   only** — an inspector may attach a flagged marker to the content's metadata
   and log; it may not rewrite or drop content (invariants 12 and 16 hold
   unchanged). Registry is DI-ordered; failures are observable and non-fatal.
   Enforcement stays where it already is: the permission overlay. Later
   implementations (heuristic scanner, LLM classifier, DLP) plug in without
   touching the runtime.
5. **Delimited rendering is the presentation-level mitigation.** Transcript
   mappers and frontends SHOULD render `tool`/`agent`-origin content inside
   explicit delimiters labeled with the origin. This is rendering hygiene, not
   a security boundary; the boundary is the write gate (decision 3) plus
   side-effect gating.

What is deliberately **not** done: prompt-side "treat data as data"
instructions are not a defense (soft mitigation at best); content is never
rewritten or dropped by inspectors; model-generated content is not blocked
from re-entering context (that would break the agent paradigm); injection
"intent detection" is not a gate.

## Consequences

- The canonical transcript schema gains additive origin metadata — a versioned,
  tolerate-unknown wire and persistence change (migration note required for
  stored transcripts).
- `write_text`/`edit_text` acquire a permission check for reserved instruction
  paths; default-deny is a behavior change for users whose workflows have the
  model maintain `AGENTS.md` — the migration is one allow entry, and it must be
  documented prominently.
- The inspection registry is the extensibility seam: new defenses are new
  inspector implementations, not new kernel branches.
- Child-report provenance composes with the task plane: snapshots already carry
  kind-specific detail; origin rides the same additive pattern.
- MCP tool descriptions remain in-context third-party content; marking their
  registry entries `tool`-origin is future work once MCP projection lands.

## Notes

- The workspace instruction conventions (`AGENTS.md`, skills) are consumed by
  coding-agent clients, not by the Maieutics kernel itself; the kernel's own
  instruction surface is operator config. The write gate protects both
  consumers: Maieutics runs cannot poison the files, and the files cannot
  poison Maieutics runs that later read them.
