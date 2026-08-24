/**
 * Host ↔ REPL process protocol (ADR 0020 migration).
 *
 * This file is the alignment draft for the .NET side (task B2: the kernel
 * binding of the REPL's broker policy and control-channel identity). It defines
 * (a) the rpc surface the REPL process actor exposes to the plugin host and
 * (b) the bus message shapes the host sends to the kernel so the kernel can
 * register the REPL child's pid with the permission broker and bind the
 * control-channel identity by pid (ADR 0020 decision 1).
 *
 * The rpc types are implemented by `maieutics-deno-repl/process_rpc.ts` and
 * consumed by `maieutics-plugin-host/repl_manager.ts`. The bus messages are
 * emitted by ReplManager when a REPL is spawned/exited (B3); the envelope shape
 * follows the shared control bus convention (`shared/protocol.ts`):
 * `{ type, payload, correlationId? }`. The .NET side consumes them in
 * `PluginHostManager.HandleHostMessage` under `host.repl.spawned` /
 * `host.repl.exited`. Version the messages if the shape ever breaks; tolerate
 * unknown fields on read, per the shared protocol conventions.
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
  /** Placeholder execution; returns the ReplActorResult-shaped envelope. */
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
  };
