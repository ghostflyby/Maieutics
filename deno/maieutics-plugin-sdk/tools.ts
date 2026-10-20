/**
 * Model-callable plugin tools.
 *
 * A tool export declares one typed function plus the JSON Schema the model sees.
 * The schema is the single source of truth: it renders the provider-visible input
 * schema, validates the model's arguments at the worker boundary, and infers the
 * handler's parameter type. There is no second descriptor to keep in sync.
 *
 * The kernel describes the export once per registry revision (`phase: "describe"`)
 * and invokes it per model tool call (`phase: "call"`). Both phases route through
 * the same extension point marker, so a tool is ordinary extension-point machinery
 * on the kernel side: registered by export, fingerprinted by the worker entry, and
 * gated by the plugin approval record.
 *
 * ```ts
 * export const search = defineTool({
 *   name: "docs_search",
 *   description: "Search the documentation index.",
 *   input: schema.object({
 *     query: schema.string({ description: "Free-text query." }),
 *     limit: schema.default(schema.integer({ minimum: 1 }), 10),
 *   }),
 *   async handler({ query, limit }, ctx) {
 *     return { matches: await index.search(query, { limit, signal: ctx.signal }) };
 *   },
 * });
 * ```
 */

import {
  defineExtensionPoint,
  type ToolDefinitionPayload,
  type ToolDispatch,
  type ToolImplementation,
  type ToolRequest,
  type ToolResponse,
} from "./mod.ts";

export type {
  ToolCallRequest,
  ToolDefinitionPayload,
  ToolDescribeRequest,
  ToolDispatch,
  ToolImplementation,
  ToolImplementationInput,
  ToolRequest,
  ToolResponse,
} from "./mod.ts";

/** A schema node: the provider-visible JSON Schema plus its runtime parser. */
export interface ToolSchema<T> {
  /** The JSON Schema fragment rendered into the tool's input schema. */
  readonly jsonSchema: Record<string, unknown>;
  /** Validates one untrusted value and narrows it to `T`. */
  readonly parse: (value: unknown) => T;
}

interface SchemaNode<T> extends ToolSchema<T> {
  /** Set by {@link schema.optional}: the property may be absent. */
  readonly optionalProperty?: boolean;
  /** Set by {@link schema.default}: the property may be absent and defaults. */
  readonly defaultValue?: T;
}

/** Raised by a handler for an expected, model-recoverable failure. */
export class ToolError extends Error {
  /** A stable machine-readable code returned to the model. */
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "ToolError";
    this.code = code;
  }
}

/** Raised by the schema parser for invalid model arguments. */
class ToolInputError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ToolInputError";
  }
}

export type Shape = Record<string, ToolSchema<unknown>>;

/** The parsed object type of a schema shape (optional properties stay `T | undefined`). */
export type InferShape<S extends Shape> = {
  [K in keyof S]: S[K] extends ToolSchema<infer T> ? T : never;
};

interface ObjectOptions {
  readonly description?: string;
  /** Emit and accept unknown properties. Defaults to false: an unknown key is an input error. */
  readonly additionalProperties?: boolean;
}

/** The value constraints one property schema accepts. */
export interface StringOptions {
  readonly description?: string;
  readonly minLength?: number;
  readonly maxLength?: number;
  readonly pattern?: string;
}

/** The range constraints one numeric property schema accepts. */
export interface NumberOptions {
  readonly description?: string;
  readonly minimum?: number;
  readonly maximum?: number;
}

/** The description one boolean property schema accepts. */
export interface BooleanOptions {
  readonly description?: string;
}

/** The length constraints one array property schema accepts. */
export interface ArrayOptions {
  readonly description?: string;
  readonly minItems?: number;
  readonly maxItems?: number;
}

