import { type Deferred, replEvalDeferred, ReplEvalQueue } from "./repl_eval_queue.ts";
import { connectIpcWebSocket, type IpcWebSocket } from "../shared/ipc_websocket.ts";
import { type CommClient, CommKind, type CommMessage, connectComm } from "./comm.ts";
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
  encodeOutputFrame,
  type OutputFrame,
  REPL_OUTPUT_MAX_MESSAGE_BYTES,
  REPL_OUTPUT_WEBSOCKET_PATH,
} from "./output_protocol.ts";
import {
  ReplActor,
  type ReplActorEvent,
  type ReplActorInputRequest,
  type ReplActorResult,
} from "./repl_actor.ts";
import {
  failInputMailbox,
  type InputMailbox,
  InputMailboxKind,
  interruptInputMailbox,
  mailboxFor,
  writeInputMailboxAck,
  writeInputMailboxAnswer,
  writeInputMailboxBoolean,
} from "./input_mailbox.ts";

const QUEUE_CAPACITY = 64;
const STARTUP_TIMEOUT_MS = 10_000;
const SHUTDOWN_TIMEOUT_MS = 5_000;

export interface ReplClientOptions {
  address: string;
  sessionId: string;
  generation: number;
  credential?: string;
  /** Optional readiness hook invoked once when the eval hello/ready handshake
   * completes. Additive: the host-derived process entry uses it to surface the
   * eval channel state through its actor surface; the kernel-derived entry and
   * every existing caller omit it. */
  onReady?: () => void;
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
  mailbox: InputMailbox;
  cleanup(): void;
}

