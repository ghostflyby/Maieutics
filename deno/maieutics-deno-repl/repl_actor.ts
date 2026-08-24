import { type ActorHandle, type Remote, spawn } from "@ghostflyby/worker-actor";
import type * as ReplWorker from "./repl_worker.ts";
import type { ReplMediaBundle } from "./protocol.ts";

export type ReplActorEvent =
  | {
    type: "console";
    executionId: string;
    sequence: number;
    stream: "stdout" | "stderr";
    text: string;
  }
  | {
    type: "display" | "updateDisplay";
    executionId: string;
    sequence: number;
    displayId?: string;
    data: ReplMediaBundle;
    metadata?: Record<string, unknown>;
  }
  | {
    type: "clearOutput";
    executionId: string;
    sequence: number;
    wait: boolean;
  }
  | {
    type: "commOpen" | "commMsg" | "commClose";
    executionId: string;
    sequence: number;
    commId: string;
    targetName?: string;
    data?: unknown;
    buffers: Uint8Array[];
  };

export interface ReplActorInputRequest {
  executionId: string;
  sequence: number;
  prompt: string;
  password: boolean;
}

export interface ReplActorResult {
  ok: boolean;
  data?: unknown;
  error?: string;
  fatal?: boolean;
  cancelled?: boolean;
}

export type ReplActorStreamEvent =
  | ReplActorEvent
  | { type: "terminal"; executionId: string; result: ReplActorResult };

export interface ReplActorCallbacks {
  input(request: ReplActorInputRequest, signal: AbortSignal): Promise<string>;
  onDeath(reason: unknown): void;
}

type ReplWorkerActor = Remote<typeof ReplWorker.rpc> & ActorHandle;

/** Owns the worker-actor proxy and preserves callback references for its lifetime. */
export class ReplActor {
  readonly #actor: ReplWorkerActor;
  readonly #input: ReplActorCallbacks["input"];
  #disposed = false;

  private constructor(actor: ReplWorkerActor, callbacks: ReplActorCallbacks) {
    this.#actor = actor;
    this.#input = callbacks.input;
  }

  static async create(callbacks: ReplActorCallbacks): Promise<ReplActor> {
    const worker = new Worker(new URL("./repl_worker.ts", import.meta.url), { type: "module" });
    const actor = await spawn<typeof ReplWorker.rpc>(worker, {
      signal: AbortSignal.timeout(10_000),
      onDeath: callbacks.onDeath,
    });
    const owner = new ReplActor(actor, callbacks);
    try {
      await actor.initialize(owner.#input);
      return owner;
    } catch (error) {
      await actor.dispose();
      throw error;
    }
  }

  execute(
    executionId: string,
    code: string,
    signal: AbortSignal,
  ): AsyncIterable<ReplActorStreamEvent> {
    if (this.#disposed) {
      return failedStream(new Error("The Deno REPL actor is disposed."));
    }
    return this.#actor.execute(executionId, code, signal);
  }

  /** Gives Aves a cooperative cancellation and scope-release opportunity. */
  async disposeRepl(): Promise<void> {
    if (!this.#disposed) {
      await this.#actor.disposeRepl();
    }
  }

  /** Delivers a frontend comm message to the worker for dispatch to registered handlers. */
  async deliverComm(message: {
    kind: number;
    commId: string;
    targetName?: string;
    data?: unknown;
    buffers: Uint8Array[];
  }): Promise<void> {
    if (this.#disposed) {
      return;
    }
    await this.#actor.deliverComm(message);
  }

  /** Hard-stops the worker after cooperative disposal has completed or failed. */
  async dispose(): Promise<void> {
    if (this.#disposed) {
      return;
    }
    this.#disposed = true;
    await this.#actor.dispose();
  }
}

function failedStream(error: Error): AsyncIterable<never> {
  return {
    [Symbol.asyncIterator](): AsyncIterator<never> {
      return { next: () => Promise.reject(error) };
    },
  };
}
