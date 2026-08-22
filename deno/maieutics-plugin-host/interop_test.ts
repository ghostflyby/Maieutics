import { assert, assertEquals } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

const SDK_URL = new URL("../maieutics-plugin-sdk/mod.ts", import.meta.url).href;
const WORKER_ENTRY_URL = new URL("./worker_entry.ts", import.meta.url).href;

function pathToFileUrl(path: string): string {
  return new URL(`file://${path}`).href;
}

interface TestPlugin {
  id: string;
  specifier: string;
  entryUrl: string;
  rootDir: string;
  source: string;
  dependencies?: string[];
}

function writePlugin(
  root: string,
  name: string,
  source: string,
  dependencies?: string[],
): TestPlugin {
  const dir = `${root}/${name}`;
  Deno.mkdirSync(dir, { recursive: true });
  const entryPath = `${dir}/mod.ts`;
  Deno.writeTextFileSync(entryPath, source);
  return {
    id: name,
    specifier: `@maieutics/${name}/main`,
    entryUrl: pathToFileUrl(entryPath),
    rootDir: dir,
    source,
    dependencies,
  };
}

function pluginConfig(plugin: TestPlugin): PluginConfig {
  return {
    id: plugin.id,
    rootDir: plugin.rootDir,
    permissions: { read: [plugin.rootDir] },
    workers: [{
      exportName: "./main",
      entryUrl: plugin.entryUrl,
      specifier: plugin.specifier,
    }],
    ...(plugin.dependencies === undefined ? {} : { dependencies: plugin.dependencies }),
  };
}

function sdkImport(): string {
  return `import { defineActor, defineExtensionPoint } from ${JSON.stringify(SDK_URL)};`;
}

Deno.test("a consumer plugin calls a dependency actor across workers", async () => {
  const root = Deno.makeTempDirSync();

  const dep = writePlugin(
    root,
    "dep",
    `${sdkImport()}
    export const math = defineActor({
      double(n: number): number { return n * 2; },
      add(a: number, b: number): Promise<number> { return Promise.resolve(a + b); },
    });
    export const discover = defineExtensionPoint("McpDiscover", { handler: () => [] });
    `,
  );

  // The consumer declares the dependency (id "dep") and calls its actor
  // surface through the canonical specifier. Compile-time types come from the
  // real module via `typeof import(...)`; the runtime stub is a lazy acquire
  // surface — no export names are extracted anywhere.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { depActor } from ${JSON.stringify(SDK_URL)};
    import type { math as MathSurface } from "@maieutics/dep/main";
    const math = depActor<typeof MathSurface>("@maieutics/dep/main", "math");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const doubled = await math.double(21);
        const sum = await math.add(1, 2);
        return { action: "continue" as const, note: \`\${doubled}/\${sum}\` };
      },
    });
    `,
    ["dep"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(dep), pluginConfig(consumer)],
  });
  try {
    const registrations = await host.startAll();
    assertEquals(
      registrations.map((entry) => entry.extensionPoint).sort(),
      ["McpDiscover", "ToolPreInvoke"],
    );
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {
      tool: "read_text",
      arguments: {},
      callId: "c1",
    }) as { action?: string; note?: string };
    assertEquals(value.action, "continue");
    assertEquals(value.note, "42/3");
  } finally {
    host.dispose();
  }
});

