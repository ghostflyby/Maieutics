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
import type { ReplEnvelope } from "../shared/protocol.ts";
import { type CommClient, CommKind, connectComm } from "../maieutics-deno-repl/comm.ts";
import { createResourceReader } from "../shared/resource_bridge.ts";

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
  /** Opens a comm channel; the kernel relays comm_open to the frontend. */
  open(commId: string, targetName?: string, data?: unknown): Promise<void>;
  /** Sends a message on an open comm channel, optionally with binary buffers. */
  msg(commId: string, data?: unknown, buffers?: Uint8Array[]): Promise<void>;
  /** Closes a comm channel. */
  close(commId: string, data?: unknown): Promise<void>;
  /** Subscribes to comm events from the frontend (open, msg, close). */
  on(
    event: "open" | "msg" | "close",
    handler: (
      message: { commId: string; targetName?: string; data?: unknown; buffers: Uint8Array[] },
    ) => void,
  ): void;
}

/** One entry of the virtual resource catalog returned by `resources.list()`. */
export interface ResourceCatalogEntry {
  readonly providerId: string;
  readonly uri: string;
  readonly name?: string;
  readonly description?: string;
  readonly mimeType?: string;
  /** "resource" is a concrete URI; "template" is an RFC 6570 `template`. */
  readonly kind: "resource" | "template";
  readonly template?: string;
}

export interface ResourceListResult {
  readonly resources: ResourceCatalogEntry[];
  readonly conflicts: {
    readonly providerId: string;
    readonly scheme: string;
    readonly authority?: string;
    readonly reason: string;
    readonly shadowedBy?: string;
  }[];
}

export interface ReplResources {
  /** Lists the virtual resources and templates registered with the kernel. */
  list(): Promise<ResourceListResult>;
  /** Reads one virtual resource URI; returns a native Response whose body
   * carries the bytes (non-2xx answers keep their status). */
  read(uri: string, options?: { signal?: AbortSignal }): Promise<Response>;
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
  /** Virtual resource reads over the control channel (ADR 0026). */
  resources: ReplResources;
  /** Model orchestration: spawn, await, and cancel subagent runs (ADR 0031). */
  model: ReplModel;
  /** Generic task-plane addressing: wait, cancel, poll by URI. */
  tasks: ReplTasks;
  /** Terminal operations via script tools. */
  terminal: ReplTerminal;

  /** Probes the kernel control channel over the multiplexed bus. */
  health(): Promise<string>;
}

/** One subagent run spawned through the model-orchestration surface (ADR 0031). */
export interface SubagentSpawnOptions {
  /** The composed task for the subagent. It cannot ask questions; include everything. */
  input: string;
  /** System instructions for the subagent; the orchestrating context's are not inherited. */
  instructions?: string;
  /** Names of parent-registered tools the subagent may use; omitted means all. */
  tools?: string[];
  /** The REPL session presenting the request; defaults to the environment-provided session. */
  sessionId?: string;
}

/** The terminal snapshot of one subagent run. */
export interface SubagentResult {
  childSessionId: string;
  runId: string;
  status: "complete" | "fail" | "cancel";
  report?: string;
  truncated?: boolean;
  usage?: { input?: number; output?: number; total?: number };
}

/** Terminal one-shot detail carried in a task-plane snapshot. */
export interface TerminalTaskDetail {
  agentSessionId: string;
  sessionId: string;
  state: string;
  exitCode?: number;
}

/** Subagent run detail carried in a task-plane snapshot. */
export interface AgentSubagentDetail {
  agentSessionId: string;
  runId: string;
  report?: string;
  reportTruncated?: boolean;
  usage?: { input?: number; output?: number; total?: number };
}

/** The bounded fresh snapshot of one task-plane object: a common envelope plus
 * additive kind-specific detail (unknown kinds carry opaque extra fields). */
export interface TaskSnapshot {
  uri: string;
  kind: string;
  status: TaskStatus;
  terminal?: TerminalTaskDetail;
  agent?: AgentSubagentDetail;
}

/** A live handle over one task-plane object: await it to wait for completion,
 * `abortController.abort()` to initiate cancellation, `uri` to address it. */
