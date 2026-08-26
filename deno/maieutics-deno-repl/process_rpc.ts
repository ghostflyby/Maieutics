/**
 * REPL process actor surface (ADR 0020, C1).
 *
 * This module is the `serveProcess` surface of the REPL child that the plugin
 * host derives via worker-actor's `spawnProcess` (see `process_main.ts`). The
 * host is the spawner of the REPL process and learns its pid through the actor
 * handshake; the pid report (`host.repl.spawned`) registers the broker policy
 * and the control-channel identity for the child BEFORE its first broker-gated
 * permission check can reach the broker (B3).
 *
 * Execution channel (plan X, C1): the host-derived process runs the SAME real
 * REPL as the kernel-derived path — a `ReplClient` against the kernel's
 * `/v1/repl/eval/ws` and `/comm` channels with the shared `repl_worker` Aves
 * kernel. The actor surface stays control/query-only (`getPid`, `initialize`,
 * `status`, `disposeRepl`, `startRepl`); `execute` is intentionally not
 * forwarded over the actor because the eval WebSocket already carries the full
 * execution machinery (backpressure, cancellation, blocking input, comm
 * delivery) and the dual-track transition keeps the eval channel live (ADR
 * 0020 out of scope). The actor surface therefore reports that execution is
 * served by the kernel over the eval channel instead of echoing a skeleton
 * result.
 *
 * Timing (B3): the ReplClient must not start before the host reported this
 * pid. Every action the client takes on startup — reading the session env and
 * opening the eval/comm WebSockets — is broker-gated once
 * DENO_PERMISSION_BROKER_PATH is set, and the broker policy exists only after
 * the kernel processed the pid report. The host therefore calls
 * `pregestPid` (side-effect-free), reports the pid, then `initialize()`
 * (also side-effect-free), and finally `startRepl()` which lazily boots the
 * WebSocket client. The process never auto-starts the client at module load.
 *
 * Lifecycle: when the eval channel completes (the kernel's repl.eval.dispose,
 * a client failure, or a host-initiated shutdown), the client's run promise
 * settles and this process exits itself; the host's spawnProcess death
 * detection then emits `host.repl.exited`, releasing the pid registration.
 * The process does not exit when the env lacks the kernel contract (the host
 * tests derive without a kernel); it stays a control-plane actor instead.
 *
 * Env contract (documented draft for the .NET side, see `host_repl_protocol.ts`):
 * - `MAIEUTICS_REPL_SESSION`    session id bound to this REPL child.
 * - `MAIEUTICS_REPL_GENERATION` generation number of the session.
 * - `MAIEUTICS_REPL_IPC`        kernel control-channel address (eval / comm WS).
 * - `MAIEUTICS_REPL_CLIENT`     script client module URL (bound as `maieutics`).
 * - `DENO_PERMISSION_BROKER_PATH` broker socket the child's permission checks
 *   go through once the host reports the pid (B3 forwards the address the
 *   kernel handed the host under MAIEUTICS_PERMISSION_BROKER).
 */

import { serveProcess } from "@ghostflyby/worker-actor";
import type { ReplActorResult } from "./repl_actor.ts";
import { ReplClient } from "./repl_client.ts";
import { readReplProcessEnvironment } from "./repl_process_env.ts";

const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const GENERATION_ENV = "MAIEUTICS_REPL_GENERATION";
const BROKER_PATH_ENV = "DENO_PERMISSION_BROKER_PATH";

/** Identity + capability shape the host binds to a session. */
export interface ReplProcessInfo {
  /** Deno.pid of this REPL process, self-reported because the host's
   * spawnProcess proxy does not expose it (worker-actor 0.4.0). */
  pid: number;
  /** Session id bound by the host at spawn (from MAIEUTICS_REPL_SESSION). */
  sessionId: string;
  /** Session generation bound by the host at spawn (from MAIEUTICS_REPL_GENERATION). */
  generation: number;
}

