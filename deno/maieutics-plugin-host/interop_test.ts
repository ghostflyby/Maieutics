import { assert, assertEquals, assertNotEquals } from "@std/assert";
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

/** Polls the definer's collection snapshot until `predicate` holds or the timeout elapses. */
async function waitForCollectionSnapshot(
  host: PluginHost,
  predicate: (snapshots: number[]) => boolean,
  timeoutMs = 3_000,
): Promise<number[]> {
  const deadline = Date.now() + timeoutMs;
  let last: number[] = [];
  while (Date.now() < deadline) {
    const value = await host.invoke("definer", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      snapshots?: number[];
    };
    last = value.snapshots ?? [];
    if (predicate(last)) return last;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  return last;
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

Deno.test("an imported contract identity is a remote identity carrying the definer's specifier", async () => {
  const root = Deno.makeTempDirSync();

  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
  );

  // The consumer imports the contract identity through the load hook; the
  // stub synthesizes a remote identity. The handler reports its shape so the
  // test can assert defSpecifier and the remote/local classification.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { isRemoteExtensionPoint } from ${JSON.stringify(SDK_URL)};
    import { ep } from "@maieutics/definer/main";
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({
        action: "continue" as const,
        name: ep.name,
        owner: ep.owner,
        defSpecifier: ep.defSpecifier,
        remote: isRemoteExtensionPoint(ep),
      }),
    });
    `,
    ["definer"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(consumer)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      name?: string;
      owner?: string;
      defSpecifier?: string;
      remote?: boolean;
    };
    assertEquals(value.action, "continue");
    assertEquals(value.name, "sample.metric");
    assertEquals(value.defSpecifier, "@maieutics/definer/main");
    assertEquals(value.remote, true);
  } finally {
    host.dispose();
  }
});

Deno.test("a provider contributes to a defining worker's collection across workers", async () => {
  const root = Deno.makeTempDirSync();

  // The definer owns the extension point identity and the collection. Its
  // ToolPreInvoke handler reports the current collection snapshot.
  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    import { snapshot } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  // The provider imports the contract identity from the definer's export
  // module; the load hook redirects the import to a stub that synthesizes a
  // remote identity carrying the definer's specifier (stub identity
  // replacement). provide() routes the contribution to the definer's remote
  // collection through the name-addressed acquire.
  const provider = writePlugin(
    root,
    "provider",
    `${sdkImport()}
    import { provide, signal } from ${JSON.stringify(SDK_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: (context: { arguments?: { step?: string } }) => {
        if (context.arguments?.step === "set-two") value.value = 2;
        if (context.arguments?.step === "clear") value.value = undefined;
        return { action: "continue" as const };
      },
    });
    `,
    ["definer"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(provider)],
  });
  try {
    await host.startAll();
    // The provider's top-level provide lands asynchronously; poll for it.
    const initial = await waitForCollectionSnapshot(host, (snapshots) => snapshots.length === 1);
    assertEquals(initial, [1]);

    // A change on the provider's signal streams to the definer's collection.
    await host.invoke("provider", "./main", "ToolPreInvoke", { arguments: { step: "set-two" } });
    const updated = await waitForCollectionSnapshot(
      host,
      (snapshots) => snapshots.length === 1 && snapshots[0] === 2,
    );
    assertEquals(updated, [2]);

    // undefined drops the contribution (the "not currently providing" convention).
    await host.invoke("provider", "./main", "ToolPreInvoke", { arguments: { step: "clear" } });
    const cleared = await waitForCollectionSnapshot(host, (snapshots) => snapshots.length === 0);
    assertEquals(cleared, []);
  } finally {
    host.dispose();
  }
});

Deno.test("a provider's unprovide withdraws its remote contribution", async () => {
  const root = Deno.makeTempDirSync();

  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    import { snapshot } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  // The provider registers the contribution and exposes an unprovide step in
  // its handler; unprovide() routes the withdrawal back to the definer.
  const provider = writePlugin(
    root,
    "provider",
    `${sdkImport()}
    import { provide, signal, unprovide, type ProviderRegistration } from ${
      JSON.stringify(SDK_URL)
    };
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(1);
    let registration: ProviderRegistration<number> | undefined;
    registration = provide(ep, value);
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: (context: { arguments?: { step?: string } }) => {
        if (context.arguments?.step === "unprovide" && registration) {
          unprovide(registration);
          registration = undefined;
        }
        return { action: "continue" as const };
      },
    });
    `,
    ["definer"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(provider)],
  });
  try {
    await host.startAll();
    const initial = await waitForCollectionSnapshot(host, (snapshots) => snapshots.length === 1);
    assertEquals(initial, [1]);

    // Explicit unprovide withdraws the remote contribution.
    await host.invoke("provider", "./main", "ToolPreInvoke", { arguments: { step: "unprovide" } });
    const withdrawn = await waitForCollectionSnapshot(host, (snapshots) => snapshots.length === 0);
    assertEquals(withdrawn, []);
  } finally {
    host.dispose();
  }
});

Deno.test("multiple provider workers aggregate into the defining worker's collection", async () => {
  const root = Deno.makeTempDirSync();

  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    import { snapshot } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("shared.metric");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  const providerSource = (pluginName: string, value: number): string =>
    `${sdkImport()}
    import { provide, signal } from ${JSON.stringify(SDK_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(${value});
    provide(ep, value);
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `;
  const providerA = writePlugin(root, "provider-a", providerSource("provider-a", 1), ["definer"]);
  const providerB = writePlugin(root, "provider-b", providerSource("provider-b", 2), ["definer"]);

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(providerA), pluginConfig(providerB)],
  });
  try {
    await host.startAll();
    // Both contributions land; registration order is arrival order, so compare
    // the values as a set.
    const snapshots = await waitForCollectionSnapshot(host, (s) => s.length === 2);
    assertEquals([...snapshots].sort(), [1, 2]);
  } finally {
    host.dispose();
  }
});

Deno.test("cascading the stop to a dependent does not hang its stream iteration", async () => {
  const root = Deno.makeTempDirSync();

  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    import { signal, provide, subscribe } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("sample.metric");
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const metrics = defineActor({
      changes(): AsyncIterable<number[]> { return subscribe(ep); },
    });
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
  );

  // The consumer acquires the stream and reads one snapshot; the second next()
  // races a timeout. When the host cascades the stop (definer disposed → the
  // dependent consumer is stopped too), the in-flight iteration must settle
  // (resolve or reject) instead of hanging forever.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { depActor } from ${JSON.stringify(SDK_URL)};
    import type { metrics as MetricsSurface } from "@maieutics/definer/main";
    const metrics = depActor<typeof MetricsSurface>("@maieutics/definer/main", "metrics");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const changes = metrics.changes();
        const iterator = changes[Symbol.asyncIterator]();
        const first = await iterator.next();
        const snapshots = [first.value as number[]];
        const second = await Promise.race([
          iterator.next().then(
            (r) => ({ kind: "resolved" as const, done: r.done }),
            (e) => ({ kind: "rejected" as const, error: (e as Error).message }),
          ),
          new Promise((r) => setTimeout(() => r({ kind: "timeout" as const }), 3_000)),
        ]);
        return { action: "continue" as const, snapshots, second };
      },
    });
    `,
    ["definer"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(consumer)],
  });
  await host.startAll();
  // Start the consumer's stream read; it acquires the stream and reads the
  // first snapshot, then waits on the second next().
  const consumerRead = host.invoke("consumer", "./main", "ToolPreInvoke", {}).catch(
    (error) => ({ __invokeError: (error as Error).message }),
  );
  // Give the consumer time to acquire the stream and read the first snapshot.
  await new Promise((resolve) => setTimeout(resolve, 800));

  // Dispose the whole host: the definer (and its dependent consumer) are
  // cascaded to stopped, which must settle the consumer's pending iteration.
  host.dispose();

  const result = await consumerRead as { second?: { kind: string } };
  assertNotEquals(
    result.second?.kind,
    "timeout",
    "the consumer's stream iteration must not hang when the cascade stops it",
  );
});

