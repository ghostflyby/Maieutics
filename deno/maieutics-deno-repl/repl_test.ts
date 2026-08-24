import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import { ReplEvalQueue } from "./repl_eval_queue.ts";
import {
  decodeReplEvalEnvelope,
  encodeReplEvalEnvelope,
  REPL_EVAL_MAX_MESSAGE_BYTES,
  REPL_EVAL_WEBSOCKET_PATH,
  ReplEvalMessageType,
  ReplEvalProtocolError,
} from "./protocol.ts";

Deno.test("the REPL eval protocol uses its domain path and vocabulary", () => {
  assertEquals(REPL_EVAL_WEBSOCKET_PATH, "/v1/repl/eval/ws");
  assertEquals(Object.values(ReplEvalMessageType), [
    "repl.eval.hello",
    "repl.eval.ready",
    "repl.eval.execute",
    "repl.eval.cancel",
    "repl.eval.dispose",
    "repl.eval.result",
    "repl.eval.error",
    "repl.eval.cancelled",
    "repl.eval.console",
    "repl.eval.display",
    "repl.eval.updateDisplay",
    "repl.eval.clearOutput",
    "repl.eval.inputRequest",
    "repl.eval.inputReply",
  ]);
});

Deno.test("the REPL eval decoder tolerates unknown envelope and payload fields", () => {
  const envelope = decodeReplEvalEnvelope(JSON.stringify({
    version: 1,
    type: ReplEvalMessageType.execute,
    correlationId: "execution-1",
    payload: { executionId: "execution-1", code: "1 + 1", future: true },
    futureEnvelopeField: true,
  }));
  assertEquals(envelope.type, ReplEvalMessageType.execute);
  assertEquals(envelope.correlationId, "execution-1");
  assertEquals(envelope.payload, {
    executionId: "execution-1",
    code: "1 + 1",
    future: true,
  });
});

Deno.test("unknown REPL eval messages fail with a typed protocol error", () => {
  const error = assertThrows(
    () =>
      decodeReplEvalEnvelope(JSON.stringify({
        version: 1,
        type: "repl.eval.future",
        correlationId: "future-1",
      })),
    ReplEvalProtocolError,
  );
  assertEquals(error.code, "unknown_message_type");
  assertEquals(error.correlationId, "future-1");
});

Deno.test("REPL eval encoding enforces the one MiB control boundary", () => {
  const error = assertThrows(
    () =>
      encodeReplEvalEnvelope({
        type: ReplEvalMessageType.console,
        correlationId: "execution-1",
        payload: { text: "x".repeat(REPL_EVAL_MAX_MESSAGE_BYTES) },
      }),
    ReplEvalProtocolError,
  );
  assertEquals(error.code, "message_too_large");
});

Deno.test("bounded queue preserves FIFO order while applying producer backpressure", async () => {
  const queue = new ReplEvalQueue<number>(1);
  await queue.enqueue(1);
  let secondAccepted = false;
  const second = queue.enqueue(2).then(() => {
    secondAccepted = true;
  });
  await Promise.resolve();
  assertEquals(secondAccepted, false);
  assertEquals(await queue.dequeue(), 1);
  await second;
  assertEquals(await queue.dequeue(), 2);
});

Deno.test("bounded queue close fails blocked producers and consumers", async () => {
  const full = new ReplEvalQueue<number>(1);
  await full.enqueue(1);
  const producer = full.enqueue(2);
  full.close(new Error("closed"));
  await assertRejects(() => producer, Error, "closed");

  const empty = new ReplEvalQueue<number>(1);
  const consumer = empty.dequeue();
  empty.close(new Error("closed"));
  await assertRejects(() => consumer, Error, "closed");
});

Deno.test("the worker actor exposes output as a pull-driven async stream", async () => {
  const actor = await Deno.readTextFile(new URL("./repl_actor.ts", import.meta.url));
  const worker = await Deno.readTextFile(new URL("./repl_worker.ts", import.meta.url));
  assertEquals(actor.includes("AsyncIterable<ReplActorStreamEvent>"), true);
  assertEquals(worker.includes("async *execute("), true);
  assertEquals(worker.includes("nextEmit"), false);
  assertEquals(worker.includes("eventTail"), false);
});

Deno.test("the standalone manifest pins the production actor dependencies", async () => {
  const config = JSON.parse(
    await Deno.readTextFile(new URL("./deno.json", import.meta.url)),
  ) as { imports: Record<string, string> };
  assertEquals(
    config.imports["@ghostflyby/aves/repl"],
    "jsr:@ghostflyby/aves@0.6.0/repl",
  );
  assertEquals(
    config.imports["@ghostflyby/worker-actor"],
    "jsr:@ghostflyby/worker-actor@0.1.0",
  );
});

Deno.test("the worker binds maieutics before creating Aves", async () => {
  const worker = await Deno.readTextFile(new URL("./repl_worker.ts", import.meta.url));
  assertEquals(
    /await installMaieuticsNamespace\(\);[\s\S]*kernel = await createReplKernel\(\);/.test(worker),
    true,
  );
  assertEquals(worker.includes(".maieutics = injected"), true);
  assertEquals(worker.includes("await health()"), false);
});

Deno.test("Windows bootstrap credential is carried by the REPL hello", async () => {
  const main = await Deno.readTextFile(new URL("./main.ts", import.meta.url));
  const client = await Deno.readTextFile(new URL("./repl_client.ts", import.meta.url));
  assertEquals(main.includes("bootstrapWindowsCredential"), true);
  assertEquals(client.includes("{ credential: this.#options.credential }"), true);
  assertEquals(client.includes("connectIpcWebSocket"), true);
  assertEquals(client.includes("REPL_EVAL_WEBSOCKET_PATH"), true);
});
