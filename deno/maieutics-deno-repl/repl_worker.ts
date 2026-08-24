import { createReplKernel, type ReplExecution, type ReplKernel } from "@ghostflyby/aves/repl";
import { type LinkHandle, serveWorker } from "@ghostflyby/worker-actor";
import { type Deferred, replEvalDeferred, ReplEvalQueue } from "./repl_eval_queue.ts";
import {
  INPUT_MAILBOX_LINK_LABEL,
  type ReplActorEvent,
  type ReplActorResult,
  type ReplActorStreamEvent,
} from "./repl_actor.ts";
import {
  createInputMailbox,
  type InputMailboxKind,
  InputMailboxStatus,
  mailboxKindCode,
  waitForInputMailbox,
} from "./input_mailbox.ts";
import type { ReplMediaBundle } from "./protocol.ts";

interface ActiveExecution {
  executionId: string;
  signal: AbortSignal;
  nextSequence: number;
  queue: ReplEvalQueue<OutputItem>;
  pendingDeliveries: Set<Deferred<void>>;
  execution?: ReplExecution;
  outputFailure?: Error;
  closed: boolean;
}

interface OutputItem {
  event: ReplActorStreamEvent;
  delivered: Deferred<void>;
}

const DISPLAY = Symbol.for("Jupyter.display");
const OUTPUT_QUEUE_CAPACITY = 64;
const CLIENT_ENV = "MAIEUTICS_REPL_CLIENT";
const originalConsole = globalThis.console;
let kernel: ReplKernel | undefined;
let inputLink: LinkHandle | undefined;
let active: ActiveExecution | undefined;

export const rpc = {
  async initialize(): Promise<void> {
    if (kernel !== undefined) {
      throw new Error("The Deno REPL actor is already initialized.");
    }
    await installMaieuticsNamespace();
    installHostEnvironment();
    kernel = await createReplKernel();
  },

  async *execute(
    executionId: string,
    code: string,
    signal: AbortSignal,
  ): AsyncIterable<ReplActorStreamEvent> {
    const repl = requireKernel();
    if (active !== undefined) {
      throw new Error("The Deno REPL actor only accepts one active execution.");
    }

    const context: ActiveExecution = {
      executionId,
      signal,
      nextSequence: 1,
      queue: new ReplEvalQueue<OutputItem>(OUTPUT_QUEUE_CAPACITY),
      pendingDeliveries: new Set(),
      closed: false,
    };
    active = context;
    const execution = repl.execute(code, { signal });
    context.execution = execution;
    const completion = enqueueTerminal(context, execution.result);
    completion.catch(() => {});
    let inFlight: OutputItem | undefined;
    try {
      while (true) {
        const item = await context.queue.dequeue();
        inFlight = item;
        yield item.event;
        deliver(context, item);
        inFlight = undefined;
        if (item.event.type === "terminal") {
          return;
        }
      }
    } finally {
      if (inFlight !== undefined) {
        deliver(context, inFlight);
      }
      context.closed = true;
      context.queue.close(new Error("The REPL output stream is closed."));
      for (const delivery of context.pendingDeliveries) {
        delivery.resolve();
      }
      context.pendingDeliveries.clear();
      execution.controller.abort(new DOMException("The REPL output stream ended.", "AbortError"));
      await completion.catch(() => {});
      if (active === context) {
        active = undefined;
      }
    }
  },

  async disposeRepl(): Promise<void> {
    const repl = kernel;
    kernel = undefined;
    if (repl !== undefined) {
      await repl.dispose();
    }
  },

  async deliverComm(message: {
    kind: number;
    commId: string;
    targetName?: string;
    data?: unknown;
    buffers: Uint8Array[];
  }): Promise<void> {
    const kind = message.kind === 0 ? "open" : message.kind === 2 ? "close" : "msg";
    deliverCommToHandlers(kind, message);
  },
};

serveWorker(rpc, {
  onLink(link: LinkHandle): void {
    if (link.label === INPUT_MAILBOX_LINK_LABEL) {
      inputLink = link;
    }
  },
});

function requireKernel(): ReplKernel {
  if (kernel === undefined) {
    throw new Error("The Deno REPL actor is not initialized.");
  }
  return kernel;
}

async function installMaieuticsNamespace(): Promise<void> {
  const moduleUrl = Deno.env.get(CLIENT_ENV);
  if (moduleUrl === undefined || moduleUrl.length === 0) {
    throw new Error(`Missing ${CLIENT_ENV} environment variable.`);
  }
  const namespace = await import(moduleUrl) as Record<string, unknown>;
  const injected = { ...namespace, comm: createCommProxy() };
  (globalThis as unknown as Record<string, unknown>).maieutics = injected;
}