Deno.test("reloading a provider drops its stale contribution from the definer", async () => {
  const root = Deno.makeTempDirSync();

  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    import { snapshot } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  const providerSource = (): string =>
    `${sdkImport()}
    import { provide, signal } from ${JSON.stringify(SDK_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `;
  const provider = writePlugin(root, "provider", providerSource(), ["definer"]);

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(provider)],
  });
  try {
    await host.startAll();
    // The provider's contribution lands.
    const initial = await waitForCollectionSnapshot(host, (s) => s.length === 1);
    assertEquals(initial, [1]);

    // Reload the provider: the host stops it (notifying the definer to drop
    // its contribution) then restarts it, which contributes again. The stale
    // contribution must not linger — the definer must show exactly one value
    // from the fresh provider, not [1, 1].
    await host.reload("provider", "./main", pluginConfig(provider));
    const after = await waitForCollectionSnapshot(host, (s) => s.length === 1);
    assertEquals(after, [1]);
  } finally {
    host.dispose();
  }
});

Deno.test("reloading a provider to a non-contributing version drops its contribution", async () => {
  const root = Deno.makeTempDirSync();

  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    import { snapshot } from ${JSON.stringify(SDK_URL)};
    export const ep = defineExtensionPoint<number>("shared.metric");
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  // provider-a contributes [1]; provider-b contributes [2]. Reloading
  // provider-a to a version that no longer contributes must drop its [1]
  // from the definer (host notifies via __maieuticsProviderDead), leaving
  // only provider-b's [2] — never a lingering stale [1].
  const contributing = (name: string, value: number): string =>
    `${sdkImport()}
    import { provide, signal } from ${JSON.stringify(SDK_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(${value});
    provide(ep, value);
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `;
  const nonContributing = (name: string): string =>
    `${sdkImport()}
    import { ep } from "@maieutics/definer/main";
    export const pre = defineExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `;
  const providerA = writePlugin(root, "provider-a", contributing("provider-a", 1), ["definer"]);
  const providerB = writePlugin(root, "provider-b", contributing("provider-b", 2), ["definer"]);

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(providerA), pluginConfig(providerB)],
  });
  try {
    await host.startAll();
    const both = await waitForCollectionSnapshot(host, (s) => s.length === 2);
    assertEquals([...both].sort(), [1, 2]);

    // Reload provider-a to a version that does not contribute.
    const nextA = writePlugin(root, "provider-a", nonContributing("provider-a"), ["definer"]);
    await host.reload("provider-a", "./main", pluginConfig(nextA));

    // Only provider-b's [2] remains.
    const after = await waitForCollectionSnapshot(host, (s) => s.length === 1);
    assertEquals(after, [2]);
  } finally {
    host.dispose();
  }
});