/** The builders that render JSON Schema and parse the matching value. */
export interface ToolSchemaBuilders {
  object<S extends Shape>(shape: S, options?: ObjectOptions): ToolSchema<InferShape<S>>;
  string(options?: StringOptions): ToolSchema<string>;
  number(options?: NumberOptions): ToolSchema<number>;
  integer(options?: NumberOptions): ToolSchema<number>;
  boolean(options?: BooleanOptions): ToolSchema<boolean>;
  array<Item>(items: ToolSchema<Item>, options?: ArrayOptions): ToolSchema<Item[]>;
  enum<const Values extends readonly string[]>(values: Values): ToolSchema<Values[number]>;
  literal<const Value extends string | number | boolean>(value: Value): ToolSchema<Value>;
  nullable<T>(inner: ToolSchema<T>): ToolSchema<T | null>;
  optional<T>(inner: ToolSchema<T>): ToolSchema<T | undefined>;
  default<T>(inner: ToolSchema<T>, value: T): ToolSchema<T>;
  raw<T>(jsonSchema: Record<string, unknown>): ToolSchema<T>;
}

function withDescription(
  schema: Record<string, unknown>,
  description: string | undefined,
): Record<string, unknown> {
  return description === undefined ? schema : { ...schema, description };
}

/** The schema builders. Every builder produces both the JSON Schema and its parser. */
export const schema: ToolSchemaBuilders = {
  object<S extends Shape>(shape: S, options?: ObjectOptions): ToolSchema<InferShape<S>> {
    const properties: Record<string, unknown> = {};
    const required: string[] = [];
    for (const [key, child] of Object.entries(shape)) {
      properties[key] = child.jsonSchema;
      const node = child as SchemaNode<unknown>;
      if (node.optionalProperty !== true && node.defaultValue === undefined) required.push(key);
    }

    const jsonSchema: Record<string, unknown> = withDescription(
      {
        type: "object",
        properties,
        required,
        additionalProperties: options?.additionalProperties === true,
      },
      options?.description,
    );

    const additional = options?.additionalProperties === true;
    return {
      jsonSchema,
      parse: (value: unknown): InferShape<S> => {
        if (value === null || typeof value !== "object" || Array.isArray(value)) {
          throw new ToolInputError("The tool input must be a JSON object.");
        }

        const record = value as Record<string, unknown>;
        const parsed: Record<string, unknown> = {};
        for (const [key, child] of Object.entries(shape)) {
          const node = child as SchemaNode<unknown>;
          const present = Object.hasOwn(record, key) && record[key] !== undefined;
          if (!present) {
            if (node.defaultValue !== undefined) {
              parsed[key] = node.defaultValue;
              continue;
            }
            if (node.optionalProperty === true) continue;
            throw new ToolInputError(`The tool input is missing the required property '${key}'.`);
          }

          try {
            parsed[key] = child.parse(record[key]);
          } catch (error) {
            const reason = error instanceof Error ? error.message : String(error);
            throw new ToolInputError(`The property '${key}' is invalid: ${reason}`);
          }
        }

        if (additional) {
          for (const key of Object.keys(record)) {
            if (!Object.hasOwn(shape, key)) parsed[key] = record[key];
          }
        } else {
          for (const key of Object.keys(record)) {
            if (!Object.hasOwn(shape, key)) {
              throw new ToolInputError(`The tool input has an unknown property '${key}'.`);
            }
          }
        }

        return parsed as InferShape<S>;
      },
    };
  },

  string(options?: StringOptions): ToolSchema<string> {
    const jsonSchema: Record<string, unknown> = {
      type: "string",
      ...(options?.minLength === undefined ? {} : { minLength: options.minLength }),
      ...(options?.maxLength === undefined ? {} : { maxLength: options.maxLength }),
      ...(options?.pattern === undefined ? {} : { pattern: options.pattern }),
    };
    return {
      jsonSchema: withDescription(jsonSchema, options?.description),
      parse: (value: unknown): string => {
        if (typeof value !== "string") throw new ToolInputError("Expected a string.");
        if (options?.minLength !== undefined && value.length < options.minLength) {
          throw new ToolInputError(`Expected at least ${options.minLength} characters.`);
        }
        if (options?.maxLength !== undefined && value.length > options.maxLength) {
          throw new ToolInputError(`Expected at most ${options.maxLength} characters.`);
        }
        if (options?.pattern !== undefined && !new RegExp(options.pattern).test(value)) {
          throw new ToolInputError(`Expected a value matching '${options.pattern}'.`);
        }
        return value;
      },
    };
  },

  number(options?: NumberOptions): ToolSchema<number> {
    return numberSchema("number", options);
  },

  integer(options?: NumberOptions): ToolSchema<number> {
    const inner = numberSchema("integer", options);
    return {
      jsonSchema: inner.jsonSchema,
      parse: (value: unknown): number => {
        const parsed = inner.parse(value);
        if (!Number.isInteger(parsed)) throw new ToolInputError("Expected an integer.");
        return parsed;
      },
    };
  },

  boolean(options?: BooleanOptions): ToolSchema<boolean> {
    return {
      jsonSchema: withDescription({ type: "boolean" }, options?.description),
      parse: (value: unknown): boolean => {
        if (typeof value !== "boolean") throw new ToolInputError("Expected a boolean.");
        return value;
      },
    };
  },

  array<Item>(items: ToolSchema<Item>, options?: ArrayOptions): ToolSchema<Item[]> {
    const jsonSchema: Record<string, unknown> = {
      type: "array",
      items: items.jsonSchema,
      ...(options?.minItems === undefined ? {} : { minItems: options.minItems }),
      ...(options?.maxItems === undefined ? {} : { maxItems: options.maxItems }),
    };
    return {
      jsonSchema: withDescription(jsonSchema, options?.description),
      parse: (value: unknown): Item[] => {
        if (!Array.isArray(value)) throw new ToolInputError("Expected an array.");
        if (options?.minItems !== undefined && value.length < options.minItems) {
          throw new ToolInputError(`Expected at least ${options.minItems} items.`);
        }
        if (options?.maxItems !== undefined && value.length > options.maxItems) {
          throw new ToolInputError(`Expected at most ${options.maxItems} items.`);
        }
        return value.map((item, index) => {
          try {
            return items.parse(item);
          } catch (error) {
            const reason = error instanceof Error ? error.message : String(error);
            throw new ToolInputError(`Item ${index} is invalid: ${reason}`);
          }
        });
      },
    };
  },

  enum<const Values extends readonly string[]>(values: Values): ToolSchema<Values[number]> {
    return {
      jsonSchema: { type: "string", enum: [...values] },
      parse: (value: unknown): Values[number] => {
        if (typeof value !== "string" || !values.includes(value as Values[number])) {
          throw new ToolInputError(`Expected one of: ${values.join(", ")}.`);
        }
        return value as Values[number];
      },
    };
  },

  literal<const Value extends string | number | boolean>(value: Value): ToolSchema<Value> {
    return {
      jsonSchema: { const: value },
      parse: (candidate: unknown): Value => {
        if (candidate !== value) throw new ToolInputError(`Expected ${JSON.stringify(value)}.`);
        return value as Value;
      },
    };
  },

  nullable<T>(inner: ToolSchema<T>): ToolSchema<T | null> {
    return {
      jsonSchema: {
        anyOf: [inner.jsonSchema, { type: "null" }],
      },
      parse: (value: unknown): T | null => value === null ? null : inner.parse(value),
    };
  },

  optional<T>(inner: ToolSchema<T>): ToolSchema<T | undefined> {
    return {
      jsonSchema: inner.jsonSchema,
      parse: (value: unknown): T | undefined => inner.parse(value),
      optionalProperty: true,
    } as SchemaNode<T | undefined>;
  },

  default<T>(inner: ToolSchema<T>, value: T): ToolSchema<T> {
    return {
      jsonSchema: { ...inner.jsonSchema, default: value },
      parse: (candidate: unknown): T => inner.parse(candidate),
      defaultValue: value,
    } as SchemaNode<T>;
  },

  /**
   * Escape hatch for a JSON Schema the builders above cannot express. The kernel only
   * enforces the top-level object shape and size; the handler receives the value as-is.
   */
  raw<T>(jsonSchema: Record<string, unknown>): ToolSchema<T> {
    return {
      jsonSchema,
      parse: (value: unknown): T => value as T,
    };
  },
};

