import { type Deferred, replEvalDeferred, ReplEvalQueue } from "./repl_eval_queue.ts";
import { connectIpcWebSocket, type IpcWebSocket } from "../shared/ipc_websocket.ts";
import {
  decodeReplEvalEnvelope,
  encodeReplEvalEnvelope,
  REPL_EVAL_WEBSOCKET_PATH,
  type ReplEvalEnvelope,
  type ReplEvalErrorPayload,
  type ReplEvalInputRequestPayload,
  ReplEvalMessageType,
  ReplEvalProtocolError,
  type ReplEvalResultPayload,
  requireInteger,
  requirePayloadRecord,
  requireString,
} from "./protocol.ts";
import {
  ReplActor,
  type ReplActorEvent,
  type ReplActorInputRequest,
  type ReplActorResult,
} from "./repl_actor.ts";

const QUEUE_CAPACITY = 64;
const STARTUP_TIMEOUT_MS = 10_000;
const SHUTDOWN_TIMEOUT_MS = 5_000;

export interface ReplClientOptions {
  address: string;
  sessionId: string;
  generation: number;
  credential?: string;
}

interface ActiveExecution {
  executionId: string;
  controller: AbortController;
  task: Promise<void>;
}

interface OutboundItem {
  envelope: Omit<ReplEvalEnvelope, "version">;
  sent: Deferred<void>;
}

interface EventItem {
  event: ReplActorEvent;
  handled: Deferred<void>;
}

interface InputWaiter {
  executionId: string;
  reply: Deferred<string>;
  cleanup(): void;
}

/** Single owner for the actor, WebSocket, pumps, pending input, and active execution. */
export class ReplClient {
  readonly #options: ReplClientOptions;
  readonly #inbound = new ReplEvalQueue<ReplEvalEnvelope>(QUEUE_CAPACITY);
  readonly #outbound = new ReplEvalQueue<OutboundItem>(QUEUE_CAPACITY);
  readonly #events = new ReplEvalQueue<EventItem>(QUEUE_CAPACITY);
  readonly #ready = replEvalDeferred<void>();
  readonly #completion = replEvalDeferred<void>();
  readonly #inputWaiters = new Map<string, InputWaiter>();
  readonly #ownedTasks = new Set<Promise<void>>();
  #socket: IpcWebSocket | undefined;
  #actor: ReplActor | undefined;
  #active: ActiveExecution | undefined;
  #shutdownTask: Promise<void> | undefined;
  #readyReceived = false;
  #closeExpected = false;

  constructor(options: ReplClientOptions) {
    if (options.address.length === 0 || options.sessionId.length === 0) {
      throw new TypeError("The REPL IPC address and session id are required.");
    }
    if (!Number.isSafeInteger(options.generation) || options.generation < 0) {
      throw new TypeError("The REPL generation must be a non-negative integer.");
    }
    this.#options = options;
    this.#completion.promise.catch(() => {});
  }

  async run(): Promise<void> {
    try {
      await this.#start();
      await this.#completion.promise;
    } catch (error) {
      await this.#shutdown(error, false);
      throw error;
    }
  }

