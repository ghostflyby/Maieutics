import { assert, assertEquals, assertRejects } from "@std/assert";
import { DatabaseSync } from "node:sqlite";
import { type PluginConfig, PluginHost } from "./host.ts";
import {
  isValidReplPid,
  type ReplDeriveRequest,
  ReplManager,
  type ReplReporter,
} from "./repl_manager.ts";
import type { ReplEnvelope } from "../shared/protocol.ts";
import type { HostReplReport } from "./host_repl_protocol.ts";

// Run with an import-granted parent (the deno.json test task passes
// --allow-import): these suites exercise the host exactly as production launches
// it, and a worker's default registry import grant may not exceed its parent —
// spawning the host without the flag fails fast with a permission-escalation
// error instead.

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;
const REPL_ENTRY_PATH = new URL("../maieutics-deno-repl/process_main.ts", import.meta.url).pathname;

function pathToFileUrl(path: string): string {
  return new URL(`file://${path}`).href;
}

function createPlugin(dir: string, source: string): PluginConfig {
  return createPluginConfig(dir, "test", source);
}

function createPluginConfig(
  dir: string,
  id: string,
  source: string,
  storage?: { dataDir: string },
): PluginConfig {
  const entryPath = `${dir}/mod.ts`;
  Deno.writeTextFileSync(entryPath, source);
  return {
    id,
    rootDir: dir,
    permissions: { read: [dir] },
    ...(storage === undefined ? {} : { storage }),
    workers: [{
      exportName: "./main",
      entryUrl: pathToFileUrl(entryPath),
      specifier: `@maieutics/${id}/main`,
    }],
  };
}

function pluginSource(body: string): string {
  return `import { defineExtensionPoint } from ${JSON.stringify(SDK_URL)};\n${body}\n`;
}

// Pool workers receive read+write on this root only, so every plugin data
// directory in these tests must live under it.
const STORAGE_DATA_ROOT = Deno.makeTempDirSync();

/** A plugin storage data directory under the pool workers' write grant. */
function makeDataDir(): string {
  return `${STORAGE_DATA_ROOT}/${crypto.randomUUID()}`;
}

function makeHost(plugin: PluginConfig): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [plugin],
    storageDataRoot: STORAGE_DATA_ROOT,
  });
}

function makeMultiHost(plugins: readonly PluginConfig[]): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [...plugins],
    storageDataRoot: STORAGE_DATA_ROOT,
  });
}

