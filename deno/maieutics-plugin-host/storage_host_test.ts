import { assert, assertEquals } from "@std/assert";
import { DatabaseSync } from "node:sqlite";
import {
  createStorageMailbox,
  STORAGE_PAYLOAD_BYTES,
  STORAGE_STATE_IDLE,
  STORAGE_STATE_REQUEST,
  STORAGE_STATE_RESPONSE,
  type StorageMailbox,
  type StorageRequest,
  type StorageResponse,
} from "../maieutics-runtime/storage_channel.ts";
import { PluginStorageHost, STORAGE_FILE_NAME } from "./storage_host.ts";

/**
 * Drives one op through the real responder: the request is announced on a
 * mailbox and `handle()` answers synchronously (the poll loop below only
 * survives for symmetry with an async responder).
 */
function createDriver(
  host: PluginStorageHost,
  plugin: { id: string; storage?: { dataDir: string } },
  mailbox: StorageMailbox = createStorageMailbox(),
) {
  return async function op(request: StorageRequest): Promise<StorageResponse> {
    announce(mailbox, request);
    host.handle(plugin, mailbox.sab);
    const deadline = Date.now() + 2_000;
    while (Atomics.load(mailbox.state, 0) !== STORAGE_STATE_RESPONSE) {
      if (Date.now() > deadline) throw new Error("the responder never answered");
      await new Promise((resolve) => setTimeout(resolve, 1));
    }
    const raw = new TextDecoder().decode(
      mailbox.payload.subarray(0, Atomics.load(mailbox.responseLength, 0)),
    );
    Atomics.store(mailbox.state, 0, STORAGE_STATE_IDLE);
    return JSON.parse(raw) as StorageResponse;
  };
}

function announce(mailbox: StorageMailbox, request: StorageRequest): void {
  const encoded = new TextEncoder().encode(JSON.stringify(request));
  assert(
    encoded.length <= STORAGE_PAYLOAD_BYTES,
    "the test request must fit the mailbox payload, like every client op",
  );
  mailbox.payload.set(encoded);
  Atomics.store(mailbox.requestLength, 0, encoded.length);
  Atomics.store(mailbox.state, 0, STORAGE_STATE_REQUEST);
}

Deno.test("ops are durable immediately and survive close and reopen", async () => {
  const dataDir = Deno.makeTempDirSync();
  const writer = new PluginStorageHost();
  const write = createDriver(writer, { id: "p1", storage: { dataDir } });
  assertEquals(await write({ op: "set", key: "a", value: "1" }), { ok: true });
  assertEquals(await write({ op: "set", key: "b", value: "2" }), { ok: true });
  // The database IS the store: closing without any flush step keeps the rows.
  writer.dispose();

  const reader = new PluginStorageHost();
  const read = createDriver(reader, { id: "p1", storage: { dataDir } });
  try {
    assertEquals(await read({ op: "get", key: "a" }), { ok: true, value: "1" });
    assertEquals(await read({ op: "length" }), { ok: true, length: 2 });
    assertEquals(await read({ op: "keyAt", index: 0 }), { ok: true, key: "a" });
    assertEquals(await read({ op: "keyAt", index: 1 }), { ok: true, key: "b" });
  } finally {
    reader.dispose();
  }
});

Deno.test("insertion order survives removals and overwrites keep position", async () => {
  const dataDir = Deno.makeTempDirSync();
  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1", storage: { dataDir } });
  try {
    await op({ op: "set", key: "a", value: "1" });
    await op({ op: "set", key: "b", value: "2" });
    await op({ op: "remove", key: "a" });
    await op({ op: "set", key: "c", value: "3" });
    // The freed ordinal must NOT be reused: c comes after b.
    assertEquals(await op({ op: "keyAt", index: 0 }), { ok: true, key: "b" });
    assertEquals(await op({ op: "keyAt", index: 1 }), { ok: true, key: "c" });
    assertEquals(await op({ op: "keyAt", index: 2 }), { ok: true, key: null });
    // Overwriting keeps the original position.
    await op({ op: "set", key: "b", value: "2b" });
    assertEquals(await op({ op: "keyAt", index: 0 }), { ok: true, key: "b" });
    assertEquals(await op({ op: "get", key: "b" }), { ok: true, value: "2b" });
  } finally {
    host.dispose();
  }
});

Deno.test("clear empties the store and restarts the insertion order", async () => {
  const dataDir = Deno.makeTempDirSync();
  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1", storage: { dataDir } });
  try {
    await op({ op: "set", key: "a", value: "1" });
    await op({ op: "remove", key: "absent" });
    await op({ op: "clear" });
    await op({ op: "clear" });
    assertEquals(await op({ op: "length" }), { ok: true, length: 0 });
    await op({ op: "set", key: "c", value: "3" });
    assertEquals(await op({ op: "keyAt", index: 0 }), { ok: true, key: "c" });
  } finally {
    host.dispose();
  }
});

