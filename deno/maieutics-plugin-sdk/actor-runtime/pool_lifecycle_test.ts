import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import { createActorPool } from "./pool.ts";
import { createMux } from "./core/transport.ts";
import { PROTOCOL_VERSION } from "./core/protocol.ts";
import { waitWithSignal } from "./core/lifetime.ts";
import { fromMessagePort } from "./core/transport.ts";

function source(valid = true) {
  const transport = createMux((frame) => {
    const request = frame as { type: string; id: number; args: number[] };
    if (request.type === "request") {
      transport.deliver({
        type: "response",
        id: request.id,
        ok: true,
        value: request.args[0] + request.args[1],
      });
    }
  });
  queueMicrotask(() =>
    transport.deliver({
      type: "handshake",
      version: PROTOCOL_VERSION,
      kind: "framed",
      codecs: valid ? ["iterable", "error", "abort-signal", "callback"] : [],
    })
  );
  return transport;
}

Deno.test("pool invokes the replacement factory and excludes disposed members from size", async () => {
  const replaced = Promise.withResolvers<void>();
  let replacements = 0;
  const pool = createActorPool<{ add(a: number, b: number): number }>({
    spawnWorker: () => source(false),
    replace: () => {
      replacements++;
      replaced.resolve();
      return source();
    },
  });
  try {
    await waitWithSignal(replaced.promise, AbortSignal.timeout(5000));
    assertEquals(await pool.invokeOn(0, "add", [20, 22]), 42);
    assertEquals(replacements, 1, "Replacement must use the supplied factory");
    assertEquals(pool.size, 1);
  } finally {
    const disposal = pool.dispose();
    assertEquals(pool.dispose(), disposal);
    await disposal;
  }
  assertEquals(pool.size, 0, "Disposed members must not count as live");
  await assertRejects(() => pool.add(1, 2));
});

Deno.test("pool disposal cancels and joins an in-progress handshake", async () => {
  const created = Promise.withResolvers<void>();
  const transport = createMux(() => {});
  const pool = createActorPool({
    signal: null,
    spawnWorker: () => {
      created.resolve();
      return transport;
    },
  });
  await waitWithSignal(created.promise, AbortSignal.timeout(5000));
  await waitWithSignal(pool.dispose(), AbortSignal.timeout(5000));
  assert(transport.closed, "Dispose must finish the pending member's teardown before resolving");
  assertEquals(pool.size, 0);
});

for (const replace of [false, true]) {
  Deno.test(`pool exposes synchronous factory failures to its caller (replace: ${replace})`, async () => {
    const failure = new Error("factory sentinel");
    let pool: ReturnType<typeof createActorPool> | undefined;
    try {
      assertEquals(
        assertThrows(() => {
          pool = createActorPool({
            spawnWorker() {
              throw failure;
            },
            replace,
          });
        }),
        failure,
      );
    } finally {
      await pool?.dispose();
    }
  });
}

Deno.test("pool construction failure closes members already starting", async () => {
  const transport = createMux(() => {});
  let count = 0;
  let pool: ReturnType<typeof createActorPool> | undefined;
  try {
    assertThrows(
      () => {
        pool = createActorPool({
          size: 2,
          signal: null,
          spawnWorker() {
            if (++count === 1) return transport;
            throw new Error("second factory failed");
          },
        });
      },
      Error,
      "second factory failed",
    );
    assert(
      transport.closed,
      "A failed constructor must synchronously cancel earlier member owners",
    );
  } finally {
    await pool?.dispose();
    transport.close();
  }
});

Deno.test("a throwing replacement factory reports its error and preserves the failure for calls", async () => {
  const failure = new Error("replacement factory sentinel");
  const reported = Promise.withResolvers<void>();
  const pool = createActorPool<{ add(a: number, b: number): number }>({
    spawnWorker: () => source(false),
    replace() {
      throw failure;
    },
    onMemberDead(_index, reason) {
      if (reason === failure) reported.resolve();
    },
  });
  try {
    await waitWithSignal(reported.promise, AbortSignal.timeout(5000));
    assertEquals(await pool.add(1, 2).catch((error) => error), failure);
    assertEquals(pool.size, 0);
  } finally {
    await pool.dispose();
  }
});