Deno.test("worker entry imports only the plugin sdk and the shared runtime bootstrap", async () => {
  const source = await Deno.readTextFile(
    new URL("./worker_entry.ts", import.meta.url),
  );
  // Match the module specifier itself, not whole lines: a `from "..."` clause
  // (single or double quoted) and a bare side-effect `import "..."` (which has
  // no `from` clause) are both extracted, so no import can slip through a
  // line-oriented match.
  const specifiers = [
    ...source.matchAll(/\bfrom\s+["']([^"']+)["']/g),
    ...source.matchAll(/\bimport\s+["']([^"']+)["']/g),
  ].map((match) => match[1]);
  assert(specifiers.length > 0, "worker entry must import its runtime pieces");
  for (const specifier of specifiers) {
    assert(
      specifier.startsWith("../maieutics-plugin-sdk/") ||
        specifier.startsWith("../maieutics-runtime/") ||
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
        specifier === "./host_repl_protocol.ts" ||
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

Deno.test("shared bootstrap runs before plugin entry loading and routes nested workers", async () => {
  const dir = Deno.makeTempDirSync();
  // The nested worker lives in the plugin directory (inside the plugin's read
  // grant) and reports the versioned bootstrap marker from its own realm. The
  // marker is installed only when the nested worker entered through the shared
  // wrapper, which itself proves the patch was installed before the plugin
  // entry's top-level code ran.
  Deno.writeTextFileSync(
    `${dir}/nested.ts`,
    `
    const marker = (globalThis as unknown as Record<PropertyKey, unknown>)[
      Symbol.for("maieutics/bootstrap/v1")
    ];
    (self as unknown as { postMessage(value: unknown): void }).postMessage({
      phase: "nested-plugin-ready",
      profile: marker !== null && typeof marker === "object"
        ? (marker as { profile?: unknown }).profile
        : null,
    });
  `,
  );
  const plugin = createPlugin(
    dir,
    pluginSource(`
    const nested = new Worker(new URL("./nested.ts", import.meta.url), { type: "module" });
    const nestedReady = new Promise((resolve) => {
      nested.onmessage = (event) => { resolve(event.data); nested.terminate(); };
      nested.onerror = (event) => {
        resolve({ phase: "nested-error", message: event.message });
        nested.terminate();
      };
    });
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => ({ action: "continue" as const, nested: await nestedReady }),
    });
  `),
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    const value = await host.invoke("test", "./main", "ToolPreInvoke", {}) as {
      nested?: { phase?: string; profile?: string | null };
    };
    assertEquals(value.nested?.phase, "nested-plugin-ready");
    assertEquals(value.nested?.profile, "plugin");
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

function makeReplManager(reporter?: ReplReporter): ReplManager {
  return new ReplManager({ replEntryPath: REPL_ENTRY_PATH, reporter });
}

/** Builds a spawnRepl request for the given session/generation, defaulting the
 * entry to the test REPL entry. `overrides` lets a test supply the kernel-side
 * env / permissions / report / entryUrl exactly as `host.repl.derive` would. */
function deriveRequest(
  sessionId: string,
  generation: number,
  overrides: Partial<ReplDeriveRequest> = {},
): ReplDeriveRequest {
  return { sessionId, generation, entryUrl: REPL_ENTRY_PATH, ...overrides };
}

/** A `host.repl.derive` envelope as the kernel would send it (B5a). */
function deriveEnvelope(payload: Record<string, unknown>): ReplEnvelope {
  return { version: 1, type: "host.repl.derive", payload };
}

function isReplProcessAlive(pid: number): boolean {
  try {
    return Deno.kill(pid, 0) === undefined;
  } catch {
    return false;
  }
}

function collectReports(): { reports: HostReplReport[]; reporter: ReplReporter } {
  const reports: HostReplReport[] = [];
  return {
    reports,
    reporter: (report: HostReplReport) => {
      reports.push(report);
    },
  };
}

// —— Plugin storage (ADR 0022) ——

/** A plugin whose handler drives its own storage over a tiny command surface. */
function storageCommandPlugin(
  dir: string,
  id: string,
  dataDir: string,
  mark: string,
): PluginConfig {
  return createPluginConfig(
    dir,
    id,
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async (request: { cmd?: string }) => {
        if (request.cmd === "set") {
          localStorage.setItem("who", ${JSON.stringify(mark)});
          return { action: "continue" as const, value: "set" };
        }
        return { action: "continue" as const, value: localStorage.getItem("who") };
      },
    });
  `),
    { dataDir },
  );
}

Deno.test("plugin storage is isolated per plugin and survives a worker reload", async () => {
  const dirA = Deno.makeTempDirSync();
  const dirB = Deno.makeTempDirSync();
  const dataA = makeDataDir();
  const dataB = makeDataDir();
  const host = makeMultiHost([
    storageCommandPlugin(dirA, "alpha", dataA, "A"),
    storageCommandPlugin(dirB, "beta", dataB, "B"),
  ]);
  try {
    await host.startAll();
    // Fresh stores, then each plugin writes its own mark.
    assertEquals(await valueOf(host, "alpha"), null);
    assertEquals(await valueOf(host, "beta"), null);
    await invokeCmd(host, "alpha", "set");
    await invokeCmd(host, "beta", "set");
    // Each plugin reads back only its own mark: no cross-plugin leakage.
    assertEquals(await valueOf(host, "alpha"), "A");
    assertEquals(await valueOf(host, "beta"), "B");
    // Hot reload replaces the worker; the authoritative store survives it.
    await host.reload("alpha", "./main", storageCommandPlugin(dirA, "alpha", dataA, "A"));
    assertEquals(await valueOf(host, "alpha"), "A");
  } finally {
    host.dispose();
  }
});

async function valueOf(host: PluginHost, pluginId: string): Promise<unknown> {
  const value = await host.invoke(pluginId, "./main", "ToolPreInvoke", {}) as {
    value?: unknown;
  };
  return value.value;
}

async function invokeCmd(host: PluginHost, pluginId: string, cmd: string): Promise<void> {
  await host.invoke(pluginId, "./main", "ToolPreInvoke", { cmd });
}

Deno.test("nested plugin workers share localStorage while sessionStorage stays per realm", async () => {
  const dir = Deno.makeTempDirSync();
  const dataDir = makeDataDir();
  Deno.writeTextFileSync(
    `${dir}/nested.ts`,
    `
    sessionStorage.setItem("nested", "1");
    localStorage.setItem("nested", "1");
    (self as unknown as { postMessage(value: unknown): void }).postMessage({
      localParent: localStorage.getItem("local"),
      localNested: localStorage.getItem("nested"),
      sessionNested: sessionStorage.getItem("nested"),
      sessionParent: sessionStorage.getItem("parent"),
    });
  `,
  );
  const plugin = createPluginConfig(
    dir,
    "test",
    pluginSource(`
    localStorage.setItem("local", "parent");
    sessionStorage.setItem("parent", "1");
    const nested = new Worker(new URL("./nested.ts", import.meta.url), { type: "module" });
    const nestedReady = new Promise((resolve) => {
      nested.onmessage = (event) => {
        // Internal storage frames transit the parent's message surface (like
        // the admission frames do); user handlers skip them by frame type.
        if (event.data?.type === "maieutics-storage") return;
        resolve(event.data);
        nested.terminate();
      };
      nested.onerror = (event) => {
        resolve({ error: event.message });
        nested.terminate();
      };
    });
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => ({
        action: "continue" as const,
        nested: await nestedReady,
        localNestedAfter: localStorage.getItem("nested"),
        sessionNestedAfter: sessionStorage.getItem("nested"),
      }),
    });
  `),
    { dataDir },
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    const value = await host.invoke("test", "./main", "ToolPreInvoke", {}) as {
      nested?: {
        localParent?: string | null;
        localNested?: string | null;
        sessionNested?: string | null;
        sessionParent?: string | null;
      };
      localNestedAfter?: string | null;
      sessionNestedAfter?: string | null;
    };
    // The nested realm is the same origin: it sees the parent's localStorage
    // and its writes are visible to the parent through the authoritative store.
    assertEquals(value.nested?.localParent, "parent");
    assertEquals(value.nested?.localNested, "1");
    assertEquals(value.localNestedAfter, "1");
    // sessionStorage is per realm, like a browser tab.
    assertEquals(value.nested?.sessionNested, "1");
    assertEquals(value.nested?.sessionParent, null);
    assertEquals(value.sessionNestedAfter, null);
  } finally {
    host.dispose();
  }
});

Deno.test("plugin storage persists to the kernel-assigned directory", async () => {
  const dir = Deno.makeTempDirSync();
  const dataDir = makeDataDir();
  const host = makeHost(
    createPluginConfig(
      dir,
      "test",
      pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        localStorage.setItem("disk", "1");
        return { action: "continue" as const, value: localStorage.getItem("disk") };
      },
    });
  `),
      { dataDir },
    ),
  );
  await host.startAll();
  await valueOf(host, "test");
  await host.shutdown();
  // The database IS the store: a plain SQLite client reads what the plugin
  // wrote, in the directory the kernel assigned.
  const db = new DatabaseSync(`${dataDir}/local-storage.db`);
  try {
    const row = db.prepare("SELECT value FROM kv WHERE key = ?").get("disk") as {
      value: string;
    };
    assertEquals(row.value, "1");
  } finally {
    db.close();
  }
});

