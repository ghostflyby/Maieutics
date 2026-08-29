import { assert, assertEquals, assertInstanceOf, assertThrows } from "@std/assert";
import {
  createMemoryStorage,
  createRpcStorage,
  createStorageMailbox,
  performStorageOp,
  readStorageRequest,
  STORAGE_PAYLOAD_BYTES,
  storageMailboxFor,
  type StorageRequest,
  type StorageRequestFrame,
  type StorageResponse,
  StorageTimeoutError,
  writeStorageResponse,
} from "./storage_channel.ts";

/** Named-property view of a storage instance (the Proxy surface the static
 * WebStorageLike type deliberately does not carry). */
function bag(store: unknown): Record<string, unknown> {
  return store as Record<string, unknown>;
}

/**
 * A synchronous in-memory responder standing in for the plugin host's main
 * isolate: it answers DURING the post callback, so the parked client's
 * `Atomics.wait` observes the response through the "not-equal" fast path —
 * exactly the race window the production host can also hit.
 */
function synchronousResponder() {
  const entries = new Map<string, string>();
  const apply = (request: StorageRequest): StorageResponse => {
    switch (request.op) {
      case "get":
        return { ok: true, value: entries.get(request.key) ?? null };
      case "set":
        entries.set(request.key, request.value);
        return { ok: true };
      case "remove":
        entries.delete(request.key);
        return { ok: true };
      case "clear":
        entries.clear();
        return { ok: true };
      case "length":
        return { ok: true, length: entries.size };
      case "keyAt":
        return { ok: true, key: [...entries.keys()][request.index] ?? null };
      default:
        return { ok: false, name: "StorageError", message: "unsupported" };
    }
  };
  return {
    entries,
    respond(frame: StorageRequestFrame): void {
      const mailbox = storageMailboxFor(frame.sab);
      const request = readStorageRequest(mailbox);
      assert(request !== null, "the responder must see an announced request");
      writeStorageResponse(mailbox, apply(request));
    },
  };
}

function rpcStore() {
  const responder = synchronousResponder();
  const store = createRpcStorage(createStorageMailbox(), (frame) => responder.respond(frame));
  return { store, responder };
}

Deno.test("rpc storage round-trips every op through the mailbox", () => {
  const { store, responder } = rpcStore();
  assertEquals(store.getItem("missing"), null);
  store.setItem("alpha", "1");
  store.setItem("beta", "2");
  assertEquals(store.length, 2, "length tracks the authoritative store");
  assertEquals(store.getItem("alpha"), "1");
  assertEquals(store.key(1), "beta");
  assertEquals(store.key(9), null);
  store.removeItem("alpha");
  assertEquals(store.getItem("alpha"), null);
  store.clear();
  assertEquals(store.length, 0);
  assertEquals(responder.entries.size, 0, "the responder is the source of truth");
});

Deno.test("named property access maps onto the storage surface", () => {
  const { store } = rpcStore();
  const properties = bag(store);
  properties.token = "abc";
  assertEquals(store.getItem("token"), "abc");
  assertEquals(properties.token, "abc");
  assert("token" in properties);
  assert(!("absent" in properties));
  delete properties.token;
  assertEquals(store.getItem("token"), null);
  assert(!("token" in properties));
  // API members stay API members: they exist and are callable, not keys.
  assertEquals(typeof store.setItem, "function");
  assertEquals(store.getItem("setItem"), null);
});

Deno.test("values and keys are stringified like the Web Storage surface", () => {
  const { store } = rpcStore();
  bag(store).num = 42;
  assertEquals(store.getItem("num"), "42");
  store.setItem("obj", String({ a: 1 }));
  assertEquals(store.getItem("obj"), "[object Object]");
});

Deno.test("an oversized request is rejected before it is posted", () => {
  let posted = false;
  const store = createRpcStorage(createStorageMailbox(), () => {
    posted = true;
  });
  assertThrows(
    () => store.setItem("big", "x".repeat(STORAGE_PAYLOAD_BYTES)),
    DOMException,
  );
  assertEquals(posted, false, "an unfitting request never reaches the transport");
});

Deno.test("an unresponsive responder times out and poisons the mailbox", () => {
  const store = createRpcStorage(createStorageMailbox(), () => {}, 20);
  assertThrows(() => store.getItem("any"), StorageTimeoutError);
  // The mailbox is poisoned: later ops fail immediately instead of parking.
  assertThrows(() => store.length, StorageTimeoutError, "unusable");
  // The raw client surfaces the same contract with its own timeout.
  assertThrows(
    () => performStorageOp(createStorageMailbox(), { op: "length" }, () => {}, 20),
    StorageTimeoutError,
  );
});

Deno.test("an error response surfaces as a DOMException with the responder's name", () => {
  const store = createRpcStorage(createStorageMailbox(), (frame) => {
    writeStorageResponse(storageMailboxFor(frame.sab), {
      ok: false,
      name: "QuotaExceededError",
      message: "quota is exhausted",
    });
  });
  try {
    store.setItem("k", "v");
    throw new Error("the op must fail");
  } catch (error) {
    assertInstanceOf(error, DOMException);
    assertEquals(error.name, "QuotaExceededError");
    assertEquals(error.message, "quota is exhausted");
  }
});

Deno.test("memory storage enforces the quota and preserves insertion order", () => {
  const store = createMemoryStorage(10);
  store.setItem("a", "12345"); // 1 + 5 = 6
  store.setItem("b", "123"); // + 1 + 3 = 10
  assertThrows(() => store.setItem("c", "x"), DOMException, "quota");
  // Replacing a value keeps its position and frees quota.
  store.setItem("a", "1");
  assertEquals(store.key(0), "a");
  store.setItem("c", "123"); // + 1 + 3 = 10
  assertEquals(store.length, 3);
  store.removeItem("absent");
  assertEquals(store.length, 3);
  store.clear();
  assertEquals(store.length, 0);
  assertEquals(store.key(0), null);
});

Deno.test("memory storage named properties behave like the spec surface", () => {
  const store = createMemoryStorage(1024);
  const properties = bag(store);
  properties.k = "v";
  assertEquals(store.getItem("k"), "v");
  assertEquals(properties.k, "v");
  assert("k" in properties);
  delete properties.k;
  assertEquals("k" in properties, false);
});

Deno.test("mailboxes validate their byte length", () => {
  assertThrows(() => storageMailboxFor(new SharedArrayBuffer(16)), Error, "bytes");
  const mailbox = storageMailboxFor(new SharedArrayBuffer(12 + STORAGE_PAYLOAD_BYTES));
  assertEquals(mailbox.state[0], 0);
});
