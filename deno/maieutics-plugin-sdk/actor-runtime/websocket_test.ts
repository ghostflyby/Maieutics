import { assertEquals, assertStrictEquals } from "@std/assert";
import { fromWebSocket } from "./core/transport_v8.ts";

Deno.test("a real WebSocket carries V8 graph data across its binary message boundary", async () => {
  const lifetime = new AbortController();
  const peer = new Set<WebSocket>();
  const transports = new Set<ReturnType<typeof fromWebSocket>>();
  const ready = Promise.withResolvers<Deno.NetAddr>();
  const failure = Promise.withResolvers<never>();
  // The test installs its first wait after the listener is created.
  void failure.promise.catch(() => {});
  const timer = setTimeout(
    () => failure.reject(new Error("V8 WebSocket exchange timed out")),
    5_000,
  );
  const wait = <T>(operation: Promise<T>) => Promise.race([operation, failure.promise]);
  const adopt = (socket: WebSocket) => {
    peer.add(socket);
    const transport = fromWebSocket(socket, {
      onClosed: (error) => {
        if (!lifetime.signal.aborted) failure.reject(error);
      },
    });
    transports.add(transport);
    socket.onmessage = (event) => transport.deliver(event.data);
    return transport;
  };
  let server: Deno.HttpServer | undefined;
  try {
    server = Deno.serve(
      { hostname: "127.0.0.1", port: 0, signal: lifetime.signal, onListen: ready.resolve },
      (request) => {
        if (request.headers.get("upgrade") !== "websocket") {
          return new Response(null, { status: 400 });
        }
        const { socket, response } = Deno.upgradeWebSocket(request);
        const transport = adopt(socket);
        transport.onMessage((frame) => transport.send(frame.data));
        return response;
      },
    );
    const { port } = await wait(ready.promise);
    const socket = new WebSocket(`ws://127.0.0.1:${port}`);
    peer.add(socket);
    const opened = Promise.withResolvers<void>();
    socket.onopen = () => opened.resolve();
    socket.onerror = () => failure.reject(new Error("V8 WebSocket upgrade failed"));
    await wait(opened.promise);
    const client = adopt(socket);
    type Graph = { bytes: Uint8Array; self?: Graph };
    const outbound: Graph = { bytes: new Uint8Array([0, 128, 255]) };
    outbound.self = outbound;
    const received = Promise.withResolvers<Graph>();
    client.onMessage((frame) => received.resolve(frame.data as Graph));
    client.send(outbound);
    const result = await wait(received.promise);
    assertEquals(result.bytes, outbound.bytes);
    assertStrictEquals(result.self, result, "The V8 message retained its cyclic reference");
  } finally {
    clearTimeout(timer);
    lifetime.abort();
    for (const transport of transports) transport.close();
    for (const socket of peer) socket.close();
    await Promise.all([
      server?.finished,
      ...[...transports].map((transport) => transport.completion),
    ]);
  }
});