Deno.test("plugin storage enforces the per-plugin quota as a typed error", async () => {
  const dir = Deno.makeTempDirSync();
  const dataDir = makeDataDir();
  // Values ride a 1 MiB mailbox payload, so the quota is reached with a few
  // near-limit writes instead of one oversized write.
  const plugin = createPluginConfig(
    dir,
    "test",
    pluginSource(`
    export default defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        let exceeded: string | null = null;
        for (let index = 0; index < 7; index++) {
          try {
            localStorage.setItem("chunk-" + index, "x".repeat(900_000));
          } catch (error) {
            exceeded = (error as Error).name;
            break;
          }
        }
        return { action: "continue" as const, value: exceeded };
      },
    });
  `),
    { dataDir },
  );
  const host = makeHost(plugin);
  try {
    await host.startAll();
    // 7 × 900_000 exceeds the 5 MiB quota: the write that crosses it fails
    // with QuotaExceededError and the earlier chunks stay intact.
    assertEquals(await valueOf(host, "test"), "QuotaExceededError");
  } finally {
    host.dispose();
  }
});

// —— REPL process derivation (ADR 0020) ——

Deno.test("host derives a REPL process actor and receives its pid", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl(deriveRequest("repl-test-1", 0));
    assert(Number.isSafeInteger(handle.pid) && handle.pid > 0, "pid must be a positive integer");
    assertEquals(handle.sessionId, "repl-test-1");
    assertEquals(handle.generation, 0);
    assertEquals(repls.get("repl-test-1"), handle);
    assertEquals(reports, [{
      type: "host.repl.spawned",
      payload: { sessionId: "repl-test-1", generation: 0, pid: handle.pid },
    }]);
  } finally {
    await repls.disposeAll();
  }
});

