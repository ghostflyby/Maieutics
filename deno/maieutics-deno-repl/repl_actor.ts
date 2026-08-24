import { type ActorHandle, type Remote, spawn } from "@ghostflyby/worker-actor";
import { failInputMailbox, mailboxFor } from "./input_mailbox.ts";
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
  /** Called when the REPL worker dies through a crash or handshake failure. */
  onDeath(reason: unknown): void;
}

/** A worker->main input request delivered over the blocking input mailbox channel. */
export interface ReplActorInputRequest {
  sab: SharedArrayBuffer;
  kind: "prompt" | "confirm" | "alert";
  prompt: string;
}

/** Handles one blocking input request; the handler writes the answer into the mailbox. */
export type ReplActorInputHandler = (request: ReplActorInputRequest) => void | Promise<void>;

/** Label of the worker-actor link that carries blocking input requests. */
export const INPUT_MAILBOX_LINK_LABEL = "maieutics-input-mailbox";

type ReplWorkerActor = Remote<typeof ReplWorker.rpc> & ActorHandle;

/**
 * Owns the worker-actor proxy and the blocking input mailbox link. The worker
 * sends input requests over the mailbox link (a dedicated MessageChannel whose
 * worker side is registered via serveWorker's onLink); this side keeps the
 * peer MessagePort and forwards each request to the registered handler, which
 * performs the async Jupyter round trip and writes the answer back into the
 * shared mailbox.
 */
export class ReplActor {
  readonly #actor: ReplWorkerActor;
  readonly #mailboxPort: MessagePort;
  #inputHandler: ReplActorInputHandler | undefined;
  #disposed = false;

  private constructor(actor: ReplWorkerActor, mailboxPort: MessagePort) {
    this.#actor = actor;
    this.#mailboxPort = mailboxPort;
  }

  static async create(callbacks: ReplActorCallbacks): Promise<ReplActor> {
    const worker = new Worker(new URL("./repl_worker.ts", import.meta.url), { type: "module" });
    const actor = await spawn<typeof ReplWorker.rpc>(worker, {
      signal: AbortSignal.timeout(10_000),
      onDeath: callbacks.onDeath,
    });
    // Establish the blocking input mailbox link: a dedicated MessageChannel
    // between the main thread and the worker. The worker side is registered by
    // repl_worker's serveWorker onLink (by label); this side keeps port2 and
    // reads the worker's LinkFrame values.
    const { port1, port2 } = new MessageChannel();
    worker.postMessage(
      { type: "__link", label: INPUT_MAILBOX_LINK_LABEL, port: port1 },
      { transfer: [port1] },
    );
    const owner = new ReplActor(actor, port2);
    try {
      await actor.initialize();
      return owner;
    } catch (error) {
      await actor.dispose();
      throw error;
    }
  }

  /** Register the handler for worker input requests (called by the client). */
  setInputHandler(handler: ReplActorInputHandler): void {
    if (this.#disposed) return;
    this.#inputHandler = handler;
    this.#mailboxPort.onmessage = (e: MessageEvent) => {
      const frame = e.data as { type: string; value?: ReplActorInputRequest };
      if (frame.type !== "__link-value" || frame.value === undefined) {
        return;
      }
      const request = frame.value;
      if (request.sab === undefined || typeof request.kind !== "string") {
        return;
      }
      const handler = this.#inputHandler;
      if (handler === undefined) {
        return;
      }
      // Errors are reported by writing an error into the mailbox (the blocked
      // worker's Atomics.wait must not hang forever on a failed request).
      Promise.resolve(handler(request)).catch((error) => {
        failInputMailbox(mailboxFor(request.sab), error);
      });
    };
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
    this.#mailboxPort.onmessage = null;
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
