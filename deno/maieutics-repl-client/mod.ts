/**
 * Client for the Maieutics REPL control channel.
 *
 * The kernel injects the channel address through `MAIEUTICS_REPL_IPC`, the
 * client module through `MAIEUTICS_REPL_CLIENT`, and the owning session id
 * through `MAIEUTICS_REPL_SESSION`. A single multiplexed WebSocket bus carries
 * tools, health, events, comm messages, and control messages under one
 * versioned envelope.
 *
 * The module namespace is the default client: `health`, `tools`, `events`,
 * and `comm` operate on the process's REPL connection. `connect()` creates
 * an additional independent client with the same shape.
 */

const ADDRESS_ENV = "MAIEUTICS_REPL_IPC";
const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const CREDENTIAL_ENV = "MAIEUTICS_REPL_CREDENTIAL";
const BUS_TIMEOUT_MS = 5_000;

interface Deferred<T> {
  promise: Promise<T>;
  resolve(value: T | PromiseLike<T>): void;
}

function deferred<T>(): Deferred<T> {
  let resolvePromise: Deferred<T>["resolve"] | undefined;
  const promise = new Promise<T>((resolve) => {
    resolvePromise = resolve;
  });
  if (resolvePromise === undefined) {
    throw new Error("The terminal promise resolver was not initialized.");
  }
  return { promise, resolve: resolvePromise };
}

import { type BusConnection, connectBus } from "../shared/bus.ts";
import { type ReplEnvelope } from "../shared/protocol.ts";

export interface ReplClientOptions {
  /** Unix-domain socket path or Windows loopback host:port of the control channel. */
  address?: string;
}

export interface ReplTools {
  /** Starts a tool call; progress arrives as "progress" events on the returned task. */
  start(
    name: string,
    args?: Record<string, unknown>,
    options?: { signal?: AbortSignal },
  ): ToolTask;
  /** Invokes a script-callable workspace tool and returns its structured result. */
  invoke(
    name: string,
    args?: Record<string, unknown>,
    options?: { signal?: AbortSignal },
  ): Promise<unknown>;
}

export interface ReplComm {
  /** Opens a comm channel. */
  open(commId: string, targetName?: string, data?: unknown): Promise<void>;
  /** Sends a message on an open comm channel, optionally with binary buffers. */
  msg(commId: string, data?: unknown, buffers?: Uint8Array[]): Promise<void>;
  /** Closes a comm channel. */
  close(commId: string): Promise<void>;
}

export interface ReplClient {
  /** Unix-domain socket path or Windows loopback host:port of the control channel. */
  readonly address: string;
  /** Script tool invocation. */
  tools: ReplTools;
  /** Bus message hub; subscribe with `addEventListener(type, handler)`. */
  events: EventTarget;
  /** Comm channel operations. */
  comm: ReplComm;

  /** Probes the kernel control channel over the multiplexed bus. */
  health(): Promise<string>;
}

interface ToolEnvelope {
  status?: string;
  code?: string;
  message?: string;
  value?: unknown;
}

/** Lifecycle vocabulary compatible with MCP task statuses. */
export type TaskStatus =
  | "pending"
  | "working"
  | "awaiting_input"
  | "paused"
  | "completed"
  | "failed"
  | "cancelled";

/** Tool progress pushed over the bus, keyed by the originating tool call. */
export interface ToolProgress {
  readonly correlationId: string;
  /** Current progress unit; with `total` it is a fraction, otherwise treated as 0-100. */
  readonly progress?: number;
  readonly total?: number;
  readonly stage?: string;
  readonly message?: string;
  readonly status?: TaskStatus;
  readonly data?: unknown;
}

/**
 * A started tool call. Awaitable (`await task`), an `EventTarget` for
 * standard "progress" `ProgressEvent`s, and always carries its own
 * `AbortController` (an external signal, if given, is linked into it).
 */
export class ToolTask<T = unknown> extends EventTarget implements PromiseLike<T> {
  /** Correlation id shared by the HTTP call, bus progress, and cancel. */
  readonly id: string;
  /** Always present; `abort.abort()` cancels the call. */
  readonly abort: AbortController;
  status: TaskStatus = "pending";
  stage?: string;
  percent?: number;
  total?: number;
  message?: string;
  data?: unknown;

