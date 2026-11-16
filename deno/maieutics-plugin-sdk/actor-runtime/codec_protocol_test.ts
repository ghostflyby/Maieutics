import { assert, assertEquals, assertInstanceOf, assertThrows } from "@std/assert";
import { PayloadCodecRegistry } from "./core/codec.ts";
import { type Channel, connectChannel, connectToken, wrapPort } from "./core/channel.ts";
import { abortSignalCodec, releaseAbortSignal } from "./core/codecs/abort_signal.ts";
import { callbackCodec, type RemoteCallback } from "./core/codecs/callback.ts";
import { iterableCodec } from "./core/codecs/iterable.ts";
import { ActorTransportError, waitWithSignal } from "./core/lifetime.ts";
import { createMux, fromMessagePort, type Transport } from "./core/transport.ts";

class TrackingRegistry extends PayloadCodecRegistry {
  readonly active = new Set<Channel>();
  override registerChannel(channel: Channel, transport?: Transport): void {
    this.active.add(channel);
    super.registerChannel(channel, transport);
  }
  override unregisterChannel(channel: Channel): void {
    this.active.delete(channel);
    super.unregisterChannel(channel);
  }
}

function pair(native: boolean) {
  const ports = new MessageChannel();
  const muxA = createMux((f) => muxB.deliver(f), { maxChannels: 4 });
  const muxB = createMux((f) => muxA.deliver(f), { maxChannels: 4 });
  const a = native ? fromMessagePort(ports.port1) : muxA;
  const b = native ? fromMessagePort(ports.port2) : muxB;
  return {
    a,
    b,
    close() {
      a.close();
      b.close();
      ports.port1.close();
      ports.port2.close();
    },
  };
}

for (const native of [true, false]) {
  Deno.test(`pre-cancelled signals release both codec endpoints (${native ? "MessagePort" : "Mux"})`, async () => {
    const peers = pair(native);
    const sender = new TrackingRegistry().register(abortSignalCodec) as TrackingRegistry;
    const receiver = new TrackingRegistry().register(abortSignalCodec) as TrackingRegistry;
    try {
      for (let i = 0; i < 100; i++) {
        const transfer: Transferable[] = [];
        const encoded = sender.encode(AbortSignal.abort(`cancel ${i}`), transfer, peers.a);
        const handle = structuredClone(encoded, { transfer });
        const signal = receiver.decode(handle, peers.b) as AbortSignal;
        if (!signal.aborted) {
          await waitWithSignal(
            new Promise<void>((resolve) => {
              signal.addEventListener("abort", () => resolve(), { once: true });
            }),
            AbortSignal.timeout(5000),
          );
        }
        // A reply on the abort channel is the strict successor of local abort notification.
        const channel = [...sender.active][0];
        if (channel && !channel.closed) {
          await waitWithSignal(
            new Promise<void>((resolve) => channel.onClose(() => resolve())),
            AbortSignal.timeout(5000),
          );
        }
        assertEquals(sender.active.size, 0, "Terminal signal must unregister its sending channel");
        assertEquals(
          receiver.active.size,
          0,
          "Terminal signal must unregister its receiving channel",
        );
        assertEquals(signal.reason, `cancel ${i}`);
        assert(
          !peers.a.closed && !peers.b.closed,
          "Released signal channels must not consume the Mux budget",
        );
      }
    } finally {
      sender.failAll();
      receiver.failAll();
      peers.close();
      await Promise.all([sender.join(), receiver.join()]);
    }
  });

  for (const side of ["sender", "receiver"]) {
    Deno.test(`never-aborted signals have bounded channels with explicit ${side} release (${native ? "MessagePort" : "Mux"})`, async () => {
      const peers = pair(native);
      const sender = new TrackingRegistry().register(abortSignalCodec) as TrackingRegistry;
      const receiver = new TrackingRegistry().register(abortSignalCodec) as TrackingRegistry;
      try {
        for (let i = 0; i < 1200; i++) {
          const original = new AbortController().signal;
          const transfer: Transferable[] = [];
          const handle = structuredClone(sender.encode(original, transfer, peers.a), { transfer });
          const signal = receiver.decode(handle, peers.b) as AbortSignal;
          const channel = [...sender.active][0];
          const closed = new Promise<void>((resolve) => channel.onClose(() => resolve()));
          const aborted = signal.aborted
            ? Promise.resolve()
            : new Promise<void>((resolve) =>
              signal.addEventListener("abort", () => resolve(), { once: true })
            );
          releaseAbortSignal(side === "sender" ? original : signal);
          await waitWithSignal(Promise.all([closed, aborted]), AbortSignal.timeout(5000));
          releaseAbortSignal(original);
          releaseAbortSignal(signal);
          assertEquals(
            original.aborted,
            false,
            "Releasing an exported capability must not cancel its source",
          );
          assertEquals(sender.active.size, 0);
          assertEquals(receiver.active.size, 0);
          assertInstanceOf(signal.reason, ActorTransportError);
          assertEquals(
            signal.reason.code,
            "closed",
            "Explicit release retires the capability with a typed closed reason",
          );
          assert(
            !peers.a.closed && !peers.b.closed,
            "Retired capabilities must not consume channel admission",
          );
        }
      } finally {
        sender.failAll();
        receiver.failAll();
        peers.close();
        await Promise.all([sender.join(), receiver.join()]);
      }
    });
  }

  for (const tag of ["iterable", "callback", "abort-signal"]) {
    Deno.test(`malformed ${tag} producer frame fails its owner (${native ? "MessagePort" : "Mux"})`, async () => {
      const peers = pair(native);
      const registry = new PayloadCodecRegistry().register(iterableCodec).register(callbackCodec)
        .register(abortSignalCodec);
      const closed = Promise.withResolvers<unknown>();
      peers.a.onClose(closed.resolve);
      try {
        const value = tag === "callback"
          ? () => 1
          : tag === "abort-signal"
          ? new AbortController().signal
          : (async function* () {
            yield 1;
          })();
        const transfer: Transferable[] = [];
        const encoded = structuredClone(registry.encode(value, transfer, peers.a), {
          transfer,
        }) as { port?: MessagePort; token?: { __mux: "open"; ch: number } };
        const channel = encoded.port
          ? connectChannel(encoded.port)
          : connectToken(peers.b, encoded.token as { __mux: "open"; ch: number });
        channel.send(
          tag === "callback" ? { type: "request", id: 1, method: "call", args: null } : null,
        );
        const cause = await waitWithSignal(closed.promise, AbortSignal.timeout(5000));
        assertInstanceOf(cause, ActorTransportError);
        assertEquals(cause.code, "protocol");
        channel.close();
      } finally {
        registry.failAll();
        peers.close();
        await registry.join();
      }
    });
  }

  for (
    const [tag, messages] of [
      ["iterable", [null, { type: "error", error: null }, { type: "item" }]],
      ["callback", [null, { type: "response", id: 1, ok: false, error: null }]],
      ["abort-signal", [null, { type: "status", aborted: "yes" }]],
    ] as const
  ) {
    for (const message of messages) {
      Deno.test(`malformed ${tag} frame fails owner with protocol cause (${native ? "MessagePort" : "Mux"}, ${JSON.stringify(message)})`, async () => {
        const peers = pair(native);
        const registry = new PayloadCodecRegistry().register(iterableCodec).register(callbackCodec)
          .register(abortSignalCodec);
        const terminal = Promise.withResolvers<unknown>();
        peers.b.onClose(terminal.resolve);
        let pending: Promise<unknown> | undefined;
        try {
          const opened = peers.a.openChannel();
          const token = opened.token;
          if (!native) peers.a.send(token);
          const handle = native ? { port: token } : { token };
          const value = registry.decode({ __wCodec: tag, ...handle }, peers.b);
          if (tag === "iterable") {
            pending = (value as AsyncIterable<unknown>)[Symbol.asyncIterator]().next();
          }
          if (tag === "callback") pending = (value as RemoteCallback)();
          const outcome = pending?.then(() => "resolved", (error: unknown) => error);
          opened.channel.send(message);
          const cause = await waitWithSignal(terminal.promise, AbortSignal.timeout(5000));
          assertInstanceOf(cause, ActorTransportError);
          assertEquals(cause.code, "protocol");
          if (outcome) assertInstanceOf(await outcome, ActorTransportError);
        } finally {
          registry.failAll();
          peers.close();
          await registry.join();
        }
      });
    }
  }
}

