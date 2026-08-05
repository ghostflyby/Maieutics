/**
 * Client for the Maieutics REPL control channel.
 *
 * The kernel injects the channel address through `MAIEUTICS_REPL_IPC`, the
 * client module through `MAIEUTICS_REPL_CLIENT`, and the owning session id
 * through `MAIEUTICS_REPL_SESSION`. HTTP serves tools and health; a single
 * multiplexed WebSocket bus carries events, comm messages, and control
 * messages under one versioned envelope.
 *
 * The module namespace is the default client: `health`, `tools`, `events`,
 * and `comm` operate on the process's REPL connection. `connect()` creates
 * an additional independent client with the same shape.
 */

const ADDRESS_ENV = "MAIEUTICS_REPL_IPC";
const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const SERVER_URL = "http://localhost";
const ENVELOPE_VERSION = 1;
const BUS_TIMEOUT_MS = 5_000;

type HttpClient = ReturnType<typeof Deno.createHttpClient>;

export interface ReplClientOptions {
  /** Unix domain socket path of the kernel control channel. */
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
  /** Unix domain socket path of the kernel control channel. */
  readonly address: string;
  /** Probes the kernel control channel health endpoint. */
  health(): Promise<string>;
  /** Script tool invocation. */
  tools: ReplTools;
  /** Bus message hub; subscribe with `addEventListener(type, handler)`. */
  events: EventTarget;
  /** Comm channel operations. */
  comm: ReplComm;
}

interface ReplEnvelope {
  version: number;
  type: string;
  correlationId?: string;
  payload?: unknown;
  buffers?: string[];
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
        this.percent = Math.round((progress.progress / progress.total) * 100);
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
    onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null,
  ): Promise<TResult1 | TResult2> {
    return this.#result.then(onfulfilled, onrejected);
  }

  catch<TResult = never>(
    onrejected?: ((reason: unknown) => TResult | PromiseLike<TResult>) | null,
  ): Promise<T | TResult> {
    return this.#result.catch(onrejected);
  }

  finally(onfinally?: (() => void) | null): Promise<T> {
    return this.#result.finally(onfinally);
  }
}

function createHttp(address: string): HttpClient {
  return Deno.createHttpClient({ proxy: { transport: "unix", path: address } });
}

async function healthProbe(http: HttpClient): Promise<string> {
  const response = await fetch(`${SERVER_URL}/health`, { client: http });
  if (!response.ok) {
    throw new Error(
      `REPL control channel health check failed with status ${response.status}.`,
    );
  }
  return response.text();
}

function abortError(): DOMException {
  return new DOMException("The operation was aborted.", "AbortError");
}

class ReplBus {
  readonly events: EventTarget;

  private readonly http: HttpClient;
  private readonly sessionId: string;
  private readonly waiters = new Map<string, (envelope: ReplEnvelope) => void>();
  private socket: WebSocket | undefined;
  private connecting: Promise<WebSocket> | undefined;

  constructor(http: HttpClient, events: EventTarget) {
    this.http = http;
    this.events = events;
    this.sessionId = Deno.env.get(SESSION_ENV) ?? "";
  }

  async connect(): Promise<WebSocket> {
    if (this.socket !== undefined && this.socket.readyState === WebSocket.OPEN) {
      return this.socket;
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

  send(envelope: ReplEnvelope): void {
    this.socket?.send(JSON.stringify(envelope));
  }

  waitCorrelation(correlationId: string): Promise<ReplEnvelope> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.waiters.delete(correlationId);
        reject(new Error(`Timed out waiting for correlation ${correlationId}.`));
      }, BUS_TIMEOUT_MS);
      this.waiters.set(correlationId, (envelope) => {
        clearTimeout(timer);
        if (envelope.type === "error") {
          const payload = envelope.payload as { code?: string; message?: string } | undefined;
          reject(
            new Error(
              `${payload?.code ?? "bus_error"}: ${payload?.message ?? "the channel failed"}`,
            ),
          );
          return;
        }
        resolve(envelope);
      });
    });
  }

  waitForType(type: string): Promise<ReplEnvelope> {
    return new Promise((resolve, reject) => {
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
    });
  }

  close(): void {
    this.socket?.close();
  }

  private async open(): Promise<WebSocket> {
    if (!this.sessionId) {
      throw new Error(
        `Missing ${SESSION_ENV} environment variable; cannot open the REPL control bus.`,
      );
    }
    const socket = new WebSocket("ws://localhost/ws", { client: this.http });
    this.socket = socket;
    socket.onmessage = (event) => {
      let envelope: ReplEnvelope;
      try {
        envelope = JSON.parse(String(event.data)) as ReplEnvelope;
      } catch {
        return;
      }
      this.route(envelope);
    };
    socket.onclose = () => {
      if (this.socket === socket) {
        this.socket = undefined;
      }
    };
    await new Promise<void>((resolve, reject) => {
      socket.onopen = () => resolve();
      socket.onerror = () => reject(new Error("the REPL control bus failed to open"));
    });
    socket.send(
      JSON.stringify({
        version: ENVELOPE_VERSION,
        type: "control.hello",
        payload: { sessionId: this.sessionId },
      }),
    );
    try {
      await this.waitForType("control.ready");
    } catch (error) {
      if (this.socket === socket) {
        this.socket = undefined;
      }
      socket.close();
      throw error;
    }
    return socket;
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
  const done = bus.waitCorrelation(correlationId);
  await bus.connect();
  bus.send({ ...envelope, version: ENVELOPE_VERSION, correlationId });
  await done;
}

function startTool(
  http: HttpClient,
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
        .then((socket) => {
          socket.send(
            JSON.stringify({
              version: ENVELOPE_VERSION,
              type: "control.cancel",
              payload: { correlationId },
            }),
          );
        })
        .catch(() => {
          // The fetch abort already covered client-side cancellation.
        });
    };
    abort.signal.addEventListener("abort", sendCancel, { once: true });
    try {
      current.status = "working";
      const response = await fetch(`${SERVER_URL}/v1/tool.invoke`, {
        client: http,
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          version: ENVELOPE_VERSION,
          tool: name,
          arguments: args,
          correlationId,
          sessionId: Deno.env.get(SESSION_ENV),
        }),
        signal: abort.signal,
      });
      if (!response.ok) {
        throw new Error(`Tool invocation failed with status ${response.status}.`);
      }
      const envelope = await response.json() as ToolEnvelope;
      if (envelope.status === "cancelled") {
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

function createTools(http: HttpClient, bus: ReplBus): ReplTools {
  return {
    start: (name, args, options) => startTool(http, bus, name, args, options),
    async invoke(name, args, options) {
      return await startTool(http, bus, name, args, options);
    },
  };
}

function createComm(bus: ReplBus): ReplComm {
  return {
    async open(commId, targetName, data) {
      await sendAndWait(bus, { type: "comm.open", payload: { commId, targetName, data } });
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

function createClient(http: HttpClient, address: string, events: EventTarget): ReplClient {
  const bus = new ReplBus(http, events);
  return {
    address,
    health: () => healthProbe(http),
    tools: createTools(http, bus),
    events,
    comm: createComm(bus),
  };
}

let defaultAddress: string | undefined;
let defaultHttp: HttpClient | undefined;
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

function ensureDefault(): HttpClient {
  if (defaultHttp !== undefined) {
    return defaultHttp;
  }
  defaultHttp = createHttp(resolveAddress());
  return defaultHttp;
}

function ensureDefaultClient(): ReplClient {
  defaultClient ??= createClient(ensureDefault(), resolveAddress(), defaultEvents);
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
  return createClient(createHttp(address), address, new EventTarget());
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
