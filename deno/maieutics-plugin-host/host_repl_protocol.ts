/**
 * Host ↔ REPL process protocol (ADR 0020 migration).
 *
 * This file is the alignment draft for the .NET side (tasks B2/B3/B5). It
 * defines (a) the rpc surface the REPL process actor exposes to the plugin
 * host, (b) the bus message shapes the host sends to the kernel so the kernel
 * can register the REPL child's pid with the permission broker and bind the
 * control-channel identity by pid (ADR 0020 decision 1), and (c) the
 * kernel → host derive instruction that tells the host how to spawn a REPL
 * (B5a).
 *
 * The rpc types are implemented by `maieutics-deno-repl/process_rpc.ts` and
 * consumed by `maieutics-plugin-host/repl_manager.ts`. The bus messages are
 * emitted by ReplManager when a REPL is derived/spawned/exited (B3/B5a); the
 * envelope shape follows the shared control bus convention
 * (`shared/protocol.ts`): `{ type, payload, correlationId? }`. The .NET side
 * consumes them in `PluginHostManager.HandleHostMessage` under
 * `host.repl.spawned` / `host.repl.exited` and sends `host.repl.derive` (B5b).
 * Version the messages if the shape ever breaks; tolerate unknown fields on
 * read, per the shared protocol conventions.
 */

/** Session identity the host binds at spawn (mirrors the existing REPL env
 * contract; the kernel writes these today, the host writes them in B2). */
export interface HostReplSessionIdentity {
  /** Session id this REPL child belongs to (MAIEUTICS_REPL_SESSION). */
  sessionId: string;
  /** Generation number of the session (MAIEUTICS_REPL_GENERATION). */
  generation: number;
}

/** RPC surface exposed by the REPL process actor (serveProcess). */
export interface HostReplRpc {
  /** Returns the process's own Deno.pid. */
  getPid(): Promise<number>;
  /** Returns the process's own Deno.pid. */
  pregestPid(): Promise<number>;
  /** Returns the broker address this process was launched with
   * (DENO_PERMISSION_BROKER_PATH); used by the env-forwarding tests. */
  pregestBrokerPath(): Promise<string>;
  /** Initializes the actor; returns the self-reported Deno.pid + session identity. */
  initialize(): Promise<{
    pid: number;
    sessionId: string;
    generation: number;
  }>;
  /**
   * Starts the real WebSocket REPL client (eval + comm channels + the Aves
   * worker). Resolves once the eval hello/ready handshake completes or the
   * client fails. The host calls this AFTER initialize(), so the kernel has
   * already registered this pid's broker policy and session identity (B3
   * ordering) when the client's broker-gated connect begins.
   */
  startRepl(): Promise<void>;
  /** Health of the WebSocket REPL client (started / ready / terminal error). */
  status(): Promise<{
    started: boolean;
    ready: boolean;
    error?: string;
  }>;
  /** Control-plane stub: real execution is served by the kernel over the eval
   * WebSocket, never over this actor method. Returns the ReplActorResult-shaped
   * envelope for host-side call-site compatibility (B5b). */
  execute(code: string): Promise<{
    ok: boolean;
    data?: unknown;
    error?: string;
    fatal?: boolean;
    cancelled?: boolean;
  }>;
  /** Cooperative shutdown of the REPL kernel inside the process. */
  disposeRepl(): Promise<void>;
}

/**
 * Static permission shell the kernel ships with a derive instruction. This is
 * the broker's fallback baseline, NOT a security boundary (ADR 0020 decision
 * 1): the kernel computes the authoritative REPL EffectivePolicy and registers
 * it with the permission broker for the reported pid, so the shell only
 * matters when the broker is absent (tests / no-kernel runs). Kinds mirror
 * `Deno.PermissionOptionsObject`. worker-actor 0.4.0 renders launch flags for
 * read/write/net/env/run/sys/ffi only; `import` is carried for the broker's
 * fallback baseline but cannot be expressed as a launch flag by worker-actor
 * 0.4.0 and is ignored at spawn. Omitted kinds are denied at launch.
 */
