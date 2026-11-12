import { acceptAdmission, admissionMailboxFor } from "./admission.ts";
import {
  assertEquals,
  assertNotEquals,
  assertRejects,
  assertStrictEquals,
  assertThrows,
} from "@std/assert";
import { shallowRef } from "./reactive.ts";
import {
  changesOf,
  collection,
  computed,
  createRemoteIdentity,
  CURRENT_MODULE,
  defineExtensionPoint,
  effectScope,
  isExtensionPoint,
  MaximumReactiveQueueLength,
  provide,
  providerCount,
  ReactiveSubscriptionError,
  setRemoteProvide,
  setRemoteUnprovide,
  setValueTransformer,
  snapshot,
  subscribe,
  unprovide,
  values,
} from "./reactive.ts";

Deno.test("closing a transformed remote service stream interrupts its pending read before later mutations", async () => {
  const source = shallowRef<object | undefined>({
    hello() {
      return 1;
    },
  });
  let iterator: AsyncIterator<unknown> | undefined;
  let pending: Promise<IteratorResult<unknown>> | undefined;
  setRemoteProvide((_specifier, _name, _initial, changes) => {
    iterator = changes[Symbol.asyncIterator]();
    pending = iterator.next();
    return Promise.resolve();
  });
  setRemoteUnprovide(() => Promise.resolve());
  setValueTransformer((value) => value);
  const originalPost = Object.getOwnPropertyDescriptor(globalThis, "postMessage");
  Object.defineProperty(globalThis, "postMessage", {
    configurable: true,
    value: (frame: { sab: SharedArrayBuffer }) => acceptAdmission(admissionMailboxFor(frame.sab)),
  });
  let registration: ReturnType<typeof provide<object>> | undefined;
  try {
    registration = provide(
      createRemoteIdentity("vue.remote.close", "test", "other/main", "service"),
      source,
    );
    if (!iterator || !pending) throw new Error("remote provide did not attach its stream");
    const closing = iterator.return?.();
    source.value = {
      hello() {
        return 2;
      },
    };
    await closing;
    assertEquals(await pending, { done: true, value: undefined });
  } finally {
    if (registration) unprovide(registration);
    if (originalPost) Object.defineProperty(globalThis, "postMessage", originalPost);
    else Reflect.deleteProperty(globalThis, "postMessage");
    setRemoteProvide(undefined);
    setRemoteUnprovide(undefined);
    setValueTransformer(undefined);
  }
});

Deno.test("Vue shallow contributions retain service identity and accept readonly computed sources", () => {
  const ep = defineExtensionPoint<object>("vue.identity");
  const service = {
    hello() {
      return "hello";
    },
  };
  const source = shallowRef(service);
  const registration = provide(ep, computed(() => source.value));
  assertStrictEquals(snapshot(ep)[0], service);
  assertStrictEquals(collection(ep).value[0], service);
  unprovide(registration);
});

Deno.test("return settles an outstanding next for every reactive stream and discards buffered changes", async () => {
  const ep = defineExtensionPoint<number>("vue.close");
  const source = shallowRef<number | undefined>(0);
  const registration = provide(ep, source);
  try {
    const changes = changesOf(source)[Symbol.asyncIterator]();
    const snapshots = subscribe(ep)[Symbol.asyncIterator]();
    const flowing = values(ep)[Symbol.asyncIterator]();
    await snapshots.next();
    await flowing.next();
    const iterators = [changes, snapshots, flowing];
    const pending = iterators.map((iterator) => iterator.next());
    for (const iterator of iterators) await iterator.return?.();
    assertEquals(
      await Promise.all(pending),
      iterators.map(() => ({ done: true as const, value: undefined })),
    );
    source.value = 1;
    for (const iterator of iterators) {
      assertEquals(await iterator.next(), { done: true, value: undefined });
    }
    const buffered = changesOf(source)[Symbol.asyncIterator]();
    source.value = 2;
    await buffered.return?.();
    assertEquals(await buffered.next(), { done: true, value: undefined });
  } finally {
    unprovide(registration);
  }
});

Deno.test("Vue scope disposal settles a pending subscription without waiting for another value", async () => {
  const scope = effectScope();
  const source = shallowRef(0);
  const iterator = scope.run(() => changesOf(source)[Symbol.asyncIterator]());
  if (iterator === undefined) throw new Error("active scope did not create its iterator");
  const pending = iterator.next();
  scope.stop();
  assertEquals(await pending, { done: true, value: undefined });
});

Deno.test("reactive queues fail explicitly at capacity instead of silently dropping transitions", async () => {
  const source = shallowRef(0);
  const iterator = changesOf(source)[Symbol.asyncIterator]();
  for (let index = 1; index <= MaximumReactiveQueueLength + 1; index++) source.value = index;
  const error = await assertRejects(() => iterator.next(), ReactiveSubscriptionError);
  assertEquals(error.code, "reactive_backpressure");
  await iterator.return?.();
});

Deno.test("concurrent next rejects the extra read without orphaning the first read", async () => {
  const source = shallowRef(0);
  const iterator = changesOf(source)[Symbol.asyncIterator]();
  const first = iterator.next();
  const error = await assertRejects(() => iterator.next(), ReactiveSubscriptionError);
  assertEquals(error.code, "concurrent_next");
  source.value = 1;
  assertEquals(await first, { done: false, value: 1 });
  await iterator.return?.();
});

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
  const s = shallowRef(1);
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
  const sdkUrl = new URL("./reactive.ts", import.meta.url).href;
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

  const s = shallowRef(7);
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
  const a = shallowRef<string | undefined>("x");
  const b = shallowRef<string | undefined>(undefined);
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
  const s = shallowRef(1);
  const reg = provide(ep, s);
  assertEquals(snapshot(ep), [1]);
  unprovide(reg);
  assertEquals(snapshot(ep), []);
  assertEquals(providerCount(ep), 0);
});

Deno.test("provide rejects a non-identity value", () => {
  assertThrows(
    () => provide({ name: "x" } as never, shallowRef(1)),
    TypeError,
    "extension point identity",
  );
  assertThrows(
    () => provide(defineExtensionPoint<number>("vue.required"), { value: 1 } as never),
    TypeError,
    "Vue ref",
  );
});

Deno.test("subscribe streams collection snapshots", async () => {
  const ep = defineExtensionPoint<number>("stream.ep");
  const a = shallowRef<number | undefined>(1);
  const b = shallowRef<number | undefined>(2);
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
  const a = shallowRef<number | undefined>(1);
  const b = shallowRef<number | undefined>(2);
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
  const a = shallowRef<number | undefined>(1);
  const b = shallowRef<number | undefined>(3);
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
  const a = shallowRef<number | undefined>(1);
  const b = shallowRef<number | undefined>(2);
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
  const c = shallowRef<number | undefined>(1);
  const d = shallowRef<number | undefined>(2);
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
  const a = shallowRef<number | undefined>(1);
  provide(ep, a);

  // take(2) makes the stream finite: initial 1, then one change.
  const read = values(ep).take(2);
  const promise = read.toArray();
  await new Promise((resolve) => setTimeout(resolve, 10));
  a.value = 2;
  const collected = await promise;
  assertEquals(collected, [1, 2]);
});
