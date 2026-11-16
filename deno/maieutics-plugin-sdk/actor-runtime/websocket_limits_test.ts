import { assertEquals, assertInstanceOf, assertThrows } from "@std/assert";
import { ActorTransportError } from "./core/lifetime.ts";
import { webSocketTransport } from "./core/websocket.ts";

class Socket extends EventTarget {
  readyState = 1;
  bufferedAmount = 0;
  closes = 0;
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
  send(_message: unknown): void {}
  close(): void {
    this.readyState = 3;
    this.closes++;
  }
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
