import { assertEquals, assertInstanceOf } from "@std/assert";
import type * as NodeWorker from "./test_fixtures/node_worker.ts";
import type * as NodeCodecModule from "./test_fixtures/node_codec_worker.ts";
import { spawnNode } from "./spawn.ts";
import { connectActor } from "./core/connect.ts";
import { createRpcProxy, makeRpcHandler, requestFrame, sendRpcResult } from "./core/rpc.ts";
import { type Codec, CODEC_PLACEHOLDER_KEY, PayloadCodecRegistry } from "./core/codec.ts";
import { iterableCodec } from "./core/codecs/iterable.ts";
import { errorCodec } from "./core/codecs/error.ts";
import { abortSignalCodec } from "./core/codecs/abort_signal.ts";
import { callbackCodec } from "./core/codecs/callback.ts";
import { messageTransport } from "./core/transport.ts";
import type { Channel } from "./core/channel.ts";
import type { Transport } from "./core/transport.ts";
import type { RequestFrame, ResponseFrame } from "./core/protocol.ts";
import type { RpcProxy } from "./core/rpc.ts";

/** A callable RPC proxy over an actor channel opened via connectActor. */
function rpcProxyFor(
  opened: { channel: Channel; transport: Transport },
): RpcProxy {
  const registry = new PayloadCodecRegistry();
  for (
    const codec of [iterableCodec, errorCodec, abortSignalCodec, callbackCodec]
  ) {
    if (!registry.has(codec.tag)) registry.register(codec);
  }
  const proxy = createRpcProxy(registry, {
    send: (request, transfer) => opened.channel.send(requestFrame(request), transfer),
    isDead: () => opened.channel.closed,
    transport: opened.transport,
  });
  opened.channel.onMessage((message) => {
    const frame = message as ResponseFrame;
    if (frame.type === "response") proxy.deliver(frame);
  });
  return proxy;
}

Deno.test("spawnNode: multiple named actors on one node process", async () => {
  const node = await spawnNode<typeof NodeWorker.actors>(
    new URL("./test_fixtures/node_worker.ts", import.meta.url).href,
  );
  try {
    assertEquals(await node.counter.inc(1), 2);
    assertEquals(await node.counter.inc(10), 11);
    assertEquals(await node.counter.reset(), 0);
    assertEquals(await node.greeter.hello("world"), "hi world");
  } finally {
    await node.dispose();
  }
});

Deno.test("spawnNode: dispose closes the node", async () => {
  const node = await spawnNode<typeof NodeWorker.actors>(
    new URL("./test_fixtures/node_worker.ts", import.meta.url).href,
  );
  await node.counter.inc(0);
  await node.dispose();
  const result = await node.counter
    .inc(1)
    .then(() => "ok", () => "rejected");
  assertEquals(result, "rejected");
});

Deno.test(
  "spawnNode: connectActor opens the SECOND actor directly over the node transport",
  async () => {
    const node = await spawnNode<typeof NodeWorker.actors>(
      new URL("./test_fixtures/node_worker.ts", import.meta.url).href,
    );
    try {
      // The surface already opened counter+greeter. Open a NEW channel to the
      // greeter directly on the node transport (bypassing the surface), using
      // the same __open-actor protocol frame serveNode understands.
      const opened = connectActor(node.transport, "greeter");
      const greeter = rpcProxyFor(opened);
      assertEquals(await greeter.call("hello", ["connect"]), "hi connect");
      // The actor is stateful per call site: a fresh channel serves the same
      // api, and the pre-opened surface channel still works.
      assertEquals(await node.greeter.hello("surface"), "hi surface");
    } finally {
      await node.dispose();
    }
  },
);

Deno.test(
  "connectActor: single-actor transport needs no name (the main channel IS the actor)",
  async () => {
    // Two message-kind transports wired back-to-back (fork-IPC style). One end
    // acts as the single-actor runtime and serves the api on the MAIN channel
    // (exactly what serveProcess does); the other is the connectActor client —
    // no sub-channel and no __open-actor frame: the address is the main
    // channel itself.
    const client = messageTransport({ send: (m) => server.deliver(m) });
    const server = messageTransport({ send: (m) => client.deliver(m) });
    const registry = new PayloadCodecRegistry();
    for (
      const codec of [
        iterableCodec,
        errorCodec,
        abortSignalCodec,
        callbackCodec,
      ]
    ) {
      if (!registry.has(codec.tag)) registry.register(codec);
    }
    const handler = makeRpcHandler(
      {
        add: (a: number, b: number) => a + b,
      } as import("./worker_runtime.ts").WorkerApi,
      registry,
      server,
    );
    server.onMessage((ev) => {
      const frame = ev.data as RequestFrame;
      if (frame.type !== "request") return;
      void handler(frame).then((res) =>
        sendRpcResult(res, (result, transfer) => server.send(result, transfer))
      );
    });

    const opened = connectActor(client);
    const proxy = rpcProxyFor(opened);
    assertEquals(await proxy.call("add", [1, 2]), 3);
    assertEquals(await proxy.call("add", [40, 2]), 42);
    client.close();
    server.close();
  },
);

/** Client-side twin of the node fixture's capsule codec (the tag must agree). */
interface Capsule {
  tag: string;
}
const capsuleCodec: Codec<Capsule> = {
  tag: "capsule",
  matches(v: unknown): v is Capsule {
    return typeof v === "object" && v !== null &&
      typeof (v as { tag?: unknown }).tag === "string";
  },
  encode(v: Capsule): unknown {
    return { [CODEC_PLACEHOLDER_KEY]: "capsule", payload: v };
  },
  decode(placeholder: { payload: Capsule }): Capsule {
    return placeholder.payload;
  },
};

Deno.test("spawnNode: custom codecs work end-to-end and drive the type projection", async () => {
  // Both type args explicit — same contract as spawn: C carries a default, so
  // TS only applies the CodecValueTypes projection when the caller spells out
  // the codecs tuple type.
  const node = await spawnNode<
    typeof NodeCodecModule.actors,
    readonly [Codec<Capsule>]
  >(
    new URL("./test_fixtures/node_codec_worker.ts", import.meta.url).href,
    { codecs: [capsuleCodec] as const },
  );
  try {
    // Runtime: the capsule value crosses the node channel through the codec.
    const echoed = await node.capsule.echo({ tag: "n" });
    assertEquals(echoed.tag, "n");
    assertEquals(await node.capsule.get(), { tag: "fresh" });
    // Compile-time assertion (projection_test.ts convention): CodecValueTypes<C>
    // flows into SpawnedNode — a sync Capsule return keeps its identity
    // (Capsule, NOT Promise<Capsule>); a regression fails `deno check`.
    const _identityKept: Capsule = null as unknown as ReturnType<
      typeof node.capsule.get
    >;
    void _identityKept;
  } finally {
    await node.dispose();
  }
});

Deno.test("spawnNode: handshake validates the codec tag list (mismatch kills)", async () => {
  // The node registered capsule; the client passes no codecs → the handshake
  // validation (same helper spawn uses) fails and spawnNode rejects.
  const outcome = await spawnNode(
    new URL("./test_fixtures/node_codec_worker.ts", import.meta.url).href,
  ).then(
    () => "unexpectedly spawned" as const,
    (e: unknown) => e,
  );
  assertInstanceOf(outcome, Error);
  assertEquals(
    (outcome as Error).message.includes("capsule"),
    true,
  );
});