function numberSchema(
  type: "number" | "integer",
  options: NumberOptions | undefined,
): ToolSchema<number> {
  const jsonSchema: Record<string, unknown> = {
    type,
    ...(options?.minimum === undefined ? {} : { minimum: options.minimum }),
    ...(options?.maximum === undefined ? {} : { maximum: options.maximum }),
  };
  return {
    jsonSchema: withDescription(jsonSchema, options?.description),
    parse: (value: unknown): number => {
      if (typeof value !== "number" || !Number.isFinite(value)) {
        throw new ToolInputError("Expected a number.");
      }
      if (options?.minimum !== undefined && value < options.minimum) {
        throw new ToolInputError(`Expected a value of at least ${options.minimum}.`);
      }
      if (options?.maximum !== undefined && value > options.maximum) {
        throw new ToolInputError(`Expected a value of at most ${options.maximum}.`);
      }
      return value;
    },
  };
}

/** The per-call context handed to a tool handler. */
export interface ToolCallContext {
  /** The kernel's tool-call identity, stable for the duration of one call. */
  readonly callId: string;
  /** The owning Agent session. */
  readonly sessionId: string;
  /** Aborts when the kernel cancels the call or its timeout expires. */
  readonly signal: AbortSignal;
}

/**
 * Optional classification hints for a tool.
 *
 * These are hints, not enforcement: the kernel records them for approval and
 * presentation surfaces, and no authorization decision depends on them. Declare
 * them honestly — a tool that mutates state and omits `readOnly: true` is
 * misdeclared metadata, and a caller that trusts `readOnly` on a mutating tool
 * has been misled by the declaration, not protected by the runtime.
 *
 * The defaults follow MCP's `ToolAnnotations`: a tool is assumed non-read-only,
 * possibly destructive, and non-idempotent, and is assumed to reach an open
 * world unless told otherwise.
 */
