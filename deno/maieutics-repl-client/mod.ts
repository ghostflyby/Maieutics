/**
 * Client for the Maieutics REPL control channel.
 *
 * The kernel injects the channel address through `MAIEUTICS_REPL_IPC`, the
 * client module through `MAIEUTICS_REPL_CLIENT`, and the owning session id
 * through `MAIEUTICS_REPL_SESSION`. HTTP serves tools and health; a single
 * multiplexed WebSocket bus carries events, comm messages, and control
 * messages under one versioned envelope.
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
  /** Invokes a script-callable workspace tool and returns its structured result. */
  invoke(
    name: string,
    args?: Record<string, unknown>,
    options?: { signal?: AbortSignal },
  ): Promise<unknown>;
}

export interface ReplClient {
  /** Unix domain socket path of the kernel control channel. */
  readonly address: string;
  /** Probes the kernel control channel health endpoint. */
  health(): Promise<string>;
  /** Script tool invocation. */
  tools: ReplTools;
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

const typedHandlers = new Map<string, Set<(message: ReplEnvelope) => void>>();
const wildcardHandlers = new Set<(message: ReplEnvelope) => void>();
let busSocket: WebSocket | undefined;
let busConnecting: Promise<WebSocket> | undefined;

function addHandler(
  type: string,
  handler: (message: ReplEnvelope) => void,
): () => void {
  let handlers = typedHandlers.get(type);
  if (handlers === undefined) {
    handlers = new Set();
    typedHandlers.set(type, handlers);
  }
  handlers.add(handler);
  return () => handlers!.delete(handler);
}

function dispatch(message: ReplEnvelope): void {
  for (const handler of [...(typedHandlers.get(message.type) ?? [])]) {
    handler(message);
  }
  for (const handler of [...wildcardHandlers]) {
    handler(message);
  }
}

function sendEnvelope(socket: WebSocket, envelope: ReplEnvelope): void {
  socket.send(JSON.stringify(envelope));
}

function waitForCorrelation(
  correlationId: string,
  timeoutMs = BUS_TIMEOUT_MS,
): Promise<ReplEnvelope> {
  return new Promise((resolve, reject) => {
    const handler = (message: ReplEnvelope): void => {
      if (message.correlationId !== correlationId) {
        return;
      }
      cleanup();
      if (message.type === "error") {
        const payload = message.payload as { code?: string; message?: string } | undefined;
        reject(
          new Error(
            `${payload?.code ?? "bus_error"}: ${payload?.message ?? "the channel failed"}`,
          ),
        );
        return;
      }
      resolve(message);
    };
    const cleanup = (): void => {
      clearTimeout(timer);
      wildcardHandlers.delete(handler);
    };
    wildcardHandlers.add(handler);
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`Timed out waiting for correlation ${correlationId}.`));
    }, timeoutMs);
  });
}

async function ensureBus(): Promise<WebSocket> {
  if (busSocket !== undefined && busSocket.readyState === WebSocket.OPEN) {
    return busSocket;
  }
  if (busConnecting !== undefined) {
    return busConnecting;
  }

  const connecting = openBus();
  busConnecting = connecting;
  try {
    return await connecting;
  } finally {
    busConnecting = undefined;
  }
}

async function openBus(): Promise<WebSocket> {
  const sessionId = Deno.env.get(SESSION_ENV);
  if (!sessionId) {
    throw new Error(
      `Missing ${SESSION_ENV} environment variable; cannot open the REPL control bus.`,
    );
  }
  const http = ensureDefault();
  const socket = new WebSocket("ws://localhost/ws", { client: http });
  busSocket = socket;
  socket.onmessage = (event) => {
    try {
      dispatch(JSON.parse(String(event.data)) as ReplEnvelope);
    } catch {
      // Malformed bus messages are dropped; the connection stays alive.
    }
  };
  socket.onclose = () => {
    if (busSocket === socket) {
      busSocket = undefined;
    }
  };
  await new Promise<void>((resolve, reject) => {
    socket.onopen = () => resolve();
    socket.onerror = () => reject(new Error("the REPL control bus failed to open"));
  });
  sendEnvelope(socket, {
    version: ENVELOPE_VERSION,
    type: "control.hello",
    payload: { sessionId },
  });
  try {
    await waitForType("control.ready");
  } catch (error) {
    if (busSocket === socket) {
      busSocket = undefined;
    }
    socket.close();
    throw error;
  }
  return socket;
}

