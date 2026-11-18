import { assert, assertEquals, assertInstanceOf, assertRejects, assertThrows } from "@std/assert";
import { ActorTransportError, createBinaryGraphCodec } from "./browser.ts";

const codec = createBinaryGraphCodec();
async function roundtrip<T>(value: T): Promise<T> {
  const bytes = codec.encode(value);
  assertInstanceOf(bytes, Uint8Array);
  return await codec.decode(bytes) as T;
}

Deno.test("binary graphs preserve cycles and shared identity across objects, arrays, maps and sets", async () => {
  const value: Record<string, unknown> = {};
  const items: unknown[] = [value];
  const map = new Map<unknown, unknown>([[value, items]]);
  const set = new Set<unknown>([value, map]);
  Object.assign(value, { self: value, items, map, set, shared: items });
  const copy = await roundtrip(value);
  assert(copy !== value);
  assert(copy.self === copy);
  assert(copy.items === copy.shared);
  assert((copy.items as unknown[])[0] === copy);
  assert((copy.map as Map<unknown, unknown>).get(copy) === copy.items);
  assert((copy.set as Set<unknown>).has(copy));
  assert((copy.set as Set<unknown>).has(copy.map));
});

Deno.test("binary graphs retain primitive edge cases, undefined properties, holes and UTF-16 strings", async () => {
  const sparse = new Array(5);
  sparse[2] = undefined;
  const value = {
    sparse,
    missing: undefined,
    nil: null,
    text: "a\ud800\u0000🌏\udfff",
    number: [-0, NaN, Infinity, -Infinity, Number.MAX_VALUE],
    bigint: [0n, -1n, 2n ** 1024n],
  };
  const copy = await roundtrip(value);
  assertEquals(copy, value);
  assert(Object.hasOwn(copy, "missing"));
  assert(!Object.hasOwn(copy.sparse, "0"));
  assert(Object.hasOwn(copy.sparse, "2"));
  assert(Object.is(copy.number[0], -0));
});

Deno.test("binary graphs retain ArrayBuffer, typed view identity, offsets and shared backing storage", async () => {
  const buffer = new ArrayBuffer(64);
  const bytes = new Uint8Array(buffer, 8, 16);
  bytes.set([0, 255, 128, 1]);
  const numbers = new Uint16Array(buffer, 8, 8);
  const data = new DataView(buffer, 9, 4);
  const value = { buffer, bytes, again: bytes, numbers, data };
  const encoded = codec.encode(value);
  assert(
    encoded.some((_, i) =>
      encoded[i] === 0 && encoded[i + 1] === 255 && encoded[i + 2] === 128 && encoded[i + 3] === 1
    ),
    "Binary data must appear as raw contiguous bytes",
  );
  const copy = await codec.decode(encoded) as typeof value;
  assert(copy.bytes === copy.again);
  assert(copy.bytes.buffer === copy.buffer);
  assert(copy.numbers.buffer === copy.buffer);
  assert(copy.data.buffer === copy.buffer);
  assertEquals(copy.bytes.byteOffset, 8);
  assertEquals(copy.data.byteOffset, 9);
  assertEquals(copy.bytes, bytes);
  copy.bytes[0] = 42;
  assertEquals(new Uint8Array(copy.numbers.buffer)[8], 42);
  assertEquals(bytes[0], 0, "Network binary is a copy, never remote shared memory");
});

Deno.test("implicit backing buffers expose only transmitted view spans, including pooled-buffer gaps", async () => {
  const buffer = new ArrayBuffer(128);
  new Uint8Array(buffer).fill(0x73);
  const left = new Uint8Array(buffer, 8, 4), right = new Uint8Array(buffer, 24, 4);
  left.set([1, 2, 3, 4]);
  right.set([5, 6, 7, 8]);
  const copy = await roundtrip({ left, right });
  assert(copy.left.buffer === copy.right.buffer);
  assertEquals(copy.left.byteOffset, 8);
  assertEquals(copy.left.buffer.byteLength, 128);
  const expected = new Uint8Array(128);
  expected.set(left, 8);
  expected.set(right, 24);
  assertEquals(
    new Uint8Array(copy.left.buffer),
    expected,
    "Unexposed slab bytes must not leak over the wire",
  );
  const explicit = await roundtrip({ left, buffer });
  assertEquals(
    new Uint8Array(explicit.buffer),
    new Uint8Array(buffer),
    "An explicitly supplied buffer authorizes its complete bytes",
  );
});

