import { assert, assertEquals, assertThrows } from "@std/assert";
import { defineTool, ExtensionPoint, schema, ToolError } from "./entry.ts";

Deno.test("declare a tool renders the provider-visible schema", async () => {
  const tool = defineTool({
    name: "docs_search",
    description: "Search the documentation index.",
    input: schema.object({
      query: schema.string({ description: "Free-text query.", minLength: 1, maxLength: 500 }),
      limit: schema.default(schema.integer({ minimum: 1, maximum: 50 }), 10),
      scopes: schema.optional(schema.array(schema.string())),
    }),
    handler: () => [],
  });

  assertEquals(tool[ExtensionPoint.Tool], true);
  const definition = await tool({ phase: "describe" });
  assertEquals(definition, {
    name: "docs_search",
    description: "Search the documentation index.",
    inputSchema: {
      type: "object",
      properties: {
        query: {
          type: "string",
          description: "Free-text query.",
          minLength: 1,
          maxLength: 500,
        },
        limit: { type: "integer", minimum: 1, maximum: 50, default: 10 },
        scopes: { type: "array", items: { type: "string" } },
      },
      required: ["query"],
      additionalProperties: false,
    },
  });
});

Deno.test("call parses the input, applies defaults, and infers parameter types", async () => {
  const tool = defineTool({
    name: "echo",
    description: "Echo the input.",
    input: schema.object({
      text: schema.string(),
      count: schema.default(schema.integer(), 3),
      note: schema.optional(schema.string()),
    }),
    handler: (input) => {
      // Compile-time proof of the inference: these only typecheck with the parsed types.
      const text: string = input.text;
      const count: number = input.count;
      const note: string | undefined = input.note;
      return { text, count, note: note ?? null };
    },
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: { text: "hi" },
  });
  assertEquals(result, { outcome: "ok", value: { text: "hi", count: 3, note: null } });
});

Deno.test("unknown properties are rejected, not silently dropped", async () => {
  const tool = defineTool({
    name: "strict",
    description: "Strict input.",
    input: schema.object({ value: schema.string() }),
    handler: () => "unreachable",
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: { value: "ok", extra: true },
  });
  assertEquals(result.outcome, "error");
  if (result.outcome === "error") assertEquals(result.code, "tool_input_invalid");
});

Deno.test("additionalProperties accepts unknown keys when declared", async () => {
  const tool = defineTool({
    name: "open",
    description: "Open input.",
    input: schema.object({ value: schema.string() }, { additionalProperties: true }),
    handler: (input) => input,
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: { value: "ok", extra: { nested: 1 } },
  });
  assertEquals(result, {
    outcome: "ok",
    value: { value: "ok", extra: { nested: 1 } },
  });
});

Deno.test("a missing required property and a wrong type are typed input errors", async () => {
  const tool = defineTool({
    name: "typed",
    description: "Typed input.",
    input: schema.object({ count: schema.integer({ minimum: 1 }) }),
    handler: () => "unreachable",
  });

  const missing = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(missing, {
    outcome: "error",
    code: "tool_input_invalid",
    message: "The tool input is missing the required property 'count'.",
  });

  const fractional = await tool({
    phase: "call",
    callId: "call-2",
    sessionId: "session-1",
    arguments: { count: 1.5 },
  });
  assertEquals(fractional, {
    outcome: "error",
    code: "tool_input_invalid",
    message: "The property 'count' is invalid: Expected an integer.",
  });
});

