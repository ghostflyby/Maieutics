import { assert, assertEquals, assertRejects } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";
import { ReplManager } from "./repl_manager.ts";

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;
const REPL_ENTRY_PATH = new URL("../maieutics-deno-repl/process_main.ts", import.meta.url).pathname;

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
    workers: [{
      exportName: "./main",
      entryUrl: pathToFileUrl(entryPath),
      specifier: "@maieutics/test/main",
    }],
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
      specifier === "./host.ts" || specifier === "./repl_manager.ts" ||
        specifier.startsWith("../shared/"),
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

Deno.test("reload with a replacement config rebuilds the worker with the new grants", async () => {
  const dir = Deno.makeTempDirSync();
  const entryPath = `${dir}/mod.ts`;
  Deno.writeTextFileSync(
    entryPath,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        await Deno.readTextFile("/etc/hosts");
        return { action: "continue" as const };
      },
    });
  `),
  );
  const original: PluginConfig = {
    id: "test",
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: pathToFileUrl(entryPath),
      specifier: "@maieutics/test/main",
    }],
  };
  const host = makeHost(original);
  try {
    await host.startAll();
    // Baseline: /etc/hosts is outside the worker's read grant.
    await assertRejects(
      () => host.invoke("test", "./main", "ToolPreInvoke", {}),
      Error,
      "Requires read access",
    );
    // Reload with a replacement config that grants /etc/hosts; the rebuilt
    // worker must pick up the new permission without a host restart.
    const replacement: PluginConfig = {
      ...original,
      permissions: { read: [dir, "/etc/hosts"] },
    };
    await host.reload("test", "./main", replacement);
    const value = await host.invoke("test", "./main", "ToolPreInvoke", {}) as {
      action?: string;
    };
    assertEquals(value.action, "continue");
  } finally {
    host.dispose();
  }
});

Deno.test("reload with a replacement entry URL rebuilds the worker and refreshes the registry", async () => {
  const dir = Deno.makeTempDirSync();
  const oldEntry = `${dir}/old.ts`;
  const newEntry = `${dir}/new.ts`;
  Deno.writeTextFileSync(
    oldEntry,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", () => ({ action: "old" as const }));
  `),
  );
  Deno.writeTextFileSync(
    newEntry,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", () => ({ action: "new" as const }));
    export const discover = defineExtensionPoint("McpDiscover", () => []);
  `),
  );
  const original: PluginConfig = {
    id: "test",
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: pathToFileUrl(oldEntry),
      specifier: "@maieutics/test/main",
    }],
  };
  const host = makeHost(original);
  try {
    await host.startAll();
    const before = await host.invoke("test", "./main", "ToolPreInvoke", {}) as {
      action?: string;
    };
    assertEquals(before.action, "old");
    assertEquals(
      host.extensions.map((entry) => entry.extensionPoint).sort(),
      ["ToolPreInvoke"],
    );
    // The replacement points at a new entry file that also registers a new
    // extension point; the rebuilt worker must load the new file and the
    // public registry snapshot must refresh.
    const replacement: PluginConfig = {
      ...original,
      workers: [{
        exportName: "./main",
        entryUrl: pathToFileUrl(newEntry),
        specifier: "@maieutics/test/main",
      }],
    };
    await host.reload("test", "./main", replacement);
    const after = await host.invoke("test", "./main", "ToolPreInvoke", {}) as {
      action?: string;
    };
    assertEquals(after.action, "new");
    assertEquals(
      host.extensions.map((entry) => entry.extensionPoint).sort(),
      ["McpDiscover", "ToolPreInvoke"],
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

function makeReplManager(): ReplManager {
  return new ReplManager({ replEntryPath: REPL_ENTRY_PATH });
}

function isReplProcessAlive(pid: number): boolean {
  try {
    return Deno.kill(pid, 0) === undefined;
  } catch {
    return false;
  }
}

// —— REPL process derivation (ADR 0020 skeleton) ——

Deno.test("host derives a REPL process actor and receives its pid", async () => {
  const repls = makeReplManager();
  try {
    const handle = await repls.spawnRepl("repl-test-1", 0);
    assert(Number.isSafeInteger(handle.pid) && handle.pid > 0, "pid must be a positive integer");
    assertEquals(handle.sessionId, "repl-test-1");
    assertEquals(handle.generation, 0);
    assertEquals(repls.get("repl-test-1"), handle);
  } finally {
    await repls.disposeAll();
  }
});

Deno.test("repl process actor exposes execute and returns the skeleton envelope", async () => {
  const repls = makeReplManager();
  try {
    const handle = await repls.spawnRepl("repl-test-2", 1);
    const result = await handle.actor.execute("1 + 1");
    assertEquals(result.ok, true);
    assertEquals(result.data, "skeleton: 1 + 1");
  } finally {
    await repls.disposeAll();
  }
});

Deno.test("dispose stops the derived repl process and clears the registry", async () => {
  const repls = makeReplManager();
  const handle = await repls.spawnRepl("repl-test-3", 2);
  const pid = handle.pid;
  assert(isReplProcessAlive(pid), "repl process must be alive before dispose");
  const disposed = await repls.disposeRepl("repl-test-3");
  assertEquals(disposed, true);
  assertEquals(repls.get("repl-test-3"), undefined);
  // dispose() resolves once the actor is torn down; the child process exit can
  // trail it by a tick, so poll briefly instead of asserting synchronously.
  for (let attempt = 0; attempt < 50 && isReplProcessAlive(pid); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  assert(!isReplProcessAlive(pid), "repl process must exit after dispose");
});

Deno.test("a crashed repl process is removed from the registry", async () => {
  const repls = makeReplManager();
  const handle = await repls.spawnRepl("repl-test-4", 3);
  const pid = handle.pid;
  assert(isReplProcessAlive(pid), "repl process must be alive before the kill");
  Deno.kill(pid, "SIGKILL");
  // The host's pid liveness monitor clears the registry once the child is
  // gone. worker-actor's onDeath does not fire on a hard kill (the library
  // does not wire child exit to the transport close), so the host-side monitor
  // is the death signal; give it a bounded window to observe the exit.
  for (let attempt = 0; attempt < 50 && repls.get("repl-test-4") !== undefined; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  assertEquals(repls.get("repl-test-4"), undefined, "registry must clear after crash");
  await repls.disposeAll();
});

Deno.test("spawnRepl rejects a duplicate running session", async () => {
  const repls = makeReplManager();
  try {
    await repls.spawnRepl("repl-test-5", 0);
    await assertRejects(
      () => repls.spawnRepl("repl-test-5", 0),
      Error,
      "already running",
    );
  } finally {
    await repls.disposeAll();
  }
});