for (
  const type of [
    Uint8Array,
    Int8Array,
    Uint8ClampedArray,
    Uint16Array,
    Int16Array,
    Uint32Array,
    Int32Array,
    Float32Array,
    Float64Array,
    BigInt64Array,
    BigUint64Array,
  ]
) {
  Deno.test(`binary graphs preserve ${type.name}`, async () => {
    const value = new type(new ArrayBuffer(32), 8, 2);
    new Uint8Array(value.buffer, value.byteOffset, value.byteLength).fill(7);
    const copy = await roundtrip(value);
    assertEquals(Object.prototype.toString.call(copy), Object.prototype.toString.call(value));
    assertEquals(copy.byteOffset, value.byteOffset);
    assertEquals(
      new Uint8Array(copy.buffer, copy.byteOffset, copy.byteLength),
      new Uint8Array(value.buffer, value.byteOffset, value.byteLength),
    );
  });
}

Deno.test("binary graph data properties cannot alter object prototypes", async () => {
  const value = Object.create(null);
  value.__proto__ = { polluted: true };
  value.constructor = value;
  const copy = await roundtrip(value);
  assertEquals(Object.getPrototypeOf(copy), null);
  assert(copy.constructor === copy);
  assertEquals(copy.__proto__, { polluted: true });
  const plain = {};
  Object.defineProperty(plain, "__proto__", { value, enumerable: true });
  const cloned = await roundtrip(plain);
  assertEquals(Object.getPrototypeOf(cloned), Object.prototype);
  assert(Object.hasOwn(cloned, "__proto__"));
  assertEquals(Object.hasOwn({}, "polluted"), false);
});

Deno.test("binary graphs retain dates and regular expressions without per-frame state", async () => {
  const copy = await roundtrip({ date: new Date(1234), invalid: new Date(NaN), regexp: /a+🌏/giu });
  assertEquals(copy.date.getTime(), 1234);
  assert(Number.isNaN(copy.invalid.getTime()));
  assertEquals(copy.regexp.source, "a+🌏");
  assertEquals(copy.regexp.flags, "giu");
  assertEquals(await codec.decode(codec.encode({ next: 1 })), { next: 1 });
});

Deno.test("binary graphs retain native errors, causes, AggregateError cycles and DOMException codes", async () => {
  const error = new RangeError("bad value", { cause: new TypeError("inner") });
  const aggregate = new AggregateError([error], "aggregate");
  aggregate.errors.push(aggregate);
  const copy = await roundtrip({ error, aggregate, dom: new DOMException("gone", "AbortError") });
  assertInstanceOf(copy.error, RangeError);
  assertInstanceOf(copy.error.cause, TypeError);
  assertEquals(copy.error.message, "bad value");
  assertEquals(copy.error.stack, error.stack);
  assertInstanceOf(copy.aggregate, AggregateError);
  assert(copy.aggregate.errors[0] === copy.error);
  assert(copy.aggregate.errors[1] === copy.aggregate);
  assertInstanceOf(copy.dom, DOMException);
  assertEquals(copy.dom.name, "AbortError");
  assertEquals(copy.dom.code, 20);
});

Deno.test("binary graph decoding accepts native ArrayBuffer and Blob with cancellation", async () => {
  const bytes = codec.encode({ value: new Uint8Array([0, 255]) });
  assertEquals(await codec.decode(bytes.buffer), { value: new Uint8Array([0, 255]) });
  assertEquals(await codec.decode(new Blob([bytes])), { value: new Uint8Array([0, 255]) });
  const abort = new AbortController();
  abort.abort(new Error("cancelled"));
  await assertRejects(
    async () => await codec.decode(new Blob([bytes]), abort.signal),
    Error,
    "cancelled",
  );
  assertThrows(() => codec.decode("text"), ActorTransportError);
});

