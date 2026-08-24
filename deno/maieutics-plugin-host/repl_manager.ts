/**
 * ReplManager: the plugin host's REPL process derivation (ADR 0020 skeleton).
 *
 * This is the first implementation slice of ADR 0020: the plugin host becomes
 * the spawner of the Deno REPL. Unlike plugin workers (spawned as `Worker`
 * actors via `spawn()`), the REPL is spawned as a separate PROCESS via
 * worker-actor's `spawnProcess` — the REPL is the carrier of the kernel's
 * permission policy and must live in its own process boundary (ADR 0020
 * decision 1), not in a host-owned worker.
 *
 * The skeleton establishes the closed loop that task B2 (.NET side) will extend:
 *
 *   host.spawnRepl(sessionId, generation)
 *     -> spawnProcess(process_main.ts)
 *     -> REPL child rpc.initialize()
 *     -> child self-reports Deno.pid (spawnProcess does not expose the pid,
 *        worker-actor 0.4.0)
 *     -> host registers { pid, actor } in memory keyed by session
 *     -> host logs the pid
 *
 * The static permission shell passed at spawn is the broker's fallback
 * baseline, NOT the security boundary. The authoritative REPL policy is
 * computed by the kernel and registered with the permission broker for this
 * pid (ADR 0020 decision 1 / 3). The host never widens the shell on its own.
 */

import { type ActorHandle, type Remote, spawnProcess } from "@ghostflyby/worker-actor";
import type { HostReplRpc } from "./host_repl_protocol.ts";

const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const GENERATION_ENV = "MAIEUTICS_REPL_GENERATION";
/** Poll interval of the host-side pid liveness monitor. */
const LIVENESS_POLL_MS = 200;

/** Static permission shell for the REPL child at spawn. This is the broker's
 * fallback baseline when the broker is absent, NOT a security boundary (ADR
 * 0020 decision 1): the kernel computes the authoritative REPL EffectivePolicy
 * and registers it with the permission broker for this pid. Read + env are the
 * skeleton's baseline grants: read to load the entry module graph, env so the
 * child can read the session identity vars (MAIEUTICS_REPL_SESSION /
 * MAIEUTICS_REPL_GENERATION). */
const REPL_SHELL_PERMISSIONS = { read: true, env: true } as const;

export interface ReplManagerOptions {
  /** Absolute path of the REPL process entry module (process_main.ts). */
  replEntryPath: string;
  /** Filesystem read grant for the REPL child's module graph (defaults to
   * allowing reads; the broker overrides in the full migration). */
  replEntryReadPath?: string;
}

/** One derived REPL process: its self-reported pid plus the actor handle. */
export interface ReplHandle {
  readonly sessionId: string;
  readonly generation: number;
  readonly pid: number;
  readonly actor: Remote<HostReplRpc> & ActorHandle;
  state: "running" | "stopped" | "crashed";
}

export class ReplManager {
  #replEntryPath: string;
  #replReadGrant: boolean | string[];
  /** In-memory pid registry keyed by session id. B2 reads this to forward the
   * pid to the kernel broker and control-channel identity check. */
  #repls = new Map<string, ReplHandle>();

  constructor(options: ReplManagerOptions) {
    this.#replEntryPath = options.replEntryPath;
    const entryRead = options.replEntryReadPath;
    this.#replReadGrant = entryRead === undefined || entryRead === null ? true : [entryRead];
  }

