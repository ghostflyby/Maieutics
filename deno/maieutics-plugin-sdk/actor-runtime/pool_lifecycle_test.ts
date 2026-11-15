import { assert, assertEquals, assertRejects } from "@std/assert";
import { createActorPool } from "./pool.ts";
import { createMux } from "./core/transport.ts";
import { PROTOCOL_VERSION } from "./core/protocol.ts";
import { waitWithSignal } from "./core/lifetime.ts";

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
