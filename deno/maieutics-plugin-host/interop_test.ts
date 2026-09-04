import { assert, assertEquals, assertNotEquals } from "@std/assert";
import { type PluginConfig, PluginHost } from "./host.ts";

const SDK_URL = new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href;
const REACTIVE_URL = new URL("../maieutics-plugin-sdk/reactive.ts", import.meta.url).href;
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
  return `import { defineActor, defineExtensionPoint as defineHostExtensionPoint } from ${
    JSON.stringify(SDK_URL)
  };`;
}

/** Reactive contract identity + collection API, from the low-level reactive path. */
function reactiveImport(): string {
  return `import { defineExtensionPoint, signal, provide, unprovide, snapshot, subscribe, values } from ${
    JSON.stringify(REACTIVE_URL)
  };`;
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
    export const discover = defineHostExtensionPoint("McpDiscover", { handler: () => [] });
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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

Deno.test("a dynamic import of an actor entry is redirected to the acquire surface to the acquire surface", async () => {
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

  // dynamicImport is a pass-through wrapper: the load hook's canonical match
  // rewrites the actor specifier to the stub, so the captured namespace's
  // default export is the lazy acquire surface — the same machinery the
  // static import path produces.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const dep = await import("@maieutics/dep/main");
        const doubled = await dep.default.math.double(21);
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
      callId: "c2d",
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    import { signal, provide, unprovide, type ProviderRegistration } from ${
      JSON.stringify(REACTIVE_URL)
    };
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(1);
    let registration: ProviderRegistration<number> | undefined;
    registration = provide(ep, value);
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("shared.metric");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  const providerSource = (_pluginName: string, value: number): string =>
    `${sdkImport()}
    import { signal, provide } from ${JSON.stringify(REACTIVE_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(${value});
    provide(ep, value);
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("sample.metric");
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const metrics = defineActor({
      changes(): AsyncIterable<number[]> { return subscribe(ep); },
    });
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("sample.metric");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  const providerSource = (): string =>
    `${sdkImport()}
    import { signal, provide } from ${JSON.stringify(REACTIVE_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(1);
    provide(ep, value);
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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
    ${reactiveImport()}
    export const ep = defineExtensionPoint<number>("shared.metric");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const, snapshots: snapshot(ep) }),
    });
    `,
  );

  // provider-a contributes [1]; provider-b contributes [2]. Reloading
  // provider-a to a version that no longer contributes must drop its [1]
  // from the definer (host notifies via __maieuticsProviderDead), leaving
  // only provider-b's [2] — never a lingering stale [1].
  const contributing = (_name: string, value: number): string =>
    `${sdkImport()}
    import { signal, provide } from ${JSON.stringify(REACTIVE_URL)};
    import { ep } from "@maieutics/definer/main";
    const value = signal<number | undefined>(${value});
    provide(ep, value);
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `;
  const nonContributing = (_name: string): string =>
    `${sdkImport()}
    import { ep } from "@maieutics/definer/main";
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
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

Deno.test("a transparent service travels through a collection as a remote reference", async () => {
  const root = Deno.makeTempDirSync();

  // The definer contributes an original-type service instance (no export, no
  // handle). The SDK converts it to a remote reference internally; the
  // consumer receives a Remote<T> proxy directly.
  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    ${reactiveImport()}
    import { defineServiceExtensionPoint, markCollectionStream } from ${JSON.stringify(SDK_URL)};
    export const ep = defineServiceExtensionPoint<{
      hello(): string;
      add(a: number, b: number): number;
    }>("sample.services");
    const service = {
      hello(): string { return "hi-from-service"; },
      add(a: number, b: number): number { return a + b; },
    };
    const value = signal<unknown | undefined>(service);
    provide(ep, value);
    export const metrics = defineActor({
      changes(): AsyncIterable<unknown[]> {
        return markCollectionStream(subscribe(ep));
      },
    });
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
  );

  // The consumer receives the service via the changes stream as a Remote<T>
  // proxy — no manual resolution needed.
  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { depActor } from ${JSON.stringify(SDK_URL)};
    import type { metrics as MetricsSurface } from "@maieutics/definer/main";
    const metrics = depActor<typeof MetricsSurface>("@maieutics/definer/main", "metrics");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const changes = metrics.changes();
        const iter = changes[Symbol.asyncIterator]();
        const first = await iter.next();
        const svc = (first.value as unknown[])[0] as {
          hello(): Promise<string>;
          add(a: number, b: number): Promise<number>;
        };
        const hello = await svc.hello();
        const sum = await svc.add(2, 3);
        return { action: "continue" as const, hello, sum };
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
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      hello?: string;
      sum?: number;
    };
    assertEquals(value.action, "continue");
    assertEquals(value.hello, "hi-from-service");
    assertEquals(value.sum, 5);
  } finally {
    host.dispose();
  }
});

