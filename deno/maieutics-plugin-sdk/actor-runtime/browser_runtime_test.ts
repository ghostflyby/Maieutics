import { assert, assertEquals, assertInstanceOf, assertRejects, assertThrows } from "@std/assert";
import {
  ActorTransportError,
  connectToken,
  createActorConnection,
  createRpcProxy,
  messageTransport,
  PayloadCodecRegistry,
} from "./browser.ts";
import { PROTOCOL_VERSION } from "./core/protocol.ts";

async function within<T>(work: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      work,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("Actor test completion budget exceeded")), 5000);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
function pair() {
  const a = messageTransport({ send: (frame) => b.deliver(structuredClone(frame)) });
  const b = messageTransport({ send: (frame) => a.deliver(structuredClone(frame)) });
  a.onClose(b.close);
  b.onClose(a.close);
  return { a, b };
}

Deno.test("Actor connections reject unadvertised and inherited methods", async () => {
  const { a, b } = pair();
  let count = 0;
  const server = createActorConnection(a, { actors: { counter: { increment: () => ++count } } });
  const client = createActorConnection<{ counter: { increment(): number } }>(b);
  try {
    await within(Promise.all([server.ready, client.ready]));
    assertEquals(await client.actor("counter").increment(), 1);
    const escaped = client.actor("counter") as unknown as { constructor(): Promise<unknown> };
    await assertRejects(() => escaped.constructor(), Error, "No such RPC method");
    assertThrows(() => client.actor("other" as "counter"), ActorTransportError, "not advertised");
  } finally {
    client.close();
    await within(Promise.all([server.completion, client.completion]));
  }
});

Deno.test("Actor streams consume one credit per next and release without polling", async () => {
  const { a, b } = pair();
  let produced = 0;
  const released = Promise.withResolvers<void>();
  const api = {
    async *values() {
      try {
        for (let i = 0; i < 1000; i++) {
          produced++;
          yield i;
        }
      } finally {
        released.resolve();
      }
    },
    produced: () => produced,
    released: () => released.promise,
  };
  const server = createActorConnection(a, { actors: { stream: api } });
  const client = createActorConnection<{ stream: typeof api }>(b);
  try {
    await within(client.ready);
    const actor = client.actor("stream");
    const iterable = actor.values();
    assertEquals(await actor.produced(), 0, "Creating a stream must not enumerate it");
    const iterator = iterable[Symbol.asyncIterator]();
    assertEquals(await iterator.next(), { done: false, value: 0 });
    assertEquals(
      await actor.produced(),
      1,
      "A strict-successor RPC must observe exactly one produced item",
    );
    assertEquals(await iterator.next(), { done: false, value: 1 });
    assertEquals(await actor.produced(), 2);
    await iterator.return?.();
    await within(actor.released());
    assertEquals(await actor.produced(), 2);
  } finally {
    client.close();
    await within(Promise.all([server.completion, client.completion]));
  }
});

Deno.test("Actor callbacks carry transport context for nested streams and cancellation", async () => {
  const { a, b } = pair();
  const api = {
    invoke: (fn: () => Promise<AsyncIterable<Uint8Array>>) => fn(),
    async wait(signal: AbortSignal, started: () => Promise<void>) {
      await started();
      if (!signal.aborted) {
        await new Promise<void>((resolve) =>
          signal.addEventListener("abort", () => resolve(), { once: true })
        );
      }
      return signal.aborted;
    },
  };
  const server = createActorConnection(a, { actors: { service: api } });
  const client = createActorConnection<{ service: typeof api }>(b);
  try {
    await within(client.ready);
    const actor = client.actor("service");
    const stream = await actor.invoke(() =>
      (async function* () {
        yield new Uint8Array([0, 255, 1]);
      })()
    );
    const values: Uint8Array[] = [];
    for await (const value of stream) values.push(value);
    assertEquals(values, [new Uint8Array([0, 255, 1])]);
    const controller = new AbortController();
    const started = Promise.withResolvers<void>();
    const waiting = actor.wait(controller.signal, () => {
      started.resolve();
    });
    await within(started.promise);
    controller.abort("test cancellation");
    assertEquals(await within(waiting), true);
  } finally {
    client.close();
    await within(Promise.all([server.completion, client.completion]));
  }
});

Deno.test("disconnect fails in-flight calls and joins cooperative remote cleanup", async () => {
  const { a, b } = pair();
  const started = Promise.withResolvers<void>();
  let serverSignal = new AbortController().signal;
  const api = {
    async wait() {
      started.resolve();
      if (!serverSignal.aborted) {
        await new Promise<void>((resolve) =>
          serverSignal.addEventListener("abort", () => resolve(), { once: true })
        );
      }
    },
  };
  const server = createActorConnection(a, { actors: { service: api } });
  serverSignal = server.signal;
  const client = createActorConnection<{ service: typeof api }>(b);
  await within(client.ready);
  const waiting = client.actor("service").wait();
  const rejected = assertRejects(() => waiting);
  await within(started.promise);
  b.close(new ActorTransportError("closed", "Disconnected by test"));
  await within(rejected);
  await within(Promise.all([server.completion, client.completion]));
  client.close();
  server.close();
  assertThrows(() => client.actor("service"), ActorTransportError);
});