Deno.test("repl process actor execute is a control-plane stub over the kernel eval channel", async () => {
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl(deriveRequest("repl-test-2", 1));
    const result = await handle.actor.execute("1 + 1");
    // C1: real execution is served by the kernel over the eval WebSocket, never
    // over the actor; the actor execute stays a ReplActorResult-shaped stub so
    // the host-side call site still type-checks against the migration surface.
    assertEquals(result.ok, false);
    assertEquals(result.data, undefined);
    assert(typeof result.error === "string" && result.error.length > 0);
  } finally {
    await repls.disposeAll();
  }
});

Deno.test("repl process actor surfaces a failed client start without the kernel env", async () => {
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl(deriveRequest("repl-test-status", 3));
    // The host fires startRepl after initialize (C1); without the kernel env
    // contract the client records the failure, status surfaces it, and the
    // process stays a control-plane actor.
    const status = await handle.actor.status();
    assertEquals(status.started, true);
    assertEquals(status.ready, false);
    assert(typeof status.error === "string" && status.error.length > 0);
    // A subsequent explicit startRepl rejects with the recorded failure.
    await assertRejects(() => handle.actor.startRepl());
  } finally {
    await repls.disposeAll();
  }
});

Deno.test("dispose stops the derived repl process and clears the registry", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  const handle = await repls.spawnRepl(deriveRequest("repl-test-3", 2));
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
  // Exactly one exited report, keyed to the reported spawn pid.
  assertEquals(reports.filter((report) => report.type === "host.repl.exited"), [{
    type: "host.repl.exited",
    payload: { sessionId: "repl-test-3", generation: 2, pid },
  }]);
  assertEquals(reports.length, 2, "spawn + exit = exactly two reports");
});

Deno.test("a crashed repl process is removed from the registry", async () => {
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  const handle = await repls.spawnRepl(deriveRequest("repl-test-4", 3));
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
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    await repls.spawnRepl(deriveRequest("repl-test-5", 0));
    await assertRejects(
      () => repls.spawnRepl(deriveRequest("repl-test-5", 0)),
      Error,
      "already running",
    );
  } finally {
    await repls.disposeAll();
  }
});

