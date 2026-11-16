import { assert, assertEquals, assertInstanceOf, assertRejects } from "@std/assert";
import { wrapPort } from "./core/channel.ts";
import { PayloadCodecRegistry } from "./core/codec.ts";
import { abortSignalCodec } from "./core/codecs/abort_signal.ts";
import { ActorTransportError, createLifetime } from "./core/lifetime.ts";
import { createMux, fromNodeIpc, messageTransport } from "./core/transport.ts";
import { framedTransport } from "./core/transport_v8.ts";
import { spawn } from "./spawn_worker.ts";
import { openChannel } from "./core/channel.ts";
import type { Codec } from "./core/codec.ts";
import { PROTOCOL_VERSION } from "./core/protocol.ts";
import { fromMessagePort, type MessageEndpoint } from "./core/transport.ts";

function endpoint(): MessageEndpoint & { closed: boolean } {
  return {
    closed: false,
    onmessage: null,
    onmessageerror: null,
    postMessage() {},
    close() {
      this.closed = true;
    },
  };
}

for (const trigger of ["message", "messageerror"] as const) {
  Deno.test(`throwing transport onClosed is reported by completion after ${trigger}`, async () => {
    const port = endpoint();
    let notified = false;
    const transport = fromMessagePort(port, {
      onClosed() {
        throw new Error("close sentinel");
      },
    });
    transport.onClose(() => {
      notified = true;
    });
    transport.onMessage(() => {
      throw new ActorTransportError("protocol", "frame sentinel");
    });
    if (trigger === "message") port.onmessage?.(new MessageEvent("message", { data: {} }));
    else port.onmessageerror?.(new MessageEvent("messageerror"));
    assert(notified, "Every owner must be notified despite a throwing close observer");
    assert(port.closed, "The endpoint must actually close despite the throwing observer");
    assertEquals(port.onmessage, null);
    assertEquals(port.onmessageerror, null);
    assertInstanceOf(transport.reason, ActorTransportError);
    assertEquals(transport.reason.code, trigger === "message" ? "protocol" : "decode");
    await assertRejects(() => transport.completion ?? Promise.resolve(), AggregateError, "close");
  });
}

Deno.test("throwing channel close observers do not escape messageerror and preserve decode cause", async () => {
  const ports = new MessageChannel();
  const closePort = ports.port1.close.bind(ports.port1);
  let closedPort = false;
  ports.port1.close = () => {
    closedPort = true;
    closePort();
  };
  const channel = wrapPort(ports.port1, {
    onClosed() {
      throw new Error("close sentinel");
    },
    onMessageError() {
      channel.close();
      throw new Error("decode callback sentinel");
    },
  });
  try {
    ports.port1.onmessageerror?.call(ports.port1, new MessageEvent("messageerror"));
    assertInstanceOf(channel.reason, ActorTransportError);
    assertEquals(channel.reason.code, "decode");
    assert(
      channel.closed && closedPort,
      "The native endpoint must close despite throwing callbacks",
    );
    assertEquals(ports.port1.onmessage, null);
    assertEquals(ports.port1.onmessageerror, null);
    await assertRejects(() => channel.completion ?? Promise.resolve(), AggregateError);
  } finally {
    channel.close();
    ports.port2.close();
  }
});

Deno.test("a late close observer is protected and its failure appears in a fresh completion read", async () => {
  const life = createLifetime();
  life.close();
  await life.completion;
  const failure = new Error("late observer sentinel");
  life.onClose(() => {
    throw failure;
  });
  const reported = await assertRejects(() => life.completion, AggregateError);
  assertEquals(reported.errors, [failure]);
});

for (const kind of ["messageport", "mux", "message", "ipc", "framed"]) {
  Deno.test(`late ${kind} close observers are protected and observable`, async () => {
    const transport = kind === "messageport"
      ? fromMessagePort(endpoint())
      : kind === "mux"
      ? createMux(() => {})
      : kind === "message"
      ? messageTransport({ send() {} })
      : kind === "ipc"
      ? fromNodeIpc(() => true)
      : framedTransport(new ReadableStream<Uint8Array>(), new WritableStream<Uint8Array>());
    transport.close();
    await transport.completion;
    const failure = new Error("late wrapper sentinel");
    transport.onClose(() => {
      throw failure;
    });
    const aggregate = await assertRejects(
      () => transport.completion ?? Promise.resolve(),
      AggregateError,
    );
    assertEquals(aggregate.errors, [failure]);
  });
}

Deno.test("distinct close failures with the same message remain distinct causes", async () => {
  const life = createLifetime();
  const first = new Error("same message");
  const second = new Error("same message");
  life.onClose(() => {
    throw first;
  });
  life.onClose(() => {
    throw second;
  });
  life.close();
  assertEquals((await assertRejects(() => life.completion, AggregateError)).errors, [
    first,
    second,
  ]);
});

Deno.test("Actor completion reports a shared channel failure exactly once without nested aggregates", async () => {
  const failure = new Error("shared observer sentinel");
  class Marker {}
  const codec: Codec<Marker> = {
    tag: "close-probe",
    matches: (value): value is Marker => value instanceof Marker,
    encode(_value, ctx) {
      const { channel } = openChannel(ctx, {
        onClosed() {
          throw failure;
        },
      });
      ctx.registry.registerChannel(channel, ctx.transport);
      return { __wCodec: "close-probe" };
    },
    decode() {
      throw new Error("unused decoder");
    },
  };
  const transport = createMux(() => {});
  const creation = spawn<{ hold(value: Marker): void }>(transport, { codecs: [codec] });
  transport.deliver({
    type: "handshake",
    version: PROTOCOL_VERSION,
    kind: "framed",
    codecs: [codec.tag, "iterable", "error", "abort-signal", "callback"],
  });
  const actor = await creation;
  const pending = actor.hold(new Marker()).catch(() => {});
  const reported = await assertRejects(() => actor.dispose(), AggregateError);
  await pending;
  assertEquals(
    reported.errors,
    [failure],
    "Transport and registry observe the same failure, not two failures",
  );
});

Deno.test("AbortSignal messageerror retains decode as the channel and owner terminal cause", async () => {
  const main = endpoint();
  const transport = fromMessagePort(main);
  const registry = new PayloadCodecRegistry().register(abortSignalCodec);
  const ports = new MessageChannel();
  try {
    const signal = registry.decode(
      { __wCodec: "abort-signal", port: ports.port1 },
      transport,
    ) as AbortSignal;
    ports.port1.onmessageerror?.call(ports.port1, new MessageEvent("messageerror"));
    assert(signal.aborted, "Decode failure must abort the rebuilt signal");
    assert(transport.closed, "Decode failure must fail the Actor owner");
    assertInstanceOf(transport.reason, ActorTransportError);
    assertEquals(transport.reason.code, "decode");
  } finally {
    registry.failAll();
    transport.close();
    ports.port2.close();
    await registry.join();
  }
});
