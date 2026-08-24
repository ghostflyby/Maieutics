# ADR 0020: Deno REPL under the Extension Host — Process Ownership, Permissions, and Call Direction

Status: Draft

Date: 2026-08-24

## Context

The Deno REPL and the plugin extension host are today two unrelated child processes
of the Maieutics executable. The REPL connects to the kernel through the
process-verified eval WebSocket (`/v1/repl/eval/ws`, ADR 0014) and a comm WebSocket;
the plugin host connects through the control bus `/ws` and speaks the
`extension.*` message protocol (ADR 0016, plugin-host-redesign). Neither process
can reach the other, and neither can expose a capability to the other.

The goal is to move the REPL under the extension host: the host derives the REPL
actor through worker-actor (`@ghostflyby/worker-actor`), so the REPL and the
plugins share one orchestration tree and can call each other through actor
capabilities. The library already provides the needed cross-process primitives
(`spawnProcess`/`serveProcess`/`spawnNode`/`serveNode`, `openChannel`/`connectToken`,
remote references), verified against `jsr:@ghostflyby/worker-actor@0.4.0`
(research branch `research/repl-host-actor-migration`, `deno/research/`):
cross-process RPC, dedicated bidirectional channels, and bidirectional
remote-ref capability sharing all run.

Two design questions must be resolved before implementation:

1. **What permission governs the REPL?** The plugin host runs with full Deno
   permissions as trusted orchestration and narrows each plugin worker to its
   manifest `deno.permissions` (ADR 0018 decision 8). If the host derives the
   REPL, the host would be the spawner — but the REPL is a kernel capability
   carrier (its baseline is the product policy: module graph, esbuild-wasm,
   workspace, control channel), not a user-declared plugin. The permission
   source must not become the plugin manifest.
2. **May extensions call into the REPL?** The REPL is the only execution surface
   that runs arbitrary user code with the REPL's effective permissions. An
   extension that could invoke REPL execution would be code running inside the
   REPL process under the REPL policy — a privilege escalation over the
   extension's own narrowed grants, and one the permission broker cannot tell
   apart from the REPL's own scripts.

A further verified fact shapes the decision: the Deno permission broker
(`DENO_PERMISSION_BROKER_PATH`, `DenoPermissionBroker`) is the **single
authority** for a child's permission checks when the env var is present —
launch-time `--allow-*` flags do not short-circuit broker requests (verified in
the broker: "a child launched with `--allow-env=PATH` still produced a broker
request and the broker's deny won"). The broker resolves each request against
the `EffectivePolicy` registered for that child's process id. A static
permission shell passed at spawn is therefore not a security boundary for a
broker-bearing REPL.

## Decision

### 1. The extension host derives the REPL actor; the kernel remains the permission authority

The plugin host process becomes the spawner of the Deno REPL. The REPL runs as a
worker-actor on the host's orchestration tree (`spawnProcess`/`serveNode` on the
host side, `serveProcess`/`serveNode` in the REPL process), so the host holds a
typed `Remote` of the REPL surface and the REPL can acquire plugin surfaces.

The REPL's **effective permission is computed by the kernel**, exactly as today:
the layered overlay of built-in baseline, app-wide defaults, the
project/workspace profile, and the session override, with denials always winning
(ADR 0018). The host is only the **enforcement point**, not the authority:

- The kernel computes the REPL's `EffectivePolicy` and registers it with the
  permission broker for the REPL process id (the broker already keys policies by
  peer pid). This is the authoritative boundary for every Deno permission check
  the REPL makes.
- The host derives the REPL with a static permission shell reflecting the same
  synthetic policy. The shell is kept because it is simple, it is the broker's
  baseline when the broker is absent, and it expresses the policy snapshot at
  spawn — but it is **not** the security boundary. Documentation and comments
  must state this so the shell is never mistaken for one.
- The host must not widen the REPL's permissions on its own. The host already
  runs with full Deno permissions as trusted orchestration (ADR 0018 decision 8);
  the same trust relationship now extends to deriving the REPL with the kernel's
  policy and no more.

