# ADR 0045: Kernel-Sandboxed Process Spawns for Deno Children

- Status: Accepted
- Date: 2026-10-25
- Deciders: kernel architecture
- Relates to: ADR 0018 (permission store, invariant 22's sandbox seam), ADR 0020 (REPL host boundary), ADR 0042 (IDE capability bridge, the polyfill precedent)

## Context

AGENTS.md invariant 22 exempts the internal Deno children (REPL, plugin host) from process
sandboxing — they are privileged by Deno permissions. But a cell inside the REPL can ask the
OS to start arbitrary processes (`new Deno.Command(...)`), and those processes are exactly
what a sandbox exists for: model-influenced code reaching outside the kernel's own gates.
Today such a spawn rides the child's own `run` permission through the broker — allow or deny,
nothing more; an allowed process runs with the user's full authority.

The broker protocol cannot fix this itself: its replies are allow/deny only, so no broker
response can rewrite the spawn into a sandboxed launch. The transformation must happen where
the kernel controls the launch.

## Decision

1. **A `Deno.Command` polyfill routes cell spawns to the kernel.** The REPL worker replaces
   `Deno.Command` before any cell runs (the ADR 0042 fs-bridge pattern): armed by the
   `MAIEUTICS_PROCESS_SANDBOX` env var the kernel sets (`prefer`/`require`; absent or `off`
   keeps the native API and the broker's run check — the exact pre-ADR behavior). The polyfill
   supports `output()` semantics (captured stdout/stderr, exit code, declarative timeout);
   `spawn()`/`outputSync()` fail typed — interactive processes belong to the terminal tools,
   and a sync call cannot await the kernel. A per-call `tool_not_found` fallback keeps the
   native API when the kernel predates the tool.

2. **The kernel owns the launch through the `Maieutics.Processes` seam.** The new
   `process_spawn` script tool (session-scoped, like every script tool) calls the one
   registered `IProcessSandboxPolicy.Plan(policy, executable, arguments)`, which returns
   either a typed refusal or the launch vector — the requested argv verbatim, or a platform
   sandbox wrapper carrying it positionally after `--` (seatbelt `sandbox-exec` on macOS,
   bubblewrap `bwrap` on Linux). The tool owns the child's lifetime: bounded output capture
   (256 KiB per stream), a timeout that kills the process tree, cooperative cancellation, and
   strict UTF-8 decoding with typed failures.

3. **One policy shapes the sandbox.** The plan derives from the calling session's
   `EffectivePolicy` — the same snapshot the broker and the renderer consume: the `run` rules
   gate the executable **deny-by-default, the broker's posture** (decision 2a below); the
   read/write rules become the sandbox's filesystem bindings, with every read and write
   denial rendered as an explicit SBPL/bwrap deny rule (deny-wins, invariant 20); the `net`
   rules decide the network switch (fail-closed: only an unqualified AllowAll turns it on —
   any denial at all keeps the network off, since host allowlists are not expressible in a
   launch profile); `ProcessEnvironment.Capture` builds the environment. The spawn surface's
   baseline matches the model-tool posture (the workspace root is readable and writable) so a
   sandboxed child can resolve its own working directory and a build tool can write its tree;
   configuration denials subtract as everywhere else.

    **2a. The run gate is deny-by-default, not the launch gate's unrestricted default.** A
    cell-chosen executable is model-controlled code — exactly what the broker's deny-by-default
    posture exists for. The first cut reused `RunGrant` (whose unrestricted default was built
    for user-configured terminal/MCP executables), which would have made the sanctioned path
    weaker than the declared escape hatch; the incremental review caught it. With no `run`
    rules configured, cell spawns are refused exactly as before, and granting `Run` allows
    admits executables into the sandbox.

4. **Modes: `Off` (the tool is not registered and the polyfill is not armed — zero behavior
   change), `Prefer` (default; wrap when a launcher exists, launch unwrapped behind the same
   run gate and environment allowlist when it does not), `Require` (refuse spawns the platform
   cannot sandbox).** The mode is startup-fixed configuration (`Maieutics:ProcessSandbox:Mode`).

5. **Seatbelt speaks realpath; the broker stays verbatim.** Every granted path is resolved
   with `realpath(3)` before it enters the seatbelt profile, with metadata grants on each
   ancestor: the OS path walk resolves symlinks (on macOS `/var` is `/private/var`), and a
   grant phrased through a symlink never matches the resolved path — a sandboxed `/bin/sh`
   could not even `getcwd`. This is deliberately the opposite of the broker matcher's verbatim
   stance (established by the recorded broker experiment in the ADR 0018 follow-up work): the
   broker judges the request as written; the sandbox confines the file as resolved.
   **5a. The steering guard:** a policy-derived grant whose realpath escapes the spelling it
   was granted by is dropped — a symlink planted on a granted path (which a cell with the
   matching write grant can create) cannot steer the grant to its target. Denials resolve
   without the guard: widening a deny is the safe direction. Kernel-owned paths (the system
   basics, the per-spawn temporary directory) resolve without the guard. Process operations
   are exec and fork only — no `process-info*` (other processes' metadata stays private) —
   and there is no mach-lookup grant at all (verified on macOS 26: shell, exec, and pipe I/O
   run without it; the original abort that seemed to need it was the missing ancestor
   metadata grants).

## Linux implementation follow-up (2026-11-02)

The bubblewrap adapter is exercised by real-process conformance cases shared with macOS.
It uses user, pid, IPC, and UTS namespaces, drops capabilities, disables nested user namespaces,
and ties the sandbox to its owning process. IP networking is unshared unless the classifier
grants it. Existing system paths include the dynamic loader and library directories; trusted
symlink spellings are recreated and grants bind their resolved paths, with the same steering
guard as seatbelt. Parent mounts precede writable child mounts, and the synthetic root stays
read-only without hiding workspace or per-launch temporary mounts.

Denials are applied after all grants and synthetic mounts, including when a denied ancestor
contains a granted workspace or private-temp mount. A root denial refuses launch.
Directories become unreadable, read-only empty mounts;
files receive a read-only mode-000 placeholder whose launch-private source directory is hidden
by another unreadable mount. A child cannot chmod that source to unlock a denied file. The
tool owns the placeholder together with its temporary tree, including startup failures.
Policy-admitted temporary-directory environment names point at the owned tree on both platforms.

The reference interface is bubblewrap 0.9's [documented mount and namespace options](https://github.com/containers/bubblewrap/blob/v0.9.0/bwrap.xml).
Linux CI installs the package, probes user/network namespaces, and, when required by Ubuntu's
AppArmor mediation, grants userns to the bwrap binary rather than disabling the host-wide rule.

## Known limits (declared, not accidental)

- **Escape path:** the polyfill is a JS-level wrapper; a cell that reaches the original
  `Deno.Command` bypasses it and hits the broker's `run` check — which denies by default
  (the baseline grants no `run`). The polyfill is the convenient path, the broker is the
  boundary — and since decision 2a the sanctioned path shares the same deny-by-default run
  posture, so the two cannot diverge.
- **IP networking is coarse and fail-closed:** only an unqualified net AllowAll turns the network
  on; host allowlists and host-specific denials are not expressible, so any denial keeps the
  IP networking entirely off.
- **Output is text and bounded:** stdout/stderr decode strictly as UTF-8 (binary output fails
  typed — redirect it to a file); the capture keeps the first 256 KiB per stream and drains
  the rest without killing the child. stdin is UTF-8 text, bounded.
- **The working directory is the workspace root**; a `cwd` option fails typed. `uid`/`gid`
  options fail typed (the child runs with the session's own identity).
- **Policy-derived grants are steering-guarded:** a symlink that redirects a grant outside
  its declared tree does not widen access. Kernel-selected workspace, system, and private-temp
  roots are trusted and can resolve ancestor aliases; the process keeps its physical working
  directory. Workspace validation separately rejects a final root directory that is itself
  a symlink.
- **Linux requires bubblewrap 0.9+ and working unprivileged user namespaces.** `Prefer`
  degrades only when the launcher is absent; `Require` refuses. An installed launcher's
  setup failure never retries unwrapped. Mount masks need existing targets: a missing denied
  entry below an exposed tree causes a typed refusal before launch, including stale `/dev`
  or `/proc` rules, without creating host mountpoints. Only allow paths existing at planning
  time are bound; nonexistent allows are omitted (a writable ancestor can still create them),
  whereas Seatbelt can grant future paths. Private synthetic `/dev` and `/proc` mounts replace
  explicit host grants under those trees; denials are still applied last. These choices can
  restrict Linux access beyond the same Seatbelt policy.
  A host-existing `/proc/<pid>` deny can pass planning but fail to mount in the new PID
  namespace's procfs. That is a nonzero bwrap process result with stderr diagnostics, not a
  pre-launch typed refusal; the requested command is not run or retried unwrapped, and the
  launch-owned temporary tree is still cleaned up. A real Linux conformance case covers it.
  Filesystem Unix sockets within granted trees remain reachable despite network
  unsharing; explicit path denials can hide them. Seatbelt can be stricter for these sockets.
- **A per-spawn temporary directory** replaces the shared system temp in the writable set
  (the shared temp stays read-only), so a sandboxed child cannot squat another process's
  temp paths.

## Consequences

- Every cell-spawned process becomes a kernel-owned general start: policy captured per spawn,
  environment allowlisted, sandbox derived from the same single source as every other
  enforcement point (AGENTS.md invariants 19-23).
- Terminal and MCP children are unaffected (they already launch through the kernel); internal
  Deno children remain exempt (invariant 22) — only the processes they *ask the kernel to
  start* are sandboxed.
- The `IProcessSandboxPolicy` seam is real now: a future enforcer (per-executable profiles,
  finer network) implements one method against the same snapshot.
