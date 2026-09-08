/// <reference lib="deno.window" />

import { assert, assertEquals, assertThrows } from "@std/assert";
import { FrontendClient } from "./client.ts";
import { ProtocolVersion } from "./protocol.ts";
import {
  CommKind,
  type CommMessage,
  decodeCommEnvelope,
  encodeCommEnvelope,
} from "../shared/comm_codec.ts";

/** A stand-in for the Maieutics frontend API: discovery + REST + one events
 * WebSocket session, implemented over `Deno.serve`. */
function startMockServer(): Promise<{
  url: string;
  discovery: unknown;
  shutdown(): Promise<void>;
  /** Sends a downlink comm frame to every open comms socket. */
  sendComm(message: CommMessage, sequence: number): void;
  /** The uplink comm frames clients sent, decoded. */
  receivedComms(): Promise<{ sequence: number; message: CommMessage }[]>;
}> {
  let sockets: WebSocket[] = [];
  let broadcast: ((frame: unknown) => void) | null = null;
  let commsSockets: WebSocket[] = [];
  const commsReceived: { sequence: number; message: CommMessage }[] = [];
  const abort = new AbortController();
  const server = Deno.serve(
    { port: 0, hostname: "127.0.0.1", signal: abort.signal },
    async (request) => {
      const url = new URL(request.url);
      const authorization = request.headers.get("Authorization");
      const tokenInQuery = url.searchParams.get("token");
      const authorized = authorization === "Bearer test-token" || tokenInQuery === "test-token";
      if (!authorized) return json(401, { code: "unauthorized", message: "no" });

      if (url.pathname === "/v1/agent/capabilities") {
        return json(200, {
          protocolVersion: ProtocolVersion,
          serverVersion: "0.0.0-test",
          session: { id: "a".repeat(32), turns: 0, persistenceEnabled: false },
        });
      }

      if (url.pathname === "/v1/agent/sessions/turns" || url.pathname.endsWith("/turns")) {
        const body = await request.json() as { text: string };
        if (body.text.startsWith("%")) {
          return json(200, { markdown: "### status", sessionId: "a".repeat(32) });
        }
        return new Response(JSON.stringify({ runId: "b".repeat(32) }), {
          status: 202,
          headers: { "content-type": "application/json" },
        });
      }

      if (url.pathname.endsWith("/comms") && request.headers.get("upgrade") === "websocket") {
        const { response, socket } = Deno.upgradeWebSocket(request);
        socket.binaryType = "arraybuffer";
        socket.onopen = () => {
          commsSockets.push(socket);
          socket.send(JSON.stringify({ live: [], replayed: false, truncated: false }));
        };
        socket.onclose = () => {
          commsSockets = commsSockets.filter((target) => target !== socket);
        };
        socket.onmessage = (event) => {
          if (typeof event.data === "string") return;
          const decoded = decodeCommEnvelope(new Uint8Array(event.data));
          commsReceived.push(decoded);
        };
        return response;
      }

      if (url.pathname.endsWith("/events") && request.headers.get("upgrade") === "websocket") {
        const { response, socket } = Deno.upgradeWebSocket(request);
        socket.onopen = () => {
          sockets.push(socket);
          broadcast ??= (frame) => {
            for (const target of sockets) target.send(JSON.stringify(frame));
          };
          broadcast({ type: "hello", session: { id: "a".repeat(32), turns: 0 } });
        };
        socket.onclose = () => {
          sockets = sockets.filter((target) => target !== socket);
        };
        return response;
      }

      return json(404, { code: "not_found", message: url.pathname });
    },
  );

  const url = `http://127.0.0.1:${server.addr.port}`;
  return Promise.resolve({
    url,
    discovery: { version: 1, url, token: "test-token", pid: 1234 },
    sendComm: (message, sequence) => {
      const payload = encodeCommEnvelope(sequence, message);
      for (const target of commsSockets) target.send(payload);
    },
    receivedComms: () => Promise.resolve(commsReceived),
    shutdown: async () => {
      // Upgraded WebSocket requests and keep-alive connections are long-lived: on Linux,
      // shutdown() waits for them. Close every open socket and abort the serve signal so
      // the shutdown is forced instead of waiting.
      for (const socket of [...sockets, ...commsSockets]) {
        try {
          socket.close();
        } catch {
          // Already closed.
        }
      }

      sockets = [];
      commsSockets = [];
      abort.abort();
      await server.shutdown();
    },
  });
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

Deno.test("client builds from a discovery object", async () => {
  const { url, discovery, shutdown } = await startMockServer();
  try {
    const client = FrontendClient.fromDiscovery(discovery);
    assertEquals(client.baseUrl, url);
    const capabilities = await client.capabilities();
    assertEquals(capabilities.protocolVersion, ProtocolVersion);
  } finally {
    await shutdown();
  }
});

Deno.test("client rejects foreign discovery versions", () => {
  assertThrows(() =>
    FrontendClient.fromDiscovery({ version: 99, url: "http://127.0.0.1:1", token: "x" })
  );
});

Deno.test("turn submission distinguishes commands from runs", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const command = await client.submitTurn("a".repeat(32), "%status");
    assertEquals(command, { kind: "command", markdown: "### status", sessionId: "a".repeat(32) });
    const turn = await client.submitTurn("a".repeat(32), "hello");
    assertEquals(turn, { kind: "turn", runId: "b".repeat(32) });
  } finally {
    await shutdown();
  }
});

Deno.test("events stream yields frames and requires the bearer token", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const iterator = client.events("a".repeat(32), { signal: controller.signal })
      [Symbol.asyncIterator]();
    const first = await iterator.next();
    assertEquals(first.value?.type, "hello");
    controller.abort();
    const rest = await iterator.next();
    assert(rest.done ?? true);
  } finally {
    await shutdown();
  }
});

Deno.test("comm socket negotiates the hello and relays frames both ways", async () => {
  const { discovery, shutdown, sendComm, receivedComms } = await startMockServer();
  try {
    const client = FrontendClient.fromDiscovery(discovery);
    const comms = await client.commSocket("a".repeat(32));
    try {
      assertEquals(comms.hello.live.length, 0);
      assertEquals(comms.hello.replayed, false);

      // Downlink: the server's envelope sequence reaches the consumer decoded.
      const frames: { sequence: number; commId: string }[] = [];
      const pump = (async () => {
        for await (const frame of comms.messages) {
          if (frame.kind === "comm") {
            frames.push({ sequence: frame.sequence, commId: frame.message.commId });
            return;
          }
        }
      })();
      sendComm({
        kind: CommKind.Open,
        commId: "w1",
        targetName: "anywidget",
        data: { ready: true },
        buffers: [],
      }, 1);
      await pump;

      // Uplink: the codec frame the server receives carries buffers natively.
      comms.send({
        kind: CommKind.Message,
        commId: "w1",
        data: { click: 1 },
        buffers: [new Uint8Array([0xEF])],
      });
      let received = await receivedComms();
      const deadline = Date.now() + 5000;
      while (received.length === 0 && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 25));
        received = await receivedComms();
      }
      assertEquals(received.length, 1);
      assertEquals(received[0].message.commId, "w1");
      assertEquals(received[0].message.data, { click: 1 });
      assertEquals(
        received[0].message.buffers[0],
        new Uint8Array([0xEF]),
      );
    } finally {
      comms.close();
    }
  } finally {
    await shutdown();
  }
});