  #result: Promise<T>;

  constructor(
    id: string,
    abort: AbortController,
    run: (task: ToolTask<T>) => Promise<T>,
  ) {
    super();
    this.id = id;
    this.abort = abort;
    this.#result = run(this);
  }

  /** @internal Applies a progress update and emits a standard progress event. */
  applyProgress(progress: ToolProgress): void {
    this.stage = progress.stage ?? this.stage;
    this.message = progress.message ?? this.message;
    this.data = progress.data ?? this.data;
    if (progress.total !== undefined) {
      this.total = progress.total;
      if (progress.progress !== undefined) {
        this.percent = Math.round(
          (progress.progress / progress.total) * 100,
        );
      }
    } else if (progress.progress !== undefined) {
      this.percent = progress.progress;
    }
    if (progress.status !== undefined) {
      this.status = progress.status;
    }
    this.dispatchEvent(
      new ProgressEvent("progress", {
        lengthComputable: this.total !== undefined,
        loaded: this.percent ?? 0,
        total: this.total ?? 0,
      }),
    );
  }

  then<TResult1 = T, TResult2 = never>(
    onfulfilled?: ((value: T) => TResult1 | PromiseLike<TResult1>) | null,
    onrejected?:
      | ((reason: unknown) => TResult2 | PromiseLike<TResult2>)
      | null,
  ): Promise<TResult1 | TResult2> {
    return this.#result.then(onfulfilled, onrejected);
  }

  catch<TResult = never>(
    onrejected?:
      | ((reason: unknown) => TResult | PromiseLike<TResult>)
      | null,
  ): Promise<T | TResult> {
    return this.#result.catch(onrejected);
  }

  finally(onfinally?: (() => void) | null): Promise<T> {
    return this.#result.finally(onfinally);
  }
}

function abortError(): DOMException {
  return new DOMException("The operation was aborted.", "AbortError");
}

class ReplBus {
  readonly events: EventTarget;

  private readonly address: string;
  private readonly sessionId: string;
  private readonly credential: string | undefined;
  private readonly waiters = new Map<
    string,
    (envelope: ReplEnvelope) => void
  >();
  private readonly terminal: Promise<Error>;
  private readonly resolveTerminal: (error: Error) => void;
  private terminalError: Error | undefined;
  private bus: BusConnection | undefined;
  private connecting: Promise<void> | undefined;

  constructor(address: string, events: EventTarget) {
    this.address = address;
    this.events = events;
    this.sessionId = Deno.env.get(SESSION_ENV) ?? "";
    this.credential = Deno.build.os === "windows"
      ? Deno.env.get(CREDENTIAL_ENV)
      : undefined;
    const terminal = deferred<Error>();
    this.terminal = terminal.promise;
    this.resolveTerminal = terminal.resolve;
  }

  async connect(): Promise<void> {
    if (this.bus !== undefined) {
      return;
    }
    if (this.connecting !== undefined) {
      return this.connecting;
    }

    const connecting = this.open();
    this.connecting = connecting;
    try {
      return await connecting;
    } finally {
      this.connecting = undefined;
    }
  }

  send(envelope: Omit<ReplEnvelope, "version">): void {
    if (this.bus === undefined) {
      throw new Error("The REPL control bus is not connected.");
    }
    this.bus.send(envelope);
  }

  waitCorrelation(correlationId: string): Promise<ReplEnvelope> {
    return Promise.race([
      new Promise<ReplEnvelope>((resolve, reject) => {
        const timer = setTimeout(() => {
          this.waiters.delete(correlationId);
          reject(new Error(`Timed out waiting for correlation ${correlationId}.`));
        }, BUS_TIMEOUT_MS);
        this.waiters.set(correlationId, (envelope) => {
          clearTimeout(timer);
          if (envelope.type === "error") {
            const payload = envelope.payload as {
              code?: string;
              message?: string;
            } | undefined;
            reject(
              new Error(
                `${payload?.code ?? "bus_error"}: ${payload?.message ?? "the channel failed"}`,
              ),
            );
            return;
          }
          resolve(envelope);
        });
      }),
      this.terminal.then((error) => Promise.reject(error)),
    ]);
  }