Deno.test("message decoding failure closes before accepting a strict successor", async () => {
  const delivered: unknown[] = [];
  const transport = messageTransport({
    send() {},
    decode(value) {
      if (value === "bad") throw new Error("bad bytes");
      return value;
    },
  });
  const closed = Promise.withResolvers<unknown>();
  transport.onClose(closed.resolve);
  transport.onMessage((frame) => delivered.push(frame.data));
  transport.deliver("bad");
  transport.deliver("successor");
  assertInstanceOf(await within(closed.promise), ActorTransportError);
  await within(transport.completion);
  assertEquals(delivered, []);
});

Deno.test("closing an asynchronous decode frees the owned wait and queued messages", async () => {
  const entered = Promise.withResolvers<void>();
  const blocker = Promise.withResolvers<unknown>();
  const transport = messageTransport({
    send() {},
    decode() {
      entered.resolve();
      return blocker.promise;
    },
  });
  transport.deliver("blocked");
  await within(entered.promise);
  transport.deliver("queued");
  transport.close();
  await within(transport.completion);
  blocker.reject(new Error("Late decoder failure remains observed"));
});

Deno.test("decoding admission closes pending channels exactly once", async () => {
  const blocker = Promise.withResolvers<unknown>();
  const transport = messageTransport({
    send() {},
    maxPendingMessages: 1,
    decode: () => blocker.promise,
  });
  const channel = transport.openChannel().channel;
  let notifications = 0;
  channel.onClose(() => notifications++);
  transport.deliver("first");
  transport.deliver("overflow");
  assert(transport.closed);
  assertInstanceOf(transport.reason, ActorTransportError);
  assertEquals(transport.reason.code, "capacity");
  assert(channel.closed);
  assertEquals(notifications, 1);
  transport.close();
  assertEquals(notifications, 1);
  await within(transport.completion);
  blocker.resolve(null);
});

Deno.test("pending channel tokens observe parent closure before binding", async () => {
  const transport = messageTransport({ send() {} });
  const channel = connectToken(transport, { __mux: "open", ch: 123 });
  assertEquals(channel.closed, false);
  channel.send("before peer");
  transport.close();
  assertEquals(channel.closed, true);
  await within(transport.completion);
});

Deno.test("RPC result decoding rejects its correlated call before fatal cleanup", async () => {
  let id = 0;
  const proxy = createRpcProxy(new PayloadCodecRegistry(), {
    send(request) {
      id = request.id;
    },
  });
  const work = proxy.call("test", []);
  const verdict = assertRejects(() => work, Error, "Unknown codec");
  assertThrows(() => proxy.deliver({ id, ok: true, value: { __wCodec: "missing" } }));
  proxy.rejectAll(new Error("disconnect"));
  await within(verdict);
});

Deno.test("Actor handshake rejects a different stream protocol version", async () => {
  const transport = messageTransport({ send() {} });
  const connection = createActorConnection(transport);
  const verdict = assertRejects(() => connection.ready, ActorTransportError, "handshake mismatch");
  transport.deliver({
    type: "actor:hello",
    version: PROTOCOL_VERSION - 1,
    kind: "message",
    codecs: ["iterable", "error", "abort-signal", "callback"],
    actors: [],
  });
  await within(verdict);
  await within(connection.completion);
});

Deno.test("Mux mailboxes share one connection budget across channels", () => {
  const transport = messageTransport({ send() {}, maxBufferedMessages: 1 });
  transport.deliver({ __mux: "open", ch: 1 });
  transport.deliver({ __mux: "open", ch: 2 });
  transport.deliver({ __mux: "data", ch: 1, value: "first" });
  transport.deliver({ __mux: "data", ch: 2, value: "overflow" });
  assert(transport.closed);
  assertInstanceOf(transport.reason, ActorTransportError);
  assertEquals(transport.reason.code, "capacity");
});

Deno.test("decode admission counts aggregate bytes including the active decoder", async () => {
  const blocker = Promise.withResolvers<unknown>();
  const transport = messageTransport({
    send() {},
    maxPendingBytes: 3,
    messageSize: () => 2,
    decode: () => blocker.promise,
  });
  transport.deliver("active");
  transport.deliver("overflow");
  assert(transport.closed);
  assertInstanceOf(transport.reason, ActorTransportError);
  assertEquals(transport.reason.code, "capacity");
  await within(transport.completion);
  blocker.resolve(null);
});