  async #start(): Promise<void> {
    this.#actor = await ReplActor.create({
      input: (request, signal) => this.#requestInput(request, signal),
      onDeath: (reason) => this.#fail(reason),
    });
    await this.#openSocket();
    this.#own(this.#outboundPump());
    this.#own(this.#eventPump());
    this.#own(this.#inboundPump());

    await this.#send({
      type: ReplEvalMessageType.hello,
      correlationId: this.#options.sessionId,
      payload: {
        sessionId: this.#options.sessionId,
        generation: this.#options.generation,
        ...(this.#options.credential === undefined ? {} : { credential: this.#options.credential }),
      },
    });
    await withTimeout(
      this.#ready.promise,
      STARTUP_TIMEOUT_MS,
      "Timed out waiting for repl.eval.ready.",
    );
  }

  async #openSocket(): Promise<void> {
    const socket = await connectIpcWebSocket(
      this.#options.address,
      REPL_EVAL_WEBSOCKET_PATH,
      this.#options.credential,
    );
    this.#socket = socket;
    socket.onMessage = (text) => this.#receive(text);
    socket.onClose = () => {
      if (!this.#closeExpected) {
        this.#fail(new Error("The REPL eval WebSocket closed unexpectedly."));
      }
    };
    socket.onError = (error) => this.#fail(error);
  }

  #receive(data: unknown): void {
    try {
      if (typeof data !== "string") {
        throw new ReplEvalProtocolError(
          "invalid_frame",
          "The REPL eval WebSocket only accepts text messages.",
        );
      }
      const envelope = decodeReplEvalEnvelope(data);
      if (!this.#inbound.tryEnqueue(envelope)) {
        throw new ReplEvalProtocolError(
          "inbound_backpressure",
          "The bounded REPL eval inbound queue is full.",
          envelope.correlationId,
        );
      }
    } catch (error) {
      if (
        error instanceof ReplEvalProtocolError &&
        error.code === "unknown_message_type"
      ) {
        this.#own(this.#sendError(error, undefined, false));
        return;
      }
      this.#fail(error);
    }
  }

  async #inboundPump(): Promise<void> {
    while (true) {
      const envelope = await this.#inbound.dequeue();
      try {
        await this.#handleEnvelope(envelope);
      } catch (error) {
        const protocolError = error instanceof ReplEvalProtocolError
          ? error
          : new ReplEvalProtocolError(
            "message_failed",
            errorMessage(error),
            envelope.correlationId,
          );
        await this.#sendError(protocolError, undefined, false);
      }
    }
  }

  async #handleEnvelope(envelope: ReplEvalEnvelope): Promise<void> {
    switch (envelope.type) {
      case ReplEvalMessageType.ready:
        this.#handleReady(envelope);
        return;
      case ReplEvalMessageType.execute:
        this.#handleExecute(envelope);
        return;
      case ReplEvalMessageType.cancel:
        this.#handleCancel(envelope);
        return;
      case ReplEvalMessageType.inputReply:
        this.#handleInputReply(envelope);
        return;
      case ReplEvalMessageType.dispose:
        await this.#handleDispose(envelope);
        return;
      default:
        throw new ReplEvalProtocolError(
          "unexpected_message_type",
          `The Deno REPL cannot receive '${envelope.type}'.`,
          envelope.correlationId,
        );
    }
  }

  #handleReady(envelope: ReplEvalEnvelope): void {
    const payload = requirePayloadRecord(envelope);
    const sessionId = requireString(payload, "sessionId", envelope);
    const generation = requireInteger(payload, "generation", envelope);
    if (sessionId !== this.#options.sessionId || generation !== this.#options.generation) {
      throw new ReplEvalProtocolError(
        "identity_mismatch",
        "The REPL eval ready identity does not match this process.",
        envelope.correlationId,
      );
    }
    if (this.#readyReceived) {
      throw new ReplEvalProtocolError(
        "duplicate_ready",
        "The REPL eval channel is already ready.",
        envelope.correlationId,
      );
    }
    this.#readyReceived = true;
    this.#ready.resolve();
  }

  #handleExecute(envelope: ReplEvalEnvelope): void {
    this.#requireReady(envelope);
    const payload = requirePayloadRecord(envelope);
    const executionId = requireString(payload, "executionId", envelope);
    const code = payload.code;
    if (typeof code !== "string") {
      throw new ReplEvalProtocolError(
        "invalid_payload",
        "repl.eval.execute requires string code.",
        envelope.correlationId,
      );
    }
    if (envelope.correlationId !== executionId) {
      throw new ReplEvalProtocolError(
        "correlation_mismatch",
        "The execute correlation id must equal its execution id.",
        envelope.correlationId,
      );
    }
    if (this.#active !== undefined) {
      throw new ReplEvalProtocolError(
        "repl_busy",
        `Execution '${this.#active.executionId}' is still active.`,
        envelope.correlationId,
      );
    }
    const controller = new AbortController();
    const active: ActiveExecution = {
      executionId,
      controller,
      task: Promise.resolve(),
    };
    this.#active = active;
    active.task = this.#runExecution(active, code);
    this.#own(active.task);
  }

  async #runExecution(active: ActiveExecution, code: string): Promise<void> {
    let fatal = false;
    try {
      let result: ReplActorResult | undefined;
      for await (
        const item of this.#actorOrThrow().execute(
          active.executionId,
          code,
          active.controller.signal,
        )
      ) {
        if (item.type === "terminal") {
          if (result !== undefined) {
            throw new Error(`Execution '${active.executionId}' produced two terminal events.`);
          }
          result = item.result;
        } else {
          if (result !== undefined) {
            throw new Error(
              `Execution '${active.executionId}' produced output after its terminal.`,
            );
          }
          await this.#enqueueEvent(item);
        }
      }
      if (result === undefined) {
        throw new Error(`Execution '${active.executionId}' ended without a terminal event.`);
      }
      fatal = result.fatal === true;
      await this.#sendExecutionTerminal(active, result);
    } catch (error) {
      if (active.controller.signal.aborted) {
        await this.#send({
          type: ReplEvalMessageType.cancelled,
          correlationId: active.executionId,
          payload: { executionId: active.executionId },
        });
      } else {
        fatal = true;
        await this.#sendError(
          new ReplEvalProtocolError("actor_failed", errorMessage(error), active.executionId),
          active.executionId,
          true,
        );
      }
    } finally {
      if (this.#active === active) {
        this.#active = undefined;
      }
    }
    if (fatal) {
      this.#fail(new Error(`Execution '${active.executionId}' left the REPL unusable.`));
    }
  }

  async #sendExecutionTerminal(
    active: ActiveExecution,
    result: ReplActorResult,
  ): Promise<void> {
    if (result.cancelled === true || active.controller.signal.aborted) {
      await this.#send({
        type: ReplEvalMessageType.cancelled,
        correlationId: active.executionId,
        payload: { executionId: active.executionId },
      });
      return;
    }
    if (result.ok) {
      const payload: ReplEvalResultPayload = {
        executionId: active.executionId,
        ...(result.data === undefined ? {} : { value: result.data }),
      };
      await this.#send({
        type: ReplEvalMessageType.result,
        correlationId: active.executionId,
        payload,
      });
      return;
    }
    await this.#send({
      type: ReplEvalMessageType.error,
      correlationId: active.executionId,
      payload: {
        executionId: active.executionId,
        code: "execution_failed",
        message: result.error ?? "The Deno REPL execution failed.",
        ...(result.fatal === undefined ? {} : { fatal: result.fatal }),
      } satisfies ReplEvalErrorPayload,
    });
  }

  #handleCancel(envelope: ReplEvalEnvelope): void {
    this.#requireReady(envelope);
    const payload = requirePayloadRecord(envelope);
    const executionId = requireString(payload, "executionId", envelope);
    const active = this.#active;
    if (active === undefined || active.executionId !== executionId) {
      throw new ReplEvalProtocolError(
        "execution_not_found",
        `Execution '${executionId}' is not active.`,
        envelope.correlationId,
      );
    }
    active.controller.abort(new DOMException("Execution cancelled by the kernel.", "AbortError"));
  }

  #handleInputReply(envelope: ReplEvalEnvelope): void {
    const payload = requirePayloadRecord(envelope);
    const executionId = requireString(payload, "executionId", envelope);
    const requestId = requireString(payload, "requestId", envelope);
    if (requestId !== envelope.correlationId) {
      throw new ReplEvalProtocolError(
        "correlation_mismatch",
        "The input reply correlation id must equal its request id.",
        envelope.correlationId,
      );
    }
    const value = payload.value;
    if (typeof value !== "string") {
      throw new ReplEvalProtocolError(
        "invalid_payload",
        "repl.eval.inputReply requires a string value.",
        envelope.correlationId,
      );
    }
    const waiter = this.#inputWaiters.get(requestId);
    if (waiter === undefined || waiter.executionId !== executionId) {
      throw new ReplEvalProtocolError(
        "input_request_not_found",
        `Input request '${requestId}' is not pending.`,
        envelope.correlationId,
      );
    }
    this.#inputWaiters.delete(requestId);
    waiter.cleanup();
    waiter.reply.resolve(value);
  }

  async #requestInput(request: ReplActorInputRequest, _signal: AbortSignal): Promise<string> {
    const active = this.#active;
    if (active === undefined || active.executionId !== request.executionId) {
      throw new Error(`Execution '${request.executionId}' is not active.`);
    }
    // The input callback channel is asynchronous relative to the event stream, so
    // it is sent on the outbound channel independently of the event pump. Input
    // does not participate in the event sequence: output events are ordered by the
    // event pump FIFO alone, and the server routes input by correlation id.
    const requestId = crypto.randomUUID();
    const reply = replEvalDeferred<string>();
    const onAbort = (): void => {
      this.#inputWaiters.delete(requestId);
      reply.reject(active.controller.signal.reason);
    };
    active.controller.signal.addEventListener("abort", onAbort, { once: true });
    const waiter: InputWaiter = {
      executionId: active.executionId,
      reply,
      cleanup: () => active.controller.signal.removeEventListener("abort", onAbort),
    };
    this.#inputWaiters.set(requestId, waiter);
    const payload: ReplEvalInputRequestPayload = {
      executionId: active.executionId,
      sequence: request.sequence,
      requestId,
      prompt: request.prompt,
      password: request.password,
    };
    try {
      await this.#send({
        type: ReplEvalMessageType.inputRequest,
        correlationId: requestId,
        payload,
      });
      return await reply.promise;
    } finally {
      this.#inputWaiters.delete(requestId);
      waiter.cleanup();
    }
  }

  async #enqueueEvent(event: ReplActorEvent): Promise<void> {
    const handled = replEvalDeferred<void>();
    await this.#events.enqueue({ event, handled });
    return handled.promise;
  }

  async #eventPump(): Promise<void> {
    while (true) {
      const item = await this.#events.dequeue();
      try {
        const active = this.#active;
        if (active === undefined || active.executionId !== item.event.executionId) {
          throw new Error(`Output arrived for inactive execution '${item.event.executionId}'.`);
        }
        await this.#send(this.#eventEnvelope(item.event));
        item.handled.resolve();
      } catch (error) {
        item.handled.reject(error);
        throw error;
      }
    }
  }

  #eventEnvelope(event: ReplActorEvent): Omit<ReplEvalEnvelope, "version"> {
    const common = {
      correlationId: event.executionId,
      payload: { ...event, type: undefined },
    };
    switch (event.type) {
      case "console":
        return { ...common, type: ReplEvalMessageType.console };
      case "display":
        return { ...common, type: ReplEvalMessageType.display };
      case "updateDisplay":
        return { ...common, type: ReplEvalMessageType.updateDisplay };
      case "clearOutput":
        return { ...common, type: ReplEvalMessageType.clearOutput };
    }
  }

  async #outboundPump(): Promise<void> {
    while (true) {
      const item = await this.#outbound.dequeue();
      try {
        const socket = this.#socket;
        if (socket === undefined || !socket.isOpen) {
          throw new Error("The REPL eval WebSocket is not open.");
        }
        socket.send(encodeReplEvalEnvelope(item.envelope));
        item.sent.resolve();
      } catch (error) {
        item.sent.reject(error);
        throw error;
      }
    }
  }

  async #send(envelope: Omit<ReplEvalEnvelope, "version">): Promise<void> {
    const sent = replEvalDeferred<void>();
    await this.#outbound.enqueue({ envelope, sent });
    return sent.promise;
  }

  async #sendError(
    error: ReplEvalProtocolError,
    executionId: string | undefined,
    fatal: boolean,
  ): Promise<void> {
    await this.#send({
      type: ReplEvalMessageType.error,
      correlationId: error.correlationId,
      payload: {
        ...(executionId === undefined ? {} : { executionId }),
        code: error.code,
        message: error.message,
        ...(fatal ? { fatal: true } : {}),
      } satisfies ReplEvalErrorPayload,
    });
  }

  async #handleDispose(envelope: ReplEvalEnvelope): Promise<void> {
    const payload = requirePayloadRecord(envelope);
    const sessionId = requireString(payload, "sessionId", envelope);
    const generation = requireInteger(payload, "generation", envelope);
    if (sessionId !== this.#options.sessionId || generation !== this.#options.generation) {
      throw new ReplEvalProtocolError(
        "identity_mismatch",
        "The dispose identity does not match this REPL actor.",
        envelope.correlationId,
      );
    }
    await this.#shutdown(undefined, true, envelope.correlationId);
  }

  #shutdown(
    reason: unknown,
    graceful: boolean,
    disposeCorrelationId?: string,
  ): Promise<void> {
    if (this.#shutdownTask !== undefined) {
      return this.#shutdownTask;
    }
    this.#shutdownTask = this.#finishShutdown(reason, graceful, disposeCorrelationId);
    return this.#shutdownTask;
  }

  async #finishShutdown(
    reason: unknown,
    graceful: boolean,
    disposeCorrelationId?: string,
  ): Promise<void> {
    const deadline = Date.now() + SHUTDOWN_TIMEOUT_MS;
    this.#active?.controller.abort(
      reason ?? new DOMException("The REPL actor is disposing.", "AbortError"),
    );
    for (const waiter of this.#inputWaiters.values()) {
      waiter.cleanup();
      waiter.reply.reject(reason ?? new Error("The REPL actor is disposing."));
    }
    this.#inputWaiters.clear();

    const actor = this.#actor;
    try {
      if (actor !== undefined) {
        await beforeDeadline(actor.disposeRepl(), deadline);
      }
      const activeTask = this.#active?.task;
      if (activeTask !== undefined) {
        await beforeDeadline(activeTask, deadline);
      }
    } catch (error) {
      reason ??= error;
      graceful = false;
    } finally {
      await actor?.dispose();
      this.#actor = undefined;
    }

    if (graceful && disposeCorrelationId !== undefined) {
      await this.#send({
        type: ReplEvalMessageType.result,
        correlationId: disposeCorrelationId,
        payload: {} satisfies ReplEvalResultPayload,
      });
    }

    this.#closeExpected = true;
    this.#socket?.close(1000, graceful ? "REPL disposed" : "REPL failed");
    const closeReason = reason ?? new Error("The REPL actor is disposed.");
    this.#inbound.close(closeReason);
    this.#events.close(closeReason);
    this.#outbound.close(closeReason);
    if (graceful) {
      this.#completion.resolve();
    } else {
      this.#completion.reject(reason ?? new Error("The REPL actor failed."));
    }
  }

  #requireReady(envelope: ReplEvalEnvelope): void {
    if (!this.#readyReceived) {
      throw new ReplEvalProtocolError(
        "not_ready",
        "The REPL eval handshake has not completed.",
        envelope.correlationId,
      );
    }
  }

  #actorOrThrow(): ReplActor {
    if (this.#actor === undefined) {
      throw new Error("The Deno REPL actor is unavailable.");
    }
    return this.#actor;
  }

  #own(task: Promise<void>): void {
    this.#ownedTasks.add(task);
    task.then(
      () => this.#ownedTasks.delete(task),
      (error) => {
        this.#ownedTasks.delete(task);
        this.#fail(error);
      },
    );
  }

  #fail(reason: unknown): void {
    void this.#shutdown(reason, false).catch(() => {});
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
  const timeout = replEvalDeferred<T>();
  const timer = setTimeout(() => timeout.reject(new Error(message)), timeoutMs);
  try {
    return await Promise.race([promise, timeout.promise]);
  } finally {
    clearTimeout(timer);
  }
}

function beforeDeadline<T>(promise: Promise<T>, deadline: number): Promise<T> {
  const remaining = Math.max(1, deadline - Date.now());
  return withTimeout(promise, remaining, "The Deno REPL shutdown timed out.");
}