/** Health of the WebSocket REPL client inside this process. */
export interface ReplProcessStatus {
  /** Whether startRepl booted the WebSocket REPL client. */
  started: boolean;
  /** Whether the eval channel completed the hello/ready handshake. */
  ready: boolean;
  /** Terminal failure of the client, when it failed or was disposed. */
  error?: string;
}

export const rpc = {
  /**
   * Pre-registration pid handshake (B3): returns the process pid WITHOUT any
   * permission-gated operation. The host calls this right after spawn resolves,
   * before the broker env can route the child's own permission checks, so it
   * can report `host.repl.spawned` and the kernel can register the broker
   * policy for this pid before `initialize()` runs (whose env reads are
   * broker-gated when DENO_PERMISSION_BROKER_PATH is set). This method is
   * optional on the host side (a child that lacks it falls back to
   * `initialize()`), but must stay side-effect-free here.
   */
  pregestPid(): number {
    return Deno.pid;
  },

  /**
   * Returns the forwarded broker address this process was launched with
   * (DENO_PERMISSION_BROKER_PATH). Present for the host-side env-forwarding
   * tests; never consulted by the REPL itself — the broker is attached by the
   * kernel through the pid the host reported, this process only carries the
   * socket address.
   */
  pregestBrokerPath(): string {
    return Deno.env.get(BROKER_PATH_ENV) ?? "";
  },

  /**
   * Initializes the REPL process actor. Returns the self-reported pid plus the
   * host-bound session identity. The host calls this immediately after the
   * pregest pid report so the broker policy for this pid is already registered
   * (B2 then forwards the pid to the kernel broker and control channel).
   */
  initialize(): ReplProcessInfo {
    const sessionId = Deno.env.get(SESSION_ENV) ?? "";
    const rawGeneration = Deno.env.get(GENERATION_ENV);
    const generation = rawGeneration === undefined || rawGeneration.length === 0
      ? 0
      : Number(rawGeneration);
    if (!Number.isSafeInteger(generation) || generation < 0) {
      throw new Error(`${GENERATION_ENV} must be a non-negative integer.`);
    }
    return {
      pid: Deno.pid,
      sessionId,
      generation,
    };
  },

  /**
   * Starts the real WebSocket REPL client (eval + comm channels + the shared
   * Aves worker). Called by the host AFTER initialize() has returned, so the
   * kernel already registered this pid's broker policy and session identity
   * (B3 ordering). Resolves once the eval hello/ready handshake completes or
   * the client fails; a failure is recorded (see status()) and, when the
   * client was booted with a full kernel env, terminates this process so the
   * host's death detection reports the exit.
   */
  async startRepl(): Promise<void> {
    await replClientManager.start();
  },

  /**
   * Health of the WebSocket REPL client: whether it started and completed the
   * eval hello/ready handshake, plus a terminal error when it failed or was
   * disposed. Control-plane only; never gated on the client being up.
   */
  status(): ReplProcessStatus {
    return replClientManager.status();
  },

  /**
   * Control-plane stub retained for host-side call-site compatibility (B5b).
   * Real execution is served by the kernel over the eval WebSocket, never over
   * this actor method; the envelope mirrors the ReplActorResult shape so the
   * host-side caller sees the migration state explicitly.
   */
  execute(code: string): ReplActorResult {
    if (typeof code !== "string") {
      throw new TypeError("execute expects a code string.");
    }
    return {
      ok: false,
      error: "The host-derived REPL serves execution over the kernel eval channel; " +
        "the actor execute surface is a control-plane stub.",
    };
  },

  /**
   * Cooperative shutdown of the WebSocket REPL client (the eval dispose
   * handshake and the Aves kernel disposal). The process exits once the
   * client's run promise settles; the host observes the exit through its
   * spawnProcess death detection.
   */
  async disposeRepl(): Promise<void> {
    await replClientManager.dispose();
  },
};

