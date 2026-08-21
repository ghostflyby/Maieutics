# ADR 0018: Declarative Permission Store, Variable Interpolation, and the Internal Deno Execution Module

Status: Draft

Date: 2026-08-19

## Context

The kernel supervises three kinds of child process, each with its own ad-hoc permission handling:

- **Terminal sessions** (`Maieutics/Execution`) start arbitrary PTY children through
  `Ghostflyby.Pty`. They have an environment allowlist (`TerminalEnvironment`) but **no path, network, or exec
  policy**. The terminal tool is privileged code execution (ADR 0017) and today grants the model everything.
- **Deno REPL generations** (`Maieutics/DenoRepl`) build `--allow-*` flags by hand inside
  `DenoReplProcess.StartAsync` for the control channel, the materialized module graph, and `SystemRoot`.
- **Plugin hosts** (`Maieutics/Plugins`) map a per-plugin manifest permission set (`deno.json` `permissions`
  field) onto `--allow-*` flags and merge the union into one host process.

The three paths share three problems this ADR fixes:

1. **No single declarative source of truth.** Terminal, REPL, and plugin permissions are three separate,
   hand-maintained code paths with different value models (`TerminalEnvironment` string arrays,
   `DenoReplProcessOptions`, `PluginPermissionGrant(bool AllowAll, IReadOnlyList<string>)`).
2. **No layered composition.** The product needs app-wide defaults, project/workspace-level overrides, and
   per-session overrides; the effective permission is the overlay of all layers. Nothing today can express that.
3. **No variable interpolation.** Many real path grants are only expressible relative to things like
   `$HOME/.cache`, `$LOCALAPPDATA`, or the workspace root. Path grants today are literal strings.

The future direction is process sandboxing (seatbelt on macOS, bubblewrap/seccomp on Linux) applied to the same
effective permission set. The permission model must therefore be the intersection of what Deno can express and what
a process sandbox can enforce, and the DENO permission mechanism (the `PermissionPromptHandler` broker) must map
onto the same store so dynamic grants work without a fixed prompt policy.

## Decision

### 1. A product permission namespace, not a library

The permission store lives in the executable as a new product domain namespace `Maieutics.Permissions` (see the
namespace section below). It is not an SDK assembly: like `Control`, `DenoRepl`, and `Execution`, it is a logical
module of the composition root that owns one domain. `Maieutics.Agent` and the Jupyter libraries do not depend on
it; only the executable's process-launching modules consume it.

### 2. Layered permission model

Permissions are declared in a strict overlay order. The effective permission for one scope is computed by merging
the layers in order; later layers win. For every permission kind, each layer may contribute *positive grants*
(`AllowAll` or an allowlist) and *denials* (`DenyAll` or a blocklist). Denials always win over grants regardless of
layer order — this mirrors Deno's own `--deny-*` over `--allow-*` precedence and is the only safe overlay rule.

```text
scope hierarchy (most specific wins)
    session-level override        (one Agent session; volatile, not persisted)
    project/workspace-level       (a project directory profile; portable, committed)
    app-wide defaults             (machine/user level)
    built-in baseline             (the kernel's own control-channel requirements)
```

Layer resolution for a single `ProcessPolicy`:

1. Start from the built-in baseline (control channel, module graph, workspace root).
2. Overlay app-wide defaults.
3. Overlay the project/workspace profile.
4. Overlay the session override.

Each layer is optional. The result is one immutable `ProcessPolicy` snapshot carrying the composed grants and
denials plus the variable table used to expand them.

### 3. Permission kinds: the Deno × process-sandbox intersection

The store keeps fine granularity wherever a future process sandbox can even partially express it. Where a sandbox
cannot express a kind at all, the store still carries it but the executor enforces it by other means (environment
construction at spawn, Deno-level flags, or a Deno broker).

| Kind | Value shape | Sandbox intersection | Enforcement today |
|---|---|---|---|
| `read` | allow/deny path patterns | seatbelt file-read-*, bwrap bind/ro-bind | `--allow-read` / `--deny-read` |
| `write` | allow/deny path patterns | seatbelt file-write-*, bwrap bind (rw) | `--allow-write` / `--deny-write` |
| `net` | allow/deny host:port (host, port, or both; `*` port = any) | seatbelt network filters (port/IP only; **no hostname matching**) | `--allow-net` / `--deny-net` |
| `env` | allow/deny variable names | **not expressible** in-process in either sandbox | env construction at spawn + `--allow-env` / `--deny-env` |
| `run` | allow/deny executable names/paths | seatbelt `process-exec` filters; bwrap by hiding the filesystem | `--allow-run` / `--deny-run` (and PTY start policy for terminal) |
| `ffi` | allow/deny library paths | seatbelt file-read on library paths; bwrap by hiding the filesystem | `--allow-ffi` / `--deny-ffi` |
| `sys` | allow/deny Deno sys API names (`hostname`, `osRelease`, `networkInterfaces`, `uid`, `loadavg`, ...) | seatbelt sysctl-read/write is a partial overlap; mostly **Deno-only** | `--allow-sys` / `--deny-sys` |
| `import` | allow/deny URL prefixes | **Deno-only** (module resolution) | `--allow-import` / `--deny-import` |

Fine granularity is retained for net ports and host allowlists. Hostname allowlists (for example
`api.example.com`) are kept in the store, but the design does not claim a process sandbox can enforce them: Deno
enforces the hostname today, and a future sandbox path resolves them to an IP/port policy or enforces them through
a DNS proxy. This is a documented limitation, not a reason to drop the granularity.

### 3a. Process-sandbox mapping constraints (researched, not implemented this phase)

Research into macOS seatbelt and Linux bubblewrap produced the following constraints that shape the store and the
future enforcer. The store must therefore keep **deny rules expressible alongside allow rules** — a sandbox
enforcer is not simply "render the allowlist".

