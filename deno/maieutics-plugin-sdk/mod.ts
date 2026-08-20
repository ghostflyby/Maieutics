/**
 * Maieutics plugin SDK.
 *
 * A plugin is a standard Deno package (deno.json with `name`, `version`,
 * `exports`, and the standard `permissions` field). The kernel discovers a
 * plugin by the presence of a `maieutics` marker field in its deno.json and
 * creates one worker per non-`.` exports entry. Each worker imports its export
 * module and scans the top-level exports for extension points.
 *
 * Extension points are identified by versioned global symbols
 * (`Symbol.for("maieutics/extensionPoint/v1/...")`). The host and every worker
 * isolate resolve the same symbol through the global registry, so identity
 * does not depend on module singletons. An export value belongs to an
 * extension point when it carries the marker symbol; the value is either an
 * object with a `handler` method or a callable function.
 */

const NAMESPACE = "maieutics/extensionPoint/v1";
const RPC_NAMESPACE = "maieutics/rpc/v1";

/**
 * Cross-plugin frames use string keys, not symbols: frames cross worker
 * boundaries through postMessage, and symbol-keyed properties are not carried
 * by structured clone. The marker is a versioned string field.
 */
const RpcFrame = {
  Kind: "__maieuticsRpc",
  Call: "call",
  Return: "return",
  Throw: "throw",
} as const;

/** Versioned extension point identity markers. */
export const ExtensionPoint = {
  McpDiscover: Symbol.for(`${NAMESPACE}/mcp.discover`),
  ToolPreInvoke: Symbol.for(`${NAMESPACE}/tools.preInvoke`),
  ToolPostInvoke: Symbol.for(`${NAMESPACE}/tools.postInvoke`),
} as const;

export type ExtensionPointName = keyof typeof ExtensionPoint;

/** Connection descriptor exported by `mcp.discover`; discovery only, never connection. */
export type McpDiscovery =
  | {
    module: string;
    transport: {
      type: "stdio";
      command: string;
      args?: readonly string[];
      env?: Readonly<Record<string, string>>;
    };
  }
  | {
    module: string;
    transport: {
      type: "http";
      url: string;
      headers?: Readonly<Record<string, string>>;
    };
  };

/** Why the host asked for a discovery pass. */
export interface DiscoverContext {
  readonly reason: "startup" | "config-changed";
}

/** Decision returned by a pre-invoke hook; hook chain semantics, not observation. */
export type ToolHookDecision =
  | { action: "continue" }
  | { action: "replace"; arguments?: Record<string, unknown> }
  | { action: "reject"; error: { code: string; message: string } };

/** Tool invocation as seen by a hook. */
export interface ToolInvokeContext {
  readonly tool: string;
  readonly arguments: Record<string, unknown>;
  readonly callId: string;
}

/** Tool invocation result as seen by a post hook; observation only. */
export interface ToolPostInvokeContext extends ToolInvokeContext {
  readonly status: "ok" | "error" | "cancelled";
  readonly result: unknown;
}

export interface McpDiscoverObjectInput {
  handler(context: DiscoverContext): McpDiscovery[] | Promise<McpDiscovery[]>;
}

export interface McpDiscoverObject extends McpDiscoverObjectInput {
  readonly [ExtensionPoint.McpDiscover]: true;
}

export type McpDiscoverFunctionInput = (
  context: DiscoverContext,
) => McpDiscovery[] | Promise<McpDiscovery[]>;

export type McpDiscoverFunction = McpDiscoverFunctionInput & {
  readonly [ExtensionPoint.McpDiscover]: true;
};

export type McpDiscoverInput =
  | McpDiscoverObjectInput
  | McpDiscoverFunctionInput;
export type McpDiscover = McpDiscoverObject | McpDiscoverFunction;

export interface ToolPreInvokeObjectInput {
  handler(
    context: ToolInvokeContext,
  ): ToolHookDecision | Promise<ToolHookDecision>;
}

export interface ToolPreInvokeObject extends ToolPreInvokeObjectInput {
  readonly [ExtensionPoint.ToolPreInvoke]: true;
}

