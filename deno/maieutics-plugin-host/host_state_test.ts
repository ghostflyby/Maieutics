// Dedicated state-machine tests: these pin the worker lifecycle invariants the
// audit called out — crash classification, closure restart with dependents,
// concurrent-reload serialization, dispose fencing, and per-plugin start
// isolation. Run via the deno.json workspace test task (import-granted parent).
import { assertEquals } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;
const SDK = JSON.stringify(SDK_URL);
const STORAGE_DATA_ROOT = Deno.makeTempDirSync();

function sdkImport(): string {
  return `import { defineActor, defineExtensionPoint } from ${SDK};`;
}

function fileUrl(path: string): string {
  return new URL(`file://${path}`).href;
}

function writePlugin(root: string, name: string, source: string): PluginConfig {
  const dir = `${root}/${name}`;
  Deno.mkdirSync(dir, { recursive: true });
  const entryPath = `${dir}/mod.ts`;
  Deno.writeTextFileSync(entryPath, source);
  return {
    id: name,
    rootDir: dir,
    permissions: { read: [dir] },
    workers: [{
      exportName: "./main",
      entryUrl: fileUrl(entryPath),
      specifier: `@maieutics/${name}/main`,
    }],
  };
}

function withDependencies(plugin: PluginConfig, dependencies: string[]): PluginConfig {
  return { ...plugin, dependencies };
}

function makeHost(plugins: readonly PluginConfig[]): PluginHost {
  return new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [...plugins],
    storageDataRoot: STORAGE_DATA_ROOT,
  });
}

function stateOf(host: PluginHost, id: string): string {
  const state = host.states().find((entry) => entry.pluginId === id);
  if (state === undefined) throw new Error(`No worker state for '${id}'.`);
  return state.state;
}

const DEFINER_SOURCE = `${sdkImport()}
export const math = defineActor({
  double(n: number): number { return n * 2; },
});
export const discover = defineExtensionPoint("McpDiscover", { handler: () => [] });
`;

const CONSUMER_SOURCE = `${sdkImport()}
import { depActor } from ${SDK};
const math = depActor<{ double(n: number): number }>("${"@"}maieutics/dep/main", "math");
export const discover = defineExtensionPoint("McpDiscover", {
  handler: async () => [{ doubled: await math.double(21) }],
});
`;

Deno.test("a reload whose replacement fails to start ends failed without corrupting its dependents", async () => {
  const root = Deno.makeTempDirSync();
  const dep = writePlugin(root, "dep", DEFINER_SOURCE);
  const consumer = withDependencies(
    writePlugin(root, "consumer", CONSUMER_SOURCE),
    ["dep"],
  );
  const host = makeHost([dep, consumer]);
  try {
    await host.startAll();

    // The replacement entry throws at import: the cascade stops the closure,
    // the rebuilt worker fails alone, and its dependent still restarts. The
    // startup death must classify as Failed (the spawn rejection), never
    // flip-flop through Crashed via the death event, and the reloaded closure
    // must end in a coherent state.
    const brokenReplacement: PluginConfig = {
      ...dep,
      workers: [{
        ...dep.workers[0],
        entryUrl: fileUrl(`${root}/broken-replacement.ts`),
      }],
    };
    Deno.writeTextFileSync(
      `${root}/broken-replacement.ts`,
      'throw new Error("bad replacement");\n',
    );

    await host.reload("dep", "./main", brokenReplacement);

    assertEquals(stateOf(host, "dep"), "failed");
    assertEquals(stateOf(host, "consumer"), "running");
    for (const state of host.states()) {
      assertEquals(
        state.state === "crashed" || state.state === "stopping" ||
          state.state === "starting",
        false,
        `worker '${state.pluginId}' ended in transient state '${state.state}'`,
      );
    }
  } finally {
    host.dispose();
  }
});

Deno.test("reloading a dependency plugin restarts its dependents into a working generation", async () => {
  const root = Deno.makeTempDirSync();
  const dep = writePlugin(root, "dep", DEFINER_SOURCE);
  const consumer = withDependencies(
    writePlugin(root, "consumer", CONSUMER_SOURCE),
    ["dep"],
  );
  const host = makeHost([dep, consumer]);
  try {
    await host.startAll();

    await host.reload("dep", "./main");

    assertEquals(stateOf(host, "dep"), "running");
    assertEquals(stateOf(host, "consumer"), "running");
    const value = await host.invoke("consumer", "./main", "McpDiscover", null) as {
      doubled?: number;
    }[];
    assertEquals(value?.[0]?.doubled, 42);
  } finally {
    host.dispose();
  }
});

Deno.test("concurrent reloads serialize and converge to a fully running topology", async () => {
  const root = Deno.makeTempDirSync();
  const dep = writePlugin(root, "dep", DEFINER_SOURCE);
  const consumer = withDependencies(
    writePlugin(root, "consumer", CONSUMER_SOURCE),
    ["dep"],
  );
  const host = makeHost([dep, consumer]);
  try {
    await host.startAll();

    // Overlapping closures: reload(dep) touches {dep, consumer}; reload(consumer)
    // touches {consumer}. The lifecycle queue serializes them; both must end
    // running with a working cross-worker call.
    const first = host.reload("dep", "./main");
    const second = host.reload("consumer", "./main");
    await Promise.all([first, second]);

    assertEquals(stateOf(host, "dep"), "running");
    assertEquals(stateOf(host, "consumer"), "running");
    const value = await host.invoke("consumer", "./main", "McpDiscover", null) as {
      doubled?: number;
    }[];
    assertEquals(value?.[0]?.doubled, 42);
  } finally {
    host.dispose();
  }
});

Deno.test("dispose during an in-flight reload never leaves a running worker", async () => {
  const root = Deno.makeTempDirSync();
  const dep = writePlugin(root, "dep", DEFINER_SOURCE);
  const consumer = withDependencies(
    writePlugin(root, "consumer", CONSUMER_SOURCE),
    ["dep"],
  );
  const host = makeHost([dep, consumer]);
  await host.startAll();

  const reload = host.reload("dep", "./main");
  host.dispose();
  await reload;

  for (const state of host.states()) {
    assertEquals(
      state.state === "running",
      false,
      `worker '${state.pluginId}' is still running after dispose`,
    );
  }
});

Deno.test("startAll isolates a broken plugin instead of failing the host", async () => {
  const root = Deno.makeTempDirSync();
  const broken = writePlugin(root, "broken", 'throw new Error("bad import");\n');
  const healthy = writePlugin(
    root,
    "healthy",
    `${sdkImport()}
export const discover = defineExtensionPoint("McpDiscover", { handler: () => [] });
`,
  );
  const host = makeHost([broken, healthy]);
  try {
    // Must resolve (not reject): the broken worker fails alone.
    await host.startAll();

    assertEquals(stateOf(host, "broken"), "failed");
    assertEquals(stateOf(host, "healthy"), "running");
    await host.invoke("healthy", "./main", "McpDiscover", null);
  } finally {
    host.dispose();
  }
});
