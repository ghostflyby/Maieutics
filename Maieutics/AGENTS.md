# Maieutics executable instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` for product composition semantics and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for host/process lifetime changes.
See `docs/architecture/decisions/0018-declarative-permission-store-and-deno-execution-module.md` for the
permission model, namespace partition, and the internal Deno execution module.

## Ownership

This project is the `maieutics` NativeAOT executable and composition root. It owns process startup and shutdown, Generic
Host and DI registration, external configuration selection, product provider factories, the frontend web API and
shared command surface, process-level logging, the declarative permission store, general process start policy, and the
internal Deno execution layer (supervised `deno run` children and the Deno permission broker).

Logical namespaces under this project are product modules, not independent SDK assemblies. A namespace is a semantic
domain whose files participate in one call tree; it is not a folder mirror. The partition is:

- `Maieutics.Execution` — workspace root and `workspace://local` read/search tools.
- `Maieutics.Terminal` — PTY sessions, headless VT screen, terminal key encoding, and `terminal_*` tools.
- `Maieutics.Permissions` — layered declarative permissions, variable interpolation, effective policy, Deno rendering.
- `Maieutics.Processes` — general process start policy (environment allowlist, future sandbox enforcement seam).
- `Maieutics.DenoExecution` — supervised internal `deno run` children and the Deno permission broker.
- `Maieutics.DenoRepl` — REPL sessions, eval protocol, presentation above `DenoExecution`.
- `Maieutics.Plugins` — plugin manifest, host manager, extension points, MCP coordination.
- `Maieutics.Control` — control channel, credentials, session registry, peer identity.
- `Maieutics.Configuration` — configuration binding, reload, catalogs, profile lifetimes.
- `Maieutics.Commands` — shared kernel control surface: command language, command execution, status, session management.
- `Maieutics.Frontend` — frontend web API: discovery, bearer auth, REST, per-session event streams (ADR 0023).

## Forbidden responsibilities

- Do not place reusable Jupyter protocol, client, or kernel-host logic here; the retained `Maieutics.Jupyter.*`
  libraries are standalone and have no executable consumer.
- Do not expose product provider or configuration implementation types as reusable public API.
- Do not move provider SDK types into `Maieutics.Agent`.
- The executable hosts the frontend web API on the shared Kestrel host; do not add another HTTP server.

## Process lifecycle

- Keep `Program` and constructors side-effect free; compose services in the host setup.
- The process starts the frontend web API when launched with `--frontend-discovery <path>`; the discovery file's
  appearance is the readiness signal, and it is deleted on shutdown so a stopped process is never rediscovered.
- Process-level connection information is captured at startup. Reloadable model and presentation settings apply only at
  operation boundaries.
- Never log full configuration, API keys, provider authorization, or the frontend bearer token.
- Keep the executable compatible with trimming and NativeAOT. Do not bypass new incompatibilities with broad warning
  suppression; provider and framework paths require publish and process smoke coverage.
- SIGINT and SIGTERM map to graceful host shutdown; run-level cancellation is a frontend-protocol operation
  (`POST /v1/agent/runs/{id}/cancel`), never a process signal.

## Permission and process rules

- Every child process start flows through the permission module: `TerminalRegistry` and the Deno REPL factory acquire
  an `EffectivePolicy` for their owning scope and render it before launch. No launch path builds its own grant list.
  The plugin host manager is the exception by design (ADR 0018 decision 8): the host launches with full Deno
  permissions as trusted orchestration code, and each plugin worker is narrowed by its own `deno.permissions` options.
- The effective policy is the overlay of the built-in baseline, app-wide defaults, the workspace profile, and the
  session override; denials win. Policies are captured once per owning scope and never change mid-operation.
- Path patterns use the single-source variable table (`${env.*}`, `${var.*}`); the `var.*` keys are owned by
  `Maieutics.Permissions` and derived from the live workspace and configuration paths through a narrow seam, never
  recomputed by consumers.
- The Deno REPL and plugin host are internal Deno children: they are privileged by Deno permissions and are not
  process-sandbox targets. The sandbox seam in `Maieutics.Processes` applies to general starts (terminal, MCP).
- On Windows, the named-pipe FFI bootstrap is a one-time grant: the child receives an ffi grant only long enough to
  bind `kernel32`, then the broker revokes it. Re-verify platform ffi path-grant behavior before narrowing the flag.
- The Deno permission broker answers `Deno.permissions.request` against the child's effective policy; `--no-prompt` is
  always passed so unsolicited requests deny by default.
- The broker must be ready before any child is spawned: its listener is bound by the time `CreateAsync` completes,
  and a request from a not-yet-registered child waits (bounded) for the policy registration instead of being denied
  by default (ADR 0018 §9 readiness invariant).

Read the narrower instructions before changing:

- `Configuration/AGENTS.md`
- `Providers/AGENTS.md`
- `Frontend/AGENTS.md`
- `Commands` follows the same domain rules; its command language and session manager are shared by every
  frontend adapter and must not grow protocol-specific behavior.