interface CommProxy {
  open(commId: string, targetName?: string, data?: unknown): Promise<void>;
  msg(commId: string, data?: unknown, buffers?: Uint8Array[]): Promise<void>;
  close(commId: string, data?: unknown): Promise<void>;
  on(
    event: "open" | "msg" | "close",
    handler: (
      message: { commId: string; targetName?: string; data?: unknown; buffers: Uint8Array[] },
    ) => void,
  ): void;
}

const commHandlers: {
  open: Array<
    (
      message: { commId: string; targetName?: string; data?: unknown; buffers: Uint8Array[] },
    ) => void
  >;
  msg: Array<
    (
      message: { commId: string; targetName?: string; data?: unknown; buffers: Uint8Array[] },
    ) => void
  >;
  close: Array<
    (
      message: { commId: string; targetName?: string; data?: unknown; buffers: Uint8Array[] },
    ) => void
  >;
} = { open: [], msg: [], close: [] };

function createCommProxy(): CommProxy {
  const sendEvent = (
    type: "commOpen" | "commMsg" | "commClose",
    commId: string,
    targetName: string | undefined,
    data: unknown,
    buffers: Uint8Array[],
  ): Promise<void> => {
    return queueAsyncEvent((execution) => ({
      type,
      executionId: execution.executionId,
      sequence: execution.nextSequence++,
      commId,
      ...(targetName === undefined ? {} : { targetName }),
      ...(data === undefined ? {} : { data }),
      buffers,
    }));
  };

  return {
    open: (commId, targetName, data) => sendEvent("commOpen", commId, targetName, data, []),
    msg: (commId, data, buffers) => sendEvent("commMsg", commId, undefined, data, buffers ?? []),
    close: (commId, data) => sendEvent("commClose", commId, undefined, data, []),
    on(event, handler) {
      commHandlers[event].push(handler);
    },
  };
}

function deliverCommToHandlers(
  kind: "open" | "msg" | "close",
  message: { commId: string; targetName?: string; data?: unknown; buffers: Uint8Array[] },
): void {
  for (const handler of commHandlers[kind]) {
    handler(message);
  }
}

function installHostEnvironment(): void {
  globalThis.console = createConsole();
  const globals = globalThis as unknown as Record<string, unknown>;
  globals.prompt = (message = ""): string => blockingInput("prompt", String(message), false);
  globals.confirm = (message = ""): boolean => blockingInput("confirm", String(message), true);
  globals.alert = (message = ""): void => {
    blockingInput("alert", String(message), false);
  };
  (Deno as unknown as { jupyter: unknown }).jupyter = createJupyterApi();
}

/**
 * Blocking input for the synchronous prompt/confirm/alert globals: creates a
 * fresh mailbox, sends the request to the main thread (repl_client), and
 * blocks THIS thread in Atomics.wait until the main thread writes the answer,
 * interrupts the input, or the mailbox times out. The main thread's event loop
 * stays alive, so the async Jupyter round trip (and interrupt delivery)
 * continues while this worker thread is frozen.
 */
function blockingInput(kind: "prompt" | "alert", message: string, asBoolean: false): string;
function blockingInput(kind: "confirm", message: string, asBoolean: true): boolean;
function blockingInput(
  kind: "prompt" | "confirm" | "alert",
  message: string,
  asBoolean: boolean,
): string | boolean {
  const link = inputLink;
  const execution = active;
  if (!isOutputActive(execution) || link === undefined) {
    throw new Error("Input is only available during an active REPL execution.");
  }
  if (execution.outputFailure !== undefined) throw execution.outputFailure;

  const mailbox = createInputMailbox();
  mailbox.kind[0] = mailboxKindCode(kind);
  link.send({ sab: mailbox.sab, kind, prompt: message });
  const result = waitForInputMailbox(mailbox);
  if (result.error !== undefined) {
    throw new Error(result.error);
  }
  switch (result.status) {
    case InputMailboxStatus.interrupted:
      throw execution.signal.aborted
        ? execution.signal.reason
        : new DOMException("Input interrupted", "AbortError");
    case InputMailboxStatus.answered:
      if (asBoolean) return result.ok === true;
      return result.answer ?? "";
    case InputMailboxStatus.pending:
    default:
      throw new Error(`The ${kind} input timed out.`);
  }
}