- **seatbelt path filters are canonicalized.** Rules match `realpath`-style paths: `/etc` → `/private/etc`,
  `/tmp` → `/private/tmp`. A permission store must canonicalize paths (and preserve the original for the Deno
  renderer, which matches literally). Verified locally on macOS 26.
- **seatbelt deny-default is unusable on modern macOS.** A deny-default profile with path-scoped `file-read-data`
  aborts every binary in dyld4 before `main` (verified). The practical macOS shape is **allow-default plus deny
  rules on specific subpaths**. This is the opposite direction from Deno's default-deny model, which is another
  reason the store models grants and denials symmetrically.
- **seatbelt network filters are port/IP-only.** Outbound `(remote ip "*:443")` works at connect time (verified);
  the host field accepts only `*` or `localhost`, so **domain allowlists are not expressible**; the grammar is
  space-separated and version-dependent (`remote.port`, `net.tcp`, `remote.numeric.ip` exist in the dyld string
  table but are rejected by this release's compiler). IPv6 filtering is effectively unavailable.
- **bwrap has no network filtering at all.** `--unshare-net` is all-or-nothing; per-host/per-port filtering
  requires seccomp (fragile for ports) or a userspace proxy. Port-granular net grants are therefore
  **Deno-only on Linux** in the near term.
- **env is not filterable in-process by either sandbox.** Both seatbelt and bwrap (verified/documented) only
  control the environment the parent passes at spawn. Env grants must be enforced by the parent building the
  child environment — exactly what the store already does for `ProcessEnvironment`.
- **sys is unreliable in both.** seatbelt `sysctl-read` exists but does not reliably block `hostname` (verified);
  bwrap needs seccomp or mount tricks. Sys grants remain Deno-only enforcement.
- **dlopen/FFI is controllable via the file view in both**, with one hole: on macOS, dylibs already in the dyld
  shared cache load regardless of path rules (verified with `libz`). FFI grants remain Deno-enforced; the sandbox
  only narrows the file view.
- **`run`/`ffi` escape a process sandbox's Deno meaning.** Deno documents that `--allow-run` and `--allow-ffi`
  "bypass the sandbox": a subprocess runs with its own permissions and FFI native code runs with the process's
  full OS rights. OS-level path rules reduce what a child or FFI library can *read*, but they do not create
  Deno-level boundaries — the parent policy must still be enforced at the Deno/launch layer.
- **Operational risks:** `sandbox-exec`/`sandbox_init` are deprecated by Apple (may be removed; the private
  `libsystem_sandbox` API used by Chromium is the real surface); unprivileged bwrap needs `--unshare-user`, which
  Ubuntu 24.04+ restricts by default via AppArmor (`kernel.apparmor_restrict_unprivileged_userns=1`) — the
  enforcer must detect and fail or fall back at runtime.

### 4. Variable interpolation with a single source of truth

Path patterns (and env-var names) support variable interpolation:

```text
${env.HOME}          environment variables (user/process environment)
${var.workspace}     internal variables (single source, owned by the permission module)
${var.dataDir}       internal variables
${var.pluginsDir}    internal variables
```

- **Environment variables** (`${env.X}`) are resolved from the kernel process environment at expansion time.
  Because the permission store already receives the allowlisted environment it hands to children, `env` variables
  are consistent with what a child can observe: an `env` grant can never be wider than the allowlist the child
  actually sees.
- **Internal variables** are a string-keyed KV table with a single source of truth. The initial set is
  `workspace` (the live workspace root), `dataDir` (the platform application-data directory that owns
  `maieutics.json`), and `pluginsDir` (the plugin root under the workspace). The `var.*` namespace is reserved for
  Maieutics-internal paths; it is intentionally separate from `env.*` so a path grant can never depend on a
  user-set environment variable expanding differently on another machine than it did in the config.
- Unknown variables fail expansion at policy build time with a typed error, so a typo cannot silently turn into a
  narrower (or wider) grant.
- Interpolation applies to `read`, `write`, and `ffi` path patterns. `run` and `env` names do not interpolate
  today. `net` and `sys` values are not paths.

### 5. Single source of truth: `VariableTable` and `EffectivePolicy`

The variable table is owned by the permission module and derived from the live `Workspace` and the resolved
configuration paths. Nothing else computes "the workspace path" for permission purposes; `WorkspaceSnapshot`
remains authoritative for workspace semantics (including the no-symlink rule), and the permission module reads it
through a narrow `IPermissionVariableSource` interface so `Workspace` does not gain a dependency on permissions.

The composed result of layers plus variable expansion is one immutable `EffectivePolicy` record. It is computed
once per launch (or per permission snapshot) and cached by scope key; the process-launch modules consume only
`EffectivePolicy`.

### 6. Rendering an `EffectivePolicy` for Deno

`DenoPermissionRenderer` converts an `EffectivePolicy` to the exact `--allow-*` / `--deny-*` argument set Deno
2.x accepts (verified locally against Deno 2.9.5):

- read/write: comma-joined paths; a path is emitted as-is (an exact file or a directory prefix both work).
- net: `host:port`, bare host, or `unix:<socket>` entries, comma-joined.
- env: comma-joined names.
- run/ffi/sys/import: comma-joined names or paths.
- `AllowAll` emits the unsuffixed flag; `DenyAll` emits the unsuffixed `--deny-*` flag.
- The renderer performs no path canonicalization: Deno matches on the literal requested path, so the grants are
  the same strings the child will request (mirrors the existing esbuild-wasm comment in `DenoReplProcess`).

### 7. Terminal and MCP process starts go through the permission module

- `TerminalRegistry`/`TerminalSession` acquire an `EffectivePolicy` for the owner session and render it into (a)
  the child environment (env allowlist from the policy) and (b) an optional exec allowlist: when the policy
  restricts `run`, the terminal factory rejects an executable that is not in the allowlist instead of starting a
  PTY with it. **The `run` default is unrestricted** (decision, 2026-08-19): the built-in baseline grants `run`
  fully, so existing terminal behavior is byte-for-byte unchanged; the exec check engages only when a layer
  restricts or denies `run`.
- `McpServerGeneration` stdio transports acquire the same policy and render it for the MCP child environment.
- The policy is captured per Agent session at session start and does not change mid-session (mirrors the
  run-profile lease rule: permissions never change inside an active turn).

### 8. The internal Deno execution module

`DenoReplProcess` and `PluginHostProcess` share ~60 lines of duplicated process plumbing (drain loops,
`Kill(true)` shutdown, exit observation). The user's requirement — "extract the internal deno process execution
module, used by the current REPL and the future plugin host" — is implemented as a new module
`Maieutics.DenoExecution` that owns:

- **`DenoRunProcess`**: the generic supervised `deno run` child (launch, drain, completion observation, graceful
  stop with `Kill(true)` escalation). `DenoReplProcess` and `PluginHostProcess` become thin adapters over it.
- **`DenoPermissionArguments`**: rendered `--allow-*` / `--deny-*` flags produced from an `EffectivePolicy` plus
  the control-channel fixed grants.
- **`DenoPermissionBroker`**: the .NET side of the Deno permission broker (section 9). Used by the REPL
  child; the plugin host does not use it (resolved decision 8).
- **`InternalDenoProcessKind`**: the "internal" marker. REPL and plugin-host children are *internal* Deno
  processes: they are not sandbox targets, because their Deno-side permissions are the full capability surface and
  their privileges come from the Deno permission system itself.

**Process sandboxes do not apply to internal Deno children.** A future seatbelt/bwrap layer applies to the
`terminal_*` PTY children and to MCP server children — the *general* process starts — while the REPL and plugin
host keep their current model (privileged code execution, bounded by Deno permissions). This is a deliberate
boundary: an internal Deno process needs the module graph, the control channel, the SDK, and (on Windows) FFI for
the pipe bootstrap, and those are expressed as Deno grants, not as sandbox mounts.

### 9. Deno permission broker: runtime policy without a prompt

The REPL child resolves every permission check through a *broker* so that grants can be enforced
at runtime. The plugin host does not use the broker (resolved decision 8): it launches with full
Deno permissions as trusted orchestration code and isolates each plugin worker via the worker's own
`deno.permissions` options. Deno 2.5+ ships the mechanism natively as the **`DENO_PERMISSION_BROKER_PATH`** broker
(unstable, `runtime/permissions/broker.rs`, versioned JSON-lines protocol over a unix socket or Windows
named pipe — the env var carries the socket path on unix and the full named-pipe path `\\.\pipe\<name>` on
Windows; a bare pipe name fails to connect, verified in CI):

```text
request  {"v":1,"pid":...,"id":...,"datetime":...,"permission":...,"value":...}   child -> broker
response {"id":...,"result":"allow"|"deny","reason":...}                          broker -> child
```

The broker is consulted on every permission check; a `deny` response produces a `NotCapable` error whose
message is the broker-supplied `reason` (verified locally against Deno 2.9.5 with a python unix-socket
broker: read requests were allowed, env requests were denied with the custom reason). Because the broker
round-trip is synchronous per check, the broker path must stay fast (in-process policy lookup, no user
prompting on the hot path).

**Broker architecture.** `Maieutics.DenoExecution.DenoPermissionBroker` is the .NET side of the official
broker protocol. Each internal Deno child gets the broker address through `DENO_PERMISSION_BROKER_PATH`; the
broker endpoint authenticates exactly like the control channel (peer process id on unix, credential on
Windows) and resolves each request against the child's *effective policy*:

- exact grant match → allow; exact deny match → deny with the configured reason;
- otherwise deny by default (`--no-prompt` is always passed; unsolicited requests never escalate);
- an interactive user prompt is a possible future extension of the deny-by-default path, never a silent grant.

**Launch-time flags plus broker — revised: the broker is the permission source.** Setting
`DENO_PERMISSION_BROKER_PATH` makes the broker the **single authority** for the child's permission
checks: when a broker is configured, the child consults it for every permission access, and local
`--allow-*`/`--deny-*` flags do **not** short-circuit broker requests (verified against Deno 2.9.5
with a unix-socket broker: with the broker env var set and no permission flags at all, every read
and env access produced a broker request and the broker's deny reasons surfaced as `NotCapable`).
The broker is therefore a **replacement** for the flag baseline, not an overlay on it. The child
still launches with `--no-prompt` so unsolicited requests never escalate to an interactive prompt,
but the grants the flags used to express are now resolved by the broker against the same
`EffectivePolicy` snapshot the flags would have rendered. Fixed control-channel and module-graph
grants become the built-in baseline layer of that policy (section 2), which is why Phase 2 (policy
routing) and Phase 4 (broker) are merged: the broker is the only enforcement point and the policy
is its only input.

**Broker readiness and registration (architecture invariant, 2026-08-19; simplified 2026-08-20).** Two
invariants make the broker safe to depend on at spawn time: (1) the broker's listener is bound **before**
any child can be spawned — the factory binds and starts the accept loop synchronously before returning, so
`Address` is unconditionally safe (the earlier `CreateAsync`/ready-signal design was removed: bind/listen
are synchronous, so an asynchronous factory and `AsyncLazy` added nothing); (2) a permission request from a
child whose policy is not yet registered **waits** (signal-driven, bounded) for the registration instead of
being denied by default — the owner registers the policy immediately after `Process.Start` (the pid is only
known then), and the broker's per-process registration slot (created on demand for any unknown pid, so no
pre-registration step is needed) makes the child's first request block until the policy lands, denying by
default only if the registration never arrives. This closes the spawn-to-register window that previously
let a REPL's first `jsr.io` import be denied on slow CI runners.

**Alternative considered and rejected:** a custom prompter over the existing control channel. Deno's
`PermissionPrompter` trait (`runtime/permissions/prompter.rs`) is synchronous, not exposed as a stable public
API for `deno run` children, and `deno jupyter` itself no longer uses a permission prompter (it runs
allow-all). The official broker protocol is versioned, already supports Windows named pipes, and needs no
injected module; the materialized client only needs the broker's socket path. The plugin SDK stays
self-contained and does not import `shared/` (deno/AGENTS.md), which the prompter-injection approach would
have broken.

### 9a. Broker and the Windows FFI bootstrap

The Windows named-pipe credential bootstrap (`maieutics-repl-client/windows_bootstrap.ts`) uses `Deno.dlopen`
against `kernel32.dll` before any control channel exists. The broker is not reachable at that moment, so the
bootstrap still needs a launch-time ffi grant (section 10). After the pipe bootstrap completes and the child
connects the control channel, the broker takes over: subsequent ffi requests (any later `Deno.dlopen`) go
through the broker, which denies by default unless the effective policy grants the library path. This closes
the post-bootstrap ffi window without a custom prompter.

### 10. Windows FFI bootstrap: one-time ffi grant, then broker-gated

The current kernel passes the unsuffixed `--allow-ffi` (the code comment records that on Windows a
path-qualified ffi grant still rejects `dlopen`). The design:

1. **Launch-time ffi grant, then broker-gate — baseline tightened** (decision, 2026-08-19). Launch with the
   path-qualified grant `--allow-ffi=<systemRoot>\System32\kernel32.dll` as the baseline; after the child
   completes the pipe bootstrap, the broker is live and answers every subsequent ffi request from the policy —
   default deny. Fallback: if the Windows verification task (below) shows the current Deno still rejects
   path-qualified ffi grants on Windows, keep the unsuffixed `--allow-ffi` for the bootstrap only and rely on
   the broker to deny post-bootstrap `dlopen`; the effective ffi window is identical either way.
2. **Windows verification task — resolved by CI, 2026-08-20.** The original plan was to run an ffi probe
   matrix on a Windows host and pick the exact baseline flag. The Windows CI runner answered it: the
   path-qualified `--allow-ffi=<systemRoot>\System32\kernel32.dll` grant lets the bootstrap's `Deno.dlopen`
   succeed, but the bootstrap also calls `Deno.UnsafePointer.of` (for the pipe path bytes), which requires ffi
   access and produces an **empty-value ffi request** that a path-qualified grant cannot match. The baseline
   therefore uses `AllowAll` for ffi (the unsuffixed form) for the bootstrap window, and the broker gates every
   post-bootstrap `dlopen` by default — the "unsuffixed fallback" in decision 1 is what Windows actually
   requires, so the effective ffi window is exactly the bootstrap and nothing more. Re-verify if the
   bootstrap's `UnsafePointer` usage changes.

### 11. Namespace partition (domain-driven)

The executable currently uses `Maieutics.Execution` as a catch-all for terminal + workspace + key encoding. The
new partition groups files by semantic domain and call-tree locality:

| Current namespace | New namespace | Files | Domain |
|---|---|---|---|
| `Maieutics.Execution` | `Maieutics.Execution` | `Workspace.cs`, `WorkspaceFunctions.cs` | workspace URI resolution and read/search tools |
| `Maieutics.Execution` | `Maieutics.Terminal` | `TerminalFunctions.cs`, `TerminalKeyEncoding.cs`, `TerminalModels.cs`, `TerminalProcess.cs`, `TerminalRegistry.cs`, `TerminalSession.cs` | PTY sessions, VT screen, terminal tools |
| — (new) | `Maieutics.Permissions` | `PermissionModel.cs`, `PermissionLayers.cs`, `VariableTable.cs`, `EffectivePolicy.cs`, `DenoPermissionRenderer.cs` | declarative permission store, layers, variables, policy rendering |
| — (new) | `Maieutics.Processes` | `ProcessEnvironment.cs` (env allowlist), `ProcessSandboxPolicy.cs` (future sandbox adapter seam), `ProcessLaunchRequest.cs` | general process start policy (terminal + MCP) |
| — (new) | `Maieutics.DenoExecution` | `DenoRunProcess.cs`, `DenoPermissionArguments.cs`, `DenoPermissionBroker.cs`, `InternalDenoProcessKind.cs` | supervised internal `deno run` children, broker |
| `Maieutics.DenoRepl` | `Maieutics.DenoRepl` | unchanged (sessions, registry, eval protocol, presentation) | REPL lifecycle above the process layer |
| `Maieutics.Plugins` | `Maieutics.Plugins` | unchanged | plugin manifest, host manager, MCP coordinator |
| `Maieutics.Control` | `Maieutics.Control` | unchanged | control channel, credentials, session registry |
| `Maieutics.Configuration` | `Maieutics.Configuration` | unchanged | configuration binding, reload, profiles |

Rules:

- **A namespace is a semantic domain, not a folder mirror.** Every file moves to the namespace whose call tree it
  participates in. `Maieutics.DenoRepl` keeps its session/eval files but loses its `Process` plumbing to
  `Maieutics.DenoExecution`. `Maieutics.Execution` keeps only workspace files; terminal files move.
- **Dependency direction:** `Permissions` depends on `Agent` (for `AgentSessionId`), `Execution` (for the
  `IPermissionVariableSource` seam over `Workspace`), and nothing else. `Processes` depends on `Permissions`.
  `DenoExecution` depends on `Permissions` and `Control` (channel authentication). `Terminal` depends on
  `Permissions` + `Processes` + `Agent`. `DenoRepl` depends on `DenoExecution`. `Plugins` depends on
  `DenoExecution` + `Control`. No new project references are created — these are namespaces inside the executable.
- **Tests move with the domain.** `TerminalInputTests`, `TerminalRegistryTests`, `TerminalSessionTests` stay
  terminal-domain tests; new `PermissionLayerTests`, `VariableInterpolationTests`,
  `DenoPermissionRendererTests` live in `Maieutics.Jupyter.Tests` following the existing product-test convention.

### 12. Out of scope this phase

- No process sandbox implementation (seatbelt/bwrap) is written. `ProcessSandboxPolicy` is only a seam and a
  future `IPolicyEnforcer` interface.
- No user-facing interactive prompt flow is built. The broker denies by policy; prompting is future work.
- The `isolation=process` plugin mode (rejected at scan today) stays rejected until the broker and module are in
  place; the module extraction is a precondition for it, not a promise to ship it.
- The interactive prompt flow and the process-sandbox implementation stay out of scope. The layer *plumbing*
  (built-in baseline + app-wide defaults + workspace profile + session override) is in scope, and the workspace
  `permissions.json` schema is part of the plan (Phase 5), aligned with Deno's config format (decision 3,
  2026-08-19). No sandbox enforcer is written in any phase of this plan.