Deno.test("a crashed repl process emits one exited report", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  const handle = await repls.spawnRepl(deriveRequest("repl-crash-1", 4));
  const pid = handle.pid;
  Deno.kill(pid, "SIGKILL");
  // The liveness monitor (or onDeath) clears the registry and emits the exit;
  // give the poll a bounded window.
  for (let attempt = 0; attempt < 100 && repls.get("repl-crash-1") !== undefined; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  assertEquals(repls.get("repl-crash-1"), undefined, "registry must clear after crash");
  const exited = reports.filter((report) => report.type === "host.repl.exited");
  assertEquals(exited.length, 1, "exactly one exited report per crash");
  assertEquals(exited[0].type, "host.repl.exited");
  assertEquals(exited[0].payload.sessionId, "repl-crash-1");
  assertEquals(exited[0].payload.generation, 4);
  assertEquals(exited[0].payload.pid, pid);
  assert(
    typeof (exited[0].payload as { failure?: string }).failure === "string",
    "a crash report must carry a failure reason",
  );
  await repls.disposeAll();
  // disposeAll must not emit a second exited report for the same handle.
  const after = reports.filter((report) => report.type === "host.repl.exited");
  assertEquals(after.length, 1, "no duplicate exited report");
});

Deno.test("disposing after a crash does not double-report the exit", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  const handle = await repls.spawnRepl(deriveRequest("repl-crash-2", 5));
  const pid = handle.pid;
  Deno.kill(pid, "SIGKILL");
  for (let attempt = 0; attempt < 100 && repls.get("repl-crash-2") !== undefined; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  await repls.disposeAll();
  const exited = reports.filter((report) => report.type === "host.repl.exited");
  assertEquals(exited.length, 1, "one exited report even when dispose races the death");
});

Deno.test("spawnRepl without a reporter refuses to derive a REPL", async () => {
  const repls = makeReplManager();
  await assertRejects(
    () => repls.spawnRepl(deriveRequest("repl-test-6", 0)),
    Error,
    "control bus is not connected",
  );
});

// —— kernel → host derive instruction (ADR 0020 / B5a) ——

/** Runs a `host.repl.derive` envelope through ReplManager.derive and drains
 * the handle registry. Returns the emitted reports. */
async function runDerive(
  payload: Record<string, unknown>,
  reporter?: ReplReporter,
): Promise<HostReplReport[]> {
  const reports: HostReplReport[] = [];
  const repls = makeReplManager(reporter ?? ((report: HostReplReport) => reports.push(report)));
  try {
    await repls.derive(deriveEnvelope(payload));
  } finally {
    await repls.disposeAll();
  }
  return reports;
}

/** Narrows a report to the deriveFailed member (throws otherwise). */
function deriveFailedOf(report: HostReplReport): {
  sessionId: string;
  generation: number;
  message: string;
} {
  if (report.type !== "host.repl.deriveFailed") {
    throw new Error(`expected a deriveFailed report, got '${report.type}'`);
  }
  return report.payload;
}

Deno.test("host.repl.derive derives a REPL from the kernel parameters and reports spawned", async () => {
  const reports: HostReplReport[] = [];
  const reporter: ReplReporter = (report) => reports.push(report);
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.derive(deriveEnvelope({
      sessionId: "repl-derive-1",
      generation: 0,
      entryUrl: REPL_ENTRY_PATH,
      env: { MAIEUTICS_REPL_SESSION: "repl-derive-1", MAIEUTICS_REPL_GENERATION: "0" },
    }));
    assert(handle !== undefined, "a valid derive resolves with a handle");
    assert(
      Number.isSafeInteger(handle.pid) && handle.pid > 0,
      "pid must be a positive integer",
    );
    assertEquals(handle.sessionId, "repl-derive-1");
    assertEquals(handle.generation, 0);
    assertEquals(repls.get("repl-derive-1"), handle);
    // One spawned report, keyed to the derived pid.
    assertEquals(reports.filter((report) => report.type === "host.repl.spawned"), [{
      type: "host.repl.spawned",
      payload: { sessionId: "repl-derive-1", generation: 0, pid: handle.pid },
    }]);
    // No exited report while the REPL is still running.
    assertEquals(
      reports.filter((report) => report.type === "host.repl.exited").length,
      0,
    );
  } finally {
    await repls.disposeAll();
  }
  // disposeAll balances the spawn with exactly one exited report.
  assertEquals(
    reports.filter((report) => report.type === "host.repl.exited").length,
    1,
    "disposeAll must emit one exited report",
  );
});