Deno.test("ToolError maps to a recoverable model-facing error", async () => {
  const tool = defineTool({
    name: "failing",
    description: "Always fails.",
    input: schema.object({}),
    handler: () => {
      throw new ToolError("no_match", "Nothing matched.");
    },
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result, { outcome: "error", code: "no_match", message: "Nothing matched." });
});

Deno.test("an unexpected handler exception becomes tool_failed", async () => {
  const tool = defineTool({
    name: "crashing",
    description: "Throws.",
    input: schema.object({}),
    handler: () => {
      throw new Error("boom");
    },
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result, { outcome: "error", code: "tool_failed", message: "boom" });
});

Deno.test("a void handler returns null, not undefined", async () => {
  const tool = defineTool({
    name: "noop",
    description: "Does nothing.",
    input: schema.object({}),
    handler: () => {},
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result, { outcome: "ok", value: null });
});

Deno.test("defineTool rejects invalid declarations at load time", () => {
  assertThrows(
    () =>
      defineTool({
        name: "has space",
        description: "Invalid name.",
        input: schema.object({}),
        handler: () => null,
      }),
    TypeError,
  );
  assertThrows(
    () =>
      defineTool({
        name: "blank",
        description: "   ",
        input: schema.object({}),
        handler: () => null,
      }),
    TypeError,
  );
  assertThrows(
    () =>
      defineTool({
        name: "bad-timeout",
        description: "Invalid timeout.",
        input: schema.object({}),
        timeoutMs: 10,
        handler: () => null,
      }),
    TypeError,
  );
  assertThrows(
    () =>
      defineTool({
        name: "bad-schema",
        description: "Non-object schema.",
        input: schema.string(),
        handler: () => null,
      }),
    TypeError,
  );
});

Deno.test("the tool cancel frame aborts the running handler's signal", async () => {
  let observed: AbortSignal | undefined;
  let release: (() => void) | undefined;
  const tool = defineTool({
    name: "cancellable",
    description: "Waits for an abort.",
    input: schema.object({}),
    handler: async (_input, ctx) => {
      observed = ctx.signal;
      await new Promise<void>((resolve) => {
        release = resolve;
        ctx.signal.addEventListener("abort", () => resolve(), { once: true });
      });
      return "released";
    },
  });

  const call = tool({
    phase: "call",
    callId: "call-cancel",
    sessionId: "session-1",
    arguments: {},
  });

  // The handler registers its abort listener asynchronously; yield until it has.
  for (let attempt = 0; attempt < 100 && observed === undefined; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 1));
  }
  assert(observed !== undefined, "the handler received a signal");

  globalThis.dispatchEvent(
    new MessageEvent("message", { data: { type: "tool.cancel", callId: "call-cancel" } }),
  );
  release?.();

  const result = await call;
  assertEquals(result, {
    outcome: "error",
    code: "tool_cancelled",
    message: "Tool 'cancellable' was cancelled.",
  });
});

Deno.test("schema escape hatch passes the raw value through", async () => {
  const tool = defineTool({
    name: "raw",
    description: "Raw schema.",
    input: schema.raw<Record<string, unknown>>({
      type: "object",
      properties: { anything: { type: "string" } },
    }),
    handler: (input) => input,
  });

  const result = await tool({
    phase: "call",
    callId: "call-1",
    sessionId: "session-1",
    arguments: { anything: "value", extra: 1 },
  });
  assertEquals(result, { outcome: "ok", value: { anything: "value", extra: 1 } });
});

Deno.test("annotations are omitted when undeclared and carried when declared", async () => {
  const bare = defineTool({
    name: "bare",
    description: "No annotations.",
    input: schema.object({}),
    handler: () => null,
  });
  const bareDefinition = await bare({ phase: "describe" });
  assertEquals("annotations" in bareDefinition, false);

  const hinted = defineTool({
    name: "hinted",
    description: "Annotated.",
    input: schema.object({}),
    annotations: {
      title: "  Search Index  ",
      readOnly: true,
      destructive: false,
      idempotent: true,
      openWorld: false,
    },
    handler: () => null,
  });
  const hintedDefinition = await hinted({ phase: "describe" });
  assertEquals("annotations" in hintedDefinition, true);
  if ("annotations" in hintedDefinition) {
    assertEquals(hintedDefinition.annotations, {
      title: "Search Index",
      readOnly: true,
      destructive: false,
      idempotent: true,
      openWorld: false,
    });
  }
});

Deno.test("a partial annotation block keeps only the declared members", async () => {
  const tool = defineTool({
    name: "partial",
    description: "Partially annotated.",
    input: schema.object({}),
    annotations: { readOnly: true },
    handler: () => null,
  });

  const definition = await tool({ phase: "describe" });
  if (!("annotations" in definition)) throw new Error("annotations are declared");
  assertEquals(definition.annotations, { readOnly: true });
});

Deno.test("an empty annotation object is normalized away", async () => {
  const tool = defineTool({
    name: "empty-hints",
    description: "Empty annotation block.",
    input: schema.object({}),
    annotations: {},
    handler: () => null,
  });

  const definition = await tool({ phase: "describe" });
  assertEquals("annotations" in definition, false);
});

Deno.test("defineTool rejects malformed annotations at load time", () => {
  assertThrows(
    () =>
      defineTool({
        name: "bad-hint",
        description: "Non-boolean hint.",
        input: schema.object({}),
        annotations: { readOnly: "yes" as unknown as boolean },
        handler: () => null,
      }),
    TypeError,
  );
  assertThrows(
    () =>
      defineTool({
        name: "blank-title",
        description: "Blank title.",
        input: schema.object({}),
        annotations: { title: "   " },
        handler: () => null,
      }),
    TypeError,
  );
  assertThrows(
    () =>
      defineTool({
        name: "long-title",
        description: "Oversized title.",
        input: schema.object({}),
        annotations: { title: "t".repeat(129) },
        handler: () => null,
      }),
    TypeError,
  );
});

Deno.test("a cancel that arrives before the dispatch still aborts the handler", async () => {
  // A cancel frame can reach the worker while it is still starting (the kernel posts it the
  // moment it stops waiting). Losing it would let the handler run to completion, side effects
  // included, with only the value discarded.
  const tool = defineTool({
    name: "early-cancel",
    description: "Observes a cancel delivered before dispatch.",
    input: schema.object({}),
    handler: (_input, ctx) => (ctx.signal.aborted ? "aborted" : "ran"),
  });

  globalThis.dispatchEvent(
    new MessageEvent("message", { data: { type: "tool.cancel", callId: "call-early" } }),
  );

  const result = await tool({
    phase: "call",
    callId: "call-early",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result, {
    outcome: "error",
    code: "tool_cancelled",
    message: "Tool 'early-cancel' was cancelled.",
  });
});

Deno.test("a cancel for an unknown call does not affect a later call", async () => {
  const tool = defineTool({
    name: "unrelated-cancel",
    description: "Unaffected by a cancel for another call.",
    input: schema.object({}),
    handler: (_input, ctx) => (ctx.signal.aborted ? "aborted" : "ran"),
  });

  globalThis.dispatchEvent(
    new MessageEvent("message", { data: { type: "tool.cancel", callId: "call-other" } }),
  );

  const result = await tool({
    phase: "call",
    callId: "call-mine",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result, { outcome: "ok", value: "ran" });
});

Deno.test("the handler budget is measured from the handler, not from the frame", async () => {
  // The kernel's wait also covers an on-demand worker start, so the worker owns the
  // handler's clock. A handler that finishes inside its budget must succeed even if a
  // cancellation frame was already in flight from a *previous* call's timeout.
  const tool = defineTool({
    name: "budget",
    description: "Finishes inside its budget.",
    input: schema.object({}),
    timeoutMs: 5_000,
    handler: async (_input, ctx) => {
      await new Promise((resolve) => setTimeout(resolve, 5));
      return ctx.signal.aborted ? "aborted" : "finished";
    },
  });

  const result = await tool({
    phase: "call",
    callId: "call-budget",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result, { outcome: "ok", value: "finished" });
});

Deno.test("an over-budget handler reports a timeout, not a cancellation", async () => {
  // The worker's own budget is the verdict: reporting a bare cancellation would leave the
  // kernel's coarser "did not complete" text as the only signal.
  const tool = defineTool({
    name: "over-budget",
    description: "Exceeds its budget.",
    input: schema.object({}),
    timeoutMs: 1_000,
    handler: async (_input, ctx) => {
      await new Promise<void>((resolve) => {
        if (ctx.signal.aborted) {
          resolve();
          return;
        }
        ctx.signal.addEventListener("abort", () => resolve(), { once: true });
      });
      return "too late";
    },
  });

  const result = await tool({
    phase: "call",
    callId: "call-over",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result.outcome, "error");
  if (result.outcome === "error") {
    assertEquals(result.code, "tool_timeout");
    assert(result.message.includes("1000"));
  }
});

Deno.test("a handler that ignores its budget is still bounded by it", async () => {
  // A handler that never observes the signal still cannot report success after the budget:
  // the abort is checked before the value is returned.
  const tool = defineTool({
    name: "ignores-budget",
    description: "Never observes the signal.",
    input: schema.object({}),
    timeoutMs: 1_000,
    handler: () => new Promise((resolve) => setTimeout(() => resolve("late"), 1_200)),
  });

  const result = await tool({
    phase: "call",
    callId: "call-ignores",
    sessionId: "session-1",
    arguments: {},
  });
  assertEquals(result.outcome, "error");
  if (result.outcome === "error") assertEquals(result.code, "tool_timeout");
});

Deno.test("defineTool rejects an unknown phase", async () => {
  const tool = defineTool({
    name: "phased",
    description: "Phase probe.",
    input: schema.object({}),
    handler: () => null,
  });

  const result = await tool({ phase: "unknown" } as unknown as Parameters<typeof tool>[0]);
  assertEquals(result, {
    outcome: "error",
    code: "tool_request_invalid",
    message: "Unknown tool phase.",
  });
});
