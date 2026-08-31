import { assert, assertEquals } from "@std/assert";
import {
  createStorageMailbox,
  STORAGE_STATE_IDLE,
  STORAGE_STATE_REQUEST,
  STORAGE_STATE_RESPONSE,
  type StorageMailbox,
  type StorageRequest,
  type StorageResponse,
} from "../maieutics-runtime/storage_channel.ts";
import { StorageEngine } from "./storage_engine.ts";
import {
  type PluginStorageOwner,
  PluginStoragePool,
  STORAGE_ACK_FRAME_TYPE,
  STORAGE_CLOSED_FRAME_TYPE,
  STORAGE_OP_FRAME_TYPE,
} from "./storage_pool.ts";

/**
 * Drives one op through the pool: the router forwards the frame to a pool
 * worker, which replies by writing the mailbox directly, so the test polls
 * for the response announcement (synchronous responders answer before the
 * first poll).
 */
function createDriver(pool: PluginStoragePool, plugin: PluginStorageOwner) {
  const mailbox = createStorageMailbox();
  return async function op(request: StorageRequest): Promise<StorageResponse> {
    announce(mailbox, request);
    pool.handle(plugin, mailbox.sab);
    const deadline = Date.now() + 5_000;
    while (Atomics.load(mailbox.state, 0) !== STORAGE_STATE_RESPONSE) {
      if (Date.now() > deadline) throw new Error("the pool never answered");
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
  mailbox.payload.set(encoded);
  Atomics.store(mailbox.requestLength, 0, encoded.length);
  Atomics.store(mailbox.state, 0, STORAGE_STATE_REQUEST);
}

function plugin(id: string, dataDir: string): PluginStorageOwner {
  return { id, storage: { dataDir } };
}

/** Materialized module directories the pool workers need read grants for. */
function moduleDirs(): string[] {
  return [
    new URL("./storage_pool_worker.ts", import.meta.url).pathname,
    new URL("../maieutics-runtime/storage_channel.ts", import.meta.url).pathname,
  ].map((path) => path.slice(0, path.lastIndexOf("/")));
}

function realPool(dataRoot: string, maxWorkers?: number): PluginStoragePool {
  return new PluginStoragePool({
    maxWorkers,
    readDirs: [...moduleDirs(), dataRoot],
    writeDirs: [dataRoot],
  });
}

Deno.test("routes ops to pool workers and persists across a pool restart", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const pool = realPool(dataRoot);
  const op = createDriver(pool, plugin("p1", `${dataRoot}/p1`));
  assertEquals(await op({ op: "set", key: "a", value: "1" }), { ok: true });
  assertEquals(await op({ op: "get", key: "a" }), { ok: true, value: "1" });
  assertEquals(await op({ op: "length" }), { ok: true, length: 1 });
  await pool.shutdown();

  // A restarted pool reopens the same database with its committed rows.
  const restarted = realPool(dataRoot);
  const reop = createDriver(restarted, plugin("p1", `${dataRoot}/p1`));
  try {
    assertEquals(await reop({ op: "get", key: "a" }), { ok: true, value: "1" });
  } finally {
    restarted.dispose();
  }
});

Deno.test("keeps one owning worker per plugin across ops", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const pool = realPool(dataRoot, 2);
  const op = createDriver(pool, plugin("p1", `${dataRoot}/p1`));
  try {
    await op({ op: "set", key: "a", value: "1" });
    const first = pool.workerOf("p1");
    assert(first !== undefined);
    await op({ op: "set", key: "b", value: "2" });
    assertEquals(pool.workerOf("p1"), first, "sticky ownership must hold");
  } finally {
    pool.dispose();
  }
});

Deno.test("fails ops with a typed error when storage is not configured", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const pool = realPool(dataRoot);
  const op = createDriver(pool, { id: "p1" });
  const response = await op({ op: "get", key: "a" });
  assertEquals(response, {
    ok: false,
    name: "StorageUnavailable",
    message: "The kernel did not configure a storage directory for this plugin.",
  });
  pool.dispose();
});

Deno.test("rejects a mailbox that reappears under another plugin", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const pool = realPool(dataRoot);
  // One mailbox, two sender identities: the binding follows the FIRST sender,
  // so a mailbox handed across plugins through an actor port cannot borrow
  // the other plugin's store.
  const mailbox = createStorageMailbox();
  const opAs = (id: string) => {
    return async function op(request: StorageRequest): Promise<StorageResponse> {
      announce(mailbox, request);
      pool.handle(plugin(id, `${dataRoot}/${id}`), mailbox.sab);
      const deadline = Date.now() + 5_000;
      while (Atomics.load(mailbox.state, 0) !== STORAGE_STATE_RESPONSE) {
        if (Date.now() > deadline) throw new Error("the pool never answered");
        await new Promise((resolve) => setTimeout(resolve, 1));
      }
      const raw = new TextDecoder().decode(
        mailbox.payload.subarray(0, Atomics.load(mailbox.responseLength, 0)),
      );
      Atomics.store(mailbox.state, 0, STORAGE_STATE_IDLE);
      return JSON.parse(raw) as StorageResponse;
    };
  };
  const opAsA = opAs("a");
  const opAsB = opAs("b");
  assertEquals(await opAsA({ op: "set", key: "who", value: "a" }), { ok: true });
  assertEquals(await opAsB({ op: "get", key: "who" }), {
    ok: false,
    name: "StorageAccessDenied",
    message: "This storage mailbox is bound to another plugin.",
  });
  pool.dispose();
});