Deno.test("host.repl.derive injects the kernel env verbatim plus the forwarded broker path", async () => {
  await using broker = TestBroker.start();
  const previous = Deno.env.get("MAIEUTICS_PERMISSION_BROKER");
  Deno.env.set("MAIEUTICS_PERMISSION_BROKER", broker.address);
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    // The kernel env is the authoritative child env: it carries the session
    // identity vars and a marker var the child can read back through rpc.
    const handle = await repls.spawnRepl(deriveRequest("repl-derive-2", 1, {
      env: {
        MAIEUTICS_REPL_SESSION: "repl-derive-2",
        MAIEUTICS_REPL_GENERATION: "1",
        MAIEUTICS_REPL_DERIVE_MARKER: "from-kernel",
      },
    }));
    try {
      // The kernel-provided marker reaches the child unchanged, and the session
      // identity the child binds matches the kernel env.
      const identity = await handle.actor.initialize();
      assertEquals(identity.sessionId, "repl-derive-2");
      assertEquals(identity.generation, 1);
      // ...and the broker address the host received is forwarded verbatim.
      const brokerPath = await handle.actor.pregestBrokerPath();
      assertEquals(brokerPath, broker.address);
      // The host's own environment is restored after spawn: no kernel var leaks
      // into the host process env.
      assertEquals(Deno.env.get("MAIEUTICS_REPL_DERIVE_MARKER"), undefined);
    } finally {
      await repls.disposeRepl("repl-derive-2");
    }
  } finally {
    if (previous === undefined) Deno.env.delete("MAIEUTICS_PERMISSION_BROKER");
    else Deno.env.set("MAIEUTICS_PERMISSION_BROKER", previous);
  }
});

Deno.test("host.repl.derive env never overwrites the host's own broker path", async () => {
  await using broker = TestBroker.start();
  const previous = Deno.env.get("MAIEUTICS_PERMISSION_BROKER");
  Deno.env.set("MAIEUTICS_PERMISSION_BROKER", broker.address);
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    // A hostile/defective kernel env that claims a different broker address:
    // the host's forwarded MAIEUTICS_PERMISSION_BROKER wins, so the child's
    // permission checks resolve through the policy registered for its pid.
    const handle = await repls.spawnRepl(deriveRequest("repl-derive-3", 2, {
      env: {
        MAIEUTICS_REPL_SESSION: "repl-derive-3",
        MAIEUTICS_REPL_GENERATION: "2",
        DENO_PERMISSION_BROKER_PATH: "/tmp/not-the-broker.sock",
      },
    }));
    try {
      const brokerPath = await handle.actor.pregestBrokerPath();
      assertEquals(brokerPath, broker.address);
    } finally {
      await repls.disposeRepl("repl-derive-3");
    }
  } finally {
    if (previous === undefined) Deno.env.delete("MAIEUTICS_PERMISSION_BROKER");
    else Deno.env.set("MAIEUTICS_PERMISSION_BROKER", previous);
  }
});

Deno.test("host.repl.derive rejects a missing entryUrl and reports deriveFailed", async () => {
  const reports = await runDerive({
    sessionId: "repl-derive-bad-1",
    generation: 0,
    // No entryUrl: the parser rejects the instruction before any spawn.
    env: {},
  });
  assertEquals(reports, [{
    type: "host.repl.deriveFailed",
    payload: {
      sessionId: "repl-derive-bad-1",
      generation: 0,
      message: "host.repl.derive requires a non-empty string entryUrl.",
    },
  }]);
  assertEquals(
    reports.filter((report) => report.type === "host.repl.spawned").length,
    0,
    "no spawned report for a rejected derive",
  );
});