export interface TaskRef extends PromiseLike<TaskSnapshot> {
  readonly uri: string;
  readonly kind: string;
  readonly status: TaskStatus;
  readonly abortController: AbortController;
  /** The most recently observed snapshot, or undefined before the first fetch. */
  snapshot(): TaskSnapshot | undefined;
  /** Pulls a fresh snapshot without waiting for completion. */
  refresh(): Promise<TaskSnapshot>;
}

/** A spawned subagent run: a task reference plus the spawn identity. */
export interface SubagentTaskRef extends TaskRef {
  readonly childSessionId: string;
  readonly runId: string;
  readonly taskUri: string;
}

/** Generic task-plane operations: address any task by its URI (ADR 0031). */
export interface ReplTasks {
  /** Returns a live reference to the task at the URI; validates existence on first await. */
  get(uri: string, options?: { signal?: AbortSignal }): TaskRef;
}

/** Terminal operation types. */
export interface TerminalRunOptions {
  executable: string;
  args?: string[];
  timeoutMs?: number;
  full?: boolean;
  maxCharacters?: number;
  signal?: AbortSignal;
}

export interface TerminalScreenResult {
  sessionId: string;
  state: string;
  settled?: boolean;
  exitCode?: number;
  taskUri?: string;
  frame: { version: number; columns: number; rows: { text: string }[]; cursor: unknown };
}

export interface TerminalInputOptions {
  sessionId?: string;
  full?: boolean;
  maxCharacters?: number;
  signal?: AbortSignal;
}

export interface TerminalInfo {
  sessionId: string;
  state: string;
  kind: string;
  exitCode?: number;
}

/** Terminal operations against the control channel (script tools). */
export interface ReplTerminal {
  run(options: TerminalRunOptions): Promise<TerminalScreenResult>;
  input(
    sessionId: string,
    lines: string[],
    options?: { signal?: AbortSignal },
  ): Promise<TerminalScreenResult>;
  snapshot(
    sessionId?: string,
    options?: { full?: boolean; signal?: AbortSignal },
  ): Promise<TerminalScreenResult>;
  paste(
    sessionId: string,
    text: string,
    options?: { signal?: AbortSignal },
  ): Promise<TerminalScreenResult>;
  interrupt(
    sessionId?: string,
    options?: { signal?: AbortSignal },
  ): Promise<TerminalInterruptResult>;
  close(sessionId?: string, options?: { signal?: AbortSignal }): Promise<void>;
  list(options?: { signal?: AbortSignal }): Promise<TerminalInfo[]>;
}

export interface TerminalInterruptResult {
  settled: boolean;
  frame: { version: number; columns: number; rows: { text: string }[]; cursor: unknown };
}

/** Model-orchestration operations against the control channel (ADR 0031). */
export interface ReplModel {
  /** Spawns a subagent run scoped to this REPL's owning Agent session; the returned
   * reference is awaitable (waits for the terminal snapshot) and abortable (cancels). */
  spawnSubagent(options: SubagentSpawnOptions): Promise<SubagentTaskRef>;
  /** Waits for a spawned subagent run to settle and returns its result. The wait is
   * unbounded — bound it via the caller's abort signal (e.g. the REPL tool-call token). */
  waitForSubagent(
    runId: string,
    options?: { sessionId?: string; signal?: AbortSignal },
  ): Promise<SubagentResult>;
  /** Cancels a spawned subagent run and returns its terminal snapshot. */
  cancelSubagent(
    runId: string,
    options?: { sessionId?: string; signal?: AbortSignal },
  ): Promise<SubagentResult>;
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

  readonly address: string;
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
    this.credential = Deno.build.os === "windows" ? Deno.env.get(CREDENTIAL_ENV) : undefined;
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
    for (const waiter of this.waiters.values()) {
      waiter({
        version: 1,
        type: "error",
        payload: { code: "control_closed", message: error.message },
      });
    }
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
  const address = bus.address;
  let client: CommClient | undefined;
  let connecting: Promise<CommClient> | undefined;

  const connect = (): Promise<CommClient> => {
    if (client !== undefined) return Promise.resolve(client);
    connecting ??= connectComm(
      address,
      Deno.env.get(SESSION_ENV) ?? "",
      Deno.build.os === "windows" ? Deno.env.get(CREDENTIAL_ENV) : undefined,
    )
      .then((value) => {
        client = value;
        connecting = undefined;
        return value;
      })
      .catch((error) => {
        connecting = undefined;
        throw error;
      });
    return connecting;
  };