Deno.test("pool construction exposes asynchronous cleanup failure together with the factory cause", async () => {
  const factoryFailure = new Error("construction sentinel");
  const cleanupFailure = new Error("construction cleanup sentinel");
  let closed = false;
  const transport = fromMessagePort({
    onmessage: null,
    onmessageerror: null,
    postMessage() {},
    close() {
      closed = true;
      throw cleanupFailure;
    },
  });
  let count = 0;
  const failure = assertThrows(() =>
    createActorPool({
      size: 2,
      signal: null,
      spawnWorker() {
        if (++count === 1) return transport;
        throw factoryFailure;
      },
    })
  );
  assert(closed, "Construction failure must close already-owned endpoints synchronously");
  const completion = (failure as Error & { completion?: Promise<void> }).completion;
  assert(
    completion instanceof Promise,
    "The thrown construction error must expose cleanup completion",
  );
  const aggregate = await assertRejects(() => completion, AggregateError);
  assertEquals(aggregate.cause, factoryFailure);
  assertEquals(aggregate.errors, [factoryFailure, cleanupFailure]);
});

Deno.test("buffered handshake failures replace on owned tasks until a member is ready", async () => {
  let returned = false;
  let attempts = 0;
  const transports: ReturnType<typeof createMux>[] = [];
  const pool = createActorPool<{ add(a: number, b: number): number }>({
    replace: true,
    spawnWorker() {
      if (++attempts > 1) assert(returned, "Replacement must not recurse inside pool construction");
      const transport = createMux((frame) => {
        const request = frame as { type: string; id: number; args: number[] };
        if (request.type === "request") {
          transport.deliver({
            type: "response",
            id: request.id,
            ok: true,
            value: request.args[0] + request.args[1],
          });
        }
      });
      transports.push(transport);
      transport.deliver({
        type: "handshake",
        version: PROTOCOL_VERSION,
        kind: "framed",
        codecs: attempts < 3 ? [] : ["iterable", "error", "abort-signal", "callback"],
      });
      return transport;
    },
  });
  returned = true;
  try {
    assertEquals(await waitWithSignal(pool.add(20, 22), AbortSignal.timeout(5000)), 42);
    assertEquals(attempts, 3);
    for (let i = 0; i < 3; i++) {
      transports.at(-1)?.close(
        i === 1
          ? new AggregateError([new Error("replace active member")])
          : new Error("replace active member"),
      );
      assertEquals(await waitWithSignal(pool.add(1, 2), AbortSignal.timeout(5000)), 3);
    }
    assertEquals(attempts, 6);
  } finally {
    await pool.dispose();
  }
  assert(transports.every((transport) => transport.closed));
});

Deno.test("disposing a repeated replacement loop cancels the next spawn and joins retired slots", async () => {
  const third = Promise.withResolvers<void>();
  let attempts = 0;
  const transports: ReturnType<typeof createMux>[] = [];
  const pool = createActorPool({
    replace: true,
    spawnWorker() {
      const transport = createMux(() => {});
      transports.push(transport);
      transport.deliver({
        type: "handshake",
        version: PROTOCOL_VERSION,
        kind: "framed",
        codecs: [],
      });
      if (++attempts === 3) third.resolve();
      if (attempts > 3) throw new Error("Disposed pool admitted an extra replacement");
      return transport;
    },
  });
  try {
    await waitWithSignal(third.promise, AbortSignal.timeout(5000));
    await waitWithSignal(pool.dispose(), AbortSignal.timeout(5000));
    assertEquals(attempts, 3);
    assertEquals(pool.size, 0);
    assert(transports.every((transport) => transport.closed));
  } finally {
    await pool.dispose();
  }
});