## Consequences

- `DenoReplProcess` and `PluginHostProcess` shrink to adapters over `DenoRunProcess`; the drain/stop/observe
  loops exist once.
- The REPL child gets one enforcement point: the official `DENO_PERMISSION_BROKER_PATH` broker
  resolves every permission check against the child's `EffectivePolicy`. The broker is the single authority
  (setting the env var replaces the flag baseline), is versioned (`v:1`), supports Windows named pipes, and
  its deny reasons surface as `NotCapable` messages (verified). `--no-prompt` stays so unsolicited requests
  deny by default. The plugin host does not use the broker (resolved decision 8): it launches with full
  Deno permissions and isolates each plugin worker via the worker's own `deno.permissions` options.
- Terminal starts become policy-governed: env allowlist from the policy, exec allowlist enforced for restricted
  policies, no behavior change for the default policy.
- The REPL and plugin host keep "privileged internal Deno" semantics; sandboxing applies to general process
  starts (terminal, MCP), never to internal Deno children.
- One `EffectivePolicy` type is the single consumption point, so a future seatbelt/bwrap enforcer renders the
  same snapshot.
- Windows ffi grant narrows to the bootstrap window (launch-time `AllowAll`, then broker-gated); the Windows
  verification (2026-08-20) confirmed the unsuffixed form is required because the bootstrap uses
  `Deno.UnsafePointer`, and the broker denies every post-bootstrap `dlopen` by default.