export type ToolPreInvokeFunctionInput = (
  context: ToolInvokeContext,
) => ToolHookDecision | Promise<ToolHookDecision>;

export type ToolPreInvokeFunction = ToolPreInvokeFunctionInput & {
  readonly [ExtensionPoint.ToolPreInvoke]: true;
};

export type ToolPreInvokeInput =
  | ToolPreInvokeObjectInput
  | ToolPreInvokeFunctionInput;
export type ToolPreInvoke = ToolPreInvokeObject | ToolPreInvokeFunction;

export interface ToolPostInvokeObjectInput {
  handler(context: ToolPostInvokeContext): void | Promise<void>;
}

export interface ToolPostInvokeObject extends ToolPostInvokeObjectInput {
  readonly [ExtensionPoint.ToolPostInvoke]: true;
}

export type ToolPostInvokeFunctionInput = (
  context: ToolPostInvokeContext,
) => void | Promise<void>;

export type ToolPostInvokeFunction = ToolPostInvokeFunctionInput & {
  readonly [ExtensionPoint.ToolPostInvoke]: true;
};

export type ToolPostInvokeInput =
  | ToolPostInvokeObjectInput
  | ToolPostInvokeFunctionInput;
export type ToolPostInvoke = ToolPostInvokeObject | ToolPostInvokeFunction;

interface ExtensionPointShape<K extends ExtensionPointName> {
  context: K extends "McpDiscover" ? DiscoverContext
    : K extends "ToolPreInvoke" ? ToolInvokeContext
    : ToolPostInvokeContext;
  input: K extends "McpDiscover" ? McpDiscoverInput
    : K extends "ToolPreInvoke" ? ToolPreInvokeInput
    : ToolPostInvokeInput;
  impl: K extends "McpDiscover" ? McpDiscover
    : K extends "ToolPreInvoke" ? ToolPreInvoke
    : ToolPostInvoke;
}

export type ExtensionPointInput<K extends ExtensionPointName> = ExtensionPointShape<K>["input"];

export type ExtensionPointImpl<K extends ExtensionPointName> = ExtensionPointShape<K>["impl"];

/**
 * Declares an extension point implementation by attaching the versioned marker
 * symbol and validating the invocation shape. The returned value keeps the
 * original runtime identity.
 */
export function defineExtensionPoint<K extends ExtensionPointName>(
  name: K,
  impl: ExtensionPointInput<K>,
): ExtensionPointImpl<K> {
  const symbol = ExtensionPoint[name];
  const kind = typeof impl === "function" ? "function" : "object";
  if (kind === "function") {
    if (typeof impl !== "function") {
      throw new TypeError(
        `Extension point '${name}' must be a function or an object with a handler.`,
      );
    }
  } else {
    const object = impl as { handler?: unknown };
    if (typeof object.handler !== "function") {
      throw new TypeError(
        `Extension point '${name}' object must expose a handler function.`,
      );
    }
  }
  Object.defineProperty(impl, symbol, {
    value: true,
    enumerable: false,
    configurable: false,
  });
  if ((impl as unknown as Record<symbol, unknown>)[symbol] !== true) {
    throw new TypeError(
      `Extension point '${name}' marker could not be attached.`,
    );
  }
  return impl as ExtensionPointImpl<K>;
}

/**
 * Cross-plugin actor channels. A consumer plugin imports another plugin's
 * module specifier and gets a remote reference whose shape is identical to the
 * real module (the stub binds a callable for every top-level export). A call on
 * such an export marshals over the acquired MessagePort to the owner worker,
 * which serves the call through `serveActor`.
 *
 * The protocol is a minimal JSON-RPC with symbol-tagged frames; arguments and
 * results must be structured-cloneable values. A cross-plugin call cannot pass
 * or return object references into another worker's world. Channel closure
 * rejects pending calls.
 */

interface RpcRequest {
  readonly [RpcFrame.Kind]: typeof RpcFrame.Call;
  readonly id: number;
  readonly path: readonly string[];
  readonly args: readonly unknown[];
}

interface RpcResponse {
  readonly [RpcFrame.Kind]: typeof RpcFrame.Return;
  readonly id: number;
  readonly value: unknown;
}