export interface ToolAnnotations {
  /** A human-readable display name; may contain spaces and punctuation, unlike `name`. */
  readonly title?: string;
  /** The tool only reads, and changes no state. Defaults to false. */
  readonly readOnly?: boolean;
  /** The tool may destroy data or resources. Defaults to true (pessimistic). */
  readonly destructive?: boolean;
  /** Repeating the call with identical arguments has no additional effect. Defaults to false. */
  readonly idempotent?: boolean;
  /** The tool reaches an open, unpredictable set of external entities. Defaults to true. */
  readonly openWorld?: boolean;
}

/** The declaration a plugin author writes for one tool. */
export interface ToolOptions<T> {
  /** The provider-visible name: 1-64 ASCII letters, digits, underscores, or hyphens. */
  readonly name: string;
  /** The provider-visible description. */
  readonly description: string;
  /** The input contract; it renders the JSON Schema and validates the model's arguments. */
  readonly input: ToolSchema<T>;
  /** The handler's time budget in milliseconds (1000-600000), measured from the moment the
   * handler starts. Worker startup is not charged to it: the kernel's wait adds its own
   * allowance for an on-demand spawn. Defaults to the kernel's 120 s. */
  readonly timeoutMs?: number;
  /** Classification hints for approval and presentation surfaces. */
  readonly annotations?: ToolAnnotations;
  /** Executes one validated call. Return a JSON-serializable value. */
  handler(input: T, context: ToolCallContext): unknown | Promise<unknown>;
}

/** The marked extension-point export `defineTool` returns. */
export type Tool = ToolImplementation;

const TOOL_NAME_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;
const MAXIMUM_DESCRIPTION_LENGTH = 4096;
const MAXIMUM_TITLE_LENGTH = 128;
const MINIMUM_TIMEOUT_MS = 1_000;
const MAXIMUM_TIMEOUT_MS = 600_000;

