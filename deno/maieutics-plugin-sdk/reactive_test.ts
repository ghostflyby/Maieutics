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

async function within<T>(work: Promise<T>, description: string): Promise<T> {
  let deadline: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      work,
      new Promise<never>((_, reject) => {
        deadline = setTimeout(() => reject(new Error(description)), 2_000);
      }),
    ]);
  } finally {
    clearTimeout(deadline);
  }
}

async function observe<T>(
  source: AsyncIterable<T>,
  verify: (read: (expected: T) => Promise<void>, expectDone: () => Promise<void>) => Promise<void>,
): Promise<void> {
  const iterator = source[Symbol.asyncIterator]();
  const next = (expected: IteratorResult<T>) => async () => {
    assertEquals(
      await within(iterator.next(), `Missing reactive event: ${JSON.stringify(expected)}`),
      expected,
    );
  };
  try {
    await verify((value) => next({ done: false, value })(), next({ done: true, value: undefined }));
  } finally {
    await within(Promise.resolve(iterator.return?.()), "Reactive iterator failed to close");
  }
}

function contributePair(name: string, first: number, second: number) {
  const point = defineExtensionPoint<number>(name);
  const a = shallowRef<number | undefined>(first);
  const b = shallowRef<number | undefined>(second);
  const firstRegistration = provide(point, a);
  const secondRegistration = provide(point, b);
  return {
    point,
    a,
    b,
    [Symbol.dispose]() {
      unprovide(secondRegistration);
      unprovide(firstRegistration);
    },
  };
}

Deno.test("subscribe streams collection snapshots", async () => {
  using pair = contributePair("stream.ep", 1, 2);
  await observe(subscribe(pair.point), async (read) => {
    await read([1, 2]);
    pair.a.value = 10;
    await read([10, 2]);
    pair.b.value = undefined;
    await read([10]);
    pair.a.value = 1;
    await read([1]);
  });
});

Deno.test("values streams the current values then each change and omits withdrawals", async () => {
  using pair = contributePair("values.ep", 1, 2);
  await observe(values(pair.point), async (read) => {
    await read(1);
    await read(2);
    pair.a.value = 10;
    await read(10);
    pair.b.value = 20;
    await read(20);
    pair.a.value = undefined;
    pair.b.value = 30;
    await read(30);
  });
});

Deno.test("values map/filter transform each flowing value lazily", async () => {
  using pair = contributePair("values.map.ep", 1, 3);
  const mapped: number[] = [];
  const stream = values(pair.point).map((value) => {
    mapped.push(value);
    return value * 2;
  }).filter((value) => value > 4);
  assertEquals(mapped, [], "Constructing a pipeline must not iterate it");
  await observe(stream, async (read) => {
    await read(6);
    pair.a.value = 5;
    await read(10);
    pair.b.value = 1;
    pair.b.value = 6;
    await read(12);
  });
  assertEquals(
    mapped,
    [1, 3, 5, 1, 6],
    "The filtered event must be evaluated before its successor",
  );
});

Deno.test("values take closes the stream at its limit", async () => {
  using pair = contributePair("values.take.ep", 1, 2);
  await observe(values(pair.point).take(3), async (read, expectDone) => {
    await read(1);
    await read(2);
    pair.a.value = 10;
    await read(10);
    pair.b.value = 20;
    await expectDone();
  });
});

Deno.test("values drop skips only the specified prefix", async () => {
  using pair = contributePair("values.drop.ep", 1, 2);
  await observe(values(pair.point).drop(1), async (read) => {
    await read(2);
    pair.a.value = 30;
    await read(30);
  });
});

Deno.test("values toArray collects a finite stream", async () => {
  using pair = contributePair("values.toArray.ep", 1, 2);
  assertEquals(
    await within(values(pair.point).take(2).toArray(), "Finite collection did not complete"),
    [1, 2],
  );
});
