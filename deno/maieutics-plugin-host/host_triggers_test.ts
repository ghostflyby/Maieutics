import { assertEquals } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

// ADR 0036 — plugin triggers: the host materializes one listener per declared
// trigger (watch/cron/interval), delivers fires through the wired sinks, and
// watch events coalesce per trigger. Missing watch roots are tolerated.

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;

function triggerPlugin(id: string, dir: string, triggers: unknown): PluginConfig {
  Deno.writeTextFileSync(
    `${dir}/mod.ts`,
    `import { defineExtensionPoint } from ${JSON.stringify(SDK_URL)};\n` +
      `export const hook = defineExtensionPoint("ToolPreInvoke", {\n` +
      `  handler() {\n    return { action: "pass" };\n  },\n});\n`,
  );
  return {
    id,
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: new URL(`file://${dir}/mod.ts`).href,
      specifier: `@maieutics/${id}/main`,
    }],
    triggers: triggers as PluginConfig["triggers"],
  };
}

function makeHost(plugins: PluginConfig[]): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [...plugins],
    storageDataRoot: Deno.makeTempDirSync(),
  });
}

Deno.test("an interval trigger fires the event sink", async () => {
  const fired: Array<{ plugin: string; trigger: string }> = [];
  const dir = Deno.makeTempDirSync();
  const plugin = triggerPlugin("ticker", dir, [
    { name: "fast", kind: "interval", seconds: 1, action: "event" },
  ]);
  const host = makeHost([plugin]);
  host.setTriggerSinks({
    onEvent(pluginId, trigger) {
      fired.push({ plugin: pluginId, trigger });
      return Promise.resolve();
    },
    onRediscover() {},
  });
  await host.startAll();

  const deadline = Date.now() + 4_000;
  while (fired.length < 2 && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  assertEquals(fired.length >= 2, true);
  assertEquals(fired[0], { plugin: "ticker", trigger: "fast" });
  await host.dispose();
});

Deno.test("a watch trigger fires the rediscover sink on path changes", async () => {
  const fired: Array<{ plugin: string; trigger: string }> = [];
  const dir = Deno.makeTempDirSync();
  const watched = `${dir}/config`;
  Deno.mkdirSync(watched);
  const plugin = triggerPlugin("watcher", dir, [
    {
      name: "cfg",
      kind: "watch",
      paths: [`${watched}/**`],
      depth: 2,
      action: { type: "rediscover" },
    },
  ]);
  const host = makeHost([plugin]);
  host.setTriggerSinks({
    async onEvent() {},
    onRediscover(pluginId, trigger) {
      fired.push({ plugin: pluginId, trigger });
    },
  });
  await host.startAll();

  Deno.writeTextFileSync(`${watched}/settings.json`, "{}");
  const deadline = Date.now() + 4_000;
  while (fired.length === 0 && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  assertEquals(fired, [{ plugin: "watcher", trigger: "cfg" }]);
  await host.dispose();
});

Deno.test("a watch trigger tolerates a missing root", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = triggerPlugin("pre-installed", dir, [
    {
      name: "future",
      kind: "watch",
      paths: ["/nonexistent-definitely-missing/root/**"],
      action: "event",
    },
  ]);
  const host = makeHost([plugin]);
  host.setTriggerSinks({
    async onEvent() {},
    onRediscover() {},
  });
  // Installs without throwing; nothing fires.
  await host.startAll();
  assertEquals(host.states()[0].state, "running");
  await host.dispose();
});

Deno.test("a cron trigger fires only on matching minutes", () => {
  const dir = Deno.makeTempDirSync();
  const plugin = triggerPlugin("cron", dir, []);
  const host = makeHost([plugin]);
  // Every minute at :30 (second field irrelevant at 30s sampling).
  const due = host.cronMatches("30 * * * *", new Date("2026-01-01T10:30:00"));
  const notDue = host.cronMatches("30 * * * *", new Date("2026-01-01T10:31:00"));
  const wildcard = host.cronMatches("* * * * *", new Date("2026-01-01T10:31:00"));
  const stepped = host.cronMatches("*/15 * * * *", new Date("2026-01-01T10:45:00"));
  host.dispose();
  assertEquals(due, true);
  assertEquals(notDue, false);
  assertEquals(wildcard, true);
  assertEquals(stepped, true);
});

// ADR 0037 — the live plugin registry: triggers must arm for plugins that join after
// boot (the approval activation path) and disarm when the plugin stops; the worker
// list the event sink resolves must follow the same registry, not the boot config.

Deno.test("a plugin activated after boot arms its triggers and receives events", async () => {
  const fired: Array<{ plugin: string; trigger: string }> = [];
  const bootDir = Deno.makeTempDirSync();
  const activatedDir = Deno.makeTempDirSync();
  const host = makeHost([triggerPlugin("boot", bootDir, [])]);
  host.setTriggerSinks({
    onEvent(pluginId, trigger) {
      fired.push({ plugin: pluginId, trigger });
      return Promise.resolve();
    },
    onRediscover() {},
  });
  await host.startAll();
  assertEquals(host.workersOf("activated"), [], "not yet known to the live registry");

  // The upsert reload (the approval activation form) carries the trigger declaration;
  // arming must follow the live registry, and the sink's target resolution too.
  const activated = triggerPlugin("activated", activatedDir, [
    { name: "beat", kind: "interval", seconds: 1, action: "event" },
  ]);
  await host.reload("activated", "./main", activated);

  const armed = Date.now() + 5_000;
  while (fired.length === 0 && Date.now() < armed) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assertEquals(fired[0], { plugin: "activated", trigger: "beat" });
  assertEquals(host.workersOf("activated").length, 1);

  // Revocation disarms: after the stop, no further fires land.
  const firedAtStop = fired.length;
  await host.stop("activated", "./main");
  const quiet = Date.now() + 2_500;
  while (Date.now() < quiet) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assertEquals(fired.length, firedAtStop, "a stopped plugin's triggers must not fire");
  assertEquals(host.workersOf("activated"), []);
  await host.dispose();
});

Deno.test("a replacement reload re-arms triggers from the new declaration", async () => {
  const fired: Array<{ plugin: string; trigger: string }> = [];
  const dir = Deno.makeTempDirSync();
  const plugin = triggerPlugin("reshaped", dir, []);
  const host = makeHost([plugin]);
  host.setTriggerSinks({
    onEvent(pluginId, trigger) {
      fired.push({ plugin: pluginId, trigger });
      return Promise.resolve();
    },
    onRediscover() {},
  });
  await host.startAll();
  assertEquals(host.workersOf("reshaped").length, 1);

  // The replacement adds an interval trigger; the reload path must re-arm from the
  // live registry (the frozen boot snapshot had none).
  const reshaped = triggerPlugin("reshaped", dir, [
    { name: "tick", kind: "interval", seconds: 1, action: "event" },
  ]);
  await host.reload("reshaped", "./main", reshaped);

  const deadline = Date.now() + 5_000;
  while (fired.length < 2 && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assertEquals(fired.length >= 2, true);
  await host.dispose();
});