function isCommEvent(
  event: ReplActorEvent,
): event is ReplActorEvent & { type: "commOpen" | "commMsg" | "commClose" } {
  return event.type === "commOpen" || event.type === "commMsg" || event.type === "commClose";
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
  #outputSocket: IpcWebSocket | undefined;
  #actor: ReplActor | undefined;
  #active: ActiveExecution | undefined;
  #commClient: CommClient | undefined;
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

  /**
   * Graceful external shutdown for process-management callers (the host-derived
   * process entry). Equivalent to a kernel repl.eval.dispose except that no
   * dispose result envelope is sent (no kernel is awaiting one). Idempotent:
   * the second call returns the same completion task. The kernel-derived entry
   * never calls this — it relies on the kernel's dispose message.
   */
  shutdown(): Promise<void> {
    return this.#shutdown(undefined, true);
  }

  async #start(): Promise<void> {
    this.#actor = await ReplActor.create({
      onDeath: (reason) => this.#fail(reason),
    });
    this.#actor.setInputHandler((request) => this.#requestInput(request));
    await this.#openSocket();
    await this.#openOutputSocket();
    this.#own(this.#openComm().catch((error) => this.#failComm(error)));
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

  /**
   * Opens the dedicated output endpoint. The output WebSocket is half-duplex:
   * this side only sends binary output frames (console/display/updateDisplay/
   * clearOutput) and never expects messages back. A failure of the output
   * channel is a REPL failure — output is an integral part of an execution,
   * so losing the channel is treated like losing the eval channel (degraded
   * fallback is deferred to a later phase).
   */
  async #openOutputSocket(): Promise<void> {
    const socket = await connectIpcWebSocket(
      this.#options.address,
      REPL_OUTPUT_WEBSOCKET_PATH,
      this.#options.credential,
      { maxMessageBytes: REPL_OUTPUT_MAX_MESSAGE_BYTES },
    );
    this.#outputSocket = socket;
    socket.onMessage = (_data) => {
      // Process -> host only: the host never sends output frames. Any inbound
      // data is a protocol violation worth surfacing on the error path.
      this.#fail(
        new Error(
          "The REPL output WebSocket received an unexpected message from the host.",
        ),
      );
    };
    socket.onClose = () => {
      if (!this.#closeExpected) {
        this.#fail(new Error("The REPL output WebSocket closed unexpectedly."));
      }
    };
    socket.onError = (error) => this.#fail(error);
    // The first frame is a JSON hello declaring the session and generation so the
    // host can attribute the connection to the exact (session, generation) slot —
    // mirroring the comm endpoint's hello (AGENTS.md invariant 27: endpoints are
    // deliberate API surface, and a restart must never leak a previous generation's
    // output connection into the new one).
    socket.send(
      JSON.stringify({
        sessionId: this.#options.sessionId,
        generation: this.#options.generation,
      }),
    );
  }

  async #openComm(): Promise<void> {
    const client = await connectComm(
      this.#options.address,
      this.#options.sessionId,
      this.#options.credential,
    );
    this.#commClient = client;
    client.onMessage = (message) => this.#deliverComm(message);
    client.onClose = () => {
      if (this.#commClient === client) this.#commClient = undefined;
    };
    client.onError = () => {
      if (this.#commClient === client) this.#commClient = undefined;
    };
  }

  #failComm(_error: unknown): void {
    // The comm channel is auxiliary: a failure to open it must not take down the
    // REPL eval channel or the worker. Script comm calls fail with a clear error.
    this.#commClient = undefined;
  }

  #deliverComm(message: CommMessage): void {
    const actor = this.#actor;
    if (actor === undefined) return;
    actor
      .deliverComm({
        kind: message.kind,
        commId: message.commId,
        targetName: message.targetName,
        data: message.data,
        buffers: message.buffers,
      })
      .catch((error) => this.#fail(error));
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
    this.#options.onReady?.();
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
    // Write the answer into the shared mailbox; this wakes the worker thread
    // that is blocked in Atomics.wait. The writer picks the slot by the kind
    // code the worker stored when it sent the request, so a prompt answer can
    // never be mistaken for a confirm boolean.
    const kindCode = waiter.mailbox.kind[0];
    if (kindCode === InputMailboxKind.confirm) {
      writeInputMailboxBoolean(waiter.mailbox, /^(?:1|true|yes|y)$/i.test(value.trim()));
    } else if (kindCode === InputMailboxKind.alert) {
      writeInputMailboxAck(waiter.mailbox);
    } else {
      writeInputMailboxAnswer(waiter.mailbox, "prompt", value);
    }
  }

  /**
   * Handles one blocking input request from the worker. The worker thread is
   * frozen in Atomics.wait on the shared mailbox; this (main-thread) side
   * performs the async Jupyter round trip and writes the answer into the
   * mailbox, waking the worker. On abort, the mailbox is marked interrupted
   * instead, so the blocked worker wakes early and throws AbortError.
   */
  async #requestInput(request: ReplActorInputRequest): Promise<void> {
    const active = this.#active;
    if (active === undefined) {
      throw new Error("No execution is active for an input request.");
    }
    const mailbox = mailboxFor(request.sab);
    const requestId = crypto.randomUUID();
    const onAbort = (): void => {
      this.#inputWaiters.delete(requestId);
      interruptInputMailbox(mailbox);
    };
    active.controller.signal.addEventListener("abort", onAbort, { once: true });
    this.#inputWaiters.set(requestId, {
      executionId: active.executionId,
      mailbox,
      cleanup: () => active.controller.signal.removeEventListener("abort", onAbort),
    });
    const payload: ReplEvalInputRequestPayload = {
      executionId: active.executionId,
      sequence: 0,
      requestId,
      prompt: request.prompt,
      password: request.kind === "prompt",
    };
    try {
      await this.#send({
        type: ReplEvalMessageType.inputRequest,
        correlationId: requestId,
        payload,
      });
    } catch (error) {
      // The round trip could not even start: mark the mailbox failed so the
      // blocked worker does not hang forever.
      failInputMailbox(mailbox, error);
      this.#inputWaiters.delete(requestId);
      throw error;
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
        if (isCommEvent(item.event)) {
          await this.#sendComm(item.event);
          item.handled.resolve();
          continue;
        }
        const active = this.#active;
        if (active === undefined || active.executionId !== item.event.executionId) {
          throw new Error(`Output arrived for inactive execution '${item.event.executionId}'.`);
        }
        this.#sendOutputFrame(this.#outputFrame(item.event));
        item.handled.resolve();
      } catch (error) {
        item.handled.reject(error);
        throw error;
      }
    }
  }

  async #sendComm(
    event: ReplActorEvent & { type: "commOpen" | "commMsg" | "commClose" },
  ): Promise<void> {
    const client = this.#commClient;
    if (client === undefined) {
      throw new Error("The comm WebSocket is not open.");
    }
    const kind = event.type === "commOpen"
      ? CommKind.Open
      : event.type === "commClose"
      ? CommKind.Close
      : CommKind.Message;
    await client.send({
      kind,
      commId: event.commId,
      targetName: event.targetName,
      data: event.data,
      ...(event.metadata === undefined ? {} : { metadata: event.metadata }),
      buffers: event.buffers,
    });
  }

  #sendOutputFrame(frame: OutputFrame): void {
    const socket = this.#outputSocket;
    if (socket === undefined || !socket.isOpen) {
      throw new Error("The REPL output WebSocket is not open.");
    }
    socket.send(encodeOutputFrame(frame));
  }

  #outputFrame(event: ReplActorEvent): OutputFrame {
    switch (event.type) {
      case "console":
        // A console event's stream selects the stdout or stderr frame type.
        return {
          type: event.stream === "stderr" ? 1 : 0,
          seq: event.sequence,
          executionId: event.executionId,
          text: event.text,
        };
      case "display":
      case "updateDisplay":
        return {
          type: 2,
          seq: event.sequence,
          executionId: event.executionId,
          data: event.data,
          ...(event.metadata === undefined ? {} : { metadata: event.metadata }),
          ...(event.displayId === undefined ? {} : { displayId: event.displayId }),
          isUpdate: event.type === "updateDisplay",
        };
      case "clearOutput":
        return { type: 3, seq: event.sequence, executionId: event.executionId, wait: event.wait };
      case "commOpen":
      case "commMsg":
      case "commClose":
        throw new Error("Comm events are delivered over the comm WebSocket, not repl.eval.");
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
      // The blocked worker must not hang forever: mark its mailbox interrupted
      // so the Atomics.wait wakes and the worker surfaces the cancellation.
      interruptInputMailbox(waiter.mailbox);
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
    this.#outputSocket?.close(1000, graceful ? "REPL disposed" : "REPL failed");
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