  return {
    async open(commId, targetName, data) {
      const value = await connect();
      await value.send({ kind: CommKind.Open, commId, targetName, data, buffers: [] });
    },
    async msg(commId, data, buffers) {
      const value = await connect();
      await value.send({ kind: CommKind.Message, commId, data, buffers: buffers ?? [] });
    },
    async close(commId, data) {
      const value = await connect();
      await value.send({ kind: CommKind.Close, commId, data, buffers: [] });
    },
    on(event, handler) {
      void connect().then((value) => {
        value.onMessage = (message) => {
          const kind = message.kind === CommKind.Open
            ? "open"
            : message.kind === CommKind.Close
            ? "close"
            : "msg";
          if (kind === event) {
            handler({
              commId: message.commId,
              targetName: message.targetName,
              data: message.data,
              buffers: message.buffers,
            });
          }
        };
      });
    },
  };
}

function createResources(address: string, tools: ReplTools): ReplResources {
  const read = createResourceReader({
    address,
    ...(Deno.build.os === "windows"
      ? ((
        credential,
      ) => (credential === undefined || credential.length === 0 ? {} : { credential }))(
        Deno.env.get(CREDENTIAL_ENV),
      )
      : {}),
  });
  return {
    async list() {
      return await tools.invoke("list_resources") as ResourceListResult;
    },
    read: (uri, options) => read(uri, options),
  };
}

const MODEL_BASE_PATH = "/v1/model/subagents";

/** A fetch transport over the control channel: unix-socket proxy on Unix, loopback plus
 * credential header on Windows — the same shape as the resource bridge. */
function createControlTransport(address: string) {
  if (address.length === 0) {
    throw new TypeError("The control-channel address is required.");
  }
  const useUnixProxy = Deno.build.os !== "windows";
  const client = useUnixProxy
    ? Deno.createHttpClient({
      proxy: { transport: "unix", path: address },
    })
    : undefined;
  const credential = Deno.build.os === "windows" ? Deno.env.get(CREDENTIAL_ENV) : undefined;
  const headers = credential === undefined || credential.length === 0
    ? undefined
    : { Authorization: `Bearer ${credential}` };
  const base = useUnixProxy ? "http://localhost" : `http://${address}`;

  return async function request<T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    const response = await fetch(`${base}${path}`, {
      method,
      redirect: "error",
      ...(body === undefined ? {} : {
        headers: { ...headers, "content-type": "application/json" },
        body: JSON.stringify(body),
      }),
      ...(headers === undefined || body !== undefined ? {} : { headers }),
      ...(client === undefined ? {} : { client }),
      ...(signal === undefined ? {} : { signal }),
    });
    const payload = await response.json().catch(() => undefined);
    if (!response.ok) {
      const code = payload !== undefined && typeof payload === "object" && payload !== null &&
          "code" in payload
        ? String((payload as { code: unknown }).code)
        : `http_${response.status}`;
      const message = payload !== undefined && typeof payload === "object" && payload !== null &&
          "message" in payload
        ? String((payload as { message: unknown }).message)
        : response.statusText;
      throw new ControlChannelError(code, message, response.status);
    }

    return payload as T;
  };
}

/** A typed control-channel failure: the wire error code and HTTP status survive as fields,
 * so callers can branch on them (a bounded wait timing out is recoverable; a hard 404 is not). */
export class ControlChannelError extends Error {
  readonly code: string;
  readonly status: number;

  constructor(code: string, message: string, status: number) {
    super(`model orchestration failed (${code}): ${message}`);
    this.name = "ControlChannelError";
    this.code = code;
    this.status = status;
  }
}