- The permission model is Deno-shaped but not Deno-only: kinds that a sandbox cannot express (env, import) are
  explicit and enforced by their owning layer.

## Resolved decisions (2026-08-19; decision 1 updated 2026-08-20)

1. **Windows `--allow-ffi` baseline: AllowAll for the bootstrap window.** The original decision was the
   path-qualified `--allow-ffi=<systemRoot>\System32\kernel32.dll` with the unsuffixed form as fallback. The
   Windows CI verification (2026-08-20, section 10) showed the bootstrap's `Deno.UnsafePointer.of` produces an
   empty-value ffi request that a path-qualified grant cannot match, so the baseline uses ffi `AllowAll` for
   the bootstrap window; the broker gates every post-bootstrap `dlopen` by default. The effective ffi window
   is exactly the bootstrap and nothing more.
2. **Terminal `run`: default unrestricted.** The built-in baseline grants `run` fully; the PTY factory checks
   the executable against an allowlist only when a layer restricts `run`. Existing behavior is unchanged
   (section 7).
3. **Workspace profile format: align with Deno.** The `permissions.json` layer uses Deno's config-permissions
   object shape per kind (`{ "allow": [...], "deny": [...] }`), a named `default` set, and relative paths
   resolved against the file's directory — matching Deno's config semantics so the renderer and broker reuse
   the same shape. On Deno 2.9.5 the config `permissions` field requires explicit `-P <set>` opt-in and the
   store renders to CLI flags and/or the broker regardless (verified locally).

