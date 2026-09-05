/// <reference lib="deno.window" />

import { assert, assertEquals, assertThrows } from "@std/assert";
import { FrontendClient } from "./client.ts";
import { ProtocolVersion } from "./protocol.ts";

/** A stand-in for the Maieutics frontend API: discovery + REST + one events
 * WebSocket session, implemented over `Deno.serve`. */
function startMockServer(): Promise<{
  url: string;
  discovery: unknown;
  shutdown(): Promise<void>;
}> {
  let sockets: WebSocket[] = [];
  let broadcast: ((frame: unknown) => void) | null = null;
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
        if (body.text.startsWith("%")) return json(200, { markdown: "### status" });
        return new Response(JSON.stringify({ runId: "b".repeat(32) }), {
          status: 202,
          headers: { "content-type": "application/json" },
        });
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
    shutdown: async () => {
      // Upgraded WebSocket requests and keep-alive connections are long-lived: on Linux,
      // shutdown() waits for them. Close every open socket and abort the serve signal so
      // the shutdown is forced instead of waiting.
      for (const socket of sockets) {
        try {
          socket.close();
        } catch {
          // Already closed.
        }
      }

      sockets = [];
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
    assertEquals(command, { kind: "command", markdown: "### status" });
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