function createConsole(): Console {
  const counts = new Map<string, number>();
  const timers = new Map<string, number>();
  let groupDepth = 0;
  const write = (stream: "stdout" | "stderr", values: unknown[]): void => {
    const prefix = "  ".repeat(groupDepth);
    queueConsole(stream, [prefix + formatConsoleArgs(values)]);
  };
  const replConsole = {
    log: (...values: unknown[]) => write("stdout", values),
    debug: (...values: unknown[]) => write("stdout", values),
    info: (...values: unknown[]) => write("stdout", values),
    dir: (...values: unknown[]) => write("stdout", values),
    dirxml: (...values: unknown[]) => write("stdout", values),
    table: (...values: unknown[]) => write("stdout", values),
    warn: (...values: unknown[]) => write("stderr", values),
    error: (...values: unknown[]) => write("stderr", values),
    trace: (...values: unknown[]) => {
      const trace = new Error(formatConsoleArgs(values)).stack ?? formatConsoleArgs(values);
      write("stderr", [trace]);
    },
    assert: (condition?: boolean, ...values: unknown[]) => {
      if (!condition) write("stderr", ["Assertion failed", ...values]);
    },
    count: (label = "default") => {
      const value = (counts.get(label) ?? 0) + 1;
      counts.set(label, value);
      write("stdout", [`${label}: ${value}`]);
    },
    countReset: (label = "default") => counts.delete(label),
    time: (label = "default") => timers.set(label, performance.now()),
    timeLog: (label = "default", ...values: unknown[]) => {
      write("stdout", [formatElapsed(label, timers), ...values]);
    },
    timeEnd: (label = "default") => {
      write("stdout", [formatElapsed(label, timers)]);
      timers.delete(label);
    },
    group: (...values: unknown[]) => {
      if (values.length > 0) write("stdout", values);
      groupDepth++;
    },
    groupCollapsed: (...values: unknown[]) => {
      if (values.length > 0) write("stdout", values);
      groupDepth++;
    },
    groupEnd: () => {
      groupDepth = Math.max(0, groupDepth - 1);
    },
    clear: () =>
      queueSyncEvent((execution) => ({
        type: "clearOutput",
        executionId: execution.executionId,
        sequence: execution.nextSequence++,
        wait: false,
      })),
    profile: () => {},
    profileEnd: () => {},
    timeStamp: () => {},
    context: () => replConsole,
  };
  return replConsole as unknown as Console;
}

function queueConsole(stream: "stdout" | "stderr", values: unknown[]): void {
  queueSyncEvent((execution) => ({
    type: "console",
    executionId: execution.executionId,
    sequence: execution.nextSequence++,
    stream,
    text: `${formatConsoleArgs(values)}\n`,
  }));
}

function queueSyncEvent(factory: (execution: ActiveExecution) => ReplActorEvent): void {
  const execution = active;
  if (!isOutputActive(execution)) {
    return;
  }
  const item = outputItem(execution, factory(execution));
  if (!execution.queue.tryEnqueue(item)) {
    execution.pendingDeliveries.delete(item.delivered);
    item.delivered.resolve();
    failOutput(execution, "The bounded REPL output queue is full.");
  }
}

async function queueAsyncEvent(
  factory: (execution: ActiveExecution) => ReplActorEvent,
): Promise<void> {
  const execution = active;
  if (!isOutputActive(execution)) {
    throw new Error("Output is only available during an active REPL execution.");
  }
  const item = outputItem(execution, factory(execution));
  await execution.queue.enqueue(item);
  await item.delivered.promise;
}

