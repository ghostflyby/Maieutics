import { assertEquals, assertInstanceOf, assertThrows } from "@std/assert";
import { ActorTransportError } from "./core/lifetime.ts";
import { webSocketTransport } from "./core/websocket.ts";

class Socket extends EventTarget {
  readyState = 1;
  bufferedAmount = 0;
  closes = 0;
  sent: unknown[] = [];
  listeners = new Map<string, EventListenerOrEventListenerObject>();
  override addEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    this.listeners.set(type, listener);
    super.addEventListener(type, listener);
  }
  override removeEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    assertEquals(
      this.listeners.get(type),
      listener,
      "Cleanup must remove the registered listener identity",
    );
    this.listeners.delete(type);
    super.removeEventListener(type, listener);
  }
  send(message: unknown): void {
    this.sent.push(message);
  }
  close(): void {
    this.readyState = 3;
    this.closes++;
  }
}

for (const shared of [false, true]) {
  Deno.test(`WebSocket encodes ${shared ? "shared" : "ordinary"} buffer views as native binary`, async () => {
    const socket = new Socket();
    const buffer = shared ? new SharedArrayBuffer(8) : new ArrayBuffer(8);
    const bytes = new Uint8Array(buffer, 2, 3);
    bytes.set([0, 128, 255]);
    const transport = webSocketTransport(socket as unknown as WebSocket, {
      codec: { encode: () => bytes, decode: (value) => value },
    });
    transport.send({});
    assertInstanceOf(socket.sent[0], Uint8Array);
    assertEquals([...socket.sent[0]], [0, 128, 255]);
    assertInstanceOf(socket.sent[0].buffer, ArrayBuffer);
    if (!shared) assertEquals(socket.sent[0].buffer, buffer, "Ordinary binary views need no copy");
    transport.close();
    await transport.completion;
  });
}

for (const mode of ["receive", "send", "buffered", "decode", "malformed"] as const) {
  Deno.test(`WebSocket ${mode} budget failure releases listeners and socket exactly once`, async () => {
    const socket = new Socket();
    const transport = webSocketTransport(socket as unknown as WebSocket, {
      codec: { encode: (v) => String(v), decode: () => new Promise(() => {}) },
      maxMessageBytes: 4,
      maxBufferedBytes: 4,
    });
    if (mode === "send") assertThrows(() => transport.send("ééé"), ActorTransportError);
    else if (mode === "buffered") {
      socket.bufferedAmount = 3;
      assertThrows(() => transport.send("é"), ActorTransportError);
    } else {
      socket.dispatchEvent(
        new MessageEvent("message", {
          data: mode === "malformed" ? null : mode === "decode" ? "éé" : "ééé",
        }),
      );
      if (mode === "decode") socket.dispatchEvent(new MessageEvent("message", { data: "x" }));
    }
    assertInstanceOf(transport.reason, ActorTransportError);
    assertEquals(transport.reason.code, mode === "malformed" ? "protocol" : "capacity");
    assertEquals(socket.listeners.size, 0);
    assertEquals(socket.closes, 1);
    transport.close();
    await transport.completion;
    assertEquals(socket.closes, 1);
  });
}
