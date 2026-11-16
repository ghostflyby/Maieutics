import { assert, assertEquals, assertInstanceOf, assertRejects } from "@std/assert";
import { spawn } from "@maieutics/plugin-sdk/actor";
import { waitWithSignal } from "./core/lifetime.ts";
import type * as CalcWorkerModule from "./test_fixtures/calculator/worker.ts";

const CALC_URL = import.meta.resolve(
  new URL("./test_fixtures/calculator/worker.ts", import.meta.url).href,
);

/** A worker module that never calls serveWorker(): the handshake never arrives. */
const HANGING_URL = import.meta.resolve(
  new URL("./test_fixtures/hanging_worker.ts", import.meta.url).href,
);

function hangingWorker() {
  const source = new Worker(HANGING_URL, { type: "module" });
  const ready = new Promise<void>((resolve) =>
    source.addEventListener("message", () => resolve(), { once: true })
  );
  return { source, ready };
}

for (const signal of [undefined, null] as const) {
  Deno.test(`spawn signal: ${signal === null ? "null disables" : "default permits"} the pending handshake`, async () => {
    const { source, ready } = hangingWorker();
    let settled = false;
    const creation = spawn(source, { signal });
    void creation.then(() => {
      settled = true;
    }, () => {
      settled = true;
    });
    try {
      await waitWithSignal(ready, AbortSignal.timeout(5000));
      assertEquals(
        settled,
        false,
        "Loading the fixture without a handshake must leave creation pending",
      );
      source.dispatchEvent(new ErrorEvent("error", { message: "owned test shutdown" }));
      await assertRejects(() => creation, Error, "owned test shutdown");
    } finally {
      source.terminate();
    }
  });
}

Deno.test("spawn signal: user signal aborts creation and propagates reason", async () => {
  const controller = new AbortController();
  const { source, ready } = hangingWorker();
  const creation = spawn(source, { signal: controller.signal });
  try {
    await waitWithSignal(ready, AbortSignal.timeout(5000));
    controller.abort(new Error("user cancelled"));
    await assertRejects(() => creation, Error, "user cancelled");
  } finally {
    source.terminate();
  }
});

Deno.test("spawn signal: signal no longer affects the actor after resolve", async () => {
  const controller = new AbortController();
  const actor = await spawn<typeof CalcWorkerModule.rpc>(
    new Worker(CALC_URL, { type: "module" }),
    { signal: controller.signal },
  );
  controller.abort("too late");
  // Creation already resolved: the abort must not kill the actor
  assertEquals(await actor.add(1, 2), 3);
  await actor.dispose();
});

Deno.test("spawn signal: TimeoutError keeps the serveWorker diagnostic", async () => {
  const outcome = await spawn(new Worker(HANGING_URL, { type: "module" }), {
    signal: AbortSignal.timeout(100),
  }).then(() => "resolved" as const, (e: unknown) => e);
  assertInstanceOf(outcome, Error);
  assertEquals((outcome as Error).name, "Error");
  assert((outcome as Error).message.includes("did it call serveWorker()"));
});
