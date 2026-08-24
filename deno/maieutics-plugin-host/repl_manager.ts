/**
 * ReplManager: the plugin host's REPL process derivation (ADR 0020).
 *
 * The plugin host is the spawner of the Deno REPL. Unlike plugin workers
 * (spawned as `Worker` actors via `spawn()`), the REPL is spawned as a separate
 * PROCESS via worker-actor's `spawnProcess` — the REPL is the carrier of the
 * kernel's permission policy and must live in its own process boundary (ADR
 * 0020 decision 1), not in a host-owned worker.
 *
 * The closed loop:
 *
 *   host.spawnRepl(sessionId, generation)
 *     -> spawnProcess(process_main.ts)
 *     -> REPL child pregest frame (the host learns the child pid before any
 *        broker-gated permission check the child can make)
 *     -> host emits `host.repl.spawned` over the control bus (B3)
 *     -> REPL child rpc.initialize() (env reads now resolve through the broker
 *        policy the kernel registered for the reported pid)
 *     -> host registers { pid, actor } in memory keyed by session
 *     -> child death / dispose -> host emits `host.repl.exited` exactly once
 *
 * The static permission shell passed at spawn is the broker's fallback
 * baseline, NOT the security boundary. The authoritative REPL policy is
 * computed by the kernel and registered with the permission broker for this
 * pid (ADR 0020 decision 1 / 3). The host never widens the shell on its own.
 */

import { type ActorHandle, type Remote, spawnProcess } from "@ghostflyby/worker-actor";
import { type HostReplReport, type HostReplRpc } from "./host_repl_protocol.ts";

const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const GENERATION_ENV = "MAIEUTICS_REPL_GENERATION";
/** The kernel hands the broker address to the host under this name; the host
 * itself must NOT consult the broker (it runs with full launch-time grants and
 * no registered policy) and only forwards the address to the REPL child as
 * DENO_PERMISSION_BROKER_PATH (B2 env contract). */
const BROKER_ENV = "MAIEUTICS_PERMISSION_BROKER";
/** Deno's own broker env: when set at process launch the broker is the single
 * authority for every explicit permission check the process makes. */
const BROKER_PATH_ENV = "DENO_PERMISSION_BROKER_PATH";
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

/** Sink for host → kernel REPL reports (see host_repl_protocol.ts). The bus
 * connection may not exist yet when a ReplManager is constructed; mod.ts wires
 * it once the control bus is connected (spawnRepl refuses to derive a REPL
 * before that — the pid report is what registers the child's broker policy and
 * control-channel identity, so deriving a REPL without a connected bus would
 * deadlock every broker-gated permission request the child makes). */
export type ReplReporter = (report: HostReplReport) => void;

export interface ReplManagerOptions {
  /** Absolute path of the REPL process entry module (process_main.ts). */
  replEntryPath: string;
  /** Filesystem read grant for the REPL child's module graph (defaults to
   * allowing reads; the broker overrides in the full migration). */
  replEntryReadPath?: string;
  /** Host → kernel reporter for pid/session reports. Set after the bus
   * connection is established; spawnRepl fails while it is still unset. */
  reporter?: ReplReporter;
}

/** One derived REPL process: its self-reported pid plus the actor handle. */
export interface ReplHandle {
  readonly sessionId: string;
  readonly generation: number;
  readonly pid: number;
  readonly actor: Remote<HostReplRpc> & ActorHandle;
  state: "running" | "stopped" | "crashed";
}

/**
 * The pid the REPL child reports back to the host. worker-actor 0.4.0's
 * spawnProcess does not expose the spawned child pid and has no env option; to
 * learn the pid the host must round-trip through the child. The child reports
 * it twice: in a pregest handshake that runs at module top level BEFORE the
 * broker env can matter (so the host can emit `host.repl.spawned` before the
 * child's first broker-gated permission check), and again from
 * rpc.initialize(). A reported pid is accepted when it is a safe integer and
 * differs from the host's own pid.
 */
export function isValidReplPid(pid: number): boolean {
  return pid !== Deno.pid && Number.isSafeInteger(pid) && pid > 0;
}