async function enqueueTerminal(
  execution: ActiveExecution,
  resultPromise: Promise<{
    ok: boolean;
    data?: unknown;
    error?: string;
    fatal?: boolean;
  }>,
): Promise<void> {
  const result = await resultPromise;
  if (execution.outputFailure === undefined && result.ok && isDisplayable(result.data)) {
    // A Displayable expression result renders through its $display symbol, matching the
    // official deno jupyter kernel: the value is displayed, not returned to the model.
    // Enqueue without awaiting delivery: the execute generator pulls the display event
    // before the terminal, and awaiting here would deadlock against that pull.
    try {
      const bundle = await normalizeMediaBundle(await result.data[DISPLAY]());
      const item = outputItem(execution, {
        type: "display",
        executionId: execution.executionId,
        sequence: execution.nextSequence++,
        data: bundle,
      });
      if (!execution.queue.tryEnqueue(item)) {
        execution.pendingDeliveries.delete(item.delivered);
        item.delivered.resolve();
        failOutput(execution, "The bounded REPL output queue is full.");
      }
      result.data = undefined;
    } catch (error) {
      result.data = undefined;
      result.ok = false;
      result.error = error instanceof Error ? error.message : String(error);
    }
  }
  const actorResult: ReplActorResult = execution.outputFailure === undefined
    ? {
      ok: result.ok,
      ...(result.data === undefined ? {} : { data: jsonSafeValue(result.data) }),
      ...(result.error === undefined ? {} : { error: result.error }),
      ...(result.fatal === undefined ? {} : { fatal: result.fatal }),
      ...(execution.signal.aborted ? { cancelled: true } : {}),
    }
    : {
      ok: false,
      error: execution.outputFailure.message,
      fatal: true,
    };
  if (execution.closed) return;
  const delivered = replEvalDeferred<void>();
  await execution.queue.enqueue({
    event: { type: "terminal", executionId: execution.executionId, result: actorResult },
    delivered,
  });
}

function outputItem(execution: ActiveExecution, event: ReplActorEvent): OutputItem {
  const delivered = replEvalDeferred<void>();
  execution.pendingDeliveries.add(delivered);
  return { event, delivered };
}

function deliver(execution: ActiveExecution, item: OutputItem): void {
  execution.pendingDeliveries.delete(item.delivered);
  item.delivered.resolve();
}

function failOutput(execution: ActiveExecution, message: string): void {
  if (execution.outputFailure !== undefined) return;
  execution.outputFailure = new Error(message);
  execution.execution?.controller.abort(execution.outputFailure);
}

function isOutputActive(
  execution: ActiveExecution | undefined,
): execution is ActiveExecution {
  return execution !== undefined && !execution.closed && kernel !== undefined &&
    kernel.current !== null;
}

function createJupyterApi(): typeof Deno.jupyter {
  const format = async (value: unknown): Promise<ReplMediaBundle> => {
    if (isDisplayable(value)) {
      return normalizeMediaBundle(await value[DISPLAY]());
    }
    return { "text/plain": Deno.inspect(value, { colors: false, depth: 6 }) };
  };

  const display = async (
    value: unknown,
    options: Deno.jupyter.DisplayOptions = {},
  ): Promise<void> => {
    const data = options.raw ? normalizeMediaBundle(value) : await format(value);
    // An update without a display id cannot target a previous display; the kernel presentation
    // adapter skips it, keeping the execution successful.
    await sendJupyterEvent(options.update ? "updateDisplay" : "display", {
      data,
      ...(options.display_id === undefined ? {} : { displayId: options.display_id }),
    });
  };

  const broadcast = async (
    messageType: string,
    content: Record<string, unknown>,
    extra?: { metadata?: Record<string, unknown>; buffers?: Uint8Array[] },
  ): Promise<void> => {
    if (messageType === "comm_open" || messageType === "comm_msg" || messageType === "comm_close") {
      if (!isRecord(content)) {
        throw new TypeError(`The '${messageType}' broadcast content must be an object.`);
      }
      const commId = typeof content.comm_id === "string" ? content.comm_id : "";
      if (commId.length === 0) {
        throw new TypeError(`The '${messageType}' broadcast requires a comm_id.`);
      }
      const buffers = extra?.buffers ?? [];
      const eventType = messageType === "comm_open"
        ? "commOpen"
        : messageType === "comm_close"
        ? "commClose"
        : "commMsg";
      await queueAsyncEvent((execution) => ({
        type: eventType,
        executionId: execution.executionId,
        sequence: execution.nextSequence++,
        commId,
        ...(messageType === "comm_open" && typeof content.target_name === "string"
          ? { targetName: content.target_name }
          : {}),
        ...(content.data === undefined ? {} : { data: content.data }),
        buffers,
      }));
      return;
    }
    if (messageType === "clear_output") {
      await sendJupyterEvent("clearOutput", { wait: content.wait === true });
      return;
    }
    if ((extra?.buffers?.length ?? 0) > 0) {
      throw new TypeError("Binary Jupyter broadcast buffers are not supported by this protocol.");
    }
    if (messageType !== "display_data" && messageType !== "update_display_data") {
      throw new TypeError(`Unsupported Jupyter broadcast message '${messageType}'.`);
    }
    const transient = isRecord(content.transient) ? content.transient : undefined;
    const displayId = typeof transient?.display_id === "string" ? transient.display_id : undefined;
    await sendJupyterEvent(
      messageType === "display_data" ? "display" : "updateDisplay",
      {
        data: normalizeMediaBundle(content.data),
        ...(displayId === undefined ? {} : { displayId }),
        ...(extra?.metadata === undefined ? {} : { metadata: extra.metadata }),
      },
    );
  };

  const tagged =
    (mime: string) =>
    (strings: TemplateStringsArray, ...values: unknown[]): Deno.jupyter.Displayable => ({
      [DISPLAY]: () => ({ [mime]: String.raw({ raw: strings }, ...values.map(String)) }),
    } as unknown as Deno.jupyter.Displayable);

  const image = (source: string | Uint8Array): Deno.jupyter.Displayable => ({
    [DISPLAY]: async () => {
      const data = typeof source === "string" ? await Deno.readFile(source) : source;
      return { [imageMime(source, data)]: encodeBase64(data) };
    },
  } as unknown as Deno.jupyter.Displayable);

  return {
    $display: DISPLAY as unknown as typeof Deno.jupyter.$display,
    display,
    format,
    broadcast,
    md: tagged("text/markdown"),
    html: tagged("text/html"),
    svg: tagged("image/svg+xml"),
    image,
  };
}

