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

export type McpDiscoverInput = McpDiscoverObjectInput | McpDiscoverFunctionInput;
export type McpDiscover = McpDiscoverObject | McpDiscoverFunction;

export interface ToolPreInvokeObjectInput {
  handler(context: ToolInvokeContext): ToolHookDecision | Promise<ToolHookDecision>;
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

export type ToolPreInvokeInput = ToolPreInvokeObjectInput | ToolPreInvokeFunctionInput;
export type ToolPreInvoke = ToolPreInvokeObject | ToolPreInvokeFunction;

export interface ToolPostInvokeObjectInput {
  handler(context: ToolPostInvokeContext): void | Promise<void>;
}

export interface ToolPostInvokeObject extends ToolPostInvokeObjectInput {
  readonly [ExtensionPoint.ToolPostInvoke]: true;
}

export type ToolPostInvokeFunctionInput = (context: ToolPostInvokeContext) => void | Promise<void>;

export type ToolPostInvokeFunction = ToolPostInvokeFunctionInput & {
  readonly [ExtensionPoint.ToolPostInvoke]: true;
};

export type ToolPostInvokeInput = ToolPostInvokeObjectInput | ToolPostInvokeFunctionInput;
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
      throw new TypeError(`Extension point '${name}' object must expose a handler function.`);
    }
  }
  Object.defineProperty(impl, symbol, { value: true, enumerable: false, configurable: false });
  if ((impl as unknown as Record<symbol, unknown>)[symbol] !== true) {
    throw new TypeError(`Extension point '${name}' marker could not be attached.`);
  }
  return impl as ExtensionPointImpl<K>;
}