Deno.test("a static default import of an actor entry is redirected to the acquire surface", async () => {
  const root = Deno.makeTempDirSync();

  const dep = writePlugin(
    root,
    "dep",
    `${sdkImport()}
    export const math = defineActor({
      double(n: number): number { return n * 2; },
    });
    `,
  );

  // The consumer imports the dependency specifier's default; the load hook
  // matches it against the actor-entry registry and serves the stub, whose
  // default is the lazy acquire surface. `dep.math.double` crosses workers.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import dep from "@maieutics/dep/main";
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const doubled = await dep.math.double(21);
        return { action: "continue" as const, note: String(doubled) };
      },
    });
    `,
    ["dep"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(dep), pluginConfig(consumer)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {
      tool: "read_text",
      arguments: {},
      callId: "c2",
    }) as { action?: string; note?: string };
    assertEquals(value.action, "continue");
    assertEquals(value.note, "42");
  } finally {
    host.dispose();
  }
});

Deno.test("a jsr:-prefixed specifier with a version segment is normalized and redirected", async () => {
  const root = Deno.makeTempDirSync();

  const dep = writePlugin(
    root,
    "dep",
    `${sdkImport()}
    export const math = defineActor({ double(n: number): number { return n * 2; } });
    `,
  );

  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import dep from "jsr:@maieutics/dep@0.1/main";
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const doubled = await dep.math.double(10);
        return { action: "continue" as const, note: String(doubled) };
      },
    });
    `,
    ["dep"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(dep), pluginConfig(consumer)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {
      tool: "read_text",
      arguments: {},
      callId: "c3",
    }) as { action?: string; note?: string };
    assertEquals(value.action, "continue");
    assertEquals(value.note, "20");
  } finally {
    host.dispose();
  }
});

Deno.test("a non-entry plain module import is not redirected and loads its real content", async () => {
  const root = Deno.makeTempDirSync();

  const dep = writePlugin(
    root,
    "dep",
    `${sdkImport()}
    export const math = defineActor({ double(n: number): number { return n * 2; } });
    `,
  );

  // The consumer imports a plain local module (not an actor entry). The hook
  // must not redirect it: the value is the real exported constant.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { localValue } from "./local.ts";
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: async () => ({ action: "continue" as const, note: String(localValue) }),
    });
    `,
    ["dep"],
  );
  Deno.writeTextFileSync(`${consumer.rootDir}/local.ts`, `export const localValue = 7;`);

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(dep), pluginConfig(consumer)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {
      tool: "read_text",
      arguments: {},
      callId: "c4",
    }) as { action?: string; note?: string };
    assertEquals(value.action, "continue");
    assertEquals(value.note, "7");
  } finally {
    host.dispose();
  }
});

Deno.test("a consumer subscribes to a provider's reactive extension point across workers", async () => {
  const root = Deno.makeTempDirSync();

  // The provider exposes its reactive extension point's collection as an
  // actor method returning an AsyncIterable. The consumer acquires the actor
  // surface via depActor and iterates the stream (worker-actor iterable codec
  // transports it across workers lazily).
  const provider = writePlugin(
    root,
    "provider",
    `${sdkImport()}
    import { signal } from "@preact/signals-core";
    import { provide, subscribe } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("sample.metric");
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const metrics = defineActor({
      changes(): AsyncIterable<number[]> {
        return subscribe(ep);
      },
    });
    `,
  );

  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { depActor } from ${JSON.stringify(SDK_URL)};
    import type { metrics as MetricsSurface } from "@maieutics/provider/main";
    const metrics = depActor<typeof MetricsSurface>("@maieutics/provider/main", "metrics");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const changes = metrics.changes();
        const isIterable = typeof (changes as { [Symbol.asyncIterator]?: unknown })[Symbol.asyncIterator] === "function";
        const snapshots: number[][] = [];
        if (isIterable) {
          for await (const snapshot of changes) {
            snapshots.push(snapshot);
            break; // initial snapshot is enough to prove the cross-worker stream
          }
        }
        return { action: "continue" as const, snapshots, isIterable };
      },
    });
    `,
    ["provider"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(provider), pluginConfig(consumer)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      snapshots?: number[][];
      isIterable?: boolean;
    };
    assertEquals(value.action, "continue");
    // The consumer received at least the initial collection snapshot [1].
    assert(value.snapshots !== undefined && value.snapshots.length >= 1);
    assertEquals(value.snapshots[0], [1]);
  } finally {
    host.dispose();
  }
});