## Resolved decisions (2026-08-20)

4. **Cold-cache first-start timeout is a real risk.** The REPL's module graph (jsr.io packages) must be
   fetched on first run; an empty `DENO_DIR` can exceed the 30s `StartupTimeout` (reproduced locally).
   Addressed by the startup pre-warm (resolved decision 7).
5. **The broker is the only permission path; the no-broker flag fallback is removed.** The merged
   Phase 2+4 kept a no-broker fallback (`BuildFixedPermissionFlags`/`AddGrant`) for tests that construct
   the REPL factory and plugin manager directly. The fallback has now been deleted: the composition root
   always creates a broker, the REPL factory and plugin manager require one, and the tests construct a
   broker (AGENTS.md invariant 19 — no launch path builds its own grant list). The plugin host's
   per-plugin grants are merged into the registered policy on top of the built-in baseline.
7. **Cold-cache first-start pre-warm is implemented.** A `DenoModuleGraphWarmer` hosted service runs
   `deno cache` on the materialized module graph in the background at startup, so the first REPL session
   does not pay the network fetch inside its startup timeout (resolved decision 4). A failed warm never
   fails startup; the REPL falls back to the existing on-demand install path.
6. **Broker factory is synchronous; the registration API is a single `RegisterPolicy`.** Bind/listen are
   synchronous, so the async factory, `AsyncLazy`, the ready signal, and the two-phase
   `RegisterProcess`/`RegisterPolicy` API were all simplified away (section 9 readiness invariant).
7. **Windows env/path matching is case-insensitive; net/sys keep case-sensitive matching.** On Windows the
   resolver compares env names and paths case-insensitively (matching the platform environment and
   filesystem semantics); net hosts, sys API names, and import values remain case-sensitive.

