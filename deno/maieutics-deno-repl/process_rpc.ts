/**
 * REPL process actor surface (migration skeleton, ADR 0020).
 *
 * This module is the `serveProcess` surface of the REPL child that the plugin
 * host derives via worker-actor's `spawnProcess` (see `process_main.ts`). It is
 * the first implementation slice of ADR 0020: the host becomes the spawner of
 * the REPL process and learns its pid through the actor handshake. The shape
 * deliberately mirrors the existing in-worker REPL actor
 * (`repl_worker.ts`/`repl_actor.ts`, `ReplActorResult`) so the later full
 * migration can move the real `initialize`/`execute`/`disposeRepl` semantics
 * here without changing the host-side protocol.
 *
 * This skeleton does NOT run Aves. `execute` is a placeholder that echoes the
 * code back with `ok: true`; the permission broker is attached once the host
 * reports this pid over the control bus (the kernel registers the effective
 * policy for it, B2/B3) and the static permission shell passed at spawn is only
 * the fallback baseline, not the security boundary (ADR 0020 decision 1).
 *
 * Env contract (documented draft for the .NET side, see `host_repl_protocol.ts`):
 * - `MAIEUTICS_REPL_SESSION`    session id bound to this REPL child.
 * - `MAIEUTICS_REPL_GENERATION` generation number of the session.
 * - `DENO_PERMISSION_BROKER_PATH` broker socket the child's permission checks
 *   go through once the host reports the pid (B3 forwards the address the
 *   kernel handed the host under MAIEUTICS_PERMISSION_BROKER).
 */

import { serveProcess } from "@ghostflyby/worker-actor";
import type { ReplActorResult } from "./repl_actor.ts";

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
  async pregestPid(): Promise<number> {
    return Deno.pid;
  },

  /**
   * Returns the forwarded broker address this process was launched with
   * (DENO_PERMISSION_BROKER_PATH). Present for the host-side env-forwarding
   * tests; never consulted by the REPL itself — the broker is attached by the
   * kernel through the pid the host reported, this process only carries the
   * socket address.
   */
  async pregestBrokerPath(): Promise<string> {
    return Deno.env.get(BROKER_PATH_ENV) ?? "";
  },

  /**
   * Initializes the REPL process actor. Returns the self-reported pid plus the
   * host-bound session identity. The host calls this immediately after the
   * pregest pid report so the broker policy for this pid is already registered
   * (B2 then forwards the pid to the kernel broker and control channel).
   */
  async initialize(): Promise<ReplProcessInfo> {
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
   * Placeholder execution. Does not run Aves (skeleton stage); it returns the
   * ReplActorResult-shaped envelope so the host-side call site already matches
   * the future real execution surface.
   */
  async execute(code: string): Promise<ReplActorResult> {
    if (typeof code !== "string") {
      throw new TypeError("execute expects a code string.");
    }
    return { ok: true, data: `skeleton: ${code}` };
  },
};

serveProcess(rpc);