/** Validates and copies the declared hints, or returns undefined when none were given. */
function NormalizeAnnotations(
  toolName: string,
  annotations: ToolAnnotations | undefined,
): ToolAnnotations | undefined {
  if (annotations === undefined) return undefined;

  const normalized: {
    title?: string;
    readOnly?: boolean;
    destructive?: boolean;
    idempotent?: boolean;
    openWorld?: boolean;
  } = {};

  if (annotations.title !== undefined) {
    const title = annotations.title.trim();
    if (title.length === 0 || title.length > MAXIMUM_TITLE_LENGTH) {
      throw new TypeError(
        `Tool '${toolName}' annotation title must be 1-${MAXIMUM_TITLE_LENGTH} characters.`,
      );
    }
    normalized.title = title;
  }

  for (const key of ["readOnly", "destructive", "idempotent", "openWorld"] as const) {
    const value = annotations[key];
    if (value === undefined) continue;
    if (typeof value !== "boolean") {
      throw new TypeError(`Tool '${toolName}' annotation '${key}' must be a boolean.`);
    }
    normalized[key] = value;
  }

  // A declaration with no members is not worth sending; the kernel applies the same
  // defaults, so an empty hint set and an absent hint set must mean the same thing.
  return Object.keys(normalized).length === 0 ? undefined : normalized;
}

/** The AbortControllers of calls currently executing in this worker, keyed by call id. */
const pendingCalls = new Map<string, AbortController>();

/** Call ids cancelled before their dispatch registered a controller. A cancel frame can
 * reach the worker while it is still starting (the kernel posts it the moment it stops
 * waiting), and the dispatch that follows must observe it: otherwise the handler runs to
 * completion, side effects included, and only the value is discarded. Bounded because a
 * cancel for a call this worker never receives would otherwise accumulate forever. */
const earlyCancels = new Set<string>();
const MAXIMUM_EARLY_CANCELS = 256;
let cancelListenerInstalled = false;

/** Aborts a running call when the kernel's cancel frame reaches this worker. */
function installCancelListener(): void {
  if (cancelListenerInstalled) return;
  const scope = globalThis as {
    addEventListener?: (type: string, listener: (event: MessageEvent) => void) => void;
  };
  if (typeof scope.addEventListener !== "function") return;
  cancelListenerInstalled = true;
  scope.addEventListener("message", (event: MessageEvent) => {
    const frame = event.data as { type?: unknown; callId?: unknown };
    if (frame?.type !== "tool.cancel" || typeof frame.callId !== "string") return;

    const controller = pendingCalls.get(frame.callId);
    if (controller !== undefined) {
      controller.abort();
      return;
    }

    // No dispatch yet: remember the cancel so the dispatch aborts immediately.
    if (earlyCancels.size >= MAXIMUM_EARLY_CANCELS) {
      // Drop the oldest: an unbounded set would grow for cancels this worker never sees.
      const oldest = earlyCancels.values().next().value;
      if (oldest !== undefined) earlyCancels.delete(oldest);
    }
    earlyCancels.add(frame.callId);
  });
}

/**
 * Declares one model-callable tool. The returned value carries the `Tool` extension
 * point marker and is dispatched with the describe/call phases the kernel speaks.
 */