## Resolved decisions (2026-08-21)

8. **The plugin host does not use the broker; it runs with full Deno permissions and isolates each
   plugin worker via its own `deno.permissions` options.** This supersedes decision 5 for the plugin
   host only (the REPL child keeps the broker). Verified on Deno 2.9.5: when a worker declares a kind
   the parent does not hold, `new Worker` fails with `NotCapable: Can't escalate parent thread
   permissions`; a kind the worker does not declare is denied inside the worker even when the parent
   holds it. So the host process is the permission ceiling and the worker options are the actual
   isolation boundary — no per-plugin grant union is computed at launch, and the host no longer
   registers a policy with the broker. `PluginHostProcess` launches with every `--allow-*` kind plus
   `--no-prompt`; `DenoRunProcess.Start` makes the broker/policy arguments optional.
9. **Plugin permission/config/source changes reload the worker in-process with the plugin's full
   replacement config.** `PluginHostManager` runs a debounced `FileSystemWatcher` over the plugins
   root; a change re-resolves the owning plugin's descriptor from disk and ships it over the new
   `plugin.reload` bus message as a complete `PluginHostConfigPlugin` (permissions, workers,
   dependencies). The host updates the worker's config and rebuilds the worker plus its transitive
   dependents — no host-process restart is needed for a permission change. Pure source edits reload
   with the same config so new module text is picked up.


## Implementation plan

Phased plan with explicit work dependencies. Every phase keeps the repository green: existing tests pass, and
no phase changes observable behavior unless the phase explicitly says so. Phases follow the repository change
workflow (smallest owning abstraction → integration at boundaries → focused tests) and the structured-concurrency
and testing skills.

### Dependency map

```text
Phase 0  Permissions core + Processes namespace        (foundation; nothing depends on it)
   |
   +---> Phase 1  DenoExecution extraction (DenoRunProcess + DenoPermissionArguments)
   |        |
   |        +---> Phase 2+4  policy routing + Deno permission broker (merged; broker is the
   |        |                single permission authority for internal Deno children)
   |        |
   |        +---> Phase 5  full layering pipeline + workspace permissions.json
   |
   +---> Phase 3  Terminal through policy (independent of Phase 1; needs Phase 0 only)
   |        |
   |        +---> Phase 6  MCP stdio policy (uses Phase 3's acquisition path)
   |
   +---> Phase 7  process-sandbox enforcement seam (future; not scheduled)
```

Parallel tracks: after Phase 0, Phase 1 (Deno) and Phase 3 (Terminal) are independent. Phase 2+4 (policy
routing plus the permission broker) needs Phase 1; Phase 5 needs Phase 2+4. Phase 6 needs Phase 3. The
Windows verification task runs in parallel from Phase 0 and gates the Windows ffi baseline in Phase 2+4.

### Phase 0 — Permissions core + Processes namespace

Goal: pure permission logic with zero behavior change.

- New files under `Maieutics/Permissions/`: `PermissionModel.cs` (kinds, per-kind allow+deny rules),
  `PermissionLayers.cs` (layer overlay, deny-wins, scope keys), `VariableTable.cs` +
  `IPermissionVariableSource.cs` (env/var namespaces, `${env.*}`/`${var.*}` expansion, unknown-variable error),
  `EffectivePolicy.cs` (immutable snapshot), `DenoPermissionRenderer.cs` (EffectivePolicy → `--allow-*`/
  `--deny-*` arg list), `PermissionJsonContext.cs` (source-gen JSON for the Phase 5 schema).
- New files under `Maieutics/Processes/`: `ProcessEnvironment.cs` (env allowlist derived from policy env
  grants; absorbs `TerminalEnvironment`), `ProcessSandboxPolicy.cs` (future enforcer seam, interface only).
- `Workspace` gains the narrow `IPermissionVariableSource` implementation (adapter lives in `Execution`; no
  dependency from `Workspace` to `Permissions`).
- Tests (`Maieutics.Jupyter.Tests`): `PermissionLayerTests`, `VariableInterpolationTests`,
  `DenoPermissionRendererTests`, `ProcessEnvironmentTests`.
- Gate: focused tests green; `dotnet build Maieutics.slnx --no-restore -warnaserror`; no production behavior
  change.

### Phase 1 — DenoExecution extraction

Goal: one supervised `deno run` child; remove the duplicated drain/stop/exit plumbing.

- New file `Maieutics/DenoExecution/DenoRunProcess.cs`: generic child (launch, stdout/stderr drain, completion
  observation, `Kill(true)` escalation ladder) extracted from `DenoReplProcess` and `PluginHostProcess`.
- New file `Maieutics/DenoExecution/DenoPermissionArguments.cs`: `Build(EffectivePolicy, fixedGrants)` → the
  exact `--allow-*`/`--deny-*` args (Phase 0 renderer is the pure function; this owns the REPL/plugin fixed
  grants such as control channel, module graph, SystemRoot).
- New file `Maieutics/DenoExecution/InternalDenoProcessKind.cs`: internal (REPL/plugin) vs general marker.
- Refactor: `DenoReplProcess` and `PluginHostProcess` become thin adapters (keep their REPL/plugin-specific
  concerns: esbuild resolution, module-graph install, config/env injection).
- Tests: existing `DenoReplSessionTests`/`DenoReplRegistryTests`/`PluginHostProcessTests` green unchanged; add
  `DenoRunProcessTests` (drain, stop, exit observation) against a trivial `deno eval` child.
- Gate: all Deno REPL and plugin host tests green; behavior identical.
- Depends on: Phase 0.

### Phase 2+4 — policy routing + Deno permission broker (merged)