function waitForType(type: string, timeoutMs = BUS_TIMEOUT_MS): Promise<ReplEnvelope> {
  return new Promise((resolve, reject) => {
    const cleanup = (): void => {
      clearTimeout(timer);
      typedHandlers.get(type)?.delete(handler);
    };
    const handler = (message: ReplEnvelope): void => {
      cleanup();
      resolve(message);
    };
    addHandler(type, handler);
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`Timed out waiting for ${type}.`));
    }, timeoutMs);
  });
}

function createTools(http: HttpClient): ReplTools {
  return {
    async invoke(name, args = {}, options = {}) {
      const { signal } = options;
      if (signal?.aborted) {
        throw abortError();
      }
      const correlationId = crypto.randomUUID();
      const sendCancel = (): void => {
        ensureBus()
          .then((socket) => {
            sendEnvelope(socket, {
              version: ENVELOPE_VERSION,
              type: "control.cancel",
              payload: { correlationId },
            });
          })
          .catch(() => {
            // The fetch abort already covered client-side cancellation.
          });
      };
      signal?.addEventListener("abort", sendCancel, { once: true });
      try {
        const response = await fetch(`${SERVER_URL}/v1/tool.invoke`, {
          client: http,
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({
            version: ENVELOPE_VERSION,
            tool: name,
            arguments: args,
            correlationId,
          }),
          signal,
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
        return envelope.value;
      } finally {
        signal?.removeEventListener("abort", sendCancel);
      }
    },
  };
}

let defaultAddress: string | undefined;
let defaultHttp: HttpClient | undefined;

function ensureDefault(): HttpClient {
  if (defaultHttp !== undefined) {
    return defaultHttp;
  }
  const address = defaultAddress ??= Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }
  defaultHttp = createHttp(address);
  return defaultHttp;
}

/** Creates a control channel client for the given or env-provided socket address. */
export function connect(options: ReplClientOptions = {}): ReplClient {
  const address = options.address ?? Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }
  const http = createHttp(address);
  return { address, health: () => healthProbe(http), tools: createTools(http) };
}

/** Probes the default client. Convenience for scripts that use the module namespace directly. */
export async function health(): Promise<string> {
  return healthProbe(ensureDefault());
}

/** Script tool invocation against the default client. */
export const tools: ReplTools = {
  invoke: (name, args, options) => createTools(ensureDefault()).invoke(name, args, options),
};

/** Subscribes to bus messages by type. Returns an unsubscribe function. */
export const events = {
  on(type: string, handler: (message: ReplEnvelope) => void): () => void {
    return addHandler(type, handler);
  },
};

/** Opens, sends on, and closes comm channels over the bus. */
export const comm = {
  async open(commId: string, targetName?: string, data?: unknown): Promise<void> {
    const socket = await ensureBus();
    const correlationId = crypto.randomUUID();
    const done = waitForCorrelation(correlationId);
    sendEnvelope(socket, {
      version: ENVELOPE_VERSION,
      type: "comm.open",
      correlationId,
      payload: { commId, targetName, data },
    });
    await done;
  },
  async msg(commId: string, data?: unknown, buffers?: Uint8Array[]): Promise<void> {
    const socket = await ensureBus();
    const correlationId = crypto.randomUUID();
    const done = waitForCorrelation(correlationId);
    sendEnvelope(socket, {
      version: ENVELOPE_VERSION,
      type: "comm.msg",
      correlationId,
      payload: { commId, data },
      buffers: buffers?.map((bytes) => bytes.toBase64()),
    });
    await done;
  },
  async close(commId: string): Promise<void> {
    const socket = await ensureBus();
    const correlationId = crypto.randomUUID();
    const done = waitForCorrelation(correlationId);
    sendEnvelope(socket, {
      version: ENVELOPE_VERSION,
      type: "comm.close",
      correlationId,
      payload: { commId },
    });
    await done;
  },
};