export function defineTool<T>(options: ToolOptions<T>): Tool {
  if (!TOOL_NAME_PATTERN.test(options.name)) {
    throw new TypeError(
      `Tool name '${options.name}' is invalid; use 1-64 ASCII letters, digits, underscores, or hyphens.`,
    );
  }
  // Installed at declaration time, not at first dispatch: a cancel frame can reach this
  // worker while it is still starting (the kernel posts it the moment it stops waiting),
  // and a listener that only exists once a call has begun would never see it.
  installCancelListener();
  const description = options.description.trim();
  if (description.length === 0) {
    throw new TypeError(`Tool '${options.name}' must have a non-empty description.`);
  }
  if (description.length > MAXIMUM_DESCRIPTION_LENGTH) {
    throw new TypeError(
      `Tool '${options.name}' description exceeds ${MAXIMUM_DESCRIPTION_LENGTH} characters.`,
    );
  }
  const timeoutMs = options.timeoutMs;
  if (
    timeoutMs !== undefined &&
    (!Number.isInteger(timeoutMs) || timeoutMs < MINIMUM_TIMEOUT_MS ||
      timeoutMs > MAXIMUM_TIMEOUT_MS)
  ) {
    throw new TypeError(
      `Tool '${options.name}' timeoutMs must be an integer between ${MINIMUM_TIMEOUT_MS} and ${MAXIMUM_TIMEOUT_MS}.`,
    );
  }
  const inputSchema = options.input.jsonSchema;
  if (inputSchema.type !== "object") {
    throw new TypeError(`Tool '${options.name}' input schema must be a JSON object schema.`);
  }
  const annotations = NormalizeAnnotations(options.name, options.annotations);

  const definition: ToolDefinitionPayload = {
    name: options.name,
    description,
    inputSchema,
    ...(timeoutMs === undefined ? {} : { timeoutMs }),
    ...(annotations === undefined ? {} : { annotations }),
  };

  const dispatch: ToolDispatch = Object.assign(
    async (
      request: ToolRequest,
    ): Promise<ToolDefinitionPayload | ToolResponse> => {
      if (request?.phase === "describe") return definition;
      if (request?.phase !== "call") {
        return { outcome: "error", code: "tool_request_invalid", message: "Unknown tool phase." };
      }

      const controller = new AbortController();
      // A cancel that arrived before this dispatch registered (the worker was still
      // starting) must take effect now, not be lost.
      if (earlyCancels.delete(request.callId)) controller.abort();
      pendingCalls.set(request.callId, controller);

      // The handler's own budget starts here, when the handler is about to run — not when
      // the kernel sent the frame. The kernel's wait also covers this worker's cold start
      // (an on-demand spawn per ADR 0035), and charging that startup time to the handler
      // would report a timeout for work that never got its full budget.
      let timedOut = false;
      const timer = timeoutMs === undefined ? undefined : setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, timeoutMs);

      try {
        let input: T;
        try {
          input = options.input.parse(request.arguments);
        } catch (error) {
          return {
            outcome: "error",
            code: "tool_input_invalid",
            message: error instanceof Error ? error.message : String(error),
          };
        }

        const value = await options.handler(input, {
          callId: request.callId,
          sessionId: request.sessionId,
          signal: controller.signal,
        });
        // A cancelled call never reports success: the kernel already stopped waiting, so a
        // value produced after the abort has no consumer.
        if (controller.signal.aborted) {
          return timedOut
            ? {
              outcome: "error",
              code: "tool_timeout",
              message: `Tool '${options.name}' did not complete within ${timeoutMs} ms.`,
            }
            : {
              outcome: "error",
              code: "tool_cancelled",
              message: `Tool '${options.name}' was cancelled.`,
            };
        }
        return { outcome: "ok", value: value === undefined ? null : value };
      } catch (error) {
        if (error instanceof ToolError) {
          return { outcome: "error", code: error.code, message: error.message };
        }
        if (controller.signal.aborted) {
          // The budget is the worker's own verdict, so it reports a timeout rather than
          // leaving the kernel's coarser "did not complete" message as the only signal.
          return timedOut
            ? {
              outcome: "error",
              code: "tool_timeout",
              message: `Tool '${options.name}' did not complete within ${timeoutMs} ms.`,
            }
            : {
              outcome: "error",
              code: "tool_cancelled",
              message: `Tool '${options.name}' was cancelled.`,
            };
        }
        return {
          outcome: "error",
          code: "tool_failed",
          message: error instanceof Error ? error.message : String(error),
        };
      } finally {
        if (timer !== undefined) clearTimeout(timer);
        pendingCalls.delete(request.callId);
      }
    },
    {},
  ) as ToolDispatch;

  return defineExtensionPoint("Tool", dispatch) as unknown as Tool;
}