Deno.test("pending channel sends survive until the matching token arrives", async () => {
  const sent: unknown[] = [];
  const transport = messageTransport({
    send: (frame) => {
      sent.push(frame);
    },
  });
  const channel = connectToken(transport, { __mux: "open", ch: 77 });
  channel.send("before binding");
  transport.deliver({ __mux: "open", ch: 77 });
  assertEquals(sent, [{ __mux: "open", ch: 77 }, {
    __mux: "data",
    ch: 77,
    value: "before binding",
  }]);
  transport.close();
  await within(transport.completion);
});

Deno.test("RPC admission preserves an admitted call while rejecting excess calls", async () => {
  let id = 0;
  const proxy = createRpcProxy(new PayloadCodecRegistry(), {
    maxPendingCalls: 1,
    send: (request) => {
      id = request.id;
    },
  });
  const first = proxy.call("first", []);
  await assertRejects(() => proxy.call("overflow", []), ActorTransportError, "budget");
  proxy.deliver({ id, ok: true, value: 42 });
  assertEquals(await first, 42);
  const later = proxy.call("later", []);
  proxy.deliver({ id, ok: true, value: 43 });
  assertEquals(await later, 43);
});

Deno.test("startup cancellation owns both the handshake and transport lifetime", async () => {
  const reason = new Error("startup cancelled");
  const transport = messageTransport({ send() {} });
  const connection = createActorConnection(transport, { signal: AbortSignal.abort(reason) });
  assertEquals(await within(connection.ready.then(() => null, (error) => error)), reason);
  assertEquals(await within(connection.closed), reason);
  assert(transport.closed);
  await within(connection.completion);
});

Deno.test("generator cleanup failures remain observable through connection completion", async () => {
  const { a, b } = pair();
  const api = {
    async *values() {
      try {
        yield 1;
        yield 2;
      } finally {
        await Promise.reject(new Error("cleanup failed"));
      }
    },
  };
  const server = createActorConnection(a, { actors: { stream: api } });
  const client = createActorConnection<{ stream: typeof api }>(b);
  await within(client.ready);
  const iterator = client.actor("stream").values()[Symbol.asyncIterator]();
  await iterator.next();
  await iterator.return?.();
  client.close();
  await within(assertRejects(() => server.completion, AggregateError, "codec cleanup failed"));
  await within(client.completion);
});

Deno.test("overlapping lazy pulls share initialization and abandonment prevents enumeration", async () => {
  const { a, b } = pair();
  let produced = 0;
  const api = {
    async *values() {
      for (let i = 0; i < 3; i++) {
        produced++;
        yield i;
      }
    },
    produced: () => produced,
  };
  const server = createActorConnection(a, { actors: { stream: api } });
  const client = createActorConnection<{ stream: typeof api }>(b);
  try {
    await within(client.ready);
    const actor = client.actor("stream");
    const abandoned = actor.values()[Symbol.asyncIterator]();
    await abandoned.return?.();
    assertEquals(await actor.produced(), 0);
    const iterator = actor.values()[Symbol.asyncIterator]();
    assertEquals(await Promise.all([iterator.next(), iterator.next()]), [
      { done: false, value: 0 },
      { done: false, value: 1 },
    ]);
    assertEquals(await actor.produced(), 2);
    await iterator.return?.();
  } finally {
    client.close();
    await within(Promise.all([client.completion, server.completion]));
  }
});

Deno.test("closing one connection preserves revocation of a callback shared by another", async () => {
  const { releaseCallback } = await import("./browser.ts");
  function service() {
    let retained: ((value: number) => Promise<number>) | undefined;
    return {
      remember(callback: (value: number) => Promise<number>) {
        retained = callback;
      },
      invoke() {
        if (!retained) throw new Error("callback missing");
        return retained(10);
      },
    };
  }
  const first = pair(), second = pair();
  const firstServer = createActorConnection(first.a, { actors: { service: service() } });
  const secondServer = createActorConnection(second.a, { actors: { service: service() } });
  const firstClient = createActorConnection<{ service: ReturnType<typeof service> }>(first.b);
  const secondClient = createActorConnection<{ service: ReturnType<typeof service> }>(second.b);
  const shared = (value: number) => value + 1;
  try {
    await within(Promise.all([firstClient.ready, secondClient.ready]));
    await firstClient.actor("service").remember(shared);
    await secondClient.actor("service").remember(shared);
    firstClient.close();
    await within(Promise.all([firstClient.completion, firstServer.completion]));
    releaseCallback(shared);
    await within(assertRejects(() => secondClient.actor("service").invoke(), Error, "Callback"));
  } finally {
    firstClient.close();
    secondClient.close();
    await within(
      Promise.all([
        firstClient.completion,
        secondClient.completion,
        firstServer.completion,
        secondServer.completion,
      ]),
    );
  }
});