function createModel(
  request: <T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ) => Promise<T>,
  resolveSession: () => string,
  tasks: ReplTasks,
): ReplModel {
  const sessionQuery = (explicit?: string) => {
    const sessionId = explicit ?? resolveSession();
    return sessionId.length === 0 ? "" : `&session=${encodeURIComponent(sessionId)}`;
  };

  return {
    async spawnSubagent(options: SubagentSpawnOptions): Promise<SubagentTaskRef> {
      const handle = await request<{ childSessionId: string; runId: string; taskUri: string }>(
        "POST",
        `${MODEL_BASE_PATH}?session=${encodeURIComponent(options.sessionId ?? resolveSession())}`,
        {
          version: 1,
          input: options.input,
          ...(options.instructions === undefined ? {} : { instructions: options.instructions }),
          ...(options.tools === undefined ? {} : { tools: options.tools }),
          sessionId: options.sessionId ?? resolveSession(),
        },
        undefined,
      );
      // The spawn identity rides on the unified task reference: await, abort, and uri in one object.
      return Object.assign(tasks.get(handle.taskUri), {
        childSessionId: handle.childSessionId,
        runId: handle.runId,
        taskUri: handle.taskUri,
      }) as SubagentTaskRef;
    },
    async waitForSubagent(
      runId: string,
      options?: { sessionId?: string; signal?: AbortSignal },
    ): Promise<SubagentResult> {
      // No timeoutMs: the wait is unbounded and bounded by the caller's abort signal.
      // The kernel's internal 60s window is retried transparently by the long-poll chain.
      return await request<SubagentResult>(
        "GET",
        `${MODEL_BASE_PATH}/${runId}?timeoutMs=60000${sessionQuery(options?.sessionId)}`,
        undefined,
        options?.signal,
      );
    },
    async cancelSubagent(
      runId: string,
      options?: { sessionId?: string; signal?: AbortSignal },
    ): Promise<SubagentResult> {
      return await request<SubagentResult>(
        "POST",
        `${MODEL_BASE_PATH}/${runId}/cancel?session=${
          encodeURIComponent(
            options?.sessionId ?? resolveSession(),
          )
        }`,
        undefined,
        options?.signal,
      );
    },
  };
}

const TASKS_BASE_PATH = "/v1/tasks";
const TASK_POLL_MS = 60_000;

class TaskRefImpl implements TaskRef {
  readonly uri: string;
  readonly kind: string;
  readonly abortController = new AbortController();
  #latest: TaskSnapshot | undefined;
  #settled = false;
  #completion: Promise<TaskSnapshot> | undefined;

  constructor(
    private readonly request: <T>(
      method: string,
      path: string,
      body?: unknown,
      signal?: AbortSignal,
    ) => Promise<T>,
    private readonly resolveSession: () => string,
    uri: string,
    external?: AbortSignal,
  ) {
    if (!uri.startsWith("task://")) {
      throw new TypeError("A task URI must start with task://");
    }
    this.uri = uri;
    this.kind = uri.slice("task://".length).split("/", 1)[0];
    if (external !== undefined) {
      external.addEventListener("abort", () => this.abortController.abort(), { once: true });
    }
    // Validate eagerly: a missing task surfaces as a rejected reference, not a silent one.
    this.#completion = this.#drive();
  }

  get status(): TaskStatus {
    return this.#latest?.status ?? "working";
  }

  snapshot(): TaskSnapshot | undefined {
    return this.#latest;
  }

