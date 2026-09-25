# ADR 0032: Instruction-Surface Write Gate

Status: Draft

Date: 2026-09-24

## Context

Prompt injection is not a vulnerability to defend against at the content
path. The model reads files, sees tool results, encounters MCP output — some
of it will contain text that looks like instructions. That is normal agent
behavior: the model reads untrusted content and decides what to do.

Security lives at the **execution boundary**: the permission overlay gates
tool side effects (deny-wins, session scoping), and the future sandbox will
grade by origin. An injected model runs within the session's permission scope
and can do nothing the session itself cannot do. Injection does not grant
additional permissions.

What the architecture lacked was a gate on one specific execution boundary:
model-initiated writes to **instruction surfaces** — files that future agent
sessions treat as trusted context (`AGENTS.md`, `.agents/skills/**`). Without
this gate, a run can persist instructions into those files, effectively
modifying the context of future sessions. That is not prompt injection — it
is an unauthorized write to a protected path, and it is handled by the
existing tool-boundary gate mechanism.

## Decision

1. **Reserved instruction paths are a protected write target.** `AGENTS.md`
   (at any depth) and `.agents/**` are classified as instruction surfaces.
   Model-initiated writes (`write_text`, `edit_text`, `apply_patch`) to these
   paths require an explicit allow in the calling session's effective write
   policy; absence of a matching allow denies, and any matching deny denies
   (deny-wins). The mechanism is a reserved-path classifier plus the existing
   permission overlay — no new permission kind, no content inspection.
2. **No content-level security.** The content path is not a security surface.
   Tool results, MCP output, child reports, and REPL output enter the model
   context as ordinary data. No provenance metadata, no inspection pipeline,
   no origin tagging. The model reads what it reads; the tools enforce what
   the session's policy allows.
3. **The task plane and the model-orchestration surface (ADR 0031) are
   execution surfaces.** Spawn, wait, and cancel are tool operations gated by
   the same overlay; child scopes inherit the parent's permission structure.
   The task plane's wait/cancel contract is an execution boundary, not a
   content filter.

## Consequences

- Prompt injection is acknowledged as a property of the model paradigm, not
  a vulnerability in the kernel. The defense is at the execution boundary:
  the permission overlay gates side effects, and the instruction-surface gate
  protects context-persistence paths.
- `write_text`/`edit_text`/`apply_patch` on instruction paths require an
  explicit allow. The shipped default denies model-initiated writes to
  instruction paths; workspaces that legitimately have the model maintain
  these files add one allow entry.
- The kernel does not scan, tag, or classify content entering the model
  context. If future sandbox enforcement needs provenance data, the
  mechanisms to provide it are a separate ADR.