  waitForType(type: string): Promise<ReplEnvelope> {
    return Promise.race([
      new Promise<ReplEnvelope>((resolve, reject) => {
        const handler = (event: Event): void => {
          clearTimeout(timer);
          this.events.removeEventListener(type, handler);
          resolve((event as CustomEvent<ReplEnvelope>).detail);
        };
        this.events.addEventListener(type, handler);
        const timer = setTimeout(() => {
          this.events.removeEventListener(type, handler);
          reject(new Error(`Timed out waiting for ${type}.`));
        }, BUS_TIMEOUT_MS);
      }),
      this.terminal.then((error) => Promise.reject(error)),
    ]);
  }

  close(): void {
    this.bus?.close();
  }

  private async open(): Promise<void> {
    if (!this.sessionId) {
      throw new Error(
        `Missing ${SESSION_ENV} environment variable; cannot open the REPL control bus.`,
      );
    }
    const ready = this.waitForType("control.ready");
    try {
      this.bus = await connectBus({
        address: this.address,
        credential: this.credential,
        hello: {
          type: "control.hello",
          payload: { sessionId: this.sessionId },
        },
        onMessage: (envelope) => this.route(envelope),
        onClose: () => {
          this.bus = undefined;
          this.fail(new Error("The REPL control WebSocket closed unexpectedly."));
        },
        onError: (error) => this.fail(error),
      });
      await ready;
    } catch (error) {
      this.bus?.close();
      this.bus = undefined;
      throw error;
    }
  }

  private fail(error: Error): void {
    if (this.terminalError !== undefined) return;
    this.terminalError = error;
    this.resolveTerminal(error);
    for (const waiter of this.waiters.values()) waiter({
      version: 1,
      type: "error",
      payload: { code: "control_closed", message: error.message },
    });
    this.waiters.clear();
  }

  private route(envelope: ReplEnvelope): void {
    this.events.dispatchEvent(
      new CustomEvent<ReplEnvelope>(envelope.type, { detail: envelope }),
    );
    if (envelope.correlationId !== undefined) {
      const waiter = this.waiters.get(envelope.correlationId);
      if (waiter !== undefined) {
        this.waiters.delete(envelope.correlationId);
        waiter(envelope);
      }
    }
  }
}

async function sendAndWait(
  bus: ReplBus,
  envelope: Omit<ReplEnvelope, "version" | "correlationId">,
): Promise<void> {
  const correlationId = crypto.randomUUID();
  await bus.connect();
  const done = bus.waitCorrelation(correlationId);
  bus.send({ ...envelope, correlationId });
  await done;
}

async function healthProbe(bus: ReplBus): Promise<string> {
  const correlationId = crypto.randomUUID();
  await bus.connect();
  const done = bus.waitCorrelation(correlationId);
  bus.send({ type: "control.ping", correlationId });
  const envelope = await done;
  if (envelope.type !== "control.pong") {
    throw new Error(`Unexpected health reply '${envelope.type}'.`);
  }
  return "ok";
}