Deno.test("host.repl.derive rejects a malformed payload without emitting spawned", async () => {
  // A number sessionId is not a string; the parser rejects the instruction.
  const reports = await runDerive({ sessionId: 42, entryUrl: REPL_ENTRY_PATH });
  assertEquals(reports.length, 1);
  const failed = deriveFailedOf(reports[0]);
  // The raw payload sessionId is echoed for the kernel to correlate the
  // rejection; it is not a valid string session id.
  assertEquals(failed.sessionId, "");
  assertEquals(failed.generation, 0);
  assert(
    failed.message.includes("non-empty string sessionId"),
    `expected a sessionId validation error, got: ${failed.message}`,
  );
  assertEquals(
    reports.filter((report) => report.type === "host.repl.spawned").length,
    0,
    "no spawned report for a malformed derive",
  );
});

Deno.test("host.repl.derive rejects a bad env record type without emitting spawned", async () => {
  const reports = await runDerive({
    sessionId: "repl-derive-bad-3",
    generation: 0,
    entryUrl: REPL_ENTRY_PATH,
    env: "not-an-object",
  });
  assertEquals(reports.length, 1);
  const failed = deriveFailedOf(reports[0]);
  assert(
    failed.message.includes("env must be a string record"),
    `expected an env validation error, got: ${failed.message}`,
  );
  assertEquals(
    reports.filter((report) => report.type === "host.repl.spawned").length,
    0,
  );
});

Deno.test("host.repl.derive reports deriveFailed on a duplicate running session", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    await repls.spawnRepl(deriveRequest("repl-derive-dup", 0));
    await repls.derive(deriveEnvelope({
      sessionId: "repl-derive-dup",
      generation: 0,
      entryUrl: REPL_ENTRY_PATH,
      env: {},
    }));
    // The duplicate is rejected before any pid exists: deriveFailed only, and
    // no second spawned report for a session that already has a running REPL.
    assertEquals(reports.filter((report) => report.type === "host.repl.deriveFailed"), [{
      type: "host.repl.deriveFailed",
      payload: {
        sessionId: "repl-derive-dup",
        generation: 0,
        message: "A REPL process is already running for session 'repl-derive-dup'.",
      },
    }]);
    assertEquals(
      reports.filter((report) => report.type === "host.repl.spawned").length,
      1,
      "only the first derive spawned",
    );
  } finally {
    await repls.disposeAll();
    // disposeAll emits one exited report for the running handle; the failed
    // duplicate never enters the registry.
    assertEquals(
      reports.filter((report) => report.type === "host.repl.exited").length,
      1,
      "one exited report for the running handle only",
    );
  }
});

Deno.test("host.repl.derive with report:false stays silent on the bus", async () => {
  const reports: HostReplReport[] = [];
  const reporter: ReplReporter = (report) => reports.push(report);
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.derive(deriveEnvelope({
      sessionId: "repl-derive-silent",
      generation: 0,
      entryUrl: REPL_ENTRY_PATH,
      env: { MAIEUTICS_REPL_SESSION: "repl-derive-silent", MAIEUTICS_REPL_GENERATION: "0" },
      report: false,
    }));
    assert(handle !== undefined, "a report:false derive still resolves with a handle");
    assert(Number.isSafeInteger(handle.pid) && handle.pid > 0, "pid must be a positive integer");
    // No spawned report, and none after dispose either.
    assertEquals(
      reports.filter((report) => report.type === "host.repl.spawned").length,
      0,
    );
  } finally {
    await repls.disposeAll();
    assertEquals(
      reports.filter((report) => report.type === "host.repl.exited").length,
      0,
      "report:false derives emit no exited report on dispose",
    );
  }
});

/**
 * A minimal real permission broker: the .NET DenoPermissionBroker shape
 * (JSON-lines over a unix socket). The host's env-forwarding test must point
 * DENO_PERMISSION_BROKER_PATH at an EXISTING socket — Deno fails the child at
 * launch when the path is absent, so a fake path would kill the REPL before
 * the handshake.
 */
