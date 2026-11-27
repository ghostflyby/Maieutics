# ADR 0045: Kernel-Sandboxed Terminal and REPL Commands

- Status: Accepted
- Date: 2026-10-25
- Deciders: kernel architecture
- Relates to: ADR 0018 (permission store, invariant 22's sandbox seam), ADR 0020 (REPL host boundary), ADR 0042 (IDE capability bridge, the polyfill precedent)

## Context

AGENTS.md invariant 22 exempts internal Deno children from the process sandbox. The
kernel controls their Deno permissions through the broker. Model-controlled commands
from the REPL and the terminal need an OS-enforced boundary for filesystem and network
access. The broker can allow or deny a native Deno permission request, and the kernel
launch path can apply that boundary to a child process.

## Decision

1. **The REPL routes captured commands to the kernel.** With the
   `MAIEUTICS_PROCESS_SANDBOX` signal set to `prefer` or `require`, the REPL worker
   forwards `Deno.Command.output()` to the session's `process_spawn` script tool.
   `spawn()` and `outputSync()` fail because there is no synchronous kernel round trip.
   A missing script tool fails the command. In both the local and host-derived REPL,
   the native Deno shell and broker disallow subprocess creation in these modes,
   including when the kernel policy grants `run` for the wrapped path.

2. **Terminal PTYs and captured REPL processes share the kernel launch decision.**
   The terminal registry owns the PTY and its temporary directory for the session
   lifetime. The REPL tool owns the captured process, bounded UTF-8 capture
   (256 KiB per stream), its timeout, cancellation, and cleanup. Both launch the
   planned argv positionally via Seatbelt on macOS or bubblewrap on Linux.
   Bubblewrap retains its isolated session; commands that depend on opening a
   controlling terminal or on shell job control can behave differently there.

3. **The effective policy controls both paths.** An unconfigured `run` policy
   permits sandboxed commands without an executable grant. An explicit allowlist
   restricts the requested executable and explicit `run` denials take precedence.
   Direct kernel launches also apply explicit run denials to the resolved
   executable and physical target, including in `Off` mode for terminal launches.
   The workspace is readable and writable by default, subject to policy denials.
   The same policy supplies filesystem mounts and deny rules and an allowlisted
   environment. IP networking stays off unless `net.AllowAll` applies without
   denials. Generic tool approvals, including a configured require-approval rule,
   remain separate from the process decision.

4. **Modes are set at startup via `Maieutics:ProcessSandbox:Mode`.**
   - `Prefer` is the default. Sandbox eligible commands without asking. If there is
     no launcher, or the caller explicitly supplies `outsideSandbox: true`, ask
     the session owner for one-time approval before starting an eligible command.
     Approval is tied to that launch's resolved executable path, argv, and working directory. Explicit
     filesystem or network denials block the bypass. An execution failure never
     triggers an automatic unwrapped retry.
   - `Require` requires the sandbox and refuses execution outside it.
   - `Off` is the explicit legacy opt-out. Terminal processes use the environment
     and executable gates. The REPL leaves `Deno.Command` on native, broker-gated
     Deno permissions and the script tool is absent.

   Eligibility for unwrapped execution is limited to a one-shot `terminal_run`
   with a timeout, or `process_spawn` without stdin. Terminal input and paste
   are disabled for the resulting PTY handle. Interrupt closes it through the
   terminal's owned shutdown path and writes no PTY input. Persistent and default shells
   require a working sandbox in `Prefer` and `Require`. A caller without a
   live approval frontend cannot authorize an exception. Existing tool-level
   approvals and executable grants do not authorize these launches.

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
   include exec and fork. The default macOS profile also permits querying sysctl,
   reading the selected developer tools and required system runtime paths, and
   querying the local account-information service. PTY launches can issue ioctls
   against tty device nodes for shell job control.

6. **The read baseline is a separate startup setting.** `ProcessSandbox:ReadMode`
   defaults to `HostRead`. `Allowlist` limits filesystem reads to granted paths
   and required system paths. An effective read allow-all selects broad host reads.
   Both terminal and REPL commands apply the same built-in credential and runtime
   protections and explicit read denials to broad reads. A read denial also denies
   writes. A write denial preserves the read policy. Network access, writes, and
   one-time approval for execution outside the sandbox use their own policy.
   The resolved discovery path is a required protection in both read modes.

## Linux implementation follow-up (2026-11-02)

The bubblewrap adapter is exercised by real-process conformance cases shared with macOS.
It uses user, pid, IPC, and UTS namespaces, drops capabilities, disables nested user namespaces,
and ties the sandbox to its owning process. IP networking is unshared unless the classifier
grants it. Existing system paths include the dynamic loader and library directories; trusted
symlink spellings are recreated and grants bind their resolved paths, with the same steering
guard as seatbelt. Parent mounts precede writable child mounts, and the synthetic root stays
read-only without hiding workspace or per-launch temporary mounts.

Linux delivers bubblewrap's parent-death signal when the thread that forked it
exits. A sandboxed PTY publishes its startup result separately and keeps its
launch thread alive until the child is reaped or disposal completes. The
session joins that thread during cleanup, including after a late startup.

Denials are applied after all grants and synthetic mounts, including when a denied ancestor
contains a granted workspace or private-temp mount. A root denial refuses launch.
Directories become unreadable, read-only empty mounts;
files receive a read-only mode-000 placeholder whose launch-private source directory is hidden
by another unreadable mount. A child cannot chmod that source to unlock a denied file. The
tool owns the placeholder together with its temporary tree, including startup failures.
Policy-admitted temporary-directory environment names point at the owned tree on both platforms.

In broad-read mode bubblewrap first mounts the host filesystem read-only. For
denials under read-only host directories, it projects each affected parent into
a read-only mount containing the permitted existing entries. Omitting the denied
entry also hides later host creation or replacement at that path. Other denied
entries use the usual masks. Bubblewrap installs a syscall filter before
execution that blocks creating Unix sockets and using io_uring, while preserving
private socket pairs. macOS uses Seatbelt's Unix-socket rules. This protection
applies even when IP networking is allowed.

An absent optional built-in target under a writable mount is left out for that
launch. Explicit denials and required runtime paths retain the fail-closed
behavior. This avoids creating host mountpoints or taking away workspace writes
when the workspace is the user's home directory.

The reference interface is bubblewrap 0.9's [documented mount and namespace options](https://github.com/containers/bubblewrap/blob/v0.9.0/bwrap.xml).
Linux CI installs the package, probes user/network namespaces, and, when required by Ubuntu's
AppArmor mediation, grants userns to the bwrap binary rather than disabling the host-wide rule.

## Known limits (declared, not accidental)

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
  can ask for approval when the system launcher is absent. System paths include
  the standard Nix profiles. An explicitly configured absolute `BubblewrapPath`
  supports other installations and is trusted as part of the kernel configuration. `Require` refuses. Native
  Windows currently has no launcher. An installed launcher's setup failure never
  retries unwrapped. Outside the broad-read parent projection, mount masks need existing targets.
  A missing explicit denial or required protection in an exposed tree causes a typed refusal,
  including stale `/dev` or `/proc` rules. An absent optional built-in protection is omitted
  when its target is under a writable grant. Planning creates no host mountpoints.
  Only allow paths existing at planning
  time are bound; nonexistent allows are omitted (a writable ancestor can still create them),
  whereas Seatbelt can grant future paths. Private synthetic `/dev` and `/proc` mounts replace
  explicit host grants under those trees; denials are still applied last. These choices can
  restrict Linux access beyond the same Seatbelt policy.
  A host-existing `/proc/<pid>` deny can pass planning but fail to mount in the new PID
  namespace's procfs. That is a nonzero bwrap process result with stderr diagnostics, not a
  pre-launch typed refusal; the requested command is not run or retried unwrapped, and the
  launch-owned temporary tree is still cleaned up. A real Linux conformance case covers it.
  In allowlist mode, filesystem Unix sockets within granted trees can remain
  reachable despite network unsharing. Explicit path denials can hide them.
- **Required Linux protections in writable directories must remain present.** If a running
  kernel's discovery file or an explicitly selected configuration file is removed from a
  writable workspace, later launches fail closed until the path is restored. Restarting
  the kernel republishes the discovery file.
- **A per-spawn temporary directory** replaces the shared system temp in the writable set
  (the shared temp stays read-only), so a sandboxed child cannot squat another process's
  temp paths.

## Consequences

- The kernel owns terminal and REPL command launches and their environment allowances
  under AGENTS.md invariants 19–23. User-configured MCP server launches remain governed
  by their existing configuration and executable policy.
- The kernel scopes REPL process requests to their verified owning Agent session.
  Requests without a verified owner are refused.
- Internal Deno children remain exempt from OS-level process sandboxing. Their
  installed Deno permissions govern their own actions.
