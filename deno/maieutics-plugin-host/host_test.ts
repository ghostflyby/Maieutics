import { assert, assertEquals, assertRejects } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

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
    name: "@maieutics/test",
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(entryPath) }],
  };
}

function pluginSource(body: string): string {
  return `import { defineExtensionPoint } from ${JSON.stringify(SDK_URL)};\n${body}\n`;
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
  const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) => match[1]);
  for (const specifier of imports) {
    assert(
      specifier.startsWith("../maieutics-plugin-sdk/") ||
        specifier === "node:module" ||
        specifier.startsWith("./"),
      `unexpected import '${specifier}' in the worker entry`,
    );
  }
});

Deno.test("host entry imports only the host implementation and shared control module", async () => {
  const source = await Deno.readTextFile(new URL("./mod.ts", import.meta.url));
  const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) => match[1]);
  for (const specifier of imports) {
    assert(
      specifier === "./host.ts" || specifier.startsWith("../shared/"),
      `unexpected import '${specifier}' in the plugin host entry`,
    );
  }
});

Deno.test("a plugin worker cannot import the repl client module", async () => {
  const dir = Deno.makeTempDirSync();
  const replClientUrl = new URL("../maieutics-repl-client/mod.ts", import.meta.url).href;
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

function twoPluginHost(
  baseDir: string,
): { host: PluginHost; source: string } {
  const depDir = `${baseDir}/dep`;
  const conDir = `${baseDir}/con`;
  Deno.mkdirSync(depDir, { recursive: true });
  Deno.mkdirSync(conDir, { recursive: true });
  const depSource = `
export function double(value: number): number { return value * 2; }
export const name = "dep";
`;
  Deno.writeTextFileSync(`${depDir}/mod.ts`, depSource);
  const conSource = pluginSource(`
import { double, name } from ${JSON.stringify("@maieutics/dep/main")};
export default defineExtensionPoint("McpDiscover", {
  handler: async () => [{ module: "x", transport: { type: "stdio", command: "deno" }, doubled: await double(21), depName: await name() }],
});
`);
  Deno.writeTextFileSync(`${conDir}/mod.ts`, conSource);
  const dep: PluginConfig = {
    id: "dep",
    name: "@maieutics/dep",
    rootDir: depDir,
    permissions: { read: [depDir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(`${depDir}/mod.ts`) }],
  };
  const con: PluginConfig = {
    id: "con",
    name: "@maieutics/con",
    rootDir: conDir,
    permissions: { read: [conDir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(`${conDir}/mod.ts`) }],
    dependencies: ["dep"],
  };
  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [dep, con],
  });
  return { host, source: depSource };
}

Deno.test("cross-plugin import calls the dependency worker over the actor channel", async () => {
  const dir = Deno.makeTempDirSync();
  const { host } = twoPluginHost(dir);
  try {
    const registered = await host.startAll();
    assertEquals(
      registered.map((entry) => entry.pluginId).sort(),
      ["con"],
    );
    assertEquals(host.stateOf("dep"), "running");
    assertEquals(host.stateOf("con"), "running");
    const value = await host.invoke("con", "./main", "McpDiscover", {
      reason: "startup",
    }) as Array<{ doubled?: number; depName?: string }>;
    assertEquals(value[0].doubled, 42);
    assertEquals(value[0].depName, "dep");
  } finally {
    host.dispose();
  }
});

Deno.test("reload stops the reloaded plugin and its dependents, then restarts them", async () => {
  const dir = Deno.makeTempDirSync();
  const { host, source } = twoPluginHost(dir);
  try {
    await host.startAll();
    assertEquals(host.stateOf("con"), "running");
    assertEquals(host.stateOf("dep"), "running");

    // Change the dependency's exported function; the consumer must observe the
    // new value after the reload cascade.
    Deno.writeTextFileSync(
      `${dir}/dep/mod.ts`,
      source.replace("value * 2", "value * 3"),
    );
    const registered = await host.reload(["dep"]);
    assertEquals(registered.length, 1);
    assertEquals(host.stateOf("dep"), "running");
    assertEquals(host.stateOf("con"), "running");
    const value = await host.invoke("con", "./main", "McpDiscover", {
      reason: "startup",
    }) as Array<{ doubled?: number }>;
    assertEquals(value[0].doubled, 63);
  } finally {
    host.dispose();
  }
});

Deno.test("cross-plugin call rejects when the dependency is not declared", async () => {
  const dir = Deno.makeTempDirSync();
  const conDir = `${dir}/con`;
  Deno.mkdirSync(conDir, { recursive: true });
  const depDir = `${dir}/dep`;
  Deno.mkdirSync(depDir, { recursive: true });
  Deno.writeTextFileSync(
    `${depDir}/mod.ts`,
    `export const value = "secret";\n`,
  );
  const conSource = pluginSource(`
import { value } from ${JSON.stringify("@maieutics/dep/main")};
export default defineExtensionPoint("McpDiscover", {
  handler: () => [{ module: "x", transport: { type: "stdio", command: "deno" }, leaked: value() }],
});
`);
  Deno.writeTextFileSync(`${conDir}/mod.ts`, conSource);
  const dep: PluginConfig = {
    id: "dep",
    name: "@maieutics/dep",
    rootDir: depDir,
    permissions: { read: [depDir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(`${depDir}/mod.ts`) }],
  };
  const con: PluginConfig = {
    id: "con",
    name: "@maieutics/con",
    rootDir: conDir,
    permissions: { read: [conDir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(`${conDir}/mod.ts`) }],
    // No declared dependency on "dep": the import must fail.
  };
  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [dep, con],
  });
  try {
    await host.startAll();
    await assertRejects(
      () => host.invoke("con", "./main", "McpDiscover", { reason: "startup" }),
      Error,
      "is not declared",
    );
  } finally {
    host.dispose();
  }
});

Deno.test("a crashing plugin is disabled after max restarts and cascades to its dependents", async () => {
  const dir = Deno.makeTempDirSync();
  const conDir = `${dir}/con`;
  const depDir = `${dir}/dep`;
  Deno.mkdirSync(conDir, { recursive: true });
  Deno.mkdirSync(depDir, { recursive: true });
  // The dependency worker crashes as soon as it is initialized (top-level throw).
  Deno.writeTextFileSync(
    `${depDir}/mod.ts`,
    `throw new Error("boom at init");\n`,
  );
  Deno.writeTextFileSync(
    `${conDir}/mod.ts`,
    pluginSource(`
export default defineExtensionPoint("McpDiscover", { handler: () => [] });
`),
  );
  const dep: PluginConfig = {
    id: "dep",
    name: "@maieutics/dep",
    rootDir: depDir,
    permissions: { read: [depDir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(`${depDir}/mod.ts`) }],
  };
  const con: PluginConfig = {
    id: "con",
    name: "@maieutics/con",
    rootDir: conDir,
    permissions: { read: [conDir] },
    workers: [{ exportName: "./main", entryUrl: pathToFileUrl(`${conDir}/mod.ts`) }],
    dependencies: ["dep"],
  };
  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [dep, con],
  });
  try {
    await host.startAll();
    assertEquals(host.stateOf("dep"), "failed");
    // The dependent never started; it is stopped with a dependency_failed reason.
    assertEquals(host.stateOf("con"), "stopped");
    assertEquals(host.reasonOf("con"), "dependency_failed:dep");
  } finally {
    host.dispose();
  }
});