export interface HostReplPermissions {
  read?: boolean | string[];
  write?: boolean | string[];
  net?: boolean | string[];
  env?: boolean | string[];
  run?: boolean | string[];
  ffi?: boolean | string[];
  sys?: boolean | string[];
  import?: boolean | string[];
}

/**
 * Kernel → host instruction to derive a Deno REPL process (ADR 0020, B5a).
 * The host is the spawner; the kernel decides the entry module, the complete
 * child environment, and the static permission shell. This is the command half
 * of the derive flow: the host answers with `host.repl.spawned` /
 * `host.repl.exited` / `host.repl.deriveFailed` (the `HostReplReport` union
 * below). Field names are CamelCase to align with the .NET side
 * (`ReplControlMessages.cs`, `JsonKnownNamingPolicy.CamelCase`).
 *
 * Env contract (authoritative, B5a): the kernel supplies the FULL child
 * environment (`env`), including MAIEUTICS_REPL_SESSION / MAIEUTICS_REPL_GENERATION
 * / MAIEUTICS_REPL_IPC / MAIEUTICS_REPL_CLIENT and, on Windows,
 * MAIEUTICS_REPL_PIPE — exactly the set `DenoReplProcess` writes today. The
 * host must NOT guess, add, or overwrite kernel-provided entries. The host
 * only appends `DENO_PERMISSION_BROKER_PATH` (forwarded verbatim from its own
 * `MAIEUTICS_PERMISSION_BROKER`; the kernel must NOT repeat it) and, on
 * Windows, `SystemRoot`. As a defensive default for the direct API call path
 * (not the bus path), the host fills MAIEUTICS_REPL_SESSION /
 * MAIEUTICS_REPL_GENERATION from the request identity when the kernel env
 * omits them; it never overrides kernel-provided values.
 */
export interface HostReplDerivePayload {
  /** Session id this REPL child belongs to (MAIEUTICS_REPL_SESSION). */
  sessionId: string;
  /** Generation number of the session (MAIEUTICS_REPL_GENERATION). */
  generation: number;
  /** Absolute file URL (or absolute filesystem path) of the REPL child entry
   * module. The kernel computes the real materialized path; the host resolves
   * it with `new URL` and never embeds its own REPL entry knowledge. */
  entryUrl: string;
  /** Complete REPL child environment; see the env contract above. */
  env: Record<string, string>;
  /** Static permission shell for `spawnProcess` (broker fallback baseline).
   * When absent the host keeps its skeleton default `{ read: true, env: true }`. */
  permissions?: HostReplPermissions;
  /** Whether the host reports the outcome: `host.repl.spawned` on success
   * (default true) and `host.repl.deriveFailed` / `host.repl.exited` on
   * failure. When false the host still derives the REPL but stays silent on
   * this channel (the kernel observes the child's control-channel hello
   * itself). */
  report?: boolean;
}

/**
 * Host → kernel bus messages. The host reports the REPL child's pid so the
 * kernel can register it with the permission broker (keyed by pid) and accept
 * the REPL's control-channel hello (identified by pid, SO_PEERCRED / named
 * pipe). Envelope shape follows the shared control bus convention
 * (`shared/protocol.ts`): `{ type, payload, correlationId? }`.
 */
export type HostReplReport =
  | {
    type: "host.repl.spawned";
    payload: {
      sessionId: string;
      generation: number;
      pid: number;
    };
  }
  | {
    type: "host.repl.exited";
    payload: {
      sessionId: string;
      generation: number;
      pid: number;
      /** Reason, when the exit was a crash rather than a dispose. */
      failure?: string;
    };
  }
  | {
    type: "host.repl.deriveFailed";
    payload: {
      sessionId: string;
      generation: number;
      /** Why the kernel's derive instruction could not be executed. Emitted
       * BEFORE any pid exists (validation / spawn failure); a failure AFTER
       * the spawn report already went out is reported as `host.repl.exited`
       * with a failure reason instead, so the kernel never sees both. */
      message: string;
    };
  };