Deno.test("data and service extension points coexist in one worker", async () => {
  const root = Deno.makeTempDirSync();

  // The definer owns a data extension point and a service extension point.
  // The consumer receives data as-is and the service as a Remote<T> proxy.
  const definer = writePlugin(
    root,
    "definer",
    `${sdkImport()}
    ${reactiveImport()}
    import { defineServiceExtensionPoint, markCollectionStream } from ${JSON.stringify(SDK_URL)};
    export const dataEp = defineExtensionPoint<{ name: string; count: number }>("sample.data");
    const data = signal<{ name: string; count: number } | undefined>(
      { name: "plain", count: 7 },
    );
    provide(dataEp, data);
    export const svcEp = defineServiceExtensionPoint<{ echo(s: string): string }>("sample.svc");
    const svc = { echo(s: string): string { return "echo:" + s; } };
    const service = signal<{ echo(s: string): string } | undefined>(svc);
    provide(svcEp, service);
    export const metrics = defineActor({
      dataChanges(): AsyncIterable<unknown[]> {
        return markCollectionStream(subscribe(dataEp));
      },
      svcChanges(): AsyncIterable<unknown[]> {
        return markCollectionStream(subscribe(svcEp));
      },
    });
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
  );

  const consumer = writePlugin(
    root,
    "consumer",
    `${sdkImport()}
    import { depActor } from ${JSON.stringify(SDK_URL)};
    import type { metrics as MetricsSurface } from "@maieutics/definer/main";
    const metrics = depActor<typeof MetricsSurface>("@maieutics/definer/main", "metrics");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const dataIter = metrics.dataChanges()[Symbol.asyncIterator]();
        const dataFirst = await dataIter.next();
        const dataElem = (dataFirst.value as unknown[])[0] as {
          name: string;
          count: number;
        };
        const svcIter = metrics.svcChanges()[Symbol.asyncIterator]();
        const svcFirst = await svcIter.next();
        const svcElem = (svcFirst.value as unknown[])[0] as {
          echo(s: string): Promise<string>;
        };
        const echo = await svcElem.echo("hi");
        return {
          action: "continue" as const,
          dataName: dataElem.name,
          dataCount: dataElem.count,
          echo,
        };
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
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      dataName?: string;
      dataCount?: number;
      echo?: string;
    };
    assertEquals(value.action, "continue");
    assertEquals(value.dataName, "plain");
    assertEquals(value.dataCount, 7);
    assertEquals(value.echo, "echo:hi");
  } finally {
    host.dispose();
  }
});