Goal: the broker is the single permission authority for internal Deno children, resolving every
permission check against the child's `EffectivePolicy`; the hand-built `--allow-*` lists are replaced
by policy-rendered flags, which are identical to today's grants but now enforced by the broker.

- New file `Maieutics/DenoExecution/DenoPermissionBroker.cs`: v1 JSON-lines broker server — unix socket
  (peer-pid attributed like the control channel) or Windows named pipe; parses
  `{"v":1,"pid","id","datetime","permission","value"}` and answers `{"id","result","reason"}`; resolves
  each request against the child's `EffectivePolicy` (exact allow → allow; exact deny → deny with policy
  reason; otherwise deny by default; never prompts). Malformed/unmatched requests follow the control
  channel's failure policy (typed errors, no loop crash).
- Composition root builds the built-in baseline layer (control channel address, module graph dirs,
  workspace root, SystemRoot/TMPDIR on Windows) as a `PermissionLayer`.
- `LocalDenoReplSessionFactory.StartAsync` acquires `EffectivePolicy` for the session; the REPL child
  launches with the broker address in `DENO_PERMISSION_BROKER_PATH` and `--no-prompt` (the broker is the
  enforcement point, so the launch flags are the minimal baseline — the broker answers everything else).
- The plugin host does not use the broker (resolved decision 8): it launches with every `--allow-*` kind
  plus `--no-prompt`, and each plugin worker is isolated by its own `deno.permissions` options. No
  per-plugin grant union is computed (`BuildProcessGrants` is deleted).
- Windows ffi baseline: **tightened** (decision 1) — `--allow-ffi=<systemRoot>\System32\kernel32.dll` at
  launch for the pipe bootstrap, then the broker gates post-bootstrap `dlopen`; unsuffixed fallback only
  if the Windows verification task disproves path grants.
- Windows verification task (parallel, earliest): ffi probe matrix on a Windows host against the pinned
  Deno; output fixes the exact baseline flag.
- Tests: `DenoPermissionBrokerTests` — protocol round-trip against a real `deno run` child (allow, deny
  with custom reason, deny-by-default), policy-resolution unit tests; Windows ffi bootstrap test on the
  Windows runner; renderer round-trip asserts the exact current arg sets (the launch commands documented
  in the `maieutics-deno-repl`/`maieutics-plugin-host` READMEs are the fixture).
- Gate: broker integration green on unix; Windows bootstrap test green on the Windows runner; full
  `dotnet test Maieutics.slnx` green; NativeAOT publish check.
- Depends on: Phase 1.

### Phase 3 — Terminal through policy

Goal: terminal process starts flow through the permission module.

- `TerminalRegistry` captures `EffectivePolicy` per `AgentSessionId` at session reserve; `TerminalSession`
  carries it.
- `ProcessEnvironment.Capture(policy)` replaces `TerminalEnvironment.Capture()` (env grants from the policy;
  default policy yields the current allowlist).
- Exec allowlist: `LocalTerminalProcessFactory.Start` checks the executable against the policy's `run`
  allowlist when restricted; **default is unrestricted** (decision 2) so nothing changes by default. A denied
  executable fails with `terminal_start_failed` (or a dedicated policy code) before any PTY is created.
- Tests: `TerminalSessionTests` restricted-policy cases (exec denied, env allowlist), default-policy cases
  unchanged.
- Gate: terminal integration tests green; default behavior byte-for-byte unchanged.
- Depends on: Phase 0. (Independent of Phase 1/2 — can run in parallel.)

### Phase 4 — Deno permission broker + Windows ffi gating

> **Merged into Phase 2+4** (2026-08-19): the broker is the single permission authority, so policy
> routing and the broker are implemented together. This section is retained for the broker-specific
> details; the phase gate is the merged Phase 2+4 gate.

- New file `Maieutics/DenoExecution/DenoPermissionBroker.cs`: v1 JSON-lines broker server — unix socket
  (shared, peer-pid attributed like the control channel) or Windows named pipe; parses
  `{"v":1,"pid","id","datetime","permission","value"}`, answers `{"id","result","reason"}`; resolves each
  request against the child's `EffectivePolicy` (exact allow → allow; exact deny → deny with policy reason;
  otherwise deny by default; never prompts). Malformed/unmatched requests follow the control channel's failure
  policy (typed errors, no loop crash).
- `DenoRunProcess`/REPL launch sets `DENO_PERMISSION_BROKER_PATH`; `--no-prompt` stays.
- Windows ffi baseline: **tightened** (decision 1) — `--allow-ffi=<systemRoot>\System32\kernel32.dll`, broker
  gates post-bootstrap `dlopen`; unsuffixed fallback only if the Windows verification task disproves path
  grants.
- Windows verification task (parallel, earliest): ffi probe matrix on a Windows host against the pinned Deno;
  output fixes the exact baseline flag.
- Tests: `DenoPermissionBrokerTests` — protocol round-trip against a real `deno run` child (allow, deny with
  custom reason, deny-by-default), policy-resolution unit tests; Windows ffi bootstrap test on the Windows
  runner.
- Gate: broker integration green on unix; Windows bootstrap test green on the Windows runner; NativeAOT
  publish check (executable-affecting change).
- Depends on: Phase 1.

### Phase 5 — full layering pipeline + workspace permissions.json

Goal: the four layers compose from real configuration.

- App-wide defaults bound from `Maieutics:Permissions` (reloads with the existing configuration mechanism).
- Workspace profile `permissions.json` beside the active `maieutics.json` (mcp.json convention); **format
  aligned with Deno** (decision 3): per-kind `{"allow":[...],"deny":[...]}`, named sets with `default`, relative
  paths resolved against the file's directory, validated with the source-gen context.
- Session override registry (in-memory, like the model-profile override) wired into the store.
- Consumers switch from baseline+defaults to the full acquisition path.
- Tests: `PermissionLayerStoreTests` (overlay order, deny-wins, variable expansion), configuration
  load/validation/reload tests, integration (workspace profile grants a path → REPL reads it; denies →
  `NotCapable`).
