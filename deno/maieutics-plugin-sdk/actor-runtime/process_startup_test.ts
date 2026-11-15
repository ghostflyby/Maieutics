import { assert, assertEquals, assertRejects } from "@std/assert";
import { connectProcessActor } from "./core/process_actor.ts";
import { fromNodeIpc } from "./core/transport.ts";

Deno.test("spawnProcess ownership preserves startup failure when stop also rejects", async () => {
  const startup = new Error("process startup sentinel");
  const cleanup = new Error("process stop sentinel");
  const stopped = Promise.withResolvers<void>();
  const transport = fromNodeIpc(() => true);
  let stopCalls = 0;
  const creation = connectProcessActor({
    transport,
    completion: stopped.promise,
    stop() {
      stopCalls++;
      stopped.reject(cleanup);
      return stopped.promise;
    },
  }, { signal: null });
  transport.close(startup);
  const error = await assertRejects(() => creation, AggregateError, "startup and cleanup");
  assertEquals(error.cause, startup);
  assertEquals(error.errors, [startup, cleanup]);
  assertEquals(stopCalls, 1);
  assert(transport.closed);
});

Deno.test("spawnProcess ownership retains the original startup error when stop succeeds", async () => {
  const startup = new Error("original startup sentinel");
  const stopped = Promise.withResolvers<void>();
  const transport = fromNodeIpc(() => true);
  const creation = connectProcessActor({
    transport,
    completion: stopped.promise,
    stop() {
      stopped.resolve();
      return stopped.promise;
    },
  }, { signal: null });
  transport.close(startup);
  assertEquals(await creation.catch((error) => error), startup);
});