The host must report the REPL child's pid (and the session binding) to the
kernel through the existing control channel, because today the kernel attributes
control-channel and broker identity by pid (SO_PEERCRED / named pipe). With the
host as spawner, the kernel no longer learns the pid at spawn. This report is
required for both the permission broker registration and the control-channel
identity check.

### 2. One-way call direction: REPL → extensions. Extensions cannot call into the REPL.

The call direction is **REPL → extension** by default and as a product
invariant:

- The REPL (with the kernel's identity) declares a dependency on a plugin,
  acquires the plugin's actor surface through the existing specifier/acquire
  machinery, and calls its extension points.
- **Extensions cannot acquire or call any REPL capability.** The REPL does not
  export a callable actor surface for extensions (no `exposeReplSurface`), and
  the extension → REPL execution direction is not supported as a product
  feature. The mechanism for reverse calls (remote references, bidirectional
  capability sharing) exists in the library and is reserved for the future
  distributed extension host, but no product path wires it today.

This is enforced by the process boundary itself, not by a separate authorization
layer: two isolated processes, and the REPL exports nothing. An extension
physically cannot reach a REPL-specific API the REPL did not explicitly export.
"Export nothing" is the rule; there is no hidden implicit surface to guard.

The one-way rule is a security requirement, not a convenience choice: any
extension code executed inside the REPL process would run under the REPL's
effective policy (the broker keys by the REPL's pid), escaping the extension's
own narrowed grants with no broker-level way to distinguish it from the REPL's
own scripts. One-way direction is what keeps the permission model closed.

### 3. Broker is the authority; static shell is baseline, not boundary

When the REPL runs with `DENO_PERMISSION_BROKER_PATH`, every Deno permission
request it makes — from the REPL's own scripts or from the plugins it calls —
is resolved by the broker against the REPL's registered `EffectivePolicy`. The
static permission shell at spawn is retained as the baseline/fallback and for
readability, and is documented as non-authoritative.

## Consequences

- The extension host owns the REPL process lifecycle (spawn, health, cancel,
  shutdown, forced termination) alongside the plugin workers, under one
  orchestration tree.
- The kernel's permission authority is preserved: the REPL's policy is computed
  by the kernel, registered with the broker, and the host only enforces it.
- The REPL can call plugin extension points through actor capabilities;
  `extension.invoke` (the kernel-mediated invoke path) becomes redundant and can
  be retired once the direct Remote path is confirmed end-to-end (the
  plugin-host-redesign §10.6 step).
- Extensions cannot call into the REPL. The REPL exports no actor surface for
  them; reverse execution is not a product feature. The library mechanism
  remains available for the future distributed host but is not wired.
- The host→kernel pid/session report is a new required handshake for the
  broker registration and control-channel identity.
- Permissions of the REPL and of plugin workers come from different sources
  (kernel policy vs. plugin manifest) and are enforced by the same host; the
  host must keep the two enforcement paths distinct.

## Out of scope this ADR

- The concrete host↔REPL actor protocol (which RPC methods the REPL exposes to
  the host).
- Migrating the eval/comm WebSockets to actor channels (they may coexist during
  transition).
- The distributed extension host (ADR 0005 planes); this ADR only reserves the
  reverse-call mechanism for it.

## Resolved decisions (2026-08-24)

1. Reverse (extension → REPL) execution: **product-level not supported.** The
   REPL exports no callable surface for extensions. Reverse **query** surfaces
   (state/metadata with no permission surface) are not implemented either; the
   default is "export nothing, no reverse".
2. Static permission shell: **keep**, as the broker baseline/fallback and policy
   snapshot — simple, and documented as non-authoritative.
3. One-way rule (REPL → extensions only) is an **invariant** (ADR-level, like
   AGENTS.md invariants) so the distributed host never re-opens the reverse
   direction accidentally.

## References

- ADR 0014 (REPL sideband IPC and HTTP control)
- ADR 0016 (script plugins and extension points)
- ADR 0018 (permission store, Deno execution module, permission broker)
- `deno/research/` on branch `research/repl-host-actor-migration` (verified
  worker-actor 0.4.0 cross-process capabilities)