class TestBroker implements AsyncDisposable {
  static start(): TestBroker {
    // The unix socket path must stay well under the platform SUN_LEN limit, so
    // use a short /tmp name (like the .NET broker's CreateSocketPath).
    const path = `/tmp/mc-broker-${crypto.randomUUID().slice(0, 8)}.sock`;
    const listener = Deno.listen({ path, transport: "unix" });
    const broker = new TestBroker(path, listener);
    void broker.#serve();
    return broker;
  }

  readonly address: string;
  #listener: Deno.Listener;

  private constructor(path: string, listener: Deno.Listener) {
    this.address = path;
    this.#listener = listener;
  }

  async #serve(): Promise<void> {
    for await (const conn of this.#listener) {
      void this.#lineReader(conn);
    }
  }

  async #lineReader(conn: Deno.Conn): Promise<void> {
    const buffer = new Uint8Array(4096);
    let pending = "";
    try {
      while (true) {
        const n = await conn.read(buffer);
        if (n === null) return;
        pending += new TextDecoder().decode(buffer.subarray(0, n));
        let index: number;
        while ((index = pending.indexOf("\n")) >= 0) {
          const line = pending.slice(0, index);
          pending = pending.slice(index + 1);
          try {
            const request = JSON.parse(line) as { id: number; permission: string; value?: string };
            // Allow by default: the real kernel policy for a REPL grants the
            // session env reads and the module-graph reads, and the child's
            // initialize()/pregestBrokerPath() depend on those being allowed.
            await conn.write(new TextEncoder().encode(
              JSON.stringify({ id: request.id, result: "allow" }) + "\n",
            ));
          } catch {
            // Keep serving the stream on a malformed line.
          }
        }
      }
    } catch {
      // Client disconnected.
    }
  }

  async [Symbol.asyncDispose](): Promise<void> {
    try {
      this.#listener.close();
      await Promise.resolve();
    } catch {
      // Already closed.
    }
  }
}

Deno.test("repl child receives the forwarded broker path in its environment", async () => {
  await using broker = TestBroker.start();
  const previous = Deno.env.get("MAIEUTICS_PERMISSION_BROKER");
  Deno.env.set("MAIEUTICS_PERMISSION_BROKER", broker.address);
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl(deriveRequest("repl-broker-1", 6));
    try {
      const brokerPath = await handle.actor.pregestBrokerPath();
      assertEquals(brokerPath, broker.address);
    } finally {
      await repls.disposeRepl("repl-broker-1");
    }
  } finally {
    if (previous === undefined) Deno.env.delete("MAIEUTICS_PERMISSION_BROKER");
    else Deno.env.set("MAIEUTICS_PERMISSION_BROKER", previous);
  }
});

Deno.test("repl child sees no broker path when the host received none", async () => {
  const previous = Deno.env.get("MAIEUTICS_PERMISSION_BROKER");
  if (previous !== undefined) Deno.env.delete("MAIEUTICS_PERMISSION_BROKER");
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl(deriveRequest("repl-broker-2", 7));
    try {
      const brokerPath = await handle.actor.pregestBrokerPath();
      assertEquals(brokerPath, "");
    } finally {
      await repls.disposeRepl("repl-broker-2");
    }
  } finally {
    if (previous !== undefined) Deno.env.set("MAIEUTICS_PERMISSION_BROKER", previous);
  }
});

Deno.test("isValidReplPid rejects the host's own pid", () => {
  assertEquals(isValidReplPid(Deno.pid), false);
  assertEquals(isValidReplPid(0), false);
  assertEquals(isValidReplPid(-1), false);
  assertEquals(isValidReplPid(Number.NaN), false);
  assertEquals(isValidReplPid(Number.POSITIVE_INFINITY), false);
  // Deno.pid is always positive, so 1 is a foreign positive pid unless the
  // test runner itself is pid 1 (essentially impossible on a normal host).
  assertEquals(isValidReplPid(1), Deno.pid !== 1);
});
