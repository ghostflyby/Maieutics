import {assert, assertEquals, assertRejects} from "jsr:@std/assert";
import {type PluginConfig, PluginHost} from "./host.ts";

const SDK_URL = new URL("../maieutics-plugin-sdk/mod.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;

function pathToFileUrl(path: string): string {
  return new URL(`file://${path}`).href;
}

function createPlugin(dir: string, source: string): PluginConfig {
  const entryPath = `${dir}/mod.ts`;
  Deno.writeTextFileSync(entryPath, source);
  return {
    id: "test",
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(entryPath) }],
  };
}

function pluginSource(body: string): string {
    return `import { defineExtensionPoint } from ${
        JSON.stringify(SDK_URL)
    };\n${body}\n`;
}

function makeHost(plugin: PluginConfig): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [plugin],
  });
}

Deno.test("worker entry imports only the plugin sdk", async () => {
    const source = await Deno.readTextFile(
        new URL("./worker_entry.ts", import.meta.url),
    );
    const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) =>
        match[1]
    );
  for (const specifier of imports) {
    assert(
        specifier.startsWith("../maieutics-plugin-sdk/") ||
        specifier.startsWith("./"),
      `unexpected import '${specifier}' in the worker entry`,
    );
  }
});

Deno.test("host entry imports only the host implementation and shared control module", async () => {
  const source = await Deno.readTextFile(new URL("./mod.ts", import.meta.url));
    const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) =>
        match[1]
    );
  for (const specifier of imports) {
    assert(
      specifier === "./host.ts" || specifier.startsWith("../shared/"),
      `unexpected import '${specifier}' in the plugin host entry`,
    );
  }
});

Deno.test("a plugin worker cannot import the repl client module", async () => {
  const dir = Deno.makeTempDirSync();
    const replClientUrl =
        new URL("../maieutics-repl-client/mod.ts", import.meta.url).href;
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        await import(${JSON.stringify(replClientUrl)});
        return { action: "continue" as const };
      },
    });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    await assertRejects(
      () => host.invoke("test", "./main", "ToolPreInvoke", {}),
      Error,
      "Requires read access",
    );
  } finally {
    host.dispose();
  }
});

Deno.test("a plugin worker cannot import host or shared internals", async () => {
  const dir = Deno.makeTempDirSync();
  const hostEntryUrl = new URL("./mod.ts", import.meta.url).href;
  const sharedBusUrl = new URL("../shared/bus.ts", import.meta.url).href;
  const plugin = createPlugin(
    dir,
    pluginSource(`
    async function tryImport(url: string): Promise<string> {
      try {
        await import(url);
        return "ok";
      } catch (error) {
        return String((error as Error).message ?? error);
      }
    }
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => ({
        action: "continue" as const,
        attempts: [
          await tryImport(${JSON.stringify(hostEntryUrl)}),
          await tryImport(${JSON.stringify(sharedBusUrl)}),
        ],
      }),
    });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    const value = await host.invoke("test", "./main", "ToolPreInvoke", {}) as {
      attempts?: string[];
    };
    assertEquals(value.attempts?.length, 2);
    for (const attempt of value.attempts ?? []) {
      assert(
        attempt.includes("Requires read access"),
        `expected a permission failure, got: ${attempt}`,
      );
    }
  } finally {
    host.dispose();
  }
});

Deno.test("scans object and function extension points", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export default defineExtensionPoint("McpDiscover", {
      handler: () => [{ module: "x", transport: { type: "http", url: "http://127.0.0.1:1" } }],
    });
    export const pre = defineExtensionPoint("ToolPreInvoke", () => ({ action: "continue" as const }));
  `),
  );
  const host = makeHost(plugin);
  try {
    const registered = await host.startAll();
    assertEquals(registered.length, 2);
    assertEquals(
      registered.map((entry) => entry.extensionPoint).sort(),
      ["McpDiscover", "ToolPreInvoke"],
    );
  } finally {
    host.dispose();
  }
});

Deno.test("invokes an object handler", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export default defineExtensionPoint("McpDiscover", {
      handler: () => [{ module: "x", transport: { type: "stdio", command: "deno" } }],
    });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
      const value = await host.invoke("test", "./main", "McpDiscover", {
          reason: "startup",
      });
    assertEquals(Array.isArray(value), true);
  } finally {
    host.dispose();
  }
});

Deno.test("invokes a function handler", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export const pre = defineExtensionPoint("ToolPreInvoke", () => ({ action: "continue" as const }));
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    const value = await host.invoke("test", "./main", "ToolPreInvoke", {
      tool: "read_text",
      arguments: {},
      callId: "c1",
    });
    assertEquals(value, { action: "continue" });
  } finally {
    host.dispose();
  }
});

Deno.test("enforces worker permission grants", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        await Deno.readTextFile("/etc/hosts");
        return { action: "continue" as const };
      },
    });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    await assertRejects(
      () => host.invoke("test", "./main", "ToolPreInvoke", {}),
      Error,
      "Requires read access",
    );
  } finally {
    host.dispose();
  }
});

Deno.test("rejects invocations of unregistered extension points", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export default defineExtensionPoint("McpDiscover", { handler: () => [] });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    await assertRejects(
      () => host.invoke("test", "./main", "ToolPostInvoke", {}),
      Error,
      "not registered",
    );
  } finally {
    host.dispose();
  }
});

Deno.test("propagates handler failures as typed errors", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createPlugin(
    dir,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: () => { throw new Error("boom"); },
    });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    await assertRejects(
      () => host.invoke("test", "./main", "ToolPreInvoke", {}),
      Error,
      "boom",
    );
  } finally {
    host.dispose();
  }
});
