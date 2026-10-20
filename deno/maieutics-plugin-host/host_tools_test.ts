import { assert, assertEquals } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

// The host suites require an import-granted parent; run this file through the
// package's `deno task test` or with --allow-import.

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;
const STORAGE_DATA_ROOT = Deno.makeTempDirSync();

function plugin(id: string, source: string): PluginConfig {
  const dir = Deno.makeTempDirSync();
  const entryPath = `${dir}/mod.ts`;
  Deno.writeTextFileSync(entryPath, source);
  return {
    id,
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: new URL(`file://${entryPath}`).href,
      specifier: `@maieutics/${id}/main`,
    }],
  };
}

function host(plugins: readonly PluginConfig[]): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [...plugins],
    storageDataRoot: STORAGE_DATA_ROOT,
  });
}

const ECHO_TOOL = `
import { defineTool, schema } from ${JSON.stringify(SDK_URL)};

export const echo = defineTool({
  name: "echo",
  description: "Echo the text back.",
  input: schema.object({ text: schema.string() }),
  annotations: {
    title: "Echo Text",
    readOnly: true,
    destructive: false,
    idempotent: true,
    openWorld: false,
  },
  handler: ({ text }) => ({ text }),
});
`;

Deno.test("a Tool export is registered as an extension point", async () => {
  const runtime = host([plugin("tools", ECHO_TOOL)]);
  try {
    await runtime.startAll();
    const registration = runtime.extensions.find((entry) =>
      entry.pluginId === "tools" && entry.exportName === "./main"
    );
    assert(registration !== undefined, "the tool export is registered");
    assertEquals(registration.extensionPoint, "Tool");
  } finally {
    runtime.dispose();
  }
});

Deno.test("describe returns the declared schema and call runs the handler", async () => {
  const runtime = host([plugin("tools", ECHO_TOOL)]);
  try {
    await runtime.startAll();

    const definition = await runtime.invoke("tools", "./main", "Tool", {
      phase: "describe",
    }) as {
      name?: string;
      description?: string;
      inputSchema?: { type?: string; properties?: Record<string, unknown> };
      annotations?: Record<string, unknown>;
    };
    assertEquals(definition.name, "echo");
    assertEquals(definition.description, "Echo the text back.");
    assertEquals(definition.inputSchema?.type, "object");
    assert(definition.inputSchema?.properties?.text !== undefined);
    // The hints must survive the worker→host→kernel hop intact; the kernel applies its
    // own defaults for absent members, so a dropped block would silently change them.
    assertEquals(definition.annotations, {
      title: "Echo Text",
      readOnly: true,
      destructive: false,
      idempotent: true,
      openWorld: false,
    });

    const response = await runtime.invoke("tools", "./main", "Tool", {
      phase: "call",
      callId: "call-1",
      sessionId: "session-1",
      arguments: { text: "hello" },
    }) as { outcome?: string; value?: { text?: string } };
    assertEquals(response.outcome, "ok");
    assertEquals(response.value?.text, "hello");
  } finally {
    runtime.dispose();
  }
});

Deno.test("a call with invalid arguments returns a typed input error", async () => {
  const runtime = host([plugin("tools", ECHO_TOOL)]);
  try {
    await runtime.startAll();
    const response = await runtime.invoke("tools", "./main", "Tool", {
      phase: "call",
      callId: "call-1",
      sessionId: "session-1",
      arguments: { text: 42 },
    }) as { outcome?: string; code?: string };
    assertEquals(response.outcome, "error");
    assertEquals(response.code, "tool_input_invalid");
  } finally {
    runtime.dispose();
  }
});