- Gate: layer-composition integration green; reload keeps last-known-good on invalid files.
- Depends on: Phase 2+4.

### Phase 6 — MCP stdio policy (follow-up)

Goal: MCP server children get policy-derived environments.

- `McpServerGeneration` stdio transports build the child environment from the policy env grants; restricted
  `run` policies check the stdio command.
- Tests: `McpServerGenerationTests` env construction; existing MCP tests green.
- Depends on: Phase 3's acquisition path (or Phase 2+4's, if simpler).

### Phase 7 — process-sandbox enforcement seam (future)

Not scheduled in this plan. `ProcessSandboxPolicy` will render `EffectivePolicy` to seatbelt/bwrap under the
section 3a constraints (allow-default + deny rules on macOS, bind/ro-bind on Linux, env at spawn, no hostname
net). The store shape (symmetric allow+deny, canonicalized paths for sandbox rendering, literal paths for Deno)
is already fixed so this phase has no model changes.

### Cross-cutting gates

- Every phase: focused tests first, then the standard acceptance
  (`dotnet test Maieutics.slnx`, `dotnet build Maieutics.slnx --no-restore -warnaserror`, `git diff --check`).
- Phases that touch the executable's launch/process surface (2+4, 3, 5) run the supported-RID NativeAOT
  publish check.
- The AGENTS.md invariants 19–23 are enforced by construction: every consumer acquires one `EffectivePolicy`
  per scope, and the no-broker flag fallback (resolved decision 5) is removed so no hand-built grant lists
  survive the broker-only path.

## Verification appendix (local, Deno 2.9.5, macOS arm64)

- `--allow-ffi=/usr/lib/libSystem.B.dylib` (exact file) → `dlopen` succeeds.
- `--allow-ffi=/usr/lib` / `/usr/lib/` (directory, with and without trailing slash) → `dlopen` succeeds.
- `--allow-ffi=/tmp` (unrelated) or no grant → `dlopen` fails with `NotCapable: Requires ffi access to ...`.
- Revoke: after `Deno.permissions.revoke({name:"ffi", path})`, the already-bound handle's symbols still work,
  and a second `Deno.dlopen` of the same library fails. This is the exact pattern the one-time bootstrap uses.
- `--deny-read` overrides `--allow-read` for the same subtree; with `--allow-read` (all) + `--deny-read=<subtree>`,
  the subtree is denied and everything else remains readable.
- `--allow-net=localhost:8080` grants only that port (`localhost:8081` → `NotCapable`); `--allow-net=localhost`
  grants all ports of that host; `sub.example.com` is NOT covered by a bare `localhost`/`example.com` grant.
- `--allow-env=HOME` grants `HOME` and denies `PATH`; `Deno.permissions.query` returns `granted`/`prompt`.
- `--allow-sys=hostname` permits `Deno.hostname()`; sys descriptors accept per-API kinds
  (`hostname`, `osRelease`, `networkInterfaces`, `uid`, `loadavg`).
- Workers inherit the parent's permissions (a worker could read a path the parent was granted).
- `Deno.permissions.request` under `--no-prompt` returns the descriptor state without prompting, and the
  subsequent sensitive call still fails — i.e. request without a prompt handler never escalates.
- There is no `--allow-prompt` flag in `deno run --help`; the interactive prompt is a separate mode.
- A `deno.json` `permissions` field requires an explicit `-P <set>` selection; bare `--config` alone grants
  nothing on Deno 2.9.5 (queries return `prompt`, sensitive calls fail). With `-P default`, the object form
  (`{"read":{"allow":["/tmp"]}}`) is accepted (the earlier bare-array form fails parse); the field is still
  flagged experimental.
- The official **`DENO_PERMISSION_BROKER_PATH`** broker works: with a unix-socket broker, every permission
  access that is not locally settled produces a versioned request (`{"v":1,"pid":...,"id":...,"permission":...,"value":...}`),
  a `deny` response surfaces as `NotCapable` with the broker-supplied reason, and locally granted flags
  (`--allow-env=PATH`) short-circuit before the broker. The broker is the enforcement point for dynamic
  policy; the config-permissions field is not a substitute.

## References

- Deno permissions reference: https://docs.deno.com/runtime/reference/permissions/
- Deno permission broker (`DENO_PERMISSION_BROKER_PATH`, unstable since 2.5.3): `runtime/permissions/broker.rs`
  in denoland/deno; request/response schemas `cli/schemas/permission-broker-request.v1.json` and
  `permission-broker-response.v1.json`; implemented by `maybe_check_with_broker` on every permission check.
- Deno config-file permissions (`deno.json` `permissions` key with named sets, stable since 2.5.0, selected via
  `-P`): https://docs.deno.com/runtime/reference/deno_json/
- macOS seatbelt: `sandbox(7)` / `sandbox-exec(1)` / `sandbox_init(3)` man pages
  (https://keith.github.io/xcode-man-pages/); Chromium's seatbelt wrapper
  (https://chromium.googlesource.com/chromium/src/+/main/sandbox/mac/seatbelt.cc); Apple `<sandbox.h>`
  (deprecated).
- Linux bubblewrap: https://github.com/containers/bubblewrap (man page bwrap.xml, SECURITY.md); Ubuntu
  unprivileged-user-namespace restriction: https://ubuntu.com/blog/ubuntu-23-10-restricted-unprivileged-user-namespaces
- ADR 0003 (Deno Jupyter REPL output bridge), ADR 0004 (out-of-process Deno extensions), ADR 0011 (REPL tools
  lifecycle), ADR 0014 (REPL sideband IPC/HTTP control channel), ADR 0017 (terminal tool protocol)
