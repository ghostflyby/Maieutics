import { assertEquals, assertNotEquals, assertThrows } from "@std/assert";
import { signal } from "@preact/signals-core";
import {
  defineExtensionPoint,
  isExtensionPoint,
  provide,
  providerCount,
  snapshot,
  subscribe,
  unprovide,
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
  // providers do not join each other's collections.
  const moduleA = `${Deno.makeTempDirSync()}/contract-a.ts`;
  const moduleB = `${Deno.makeTempDirSync()}/contract-b.ts`;
  const sdkUrl = new URL("./mod.ts", import.meta.url).href;
  Deno.writeTextFileSync(
    moduleA,
    `import { defineExtensionPoint } from ${JSON.stringify(sdkUrl)};\n` +
      `export const ep = defineExtensionPoint<number>("same.name", import.meta.url);\n`,
  );
  Deno.writeTextFileSync(
    moduleB,
    `import { defineExtensionPoint } from ${JSON.stringify(sdkUrl)};\n` +
      `export const ep = defineExtensionPoint<number>("same.name", import.meta.url);\n`,
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
