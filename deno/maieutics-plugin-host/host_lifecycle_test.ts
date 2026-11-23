import { pathToFileURL } from "node:url";
import { assertEquals, assertRejects } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

// ADR 0035 — Service-Worker lifecycle: workers install at boot, stay running
// while referenced, and are reclaimed after the idle grace with zero
// references. A stopped worker keeps its registry entries and is restarted on
// demand by an invoke or an acquire.

const IDLE_GRACE = 60_000;
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
      entryUrl: pathToFileURL(`${dir}/mod.ts`).href,
      specifier: `@maieutics/${id}/main`,
    }],
  };
}

function hookSource(body: string): string {
  return `import { defineExtensionPoint } from ${JSON.stringify(SDK_URL)};\n${body}\n`;
}

class LifecycleFixture implements AsyncDisposable {
  readonly #root = Deno.makeTempDirSync();
  #host?: PluginHost;

  plugin(id: string): PluginConfig {
    const directory = Deno.makeTempDirSync({ dir: this.#root });
    return createWorkerPlugin(directory, id, hookSource(PASS_HOOK));
  }

  start(plugins: PluginConfig[]): PluginHost {
    this.#host = new PluginHost({
      sdkUrl: SDK_URL,
      workerEntryUrl: WORKER_ENTRY_URL,
      plugins,
      storageDataRoot: this.#root,
      idleGraceMs: IDLE_GRACE,
    });
    return this.#host;
  }

  async [Symbol.asyncDispose](): Promise<void> {
    try {
      await this.#host?.shutdown();
    } finally {
      await Deno.remove(this.#root, { recursive: true });
    }
  }
}

const PASS_HOOK = `export const hook = defineExtensionPoint("ToolPreInvoke", {
  handler() {
    return { action: "pass" };
  },
});`;

Deno.test("idle reclamation preserves registrations and the next invocation wakes the worker", async () => {
  await using fixture = new LifecycleFixture();
  const host = fixture.start([fixture.plugin("idle")]);
  const startedAt = Date.now();
  await host.startAll();

  const sweep = Date.now();
  await host.reclaimIdleWorkers(sweep);
  await host.reclaimIdleWorkers(startedAt + IDLE_GRACE - 1);
  assertEquals(host.states()[0].state, "running", "the idle grace has not elapsed");

  await host.reclaimIdleWorkers(sweep + IDLE_GRACE);
  assertEquals(host.states()[0].state, "stopped", "the completed sweep must reclaim the worker");
  assertEquals(host.extensions.map((entry) => entry.extensionPoint), ["ToolPreInvoke"]);

  const result = await host.invoke("idle", "./main", "ToolPreInvoke", {});
  assertEquals(result, { action: "pass" });
  assertEquals(host.states()[0].state, "running", "the invocation must restart the worker");
});

Deno.test("a running consumer pins its dependency until reclamation stops the consumer", async () => {
  await using fixture = new LifecycleFixture();
  const provider = fixture.plugin("provider");
  const consumer = fixture.plugin("consumer");
  consumer.dependencies = ["provider"];
  const host = fixture.start([provider, consumer]);
  await host.startAll();
  const state = () =>
    Object.fromEntries(host.states().map((entry) => [entry.pluginId, entry.state]));

  const sweep = Date.now();
  await host.reclaimIdleWorkers(sweep);
  await host.reclaimIdleWorkers(sweep + IDLE_GRACE);
  assertEquals(
    state(),
    { provider: "running", consumer: "stopped" },
    "the provider was pinned during the sweep that stopped the consumer",
  );

  await host.reclaimIdleWorkers(sweep + IDLE_GRACE);
  await host.reclaimIdleWorkers(sweep + 2 * IDLE_GRACE);
  assertEquals(
    state(),
    { provider: "stopped", consumer: "stopped" },
    "the provider is reclaimable after its consumer stops",
  );
});

// ADR 0037 — revocation and activation: `stop` closes a worker and its dependents
// without restarting and removes them from the host set; a replacement-bearing
// reload creates a worker the host does not know (upsert) and restarts the
// dependents a stop cascade closed.

Deno.test("stop removes the worker and its registrations from the host set", async () => {
  await using fixture = new LifecycleFixture();
  const host = fixture.start([fixture.plugin("revoked")]);
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
});

Deno.test("a reload with a replacement creates a worker the host does not know", async () => {
  await using fixture = new LifecycleFixture();
  const host = fixture.start([fixture.plugin("boot")]);
  await host.startAll();

  // The activated plugin was never in the boot config; its upsert reload must
  // create and start it (the approval activation path rides this).
  const activated = fixture.plugin("activated");
  await host.reload("activated", "./main", activated);

  const decision = await host.invoke("activated", "./main", "ToolPreInvoke", {}) as {
    action: string;
  };
  assertEquals(decision.action, "pass");
  assertEquals(
    host.states().map((state) => state.pluginId).sort(),
    ["activated", "boot"],
  );
});

Deno.test("stop cascades to dependents and an upsert reload restarts the closure", async () => {
  await using fixture = new LifecycleFixture();
  const provider = fixture.plugin("provider");
  const consumer = fixture.plugin("consumer");
  consumer.dependencies = ["provider"];
  const host = fixture.start([provider, consumer]);
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
});