Deno.test("unreleased live signals exhaust the declared channel budget and fail the owner", async () => {
  const peers = pair(false);
  peers.a.onClose(peers.b.close);
  peers.b.onClose(peers.a.close);
  const sender = new TrackingRegistry().register(abortSignalCodec) as TrackingRegistry;
  const receiver = new TrackingRegistry().register(abortSignalCodec) as TrackingRegistry;
  const originals: AbortSignal[] = [];
  const remote: AbortSignal[] = [];
  try {
    for (let i = 0; i < 4; i++) {
      const signal = new AbortController().signal;
      originals.push(signal);
      remote.push(receiver.decode(sender.encode(signal, [], peers.a), peers.b) as AbortSignal);
    }
    assertEquals(sender.active.size, 4);
    assertEquals(receiver.active.size, 4);
    assert(remote.every((signal) => !signal.aborted));
    const failure = assertThrows(
      () => sender.encode(new AbortController().signal, [], peers.a),
      ActorTransportError,
    );
    assertEquals(failure.code, "capacity");
    assert(peers.a.closed && peers.b.closed);
    assert(remote.every((signal) => signal.aborted));
    assert(
      originals.every((signal) => !signal.aborted),
      "Budget failure must not mutate source signals",
    );
    assertEquals(sender.active.size, 0);
    assertEquals(receiver.active.size, 0);
  } finally {
    sender.failAll();
    receiver.failAll();
    peers.close();
    await Promise.all([sender.join(), receiver.join()]);
  }
});

Deno.test("MessagePort dispatch exceptions become typed owner failures", async () => {
  const ports = new MessageChannel();
  const channel = wrapPort(ports.port1);
  const closed = Promise.withResolvers<unknown>();
  channel.onClose(closed.resolve);
  channel.onMessage(() => {
    throw new TypeError("dispatch sentinel");
  });
  try {
    ports.port2.postMessage(null);
    const cause = await waitWithSignal(closed.promise, AbortSignal.timeout(5000));
    assertInstanceOf(cause, ActorTransportError);
    assertEquals(cause.code, "protocol");
    assertInstanceOf(cause.cause, TypeError);
  } finally {
    channel.close();
    ports.port2.close();
  }
});