/**
 * Owns the lazily-started WebSocket REPL client. The client must only boot
 * after the host reported this pid (B3); `start()` is the only entry point and
 * is invoked by the host actor call, never at module load. When the client
 * completes — graceful kernel dispose, a client failure, or a host-initiated
 * shutdown — the process exits itself unless the env lacked the kernel
 * contract (the host tests derive without a kernel and keep a control-plane
 * actor). Dispose is idempotent.
 */
const replClientManager = new (class ReplClientManager {
  #client: ReplClient | undefined;
  #startTask: Promise<void> | undefined;
  #disposed = false;
  #error: Error | undefined;
  #ready = false;

  async start(): Promise<void> {
    if (this.#disposed) {
      throw new Error("The REPL process actor is disposed.");
    }
    if (this.#client !== undefined) {
      if (this.#error !== undefined) throw this.#error;
      return;
    }
    if (this.#startTask !== undefined) {
      await this.#startTask;
      if (this.#error !== undefined) throw this.#error;
      return;
    }

    let options;
    try {
      options = readReplProcessEnvironment();
    } catch (error) {
      // The kernel env contract is absent (host tests derive without a kernel).
      // The process stays a control-plane actor; the error is recorded and the
      // process does NOT exit (the host owns it and disposes it later).
      this.#error = error instanceof Error ? error : new Error(String(error));
      const failure = this.#error;
      this.#startTask = Promise.reject(failure);
      this.#startTask.catch(() => {});
      throw failure;
    }

    let resolveReady: (() => void) | undefined;
    const ready = new Promise<void>((resolve) => {
      resolveReady = resolve;
    });
    const client = new ReplClient({
      ...options,
      onReady: () => {
        this.#ready = true;
        resolveReady?.();
      },
    });
    this.#client = client;
    const run = client.run();
    // The eval channel is the process's reason to live: when it completes
    // (kernel dispose / failure / host shutdown) the process exits itself and
    // the host's spawnProcess death detection emits host.repl.exited. The
    // settle handler must not race the host's RPC reply, so it is scheduled
    // after the current call stack (startRepl resolves first via the ready
    // race below, then the exit happens on a later microtask).
    run.then(
      () => this.#selfTerminate(),
      (error) => {
        this.#error = error instanceof Error ? error : new Error(String(error));
        this.#selfTerminate();
      },
    );
    this.#startTask = Promise.race([
      ready,
      run.then(
        () => {},
        (error: unknown) => {
          throw error;
        },
      ),
    ]);
    this.#startTask.catch(() => {});
    await this.#startTask;
  }

  status(): ReplProcessStatus {
    if (this.#error !== undefined) {
      return { started: true, ready: this.#ready, error: this.#error.message };
    }
    if (this.#disposed) {
      return { started: true, ready: this.#ready, error: "The REPL process actor is disposed." };
    }
    if (this.#client === undefined) {
      return { started: false, ready: false };
    }
    return { started: true, ready: this.#ready };
  }

  async dispose(): Promise<void> {
    this.#disposed = true;
    const client = this.#client;
    this.#client = undefined;
    if (client === undefined) return;
    // Graceful shutdown mirrors the kernel's repl.eval.dispose without a
    // result envelope; run() then settles and the process exits itself.
    await client.shutdown().catch(() => {});
  }

  #selfTerminate(): void {
    // Exit on the NEXT macrotask, after the current one's microtasks flushed:
    // an in-flight RPC reply (disposeRepl / the kernel's dispose result) is
    // sent by worker-actor in the microtask phase after the handler resolves,
    // and setTimeout(0) callbacks run only once that phase is complete. A
    // queueMicrotask here would race the reply and kill the child mid-answer.
    setTimeout(() => Deno.exit(this.#error === undefined ? 0 : 1), 0);
  }
})();

serveProcess(rpc);
