import { assertEquals } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

// ADR 0035 — Service-Worker lifecycle: workers install at boot, stay running
// while referenced, and are reclaimed after the idle grace with zero
// references. A stopped worker keeps its registry entries and is restarted on
// demand by an invoke or an acquire.

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;

function createWorkerPlugin(dir: string, id: string, source: string): PluginConfig {
  Deno.writeTextFileSync(`${dir}/mod.ts`, source);
  return {
    id,
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: new URL(`file://${dir}/mod.ts`).href,
      specifier: `@maieutics/${id}/main`,
    }],
  };
}

function hookSource(body: string): string {
  return `import { defineExtensionPoint } from ${JSON.stringify(SDK_URL)};\n${body}\n`;
}

function makeHost(plugins: PluginConfig[], idleGraceMs = 1_000): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [...plugins],
    storageDataRoot: Deno.makeTempDirSync(),
    idleGraceMs,
  });
}

const PASS_HOOK = `export const hook = defineExtensionPoint("ToolPreInvoke", {
  handler() {
    return { action: "pass" };
  },
});`;

async function waitFor(condition: () => boolean, ms: number): Promise<void> {
  const deadline = Date.now() + ms;
  while (!condition()) {
    if (Date.now() > deadline) throw new Error("condition not met in time");
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

Deno.test("an unreferenced worker is reclaimed after the idle grace", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createWorkerPlugin(dir, "idle", hookSource(PASS_HOOK));
  const host = makeHost([plugin], 1_000);
  await host.startAll();

  // An invocation references the worker: it stays running through the grace.
  const decision = await host.invoke("idle", "./main", "ToolPreInvoke", {}) as {
    action: string;
  };
  assertEquals(decision.action, "pass");
  await new Promise((resolve) => setTimeout(resolve, 500));
  assertEquals(host.states()[0].state, "running");

  // Zero references for the whole grace: reclaimed.
  await waitFor(() => host.states()[0].state === "stopped", 3_000);
  assertEquals(host.states()[0].state, "stopped");
  // Registration survives termination (Service-Worker registrations persist).
  assertEquals(host.extensions.length, 1);
  await host.dispose();
});

Deno.test("an invoke on a reclaimed worker restarts it on demand", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createWorkerPlugin(dir, "woken", hookSource(PASS_HOOK));
  const host = makeHost([plugin], 300);
  await host.startAll();
  await host.invoke("woken", "./main", "ToolPreInvoke", {});
  await waitFor(() => host.states()[0].state === "stopped", 3_000);

  // The wake: invoking the stopped worker restarts it and dispatches.
  const decision = await host.invoke("woken", "./main", "ToolPreInvoke", {}) as {
    action: string;
  };
  assertEquals(decision.action, "pass");
  assertEquals(host.states()[0].state, "running");
  await host.dispose();
});

Deno.test("a running consumer pins its dependency from reclamation", async () => {
  const providerDir = Deno.makeTempDirSync();
  const provider = createWorkerPlugin(
    providerDir,
    "provider",
    hookSource(PASS_HOOK),
  );
  const consumerDir = Deno.makeTempDirSync();
  const consumer = createWorkerPlugin(
    consumerDir,
    "consumer",
    hookSource(PASS_HOOK),
  );
  // The consumer declares the provider as a dependency: while the consumer
  // runs, the provider is pinned.
  consumer.dependencies = ["provider"];
  const host = makeHost([provider, consumer], 300);
  await host.startAll();

  // Keep the consumer referenced while the grace elapses twice over.
  const deadline = Date.now() + 900;
  while (Date.now() < deadline) {
    await host.invoke("consumer", "./main", "ToolPreInvoke", {});
    await new Promise((resolve) => setTimeout(resolve, 50));
  }

  const providerState = host.states().find((state) => state.pluginId === "provider");
  assertEquals(providerState?.state, "running", "the pinned provider must survive");

  // The consumer stops being referenced and is reclaimed; the provider then
  // loses its pin and follows.
  await waitFor(() => host.states().every((state) => state.state === "stopped"), 5_000);
  await host.dispose();
});

Deno.test("a stopped worker keeps its registry entries", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createWorkerPlugin(dir, "registered", hookSource(PASS_HOOK));
  const host = makeHost([plugin], 300);
  const registered = await host.startAll();
  assertEquals(registered.length, 1);
  await waitFor(() => host.states()[0].state === "stopped", 3_000);
  // Registrations survive termination — the kernel keeps routing by them.
  assertEquals(host.extensions.length, 1);
  assertEquals(host.extensions[0].extensionPoint, "ToolPreInvoke");
  await host.dispose();
});
