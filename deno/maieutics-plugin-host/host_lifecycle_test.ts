import { assertEquals, assertRejects } from "@std/assert";
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

// ADR 0037 — revocation and activation: `stop` closes a worker and its dependents
// without restarting and removes them from the host set; a replacement-bearing
// reload creates a worker the host does not know (upsert) and restarts the
// dependents a stop cascade closed.

Deno.test("stop removes the worker and its registrations from the host set", async () => {
  const dir = Deno.makeTempDirSync();
  const plugin = createWorkerPlugin(dir, "revoked", hookSource(PASS_HOOK));
  const host = makeHost([plugin], 60_000);
  await host.startAll();
  const decision = await host.invoke("revoked", "./main", "ToolPreInvoke", {}) as {
    action: string;
  };
  assertEquals(decision.action, "pass");
  assertEquals(host.extensions.length, 1);

  await host.stop("revoked", "./main");

  // The worker left the set entirely — unlike an idle reclamation, there is no
  // registration left to wake, so an invoke fails instead of restarting it.
  assertEquals(host.states().length, 0, "the stopped worker must leave the host set");
  assertEquals(host.extensions.length, 0, "the stopped worker's registrations must drop");
  await assertRejects(
    () => host.invoke("revoked", "./main", "ToolPreInvoke", {}),
    Error,
    "No worker for key",
  );
  await host.dispose();
});

Deno.test("a reload with a replacement creates a worker the host does not know", async () => {
  const dir = Deno.makeTempDirSync();
  const bootPlugin = createWorkerPlugin(dir, "boot", hookSource(PASS_HOOK));
  const host = makeHost([bootPlugin], 60_000);
  await host.startAll();

  // The activated plugin was never in the boot config; its upsert reload must
  // create and start it (the approval activation path rides this).
  const activatedDir = Deno.makeTempDirSync();
  const activated = createWorkerPlugin(activatedDir, "activated", hookSource(PASS_HOOK));
  await host.reload("activated", "./main", activated);

  const decision = await host.invoke("activated", "./main", "ToolPreInvoke", {}) as {
    action: string;
  };
  assertEquals(decision.action, "pass");
  assertEquals(
    host.states().map((state) => state.pluginId).sort(),
    ["activated", "boot"],
  );
  await host.dispose();
});

Deno.test("stop cascades to dependents and an upsert reload restarts the closure", async () => {
  const providerDir = Deno.makeTempDirSync();
  const provider = createWorkerPlugin(providerDir, "provider", hookSource(PASS_HOOK));
  const consumerDir = Deno.makeTempDirSync();
  const consumer = createWorkerPlugin(consumerDir, "consumer", hookSource(PASS_HOOK));
  consumer.dependencies = ["provider"];
  const host = makeHost([provider, consumer], 60_000);
  await host.startAll();
  assertEquals(
    host.states().map((state) => state.state).every((state) => state === "running"),
    true,
  );

  // Revoking the provider closes the consumer too — it cannot honor its own
  // declarations without the dependency. The consumer's handle stays (its own
  // approval is not the revoked one), stopped.
  await host.stop("provider", "./main");
  assertEquals(
    host.states().map((state) => state.pluginId).sort(),
    ["consumer"],
  );
  assertEquals(host.states()[0].state, "stopped");

  // Re-approval of the provider upserts it back and restarts the closure,
  // dependents included.
  await host.reload("provider", "./main", provider);
  assertEquals(
    host.states().map((state) => state.pluginId).sort(),
    ["consumer", "provider"],
  );
  assertEquals(
    host.states().map((state) => state.state).every((state) => state === "running"),
    true,
  );
  const decision = await host.invoke("consumer", "./main", "ToolPreInvoke", {}) as {
    action: string;
  };
  assertEquals(decision.action, "pass");
  await host.dispose();
});