Deno.test("a service contributed from another worker arrives as a Remote proxy without defineService", async () => {
  const root = Deno.makeTempDirSync();

  // The provider contributes a live service instance to the definer's service
  // extension point across workers, WITHOUT defineService marking. The SDK
  // converts it to a remote reference in the providing worker, so the consumer
  // receives a Remote<T> proxy — never raw data that would trip the callback
  // codec.
  const fullSdkImport =
    `import { defineActor, defineExtensionPoint as defineHostExtensionPoint, defineServiceExtensionPoint, provide, signal, subscribe, markCollectionStream, depActor } from ${
      JSON.stringify(SDK_URL)
    };`;
  const definer = writePlugin(
    root,
    "definer",
    `${fullSdkImport}
    export const ep = defineServiceExtensionPoint<{ hello(): string }>("sample.svc");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    export const metrics = defineActor({
      changes(): AsyncIterable<unknown[]> { return markCollectionStream(subscribe(ep)); },
    });
    `,
  );

  const provider = writePlugin(
    root,
    "provider",
    `${fullSdkImport}
    import { ep } from "@maieutics/definer/main";
    const svc = { hello(): string { return "remote-plain-hi"; } };
    provide(ep, signal<unknown | undefined>(svc));
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
    ["definer"],
  );

  const consumer = writePlugin(
    root,
    "consumer",
    `${fullSdkImport}
    import type { metrics as MetricsSurface } from "@maieutics/definer/main";
    const metrics = depActor<typeof MetricsSurface>("@maieutics/definer/main", "metrics");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const iter = metrics.changes()[Symbol.asyncIterator]();
        const first = await iter.next();
        const elem = (first.value as unknown[])[0] as { hello?: unknown };
        const hello = typeof elem?.hello === "function"
          ? await (elem.hello as () => Promise<unknown>)()
          : "no-fn";
        return { action: "continue" as const, hello };
      },
    });
    `,
    ["definer"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(provider), pluginConfig(consumer)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("consumer", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      hello?: unknown;
    };
    assertEquals(value.action, "continue");
    assertEquals(value.hello, "remote-plain-hi");
  } finally {
    host.dispose();
  }
});

Deno.test("a service reference forwarded to a third worker keeps its routing identity", async () => {
  const root = Deno.makeTempDirSync();

  // mid receives the service proxy from the definer's changes stream and
  // forwards it as an argument to a third worker's actor method. The
  // re-encoded reference must keep its specifier and surface name, so the
  // third worker's call routes back to the service's owning worker.
  const fullSdkImport =
    `import { defineActor, defineExtensionPoint as defineHostExtensionPoint, defineServiceExtensionPoint, provide, signal, subscribe, markCollectionStream, depActor } from ${
      JSON.stringify(SDK_URL)
    };`;
  const definer = writePlugin(
    root,
    "definer",
    `${fullSdkImport}
    export const ep = defineServiceExtensionPoint<{ hello(): string }>("sample.svc");
    const svc = { hello(): string { return "fwd-hi"; } };
    provide(ep, signal<unknown | undefined>(svc));
    export const metrics = defineActor({
      changes(): AsyncIterable<unknown[]> { return markCollectionStream(subscribe(ep)); },
    });
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
  );

  const echo = writePlugin(
    root,
    "echo",
    `${fullSdkImport}
    export const echo = defineActor({
      async forward(svc: unknown): Promise<{ action: string; hello: unknown }> {
        const s = svc as { hello?: () => Promise<unknown> };
        return { action: "continue" as const, hello: await s.hello() };
      },
    });
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: () => ({ action: "continue" as const }),
    });
    `,
  );

  const mid = writePlugin(
    root,
    "mid",
    `${fullSdkImport}
    import type { metrics as MetricsSurface } from "@maieutics/definer/main";
    import type { echo as EchoSurface } from "@maieutics/echo/main";
    const metrics = depActor<typeof MetricsSurface>("@maieutics/definer/main", "metrics");
    const echo = depActor<typeof EchoSurface>("@maieutics/echo/main", "echo");
    export const pre = defineHostExtensionPoint("ToolPreInvoke", {
      handler: async () => {
        const iter = metrics.changes()[Symbol.asyncIterator]();
        const first = await iter.next();
        const svc = (first.value as unknown[])[0];
        return await echo.forward(svc);
      },
    });
    `,
    ["definer", "echo"],
  );

  const host = new PluginHost({
    sdkUrl: SDK_URL,
    workerEntryUrl: WORKER_ENTRY_URL,
    plugins: [pluginConfig(definer), pluginConfig(echo), pluginConfig(mid)],
  });
  try {
    await host.startAll();
    const value = await host.invoke("mid", "./main", "ToolPreInvoke", {}) as {
      action?: string;
      hello?: unknown;
    };
    assertEquals(value.action, "continue");
    assertEquals(value.hello, "fwd-hi");
  } finally {
    host.dispose();
  }
});
