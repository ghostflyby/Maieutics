import { assertEquals, assertNotEquals, assertThrows } from "@std/assert";
import { signal } from "@preact/signals-core";
import {
  CURRENT_MODULE,
  defineExtensionPoint,
  isExtensionPoint,
  provide,
  providerCount,
  snapshot,
  subscribe,
  unprovide,
  values,
} from "./mod.ts";

Deno.test("defineExtensionPoint(name) returns a pure identity value", () => {
  const ep = defineExtensionPoint<number>("my.ep");
  assertEquals(ep.name, "my.ep");
  assertEquals(isExtensionPoint(ep), true);
});

Deno.test("same-name identities are the same extension point", () => {
  const a = defineExtensionPoint<number>("shared.ep");
  const b = defineExtensionPoint<number>("shared.ep");
  assertEquals(a.name, b.name);
  assertEquals(a.owner, b.owner);
  // Same module + same name → same identity; both join the same collection.
  const s = signal(1);
  provide(a, s);
  assertEquals(snapshot(b), [1]);
});

Deno.test("identities from different modules with the same name do not merge", async () => {
  // Two contract modules declare the same extension-point name. Contract mode
  // keys on (owner = module URL, name), so they are different identities and
  // providers do not join each other's collections. The test modules set
  // CURRENT_MODULE themselves, mimicking the worker load hook that records
  // each module's URL before its top-level code runs.
  const moduleA = `${Deno.makeTempDirSync()}/contract-a.ts`;
  const moduleB = `${Deno.makeTempDirSync()}/contract-b.ts`;
  const sdkUrl = new URL("./mod.ts", import.meta.url).href;
  Deno.writeTextFileSync(
    moduleA,
    `import { CURRENT_MODULE, defineExtensionPoint } from ${JSON.stringify(sdkUrl)};\n` +
      `(globalThis as Record<symbol, unknown>)[CURRENT_MODULE] = import.meta.url;\n` +
      `export const ep = defineExtensionPoint<number>("same.name");\n`,
  );
  Deno.writeTextFileSync(
    moduleB,
    `import { CURRENT_MODULE, defineExtensionPoint } from ${JSON.stringify(sdkUrl)};\n` +
      `(globalThis as Record<symbol, unknown>)[CURRENT_MODULE] = import.meta.url;\n` +
      `export const ep = defineExtensionPoint<number>("same.name");\n`,
  );

  const { ep: epA } = await import(moduleA);
  const { ep: epB } = await import(moduleB);
  assertEquals(epA.name, epB.name);
  assertNotEquals(epA.owner, epB.owner);

  const s = signal(7);
  provide(epA, s);
  assertEquals(snapshot(epA), [7]);
  assertEquals(snapshot(epB), []); // different module → different collection
});

Deno.test("owner falls back to the SDK module when no loader is installed", () => {
  // Without a load hook (plain process context), CURRENT_MODULE is unset and
  // the owner falls back to the SDK's own module URL — all such identities
  // share the fallback owner but still work.
  delete (globalThis as Record<symbol, unknown>)[CURRENT_MODULE];
  const ep = defineExtensionPoint<number>("fallback.ep");
  assertEquals(ep.owner, new URL("./reactive.ts", import.meta.url).href);
});

Deno.test("provide contributes to the collection; undefined does not", () => {
  const ep = defineExtensionPoint<string>("collect.ep");
  const a = signal<string | undefined>("x");
  const b = signal<string | undefined>(undefined);
  provide(ep, a);
  provide(ep, b);
  assertEquals(snapshot(ep), ["x"]);

  a.value = "y";
  assertEquals(snapshot(ep), ["y"]);

  b.value = "z";
  assertEquals(snapshot(ep), ["y", "z"]);

  a.value = undefined;
  assertEquals(snapshot(ep), ["z"]);
});

Deno.test("unprovide withdraws a provider", () => {
  const ep = defineExtensionPoint<number>("withdraw.ep");
  const s = signal(1);
  const reg = provide(ep, s);
  assertEquals(snapshot(ep), [1]);
  unprovide(reg);
  assertEquals(snapshot(ep), []);
  assertEquals(providerCount(ep), 0);
});

Deno.test("provide rejects a non-identity value", () => {
  assertThrows(
    () => provide({ name: "x" } as never, signal(1)),
    TypeError,
    "extension point identity",
  );
});

Deno.test("subscribe streams collection snapshots", async () => {
  const ep = defineExtensionPoint<number>("stream.ep");
  const a = signal<number | undefined>(1);
  const b = signal<number | undefined>(2);
  provide(ep, a);
  provide(ep, b);

  const collected: number[][] = [];
  const iterator = subscribe(ep)[Symbol.asyncIterator]();
  const read = async (): Promise<void> => {
    for (let index = 0; index < 4; index++) {
      const next = await iterator.next();
      if (next.done) break;
      collected.push(next.value);
    }
  };
  const reading = read();

  // Let the initial snapshot be emitted.
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 10; // snapshot [10, 2]
  await new Promise((resolve) => setTimeout(resolve, 10));
  b.value = undefined; // snapshot [10]
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 1; // snapshot [1]
  await new Promise((resolve) => setTimeout(resolve, 10));

  await reading;
  assertEquals(collected[0], [1, 2]);
  assertEquals(collected[1], [10, 2]);
  assertEquals(collected[2], [10]);
  assertEquals(collected[3], [1]);
});