Deno.test("a plugin may declare only one tool per carrier module", async () => {
  const source = `
import { defineTool, schema } from ${JSON.stringify(SDK_URL)};

export const first = defineTool({
  name: "first",
  description: "First tool.",
  input: schema.object({}),
  handler: () => 1,
});

export const second = defineTool({
  name: "second",
  description: "Second tool.",
  input: schema.object({}),
  handler: () => 2,
});
`;
  const runtime = host([plugin("two-tools", source)]);
  try {
    await runtime.startAll();
    const definitions = runtime.extensions.filter((entry) => entry.extensionPoint === "Tool");
    assertEquals(definitions.length, 1, "one extension-point registration wins");
    const definition = await runtime.invoke("two-tools", "./main", "Tool", {
      phase: "describe",
    }) as { name?: string };
    // The worker logs the collision; the observable contract is one registration and
    // the later export winning instead of a silent merge.
    assertEquals(definition.name, "second", "the later export replaces the earlier one");
  } finally {
    runtime.dispose();
  }
});

Deno.test("one cancel frame aborts a handler that has started", async () => {
  // The handler signals that it is running by making a capability call, so the test sends
  // its single cancel frame at a known point instead of retrying until something happens.
  // Production sends exactly one frame per stopped call, so a test that needs several would
  // be hiding a delivery gap rather than covering the behavior.
  const source = `
import { defineTool, schema, capabilities } from ${JSON.stringify(SDK_URL)};

export const slow = defineTool({
  name: "slow",
  description: "Waits until cancelled.",
  input: schema.object({}),
  handler: async (_input, context) => {
    // Announces "the handler is running" through the capability relay.
    await capabilities.invokeTool("test_started", {});
    if (context.signal.aborted) return "aborted";
    await new Promise<void>((resolve) => {
      context.signal.addEventListener("abort", () => resolve(), { once: true });
    });
    return "aborted";
  },
});
`;
  const runtime = host([plugin("slow-tools", source)]);
  const started = new Promise<void>((resolve) => {
    runtime.setCapabilityCaller((_plugin, capability) => {
      if (capability === "tools.invoke") resolve();
      return Promise.resolve({ status: "ok", value: null });
    });
  });
  try {
    await runtime.startAll();

    const call = runtime.invoke("slow-tools", "./main", "Tool", {
      phase: "call",
      callId: "call-slow",
      sessionId: "session-1",
      arguments: {},
    });

    // Wait for the handler's own announcement, then cancel once.
    await started;
    runtime.cancelInvoke("slow-tools", "./main", "call-slow");

    const response = await call as { outcome?: string; code?: string };
    assertEquals(response.outcome, "error");
    assertEquals(response.code, "tool_cancelled");
  } finally {
    runtime.dispose();
  }
});

Deno.test("a cancel that arrives before the handler starts still aborts it", async () => {
  // The kernel posts the cancel the moment it stops waiting, which can be before the worker
  // finishes starting. Dropping that frame would let the handler run to completion — side
  // effects included — with only its value discarded.
  const source = `
import { defineTool, schema } from ${JSON.stringify(SDK_URL)};

export const slow = defineTool({
  name: "slow",
  description: "Reports whether it started already cancelled.",
  input: schema.object({}),
  handler: (_input, context) => context.signal.aborted ? "aborted" : "ran",
});
`;
  const runtime = host([plugin("early-cancel", source)]);
  try {
    // The worker has never been started: the invoke below is its first wake.
    const call = runtime.invoke("early-cancel", "./main", "Tool", {
      phase: "call",
      callId: "call-early",
      sessionId: "session-1",
      arguments: {},
    });
    runtime.cancelInvoke("early-cancel", "./main", "call-early");

    const response = await call as { outcome?: string; code?: string };
    assertEquals(response.outcome, "error");
    assertEquals(response.code, "tool_cancelled");
  } finally {
    runtime.dispose();
  }
});

Deno.test("cancelInvoke is a no-op only for a worker the host does not know", () => {
  // A known-but-stopped worker queues the cancel (an invocation wakes it on demand,
  // ADR 0035), so "no-op" applies exactly to a worker id the host never registered.
  const runtime = host([plugin("tools", ECHO_TOOL)]);
  try {
    runtime.cancelInvoke("never-registered", "./main", "call-1");
  } finally {
    runtime.dispose();
  }
});