Deno.test("every truncated binary graph fails with a typed decoding error", () => {
  const value: Record<string, unknown> = {
    binary: new Uint8Array([0, 255]),
    map: new Map([["a", 2n]]),
  };
  value.self = value;
  const bytes = codec.encode(value);
  for (let length = 0; length < bytes.length; length++) {
    const error = assertThrows(() => codec.decode(bytes.subarray(0, length)), ActorTransportError);
    assertEquals(error.code, "decode");
  }
  assertThrows(() => codec.decode(new Uint8Array([...bytes, 0])), ActorTransportError);
  const corrupt = bytes.slice();
  corrupt[4] = 99;
  assertThrows(() => codec.decode(corrupt), ActorTransportError);
});

Deno.test("binary graphs enforce encoded, node, item, array-storage and depth budgets before use", () => {
  assertThrows(
    () => createBinaryGraphCodec({ maxBytes: 32 }).encode(new Uint8Array(33)),
    ActorTransportError,
  );
  assertThrows(
    () => createBinaryGraphCodec({ maxNodes: 1 }).encode({ nested: {} }),
    ActorTransportError,
  );
  assertThrows(
    () => createBinaryGraphCodec({ maxItems: 4 }).encode(new Array(10)),
    ActorTransportError,
  );
  assertThrows(
    () => createBinaryGraphCodec({ maxDepth: 1 }).encode({ a: { b: {} } }),
    ActorTransportError,
  );
  const large = codec.encode(new Array(10));
  assertThrows(() => createBinaryGraphCodec({ maxItems: 4 }).decode(large), ActorTransportError);
  assertThrows(() => createBinaryGraphCodec({ maxBytes: 16 }).decode(large), ActorTransportError);
  assertThrows(() => codec.encode(Symbol("unsupported")), ActorTransportError);
  assertThrows(() => codec.encode(new WeakMap()), ActorTransportError);
  assertThrows(() => codec.encode(new SharedArrayBuffer(8)), ActorTransportError);
});

Deno.test("binary graph writer growth keeps every tag and byte", async () => {
  const value = Array.from({ length: 3000 }, (_, i) => ({ name: `item-${i}`, value: i }));
  assertEquals(await roundtrip(value), value);
});

Deno.test("malformed graph tags, references, views and binary ranges fail before becoming values", () => {
  const mutate = (input: Uint8Array, offset: number, value: number) => {
    const copy = input.slice();
    new DataView(copy.buffer).setUint32(offset, value, true);
    return copy;
  };
  const object = codec.encode({});
  assertThrows(() => codec.decode(mutate(object, 5, 0xffffffff)), ActorTransportError);
  assertThrows(() => codec.decode(mutate(object, 10, 1)), ActorTransportError);
  const tag = object.slice();
  tag[14] = 255;
  assertThrows(() => codec.decode(tag), ActorTransportError);
  const view = codec.encode(new Uint16Array(4));
  assertThrows(() => codec.decode(mutate(view, 16, 0)), ActorTransportError);
  assertThrows(() => codec.decode(mutate(view, 20, 1)), ActorTransportError);
  const buffer = codec.encode(new ArrayBuffer(8));
  assertThrows(() => codec.decode(mutate(buffer, 15, 0xffffffff)), ActorTransportError);
  assertThrows(() => codec.decode(mutate(buffer, 23, 9)), ActorTransportError);
  let deep: unknown = null;
  for (let i = 0; i < 80; i++) deep = { deep };
  assertThrows(
    () => codec.decode(createBinaryGraphCodec({ maxDepth: 100 }).encode(deep)),
    ActorTransportError,
  );
  const unreachable = codec.encode({ child: {} });
  assertThrows(() => codec.decode(mutate(unreachable, 10, 1)), ActorTransportError);
});