  async refresh(): Promise<TaskSnapshot> {
    // Single-writer discipline: once the terminal snapshot landed (possibly via the abort
    // path), a racing refresh must not regress the visible status.
    if (this.#settled) return this.#latest!;
    const snapshot = await this.request<TaskSnapshot>(
      "GET",
      `/v1/resource?uri=${encodeURIComponent(this.uri)}`,
    );
    if (!this.#settled) this.#latest = snapshot;
    return this.#latest!;
  }

  then<TResult1 = TaskSnapshot, TResult2 = never>(
    onfulfilled?: ((value: TaskSnapshot) => TResult1 | PromiseLike<TResult1>) | null,
    onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null,
  ): PromiseLike<TResult1 | TResult2> {
    return this.#completion!.then(onfulfilled, onrejected);
  }

  catch<TResult = never>(
    onrejected?: ((reason: unknown) => TResult | PromiseLike<TResult>) | null,
  ): PromiseLike<TaskSnapshot | TResult> {
    return this.#completion!.catch(onrejected);
  }

  finally(onfinally?: () => void): PromiseLike<TaskSnapshot> {
    return this.#completion!.finally(onfinally);
  }

  async #drive(): Promise<TaskSnapshot> {
    while (true) {
      let snapshot: TaskSnapshot;
      try {
        snapshot = await this.request<TaskSnapshot>(
          "GET",
          `${TASKS_BASE_PATH}?uri=${encodeURIComponent(this.uri)}&timeoutMs=${TASK_POLL_MS}`,
          undefined,
          this.abortController.signal,
        );
      } catch (error) {
        if (error instanceof ControlChannelError && error.code === "task_wait_timeout") {
          // The bounded window expired while the task is still working: keep polling. The
          // await must survive any number of windows — a subagent legitimately runs minutes.
          continue;
        }

        if (this.abortController.signal.aborted) {
          // Abort means cancel the task, not just stop waiting: settle it and land on the
          // cancelled terminal snapshot.
          const cancelled = await this.request<TaskSnapshot>(
            "POST",
            `${TASKS_BASE_PATH}/cancel`,
            { uri: this.uri, sessionId: this.resolveSession() },
          ).catch(() => this.refresh());
          this.#settled = true;
          this.#latest = cancelled;
          return cancelled;
        }

        throw error;
      }

      this.#latest = snapshot;
      if (snapshot.status !== "working") {
        this.#settled = true;
        return snapshot;
      }
    }
  }
}

function createTasks(
  request: <T>(
    method: string,
    path: string,
    body?: unknown,
    signal?: AbortSignal,
  ) => Promise<T>,
  resolveSession: () => string,
): ReplTasks {
  return {
    get: (uri, options) => new TaskRefImpl(request, resolveSession, uri, options?.signal),
  };
}

function createTerminal(tools: ReplTools): ReplTerminal {
  const invoke = <T>(tool: string, args?: Record<string, unknown>, signal?: AbortSignal) =>
    tools.invoke(tool, args, { signal }) as Promise<T>;

  return {
    run: (options) =>
      invoke("terminal_run", {
        executable: options.executable,
        ...(options.args ? { args: options.args } : {}),
        ...(options.timeoutMs !== undefined ? { timeout: options.timeoutMs } : {}),
        ...(options.full ? { full: true } : {}),
      }, options.signal),
    input: (sessionId, lines, options) =>
      invoke("terminal_input", { sessionId, input: lines }, options?.signal) as Promise<
        TerminalScreenResult
      >,
    snapshot: (sessionId, options) =>
      invoke("terminal_snapshot", { sessionId, ...(options ?? {}) }, options?.signal) as Promise<
        TerminalScreenResult
      >,
    paste: (sessionId, text, options) =>
      invoke("terminal_paste", { sessionId, text }, options?.signal) as Promise<
        TerminalScreenResult
      >,
    interrupt: (sessionId, options) =>
      invoke("terminal_interrupt", { sessionId }, options?.signal) as Promise<
        TerminalInterruptResult
      >,
    close: (sessionId, options) =>
      invoke("terminal_close", { sessionId }, options?.signal) as Promise<void>,
    list: (options) => invoke("terminal_list", {}, options?.signal) as Promise<TerminalInfo[]>,
  };
}

function createClient(address: string, events: EventTarget): ReplClient {
  const bus = new ReplBus(address, events);
  const tools = createTools(bus);
  const resolveSession = () => Deno.env.get(SESSION_ENV) ?? "";
  const request = createControlTransport(address);
  const tasks = createTasks(request, resolveSession);
  return {
    address,
    health: () => healthProbe(bus),
    tools,
    events,
    comm: createComm(bus),
    resources: createResources(address, tools),
    model: createModel(request, resolveSession, tasks),
    tasks,
    terminal: createTerminal(tools),
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
  close: (commId, data) => ensureDefaultClient().comm.close(commId, data),
  on: (event, handler) => ensureDefaultClient().comm.on(event, handler),
};

/** Virtual resource reads against the default client (ADR 0026). */
export const resources: ReplResources = {
  list: () => ensureDefaultClient().resources.list(),
  read: (uri, options) => ensureDefaultClient().resources.read(uri, options),
};

/** Model orchestration against the default client (ADR 0031): spawn, await, cancel subagent runs. */
export const model: ReplModel = {
  spawnSubagent: (options) => ensureDefaultClient().model.spawnSubagent(options),
  waitForSubagent: (runId, options) => ensureDefaultClient().model.waitForSubagent(runId, options),
  cancelSubagent: (runId, options) => ensureDefaultClient().model.cancelSubagent(runId, options),
};

/** Generic task-plane operations against the default client: address any task by URI (ADR 0031). */
export const tasks: ReplTasks = {
  get: (uri, options) => ensureDefaultClient().tasks.get(uri, options),
};