Deno.test("values streams the current values then each change", async () => {
  const ep = defineExtensionPoint<number>("values.ep");
  const a = signal<number | undefined>(1);
  const b = signal<number | undefined>(2);
  provide(ep, a);
  provide(ep, b);

  const collected: number[] = [];
  const iterator = values(ep)[Symbol.asyncIterator]();
  const read = async (): Promise<void> => {
    for (let index = 0; index < 5; index++) {
      const next = await iterator.next();
      if (next.done) break;
      collected.push(next.value);
    }
  };
  const reading = read();

  // Initial values are emitted on subscription.
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 10; // change → 10
  await new Promise((resolve) => setTimeout(resolve, 10));
  b.value = 20; // change → 20
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = undefined; // silent: no value flows
  await new Promise((resolve) => setTimeout(resolve, 10));
  b.value = 30; // change → 30
  await new Promise((resolve) => setTimeout(resolve, 10));

  await reading;
  // Initial [1, 2], then each changed value; the undefined transition is silent.
  assertEquals(collected.slice(0, 4), [1, 2, 10, 20]);
  assertEquals(collected[4], 30);
});

Deno.test("values map/filter transform each flowing value lazily", async () => {
  const ep = defineExtensionPoint<number>("values.map.ep");
  const a = signal<number | undefined>(1);
  const b = signal<number | undefined>(3);
  provide(ep, a);
  provide(ep, b);

  // map and filter compose; the stream only emits values where the predicate
  // holds, transformed. The pipeline is lazy: nothing runs until iterated.
  const stream = values(ep).map((v) => v * 2).filter((v) => v > 4);
  const collected: number[] = [];
  const iterator = stream[Symbol.asyncIterator]();
  const read = async (): Promise<void> => {
    for (let index = 0; index < 3; index++) {
      const next = await iterator.next();
      if (next.done) break;
      collected.push(next.value);
    }
  };
  const reading = read();

  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 5; // 10 > 4 → 10
  await new Promise((resolve) => setTimeout(resolve, 10));
  b.value = 1; // 2, not > 4 → skipped
  await new Promise((resolve) => setTimeout(resolve, 10));
  b.value = 6; // 12 > 4 → 12
  await new Promise((resolve) => setTimeout(resolve, 10));

  await reading;
  // Initial [1,3] → map [2,6] → filter [6]; then 10, then 12.
  assertEquals(collected, [6, 10, 12]);
});

Deno.test("values take/drop bound the stream", async () => {
  const ep = defineExtensionPoint<number>("values.take.ep");
  const a = signal<number | undefined>(1);
  const b = signal<number | undefined>(2);
  provide(ep, a);
  provide(ep, b);

  // take(3): the first three flowing values (initial 1, 2, then a change).
  const taken: number[] = [];
  const takeIterator = values(ep).take(3)[Symbol.asyncIterator]();
  const readTake = async (): Promise<void> => {
    for (;;) {
      const next = await takeIterator.next();
      if (next.done) break;
      taken.push(next.value);
    }
  };
  const readingTake = readTake();
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 10; // the third value
  await new Promise((resolve) => setTimeout(resolve, 10));
  b.value = 20; // beyond take(3): ignored
  await new Promise((resolve) => setTimeout(resolve, 10));
  await readingTake;
  assertEquals(taken, [1, 2, 10]);

  // drop(1): skip the first flowing value, then emit the rest. Uses a fresh
  // extension point so the initial state is independent of the take part.
  const dropEp = defineExtensionPoint<number>("values.drop.ep");
  const c = signal<number | undefined>(1);
  const d = signal<number | undefined>(2);
  provide(dropEp, c);
  provide(dropEp, d);
  const dropped: number[] = [];
  const dropIterator = values(dropEp).drop(1)[Symbol.asyncIterator]();
  const readDrop = async (): Promise<void> => {
    for (let index = 0; index < 2; index++) {
      const next = await dropIterator.next();
      if (next.done) break;
      dropped.push(next.value);
    }
  };
  const readingDrop = readDrop();
  await new Promise((resolve) => setTimeout(resolve, 10));
  c.value = 30;
  await new Promise((resolve) => setTimeout(resolve, 10));
  await readingDrop;
  // Initial [1, 2] → drop 1 → [2], then 30.
  assertEquals(dropped, [2, 30]);
});

Deno.test("values toArray collects a finite stream", async () => {
  const ep = defineExtensionPoint<number>("values.toArray.ep");
  const a = signal<number | undefined>(1);
  provide(ep, a);

  // take(2) makes the stream finite: initial 1, then one change.
  const read = values(ep).take(2);
  const promise = read.toArray();
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 2;
  const collected = await promise;
  assertEquals(collected, [1, 2]);
});