  /** Registers `sessionId` as a running REPL in the pid registry. */
  async spawnRepl(
    sessionId: string,
    generation: number,
  ): Promise<ReplHandle> {
    const existing = this.#repls.get(sessionId);
    if (existing !== undefined && existing.state === "running") {
      throw new Error(`A REPL process is already running for session '${sessionId}'.`);
    }

    const permissions: Deno.PermissionOptionsObject = {
      ...REPL_SHELL_PERMISSIONS,
    };
    if (typeof this.#replReadGrant !== "boolean") {
      permissions.read = this.#replReadGrant;
    }
    // The REPL child reads its session identity from the environment
    // (MAIEUTICS_REPL_SESSION / MAIEUTICS_REPL_GENERATION, the same contract the
    // kernel writes today). spawnProcess (worker-actor 0.4.0) has no `env`
    // option and the child captures its environment at process launch, so the
    // host injects the identity by setting the vars on itself around the spawn
    // and restoring them immediately after the handshake. This is safe while
    // spawnRepl calls are serialized (the host drives REPL sessions one at a
    // time in the skeleton); B2 should add a real per-spawn env mechanism when
    // concurrent REPL sessions are derived.
    const previousSession = Deno.env.get(SESSION_ENV);
    const previousGeneration = Deno.env.get(GENERATION_ENV);
    Deno.env.set(SESSION_ENV, sessionId);
    Deno.env.set(GENERATION_ENV, String(generation));
    let actor: Remote<HostReplRpc> & ActorHandle;
    try {
      actor = await spawnProcess<HostReplRpc>(
        this.#replEntryPath,
        {
          permissions,
          onDeath: (reason: unknown) => this.#handleDeath(sessionId, generation, reason),
        },
      );
    } finally {
      if (previousSession === undefined) Deno.env.delete(SESSION_ENV);
      else Deno.env.set(SESSION_ENV, previousSession);
      if (previousGeneration === undefined) Deno.env.delete(GENERATION_ENV);
      else Deno.env.set(GENERATION_ENV, previousGeneration);
    }

    // The pid comes from the child itself: spawnProcess (worker-actor 0.4.0)
    // does not expose the spawned process id. The child reports Deno.pid from
    // rpc.initialize, and the host registers it before any other RPC is issued
    // so B2 can forward it to the broker.
    const info = await actor.initialize();
    if (info.pid !== Deno.pid && !Number.isSafeInteger(info.pid)) {
      throw new Error(`REPL process for session '${sessionId}' reported an invalid pid.`);
    }
    if (info.sessionId !== sessionId) {
      // The child must be the REPL for this session; mismatched identity means
      // the host passed the wrong session env or the child read a stale one.
      await actor.dispose();
      throw new Error(
        `REPL process for session '${sessionId}' reported session '${info.sessionId}'.`,
      );
    }

    const handle: ReplHandle = {
      sessionId,
      generation,
      pid: info.pid,
      actor,
      state: "running",
    };
    this.#repls.set(sessionId, handle);
    this.#startLivenessMonitor(sessionId, handle);
    console.error(
      `[plugin-host] derived REPL process for session '${sessionId}' generation ` +
        `${generation}: pid ${info.pid}.`,
    );
    return handle;
  }

  /** The registered handle for a session, if one is currently running. */
  get(sessionId: string): ReplHandle | undefined {
    const handle = this.#repls.get(sessionId);
    return handle !== undefined && handle.state === "running" ? handle : undefined;
  }

  /** Disposes the REPL process for a session and removes it from the registry.
   * Returns false when the session had no running REPL. */
  async disposeRepl(sessionId: string): Promise<boolean> {
    const handle = this.#repls.get(sessionId);
    if (handle === undefined || handle.state !== "running") return false;
    handle.state = "stopped";
    try {
      await handle.actor.dispose();
    } finally {
      this.#repls.delete(sessionId);
    }
    return true;
  }

  /** Disposes every derived REPL process (host shutdown path). */
  async disposeAll(): Promise<void> {
    for (const sessionId of [...this.#repls.keys()]) {
      await this.disposeRepl(sessionId);
    }
  }

  /**
   * Host-side liveness monitor: polls the REPL child pid and clears the registry
   * when the process is gone. This is the reliable death signal for the
   * skeleton because worker-actor 0.4.0's `onDeath` only fires when the IPC
   * channel closes (crash / handshake failure), not when the child is killed
   * outright (e.g. SIGKILL) — the library never wires `child.on("exit")` to the
   * transport close. The host owns the pid, so it can and must watch it.
   * Polling stops once the handle leaves the registry (dispose / onDeath).
   */
  #startLivenessMonitor(sessionId: string, handle: ReplHandle): void {
    const timer = setInterval(() => {
      if (this.#repls.get(sessionId) !== handle) {
        clearInterval(timer);
        return;
      }
      let alive = true;
      try {
        Deno.kill(handle.pid, 0);
      } catch {
        alive = false;
      }
      if (!alive) {
        clearInterval(timer);
        this.#handleDeath(
          sessionId,
          handle.generation,
          new Error(`REPL process ${handle.pid} is no longer alive.`),
        );
      }
    }, LIVENESS_POLL_MS);
  }

  #handleDeath(sessionId: string, generation: number, reason: unknown): void {
    const handle = this.#repls.get(sessionId);
    if (handle === undefined || handle.state === "stopped") return;
    handle.state = "crashed";
    console.error(
      `[plugin-host] REPL process for session '${sessionId}' generation ${generation} died: ` +
        `${reason instanceof Error ? reason.message : String(reason)}.`,
    );
    this.#repls.delete(sessionId);
    // The kernel-facing report (`host.repl.exited`, see host_repl_protocol.ts)
    // is emitted by the .NET side (B2) once the host->kernel pid reporting
    // lands; the skeleton only cleans the local registry.
  }
}