interface RpcThrow {
  readonly [RpcFrame.Kind]: typeof RpcFrame.Throw;
  readonly id: number;
  readonly message: string;
}

type RpcFrameData = RpcRequest | RpcResponse | RpcThrow;

function isRequest(data: unknown): data is RpcRequest {
  return typeof data === "object" && data !== null &&
    (data as Record<string, unknown>)[RpcFrame.Kind] === RpcFrame.Call;
}

function isResponse(data: unknown): data is RpcResponse {
  return typeof data === "object" && data !== null &&
    (data as Record<string, unknown>)[RpcFrame.Kind] === RpcFrame.Return;
}

function isThrow(data: unknown): data is RpcThrow {
  return typeof data === "object" && data !== null &&
    (data as Record<string, unknown>)[RpcFrame.Kind] === RpcFrame.Throw;
}

/**
 * Serves calls for a cross-plugin surface over a MessagePort. `path` resolves
 * into `surface` (nested objects are traversed). A resolved function is
 * invoked with the request arguments; any other value is returned as-is (the
 * consumer's stub binds every export as a callable, so a value export is read
 * as a zero-argument call). Returns a detach function.
 */
export function serveActor(
  port: MessagePort,
  surface: Record<string, unknown>,
): () => void {
  port.start();
  const messageHandler = (event: MessageEvent): void => {
    const data = event.data;
    if (!isRequest(data)) return;
    const path = data.path;
    Promise.resolve()
      .then(() => {
        const target = path.reduce<unknown>(
          (current, segment) => {
            if (
              typeof current !== "object" || current === null ||
              !(segment in current)
            ) {
              throw new Error(
                `Remote member '${path.join(".")}' does not exist on the served surface.`,
              );
            }
            return (current as Record<string, unknown>)[segment];
          },
          surface,
        );
        if (typeof target === "function") {
          return (target as (...args: unknown[]) => unknown)(...data.args);
        }
        return target;
      })
      .then(
        (value) => {
          port.postMessage({
            [RpcFrame.Kind]: RpcFrame.Return,
            id: data.id,
            value,
          });
        },
        (error: Error) => {
          port.postMessage({
            [RpcFrame.Kind]: RpcFrame.Throw,
            id: data.id,
            message: error instanceof Error ? error.message : String(error),
          });
        },
      );
  };
  port.addEventListener("message", messageHandler);
  return () => port.removeEventListener("message", messageHandler);
}

interface PendingRemote {
  path: readonly string[];
  resolve(value: unknown): void;
  reject(error: Error): void;
}

/**
 * Creates a caller bound to one acquired channel. The channel is acquired
 * lazily on the first call (`importing gets the reference, first use gets the
 * connection`); every call on any stub binding shares it. `path` selects the
 * export on the owner surface; a path of `[]` addresses the module namespace
 * itself (used when a plugin module's default export is callable).
 */
export function createActorCaller(
  acquire: () => Promise<MessagePort>,
  callTimeoutMs = 15_000,
): (path: readonly string[], args: readonly unknown[]) => Promise<unknown> {
  let port: MessagePort | undefined;
  let pending = new Map<number, PendingRemote>();
  let nextId = 0;

  const onMessage = (event: MessageEvent): void => {
    const data = event.data;
    if (isResponse(data)) {
      const entry = pending.get(data.id);
      if (entry === undefined) return;
      pending.delete(data.id);
      entry.resolve(data.value);
    } else if (isThrow(data)) {
      const entry = pending.get(data.id);
      if (entry === undefined) return;
      pending.delete(data.id);
      entry.reject(new Error(data.message));
    }
  };

  return async (path, args) => {
    if (port === undefined) {
      port = await acquire();
        pending = new Map<number, PendingRemote>();
      port.start();
      port.addEventListener("message", onMessage);
    }
    const id = ++nextId;
    return await new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(
          new Error(`Remote call '${path.join(".") || "<module>"}' timed out.`),
        );
      }, callTimeoutMs);
      pending.set(id, {
        path,
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
      port!.postMessage({ [RpcFrame.Kind]: RpcFrame.Call, id, path, args });
    });
  };
}