Deno.test("enforces the per-plugin quota with a typed error", async () => {
  const dataDir = Deno.makeTempDirSync();
  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1", storage: { dataDir } });
  try {
    // Six near-limit writes: each fits the 1 MiB mailbox payload, five of
    // them fit the 5 MiB store quota, and the sixth crosses it.
    for (let index = 0; index < 5; index++) {
      assertEquals(
        await op({ op: "set", key: `chunk-${index}`, value: "x".repeat(950_000) }),
        { ok: true },
      );
    }
    const rejected = await op({ op: "set", key: "chunk-5", value: "x".repeat(950_000) });
    assertEquals(rejected.ok, false);
    if (!rejected.ok) {
      assertEquals(rejected.name, "QuotaExceededError");
      assert(rejected.message.length > 0);
    }
    // The rejected op left the store untouched.
    assertEquals(await op({ op: "get", key: "chunk-5" }), { ok: true, value: null });
  } finally {
    host.dispose();
  }
});

Deno.test("quota accounting survives a reopen", async () => {
  const dataDir = Deno.makeTempDirSync();
  const writer = new PluginStorageHost();
  const write = createDriver(writer, { id: "p1", storage: { dataDir } });
  for (let index = 0; index < 5; index++) {
    await write({ op: "set", key: `chunk-${index}`, value: "x".repeat(950_000) });
  }
  writer.dispose();

  // The reopened host hydrates its quota accounting from the rows on disk:
  // the store is already at ~4.75 MiB, so the next near-limit write fails.
  const reader = new PluginStorageHost();
  const read = createDriver(reader, { id: "p1", storage: { dataDir } });
  try {
    const rejected = await read({ op: "set", key: "chunk-5", value: "x".repeat(950_000) });
    assertEquals(rejected.ok, false);
    if (!rejected.ok) assertEquals(rejected.name, "QuotaExceededError");
  } finally {
    reader.dispose();
  }
});

Deno.test("rejects a mailbox that reappears under another plugin", async () => {
  const dataDirA = Deno.makeTempDirSync();
  const dataDirB = Deno.makeTempDirSync();
  const host = new PluginStorageHost();
  // One mailbox, two sender identities: the binding follows the FIRST sender,
  // so a mailbox handed across plugins through an actor port cannot borrow
  // the other plugin's store.
  const mailbox = createStorageMailbox();
  const opAsA = createDriver(host, { id: "a", storage: { dataDir: dataDirA } }, mailbox);
  const opAsB = createDriver(host, { id: "b", storage: { dataDir: dataDirB } }, mailbox);
  assertEquals(await opAsA({ op: "set", key: "who", value: "a" }), { ok: true });
  const rejected = await opAsB({ op: "get", key: "who" });
  assertEquals(rejected, {
    ok: false,
    name: "StorageAccessDenied",
    message: "This storage mailbox is bound to another plugin.",
  });
  host.dispose();
});

Deno.test("fails ops with a typed error when storage is not configured", async () => {
  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1" });
  const response = await op({ op: "get", key: "a" });
  assertEquals(response, {
    ok: false,
    name: "StorageUnavailable",
    message: "The kernel did not configure a storage directory for this plugin.",
  });
  host.dispose();
});

Deno.test("rejects an undersized mailbox without replying", () => {
  const host = new PluginStorageHost();
  const sab = new SharedArrayBuffer(16);
  const state = new Int32Array(sab, 0, 1);
  Atomics.store(state, 0, STORAGE_STATE_REQUEST);
  host.handle({ id: "p1", storage: { dataDir: Deno.makeTempDirSync() } }, sab);
  // The protocol violation is swallowed (nothing sane to reply to); the
  // announcing state is left untouched so a test can observe the no-op.
  assertEquals(Atomics.load(state, 0), STORAGE_STATE_REQUEST);
  host.dispose();
});

Deno.test("an unknown op fails with a typed error", async () => {
  const dataDir = Deno.makeTempDirSync();
  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1", storage: { dataDir } });
  const response = await op({ op: "explode" } as unknown as StorageRequest);
  assertEquals(response.ok, false);
  host.dispose();
});

Deno.test("a database with a newer schema version fails with a typed error", async () => {
  const dataDir = Deno.makeTempDirSync();
  const probe = new DatabaseSync(`${dataDir}/${STORAGE_FILE_NAME}`);
  probe.exec("PRAGMA user_version = 99");
  probe.close();

  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1", storage: { dataDir } });
  const response = await op({ op: "get", key: "a" });
  assertEquals(response.ok, false);
  if (!response.ok) {
    assertEquals(response.name, "StorageError");
    assert(response.message.includes("schema version"));
  }
  host.dispose();
});

Deno.test("dispose closes the database and rejects later ops", async () => {
  const dataDir = Deno.makeTempDirSync();
  const host = new PluginStorageHost();
  const op = createDriver(host, { id: "p1", storage: { dataDir } });
  await op({ op: "set", key: "k", value: "v" });
  host.dispose();
  const response = await op({ op: "get", key: "k" });
  assertEquals(response, {
    ok: false,
    name: "StorageUnavailable",
    message: "The plugin storage host is shutting down.",
  });
});
