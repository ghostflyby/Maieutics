import { assert, assertEquals, assertInstanceOf } from "@std/assert";
import { createActorConnection, createBinaryGraphCodec, messageTransport } from "./browser.ts";

const codec = createBinaryGraphCodec();
function pair() {
  const left = messageTransport({
    send: (value) => queueMicrotask(() => right.deliver(codec.encode(value))),
    decode: (value) => codec.decode(value as Uint8Array),
  });
  const right = messageTransport({
    send: (value) => queueMicrotask(() => left.deliver(codec.encode(value))),
    decode: (value) => codec.decode(value as Uint8Array),
  });
  return { left, right };
}

for (const nullPrototype of [false, true]) {
  Deno.test(`Actor payload and wire codecs preserve ${nullPrototype ? "null" : "ordinary"} prototypes and own __proto__ graph data`, async () => {
    const { left, right } = pair();
    const server = createActorConnection(right, {
      actors: { data: { echo: (value: unknown) => value } },
    });
    const client = createActorConnection<{ data: { echo(value: unknown): unknown } }>(left);
    try {
      await Promise.all([client.ready, server.ready]);
      const source = Object.create(nullPrototype ? null : Object.prototype);
      source.self = source;
      Object.defineProperty(source, "__proto__", {
        value: { data: "ordinary data" },
        enumerable: true,
      });
      const copy = await client.actor("data").echo(source) as Record<string, unknown>;
      assertEquals(Object.getPrototypeOf(copy), nullPrototype ? null : Object.prototype);
      assert(Object.hasOwn(copy, "__proto__"));
      assertEquals(copy.__proto__, { data: "ordinary data" });
      assert(copy.self === copy);
    } finally {
      client.close();
      server.close();
      await Promise.all([client.completion, server.completion]);
    }
  });
}

Deno.test("cyclic binary graphs survive callback channels, credit-driven streams and native RPC errors", async () => {
  const { left, right } = pair();
  const graph: { self?: unknown; bytes: Uint8Array; again?: Uint8Array } = {
    bytes: new Uint8Array([0, 255, 128]),
  };
  graph.self = graph;
  graph.again = graph.bytes;
  const api = {
    apply: (callback: (value: unknown) => Promise<unknown>) => callback(graph),
    async *stream() {
      yield graph;
    },
    fail(): never {
      throw new RangeError("recoverable");
    },
  };
  const server = createActorConnection(right, { actors: { data: api } });
  const client = createActorConnection<{ data: typeof api }>(left);
  const check = (value: unknown) => {
    const copy = value as typeof graph;
    assert(copy.self === copy && copy.again === copy.bytes);
    assertEquals(copy.bytes, new Uint8Array([0, 255, 128]));
  };
  try {
    await Promise.all([client.ready, server.ready]);
    const remote = client.actor("data");
    check(
      await remote.apply((value) => {
        check(value);
        return value;
      }),
    );
    for await (const value of remote.stream()) check(value);
    let failure: unknown;
    try {
      await remote.fail();
    } catch (error) {
      failure = error;
    }
    assertInstanceOf(failure, Error);
    assertEquals(failure.name, "RangeError");
    check(await remote.apply((value) => value));
  } finally {
    client.close();
    server.close();
    await Promise.all([client.completion, server.completion]);
  }
});

Deno.test("Actor payload traversal retains sparse array holes and cyclic enumerable properties", async () => {
  const { left, right } = pair();
  const server = createActorConnection(right, {
    actors: { data: { echo: (value: unknown) => value } },
  });
  const client = createActorConnection<{ data: { echo(value: unknown): unknown } }>(left);
  try {
    await Promise.all([client.ready, server.ready]);
    const source: unknown[] & { extra?: unknown } = new Array(3);
    source[1] = source;
    source.extra = source;
    Object.defineProperty(source, "__proto__", { value: "data", enumerable: true });
    const copy = await client.actor("data").echo(source) as typeof source;
    assertEquals(copy.length, 3);
    assert(
      !Object.hasOwn(copy, "0") && !Object.hasOwn(copy, "2"),
      "Sparse slots must remain holes",
    );
    assert(copy[1] === copy && copy.extra === copy);
    assertEquals(Object.getPrototypeOf(copy), Array.prototype);
    assertEquals(Object.getOwnPropertyDescriptor(copy, "__proto__")?.value, "data");
  } finally {
    client.close();
    server.close();
    await Promise.all([client.completion, server.completion]);
  }
});