function startTool(
  bus: ReplBus,
  name: string,
  args: Record<string, unknown> = {},
  options: { signal?: AbortSignal } = {},
): ToolTask {
  const { signal } = options;
  const abort = new AbortController();
  if (signal?.aborted) {
    abort.abort();
  } else {
    signal?.addEventListener("abort", () => abort.abort(), { once: true });
  }
  const correlationId = crypto.randomUUID();

  const onProgress = (event: Event): void => {
    const envelope = (event as CustomEvent<ReplEnvelope>).detail;
    if (envelope.correlationId !== correlationId) {
      return;
    }
    const progress: ToolProgress = {
      correlationId: envelope.correlationId ?? correlationId,
      ...(envelope.payload ?? {}) as Partial<ToolProgress>,
    };
    task.applyProgress(progress);
  };
  bus.events.addEventListener("tool.progress", onProgress);

  const task = new ToolTask(correlationId, abort, async (current) => {
    const sendCancel = (): void => {
      bus.connect()
        .then(() => {
          bus.send({
            type: "control.cancel",
            payload: { correlationId },
          });
        })
        .catch(() => {
          // The connection failure already makes cancellation moot.
        });
    };
    abort.signal.addEventListener("abort", sendCancel, { once: true });
    try {
      current.status = "working";
      abort.signal.throwIfAborted();
      const done = bus.waitCorrelation(correlationId);
      await bus.connect();
      abort.signal.throwIfAborted();
      bus.send({
        type: "tool.invoke",
        correlationId,
        payload: { tool: name, arguments: args },
      });
      const reply = await done;
      if (reply.type !== "tool.result") {
        throw new Error(`Unexpected tool reply '${reply.type}'.`);
      }
      const envelope = (reply.payload ?? {}) as ToolEnvelope;
      if (envelope.status === "cancelled" || abort.signal.aborted) {
        throw abortError();
      }
      if (envelope.status !== "ok") {
        throw new Error(
          `${envelope.code ?? "tool_failed"}: ${envelope.message ?? "the tool failed"}`,
        );
      }
      current.status = "completed";
      return envelope.value;
    } catch (error) {
      current.status = abort.signal.aborted ? "cancelled" : "failed";
      throw error;
    } finally {
      bus.events.removeEventListener("tool.progress", onProgress);
      abort.signal.removeEventListener("abort", sendCancel);
    }
  });

  return task;
}

function createTools(bus: ReplBus): ReplTools {
  return {
    start: (name, args, options) => startTool(bus, name, args, options),
    async invoke(name, args, options) {
      return await startTool(bus, name, args, options);
    },
  };
}

function createComm(bus: ReplBus): ReplComm {
  return {
    async open(commId, targetName, data) {
      await sendAndWait(bus, {
        type: "comm.open",
        payload: { commId, targetName, data },
      });
    },
    async msg(commId, data, buffers) {
      await sendAndWait(bus, {
        type: "comm.msg",
        payload: { commId, data },
        buffers: buffers?.map((bytes) => bytes.toBase64()),
      });
    },
    async close(commId) {
      await sendAndWait(bus, { type: "comm.close", payload: { commId } });
    },
  };
}

function createClient(address: string, events: EventTarget): ReplClient {
  const bus = new ReplBus(address, events);
  return {
    address,
    health: () => healthProbe(bus),
    tools: createTools(bus),
    events,
    comm: createComm(bus),
  };
}

let defaultAddress: string | undefined;
let defaultClient: ReplClient | undefined;
const defaultEvents = new EventTarget();

function resolveAddress(): string {
  const address = defaultAddress ??= Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }
  return address;
}

function ensureDefaultClient(): ReplClient {
  defaultClient ??= createClient(resolveAddress(), defaultEvents);
  return defaultClient;
}

/** Creates an independent control channel client for the given or env-provided socket address. */
export function connect(options: ReplClientOptions = {}): ReplClient {
  const address = options.address ?? Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }
  return createClient(address, new EventTarget());
}

/** Probes the default client. Convenience for scripts that use the module namespace directly. */
export function health(): Promise<string> {
  return ensureDefaultClient().health();
}

/** Script tool invocation against the default client. */
export const tools: ReplTools = {
  start: (name, args, options) => ensureDefaultClient().tools.start(name, args, options),
  invoke: (name, args, options) => ensureDefaultClient().tools.invoke(name, args, options),
};

/** Bus message hub for the default client; subscribe with `addEventListener(type, handler)`. */
export const events: EventTarget = defaultEvents;

/** Comm channel operations against the default client. */
export const comm: ReplComm = {
  open: (commId, targetName, data) => ensureDefaultClient().comm.open(commId, targetName, data),
  msg: (commId, data, buffers) => ensureDefaultClient().comm.msg(commId, data, buffers),
  close: (commId) => ensureDefaultClient().comm.close(commId),
};