export class ReplManager {
  #replEntryPath: string;
  #replReadGrant: boolean | string[];
  #reporter?: ReplReporter;
  /** In-memory pid registry keyed by session id. B2 reads this to forward the
   * pid to the kernel broker and control-channel identity check. */
  #repls = new Map<string, ReplHandle>();

  constructor(options: ReplManagerOptions) {
    this.#replEntryPath = options.replEntryPath;
    const entryRead = options.replEntryReadPath;
    this.#replReadGrant = entryRead === undefined || entryRead === null ? true : [entryRead];
    this.#reporter = options.reporter;
  }

  /** Sets (or replaces) the host → kernel reporter. Must be non-null before
   * spawnRepl runs; the bus is established after the control hello. */
  setReporter(reporter: ReplReporter | undefined): void {
    this.#reporter = reporter;
  }

  /** The session identity vars + broker address are injected on the host's own
   * environment around each spawn because worker-actor 0.4.0's spawnProcess has
   * no env option and the child captures its environment at launch. This is
   * safe only while spawnRepl calls are serialized (the host drives REPL
   * sessions one at a time in the skeleton); concurrent derivations would race
   * on the shared environment. B1's single-threaded skeleton assumption is
   * documented in place. */
  #injectSessionEnv(sessionId: string, generation: number): () => void {
    const previousSession = Deno.env.get(SESSION_ENV);
    const previousGeneration = Deno.env.get(GENERATION_ENV);
    const previousBroker = Deno.env.get(BROKER_PATH_ENV);
    Deno.env.set(SESSION_ENV, sessionId);
    Deno.env.set(GENERATION_ENV, String(generation));
    // The broker address is forwarded verbatim. When the host was launched
    // without a broker (no MAIEUTICS_PERMISSION_BROKER), the env stays unset so
    // the child launches with the static shell only (tests / no-kernel runs).
    const broker = Deno.env.get(BROKER_ENV);
    if (broker !== undefined && broker.length > 0) Deno.env.set(BROKER_PATH_ENV, broker);
    return () => {
      if (previousSession === undefined) Deno.env.delete(SESSION_ENV);
      else Deno.env.set(SESSION_ENV, previousSession);
      if (previousGeneration === undefined) Deno.env.delete(GENERATION_ENV);
      else Deno.env.set(GENERATION_ENV, previousGeneration);
      if (previousBroker === undefined) Deno.env.delete(BROKER_PATH_ENV);
      else Deno.env.set(BROKER_PATH_ENV, previousBroker);
    };
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
    if (this.#reporter === undefined) {
      throw new Error(
        `The control bus is not connected; the host.repl.spawned pid report ` +
          `registers the child's broker policy and control-channel identity, so ` +
          `no REPL can be derived before it (session '${sessionId}').`,
      );
    }

