import { assert, assertEquals, assertInstanceOf, assertRejects } from "@std/assert";
import { wrapPort } from "./core/channel.ts";
import { PayloadCodecRegistry } from "./core/codec.ts";
import { abortSignalCodec } from "./core/codecs/abort_signal.ts";
import { ActorTransportError } from "./core/lifetime.ts";
import { fromMessagePort, type MessageEndpoint } from "./core/transport.ts";

function endpoint(): MessageEndpoint {
  return { onmessage: null, onmessageerror: null, postMessage() {}, close() {} };
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
    assertInstanceOf(transport.reason, ActorTransportError);
    assertEquals(transport.reason.code, trigger === "message" ? "protocol" : "decode");
    await assertRejects(() => transport.completion ?? Promise.resolve(), AggregateError, "close");
  });
}

Deno.test("throwing channel close observers do not escape messageerror and preserve decode cause", async () => {
  const ports = new MessageChannel();
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
    await assertRejects(() => channel.completion ?? Promise.resolve(), AggregateError);
  } finally {
    channel.close();
    ports.port2.close();
  }
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