Deno.test("rejects ops after dispose with an immediate typed error", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const pool = realPool(dataRoot);
  const op = createDriver(pool, plugin("p1", `${dataRoot}/p1`));
  assertEquals(await op({ op: "set", key: "k", value: "v" }), { ok: true });
  pool.dispose();
  assertEquals(await op({ op: "get", key: "k" }), {
    ok: false,
    name: "StorageUnavailable",
    message: "The plugin storage host is shutting down.",
  });
});

/** A controllable stand-in for a pool worker: either processes ops through a
 * real engine (like the real worker) or stays silent, and can be crashed. */
class FakePoolWorker {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: ((event: ErrorEvent) => void) | null = null;
  readonly #engine = new StorageEngine();
  constructor(private readonly process: boolean) {}

  postMessage(message: {
    type?: string;
    sab?: SharedArrayBuffer;
    pluginId?: string;
    dataDir?: string;
  }): void {
    if (message.type !== STORAGE_OP_FRAME_TYPE || !message.sab) return;
    queueMicrotask(() => {
      if (!this.process) return;
      // Ack on receipt first, exactly like the real worker.
      this.#emit({ data: { type: STORAGE_ACK_FRAME_TYPE, sab: message.sab } });
      this.#engine.handle(message.pluginId!, message.dataDir!, message.sab!);
    });
  }

  /** Simulates the worker crashing with every frame left unacked. */
  crash(message: string): void {
    this.#emit({ data: { type: STORAGE_OP_FRAME_TYPE }, error: true, message });
  }

  close(): void {
    queueMicrotask(() => {
      this.#engine.shutdown();
      this.#emit({ data: { type: STORAGE_CLOSED_FRAME_TYPE } });
    });
  }

  #emit({ data, error = false, message = "boom" }: {
    data: unknown;
    error?: boolean;
    message?: string;
  }): void {
    if (error) {
      this.onerror?.(new ErrorEvent("error", { message }));
    } else {
      this.onmessage?.(new MessageEvent("message", { data }));
    }
  }
}

Deno.test("fails pending ops and rebinds when a pool worker crashes", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const spawned: FakePoolWorker[] = [];
  const pool = new PluginStoragePool({
    maxWorkers: 2,
    readDirs: [...moduleDirs(), dataRoot],
    writeDirs: [dataRoot],
    spawnWorker: () => {
      // The first worker stays silent and then crashes with its frame
      // unacked; any later worker processes for real.
      const fake = new FakePoolWorker(spawned.length > 0);
      spawned.push(fake);
      return fake as unknown as Worker;
    },
  });
  const op = createDriver(pool, plugin("p1", `${dataRoot}/p1`));
  try {
    // Start the op (it is forwarded to the silent worker, which never acks
    // or replies), then crash that worker.
    const pending = op({ op: "set", key: "a", value: "1" });
    await new Promise((resolve) => setTimeout(resolve, 20));
    spawned[0].crash("The storage pool worker crashed.");
    const failed = await pending;
    // The crash is surfaced as a typed error on the parked realm's mailbox —
    // not as a timeout.
    assertEquals(failed.ok, false);
    if (!failed.ok) {
      assertEquals(failed.name, "StorageError");
      assert(failed.message.includes("crashed"));
    }
    // The plugin is rebound to a fresh worker, which serves for real.
    const rebound = await op({ op: "set", key: "a", value: "1" });
    assertEquals(rebound, { ok: true });
    assertEquals(await op({ op: "get", key: "a" }), { ok: true, value: "1" });
    assertEquals(spawned.length, 2, "a fresh worker must have been spawned");
  } finally {
    pool.dispose();
  }
});

Deno.test("graceful shutdown closes worker databases and stays durable", async () => {
  const dataRoot = Deno.makeTempDirSync();
  const pool = realPool(dataRoot);
  const op = createDriver(pool, plugin("p1", `${dataRoot}/p1`));
  assertEquals(await op({ op: "set", key: "k", value: "v" }), { ok: true });
  const started = Date.now();
  await pool.shutdown();
  // The close handshake bounds the shutdown instead of terminating blindly.
  assert(Date.now() - started < 5_000, "graceful close must be bounded");
  const restarted = realPool(dataRoot);
  const reop = createDriver(restarted, plugin("p1", `${dataRoot}/p1`));
  try {
    assertEquals(await reop({ op: "get", key: "k" }), { ok: true, value: "v" });
  } finally {
    restarted.dispose();
  }
});