    const permissions: Deno.PermissionOptionsObject = {
      ...REPL_SHELL_PERMISSIONS,
    };
    if (typeof this.#replReadGrant !== "boolean") {
      permissions.read = this.#replReadGrant;
    }
    // The REPL child reads its session identity from the environment
    // (MAIEUTICS_REPL_SESSION / MAIEUTICS_REPL_GENERATION, the same contract the
    // kernel writes today) and the broker address from DENO_PERMISSION_BROKER_PATH.
    const restoreEnv = this.#injectSessionEnv(sessionId, generation);
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
      // Deno reads DENO_PERMISSION_BROKER_PATH at launch; restoring the host's
      // own environment before the handshake resolves keeps the host's later
      // permission checks on their full-grant flags.
      restoreEnv();
    }

    // The pid comes from the child itself: spawnProcess (worker-actor 0.4.0)
    // does not expose the spawned process id. The child reports Deno.pid from
    // the pregest frame; the host reports it to the kernel BEFORE calling
    // initialize() so the broker policy the kernel registers for the pid is in
    // place when the child's first broker-gated permission check (the env reads
    // inside initialize) arrives.
    const pid = await this.#requestPid(actor);
    this.#emitSpawned(sessionId, generation, pid);

    let info: { pid: number; sessionId: string; generation: number };
    try {
      info = await actor.initialize();
      if (!isValidReplPid(info.pid)) {
        throw new Error(`REPL process for session '${sessionId}' reported an invalid pid.`);
      }
      if (info.pid !== pid) {
        // The pregest and initialize handshakes must report the same process;
        // a mismatch means the child re-executed between the two or misread its
        // own pid.
        throw new Error(
          `REPL process for session '${sessionId}' reported pid ${info.pid} after the ` +
            `pregest pid ${pid}.`,
        );
      }
      if (info.sessionId !== sessionId) {
        // The child must be the REPL for this session; mismatched identity means
        // the host passed the wrong session env or the child read a stale one.
        throw new Error(
          `REPL process for session '${sessionId}' reported session '${info.sessionId}'.`,
        );
      }
    } catch (error) {
      // The spawn report already reached the kernel; balance it with an exited
      // report so the pid registration (broker policy + session identity) is
      // released even though no handle ever entered the registry.
      await actor.dispose().catch(() => {});
      this.#emitExited(
        sessionId,
        generation,
        pid,
        error instanceof Error ? error.message : String(error),
      );
      throw error;
    }

    const handle: ReplHandle = {
      sessionId,
      generation,
      pid,
      actor,
      state: "running",
    };
    this.#repls.set(sessionId, handle);
    this.#startLivenessMonitor(sessionId, handle);
    console.error(
      `[plugin-host] derived REPL process for session '${sessionId}' generation ` +
        `${generation}: pid ${pid}.`,
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
      this.#emitExited(sessionId, handle.generation, handle.pid, undefined);
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

  /** Requests the child pid through the pregest rpc so the host can emit
   * `host.repl.spawned` before any broker-gated child request. The pregest
   * handshake is required: falling back to `initialize()` would run the
   * child's broker-gated env reads before the pid report reached the kernel
   * (a 10s broker wait then a default deny). The pid is only reported onward
   * after it passes {@link isValidReplPid}; a bogus pid must not reach the
   * kernel (the .NET side rejects pid <= 0, but a negative or NaN report
   * would fail the handshake on the way there). */
  async #requestPid(actor: Remote<HostReplRpc> & ActorHandle): Promise<number> {
    if (typeof actor.pregestPid !== "function") {
      throw new Error("The REPL child does not expose the pregest pid handshake.");
    }
    const pid = await actor.pregestPid();
    if (!isValidReplPid(pid)) {
      throw new Error(`The REPL child reported an invalid pid ${pid}.`);
    }
    return pid;
  }

  /** Emits `host.repl.spawned`. The reporter is guaranteed to exist (spawnRepl
   * refuses to run without it), but the bus may have dropped meanwhile; a
   * failed report is logged, not fatal — the child is still derived locally. */
  #emitSpawned(sessionId: string, generation: number, pid: number): void {
    const report: HostReplReport = {
      type: "host.repl.spawned",
      payload: { sessionId, generation, pid },
    };
    try {
      this.#reporter?.(report);
    } catch (error) {
      console.error(
        `[plugin-host] could not report REPL spawn for session '${sessionId}': ` +
          `${error instanceof Error ? error.message : String(error)}.`,
      );
    }
  }

  /** Emits `host.repl.exited` exactly once per handle. disposeRepl marks the
   * handle stopped before emitting; a later liveness/onDeath report sees no
   * running handle and stays silent. */
  #emitExited(
    sessionId: string,
    generation: number,
    pid: number,
    failure: string | undefined,
  ): void {
    const report: HostReplReport = {
      type: "host.repl.exited",
      payload: { sessionId, generation, pid, ...(failure === undefined ? {} : { failure }) },
    };
    try {
      this.#reporter?.(report);
    } catch (error) {
      console.error(
        `[plugin-host] could not report REPL exit for session '${sessionId}': ` +
          `${error instanceof Error ? error.message : String(error)}.`,
      );
    }
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
    this.#emitExited(
      sessionId,
      handle.generation,
      handle.pid,
      reason instanceof Error ? reason.message : String(reason),
    );
  }
}
