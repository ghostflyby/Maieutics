import { assert, assertEquals, assertRejects } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";
import { isValidReplPid, ReplManager, type ReplReporter } from "./repl_manager.ts";
import type { HostReplReport } from "./host_repl_protocol.ts";

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

// —— REPL process derivation (ADR 0020) ——

Deno.test("host derives a REPL process actor and receives its pid", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl("repl-test-1", 0);
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

Deno.test("repl process actor exposes execute and returns the skeleton envelope", async () => {
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
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
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
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
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
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

Deno.test("a crashed repl process emits one exited report", async () => {
  const { reports, reporter } = collectReports();
  const repls = makeReplManager(reporter);
  const handle = await repls.spawnRepl("repl-crash-1", 4);
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
  const handle = await repls.spawnRepl("repl-crash-2", 5);
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
    () => repls.spawnRepl("repl-test-6", 0),
    Error,
    "control bus is not connected",
  );
});

/**
 * A minimal real permission broker: the .NET DenoPermissionBroker shape
 * (JSON-lines over a unix socket). The host's env-forwarding test must point
 * DENO_PERMISSION_BROKER_PATH at an EXISTING socket — Deno fails the child at
 * launch when the path is absent, so a fake path would kill the REPL before
 * the handshake.
 */
class TestBroker implements AsyncDisposable {
  static async start(): Promise<TestBroker> {
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
    } catch {
      // Already closed.
    }
  }
}

Deno.test("repl child receives the forwarded broker path in its environment", async () => {
  await using broker = await TestBroker.start();
  const previous = Deno.env.get("MAIEUTICS_PERMISSION_BROKER");
  Deno.env.set("MAIEUTICS_PERMISSION_BROKER", broker.address);
  const { reporter } = collectReports();
  const repls = makeReplManager(reporter);
  try {
    const handle = await repls.spawnRepl("repl-broker-1", 6);
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
    const handle = await repls.spawnRepl("repl-broker-2", 7);
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

Deno.test("isValidReplPid rejects the host's own pid", async () => {
  assertEquals(isValidReplPid(Deno.pid), false);
  assertEquals(isValidReplPid(0), false);
  assertEquals(isValidReplPid(-1), false);
  assertEquals(isValidReplPid(Number.NaN), false);
  assertEquals(isValidReplPid(Number.POSITIVE_INFINITY), false);
  // Deno.pid is always positive, so 1 is a foreign positive pid unless the
  // test runner itself is pid 1 (essentially impossible on a normal host).
  assertEquals(isValidReplPid(1), Deno.pid !== 1);
});