async function sendJupyterEvent(
  type: "display" | "updateDisplay" | "clearOutput",
  value: {
    data?: ReplMediaBundle;
    displayId?: string;
    metadata?: Record<string, unknown>;
    wait?: boolean;
  },
): Promise<void> {
  await queueAsyncEvent((execution) =>
    type === "clearOutput"
      ? {
        type,
        executionId: execution.executionId,
        sequence: execution.nextSequence++,
        wait: value.wait === true,
      }
      : {
        type,
        executionId: execution.executionId,
        sequence: execution.nextSequence++,
        data: value.data ?? { "text/plain": "" },
        ...(value.displayId === undefined ? {} : { displayId: value.displayId }),
        ...(value.metadata === undefined ? {} : { metadata: value.metadata }),
      }
  );
}

function normalizeMediaBundle(value: unknown): ReplMediaBundle {
  if (!isRecord(value)) {
    throw new TypeError("Raw Jupyter display data must be a MIME bundle object.");
  }
  const bundle: ReplMediaBundle = {};
  for (const [mime, content] of Object.entries(value)) {
    if (typeof content === "string" || isRecord(content)) {
      bundle[mime] = content;
    }
  }
  if (Object.keys(bundle).length === 0) {
    throw new TypeError("The Jupyter MIME bundle is empty or invalid.");
  }
  if (bundle["text/plain"] === undefined) {
    bundle["text/plain"] = Deno.inspect(value, { colors: false, depth: 4 });
  }
  return bundle;
}

function isDisplayable(value: unknown): value is { [DISPLAY]: () => unknown | Promise<unknown> } {
  return (typeof value === "object" && value !== null || typeof value === "function") &&
    typeof (value as Record<PropertyKey, unknown>)[DISPLAY] === "function";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function formatConsoleArgs(values: unknown[]): string {
  return values.map((value) =>
    typeof value === "string" ? value : Deno.inspect(value, { colors: false, depth: 6 })
  ).join(" ");
}

function formatElapsed(label: string, timers: Map<string, number>): string {
  const started = timers.get(label);
  return started === undefined
    ? `${label}: timer does not exist`
    : `${label}: ${(performance.now() - started).toFixed(3)}ms`;
}

function imageMime(source: string | Uint8Array, data: Uint8Array): "image/png" | "image/jpeg" {
  if (typeof source === "string" && /\.jpe?g$/i.test(source)) {
    return "image/jpeg";
  }
  return data[0] === 0xff && data[1] === 0xd8 ? "image/jpeg" : "image/png";
}

function encodeBase64(data: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < data.length; offset += 0x8000) {
    binary += String.fromCharCode(...data.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

function jsonSafeValue(value: unknown): unknown {
  try {
    const json = JSON.stringify(
      value,
      (_key, current) => typeof current === "bigint" ? current.toString() : current,
    );
    return json === undefined ? Deno.inspect(value, { colors: false, depth: 6 }) : JSON.parse(json);
  } catch {
    return Deno.inspect(value, { colors: false, depth: 6 });
  }
}

// Keep startup failures visible in the worker's stderr before console injection.
addEventListener("unhandledrejection", (event) => originalConsole.error(event.reason));
