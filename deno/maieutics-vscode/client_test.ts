/// <reference lib="deno.window" />

import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import { FrontendClient } from "./client.ts";
import { FrontendError, ProtocolVersion } from "./protocol.ts";
import {
  CommKind,
  type CommMessage,
  decodeCommEnvelope,
  encodeCommEnvelope,
} from "../shared/comm_codec.ts";

/** One events connection observed by the mock: its requested resume offset, a
 * sender for scripted frames, and a server-side close. */
interface MockEventsConnection {
  sinceSequence: number;
  send(frame: unknown): void;
  close(): void;
}

/** Immutable display objects served by the mock (content-addressed stand-ins). */
const objectBodies: Record<string, number[]> = {
  "/v1/objects/one": [1, 2, 3],
  "/v1/objects/two": [4, 5, 6],
};

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
  /** The events connections so far, in accept order. */
  eventConnections(): MockEventsConnection[];
  /** Scripts each events connection (invoked on open, after the hello). */
  onEventsConnection(handler: (connection: MockEventsConnection) => void): void;
  /** Object fetch counts by path. */
  objectHits(): Record<string, number>;
}> {
  let sockets: WebSocket[] = [];
  let broadcast: ((frame: unknown) => void) | null = null;
  let commsSockets: WebSocket[] = [];
  const commsReceived: { sequence: number; message: CommMessage }[] = [];
  const eventConnections: MockEventsConnection[] = [];
  let onEventsConnection: ((connection: MockEventsConnection) => void) | undefined;
  const objectFetches = new Map<string, number>();
  const abort = new AbortController();
  const server = Deno.serve(
    { port: 0, hostname: "127.0.0.1", signal: abort.signal },
    async (request) => {
      const url = new URL(request.url);
      const authorization = request.headers.get("Authorization");
      const tokenInQuery = url.searchParams.get("token");
      const authorized = authorization === "Bearer test-token" || tokenInQuery === "test-token";
      if (!authorized) return json(401, { code: "unauthorized", message: "no" });

      if (url.pathname.startsWith("/v1/objects/")) {
        objectFetches.set(url.pathname, (objectFetches.get(url.pathname) ?? 0) + 1);
        const bytes = objectBodies[url.pathname];
        if (bytes === undefined) {
          return json(404, { code: "not_found", message: url.pathname });
        }
        return new Response(new Uint8Array(bytes), {
          headers: { "content-type": "application/octet-stream" },
        });
      }

      if (url.pathname === "/v1/model/profiles") {
        return json(200, [
          { id: "default", provider: "OpenAI", model: "test-model", selected: true },
          { id: "claude", provider: "Anthropic", model: "claude-test", selected: false },
        ]);
      }

      if (url.pathname === "/v1/agent/capabilities") {
        return json(200, {
          protocolVersion: ProtocolVersion,
          serverVersion: "0.0.0-test",
          session: { id: "a".repeat(32), turns: 0, persistenceEnabled: false },
          workspaceRoot: "/repos/alpha",
        });
      }

      if (url.pathname === "/v1/agent/sessions") {
        return json(200, [{
          id: "a".repeat(32),
          turns: 2,
          createdAt: "2026-09-08T09:00:00Z",
          lastActivityAt: "2026-09-08T10:00:00Z",
          title: "Design review",
          preview: "First question",
          workspaceRoot: "/repos/alpha",
        }]);
      }

      if (url.pathname.endsWith("/rename") && request.method === "POST") {
        const body = await request.json() as { title: string };
        if (body.title.length > 200) {
          return json(400, { code: "invalid_request", message: "too long" });
        }
        const cleared = body.title.trim() === "";
        // The real server omits null fields on write, so a cleared answer
        // carries no title key at all.
        return json(
          200,
          cleared ? { id: "a".repeat(32) } : { id: "a".repeat(32), title: body.title },
        );
      }

      if (url.pathname.endsWith("/fork") && request.method === "POST") {
        const body = await request.json() as { runId?: string; seq?: number; profileId?: string };
        if (body.runId === undefined && body.seq === undefined) {
          return json(400, { code: "invalid_request", message: "runId or seq required" });
        }
        if (body.profileId === "no-such-profile") {
          return json(400, { code: "invalid_request", message: "no model profile matches" });
        }
        if (body.runId !== undefined && body.runId === "f".repeat(32)) {
          return json(404, { code: "not_found", message: "no committed turn matches" });
        }
        return json(200, { id: "c".repeat(32), title: "first · branch @ turn 1" });
      }

      if (url.pathname.endsWith("/gc")) {
        return json(200, { markdown: "**GC** removed 3 unreferenced object(s)" });
      }

      if (url.pathname.endsWith("/repair")) {
        return json(200, { markdown: "**View** ensured 4 object link(s)" });
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
        const sinceSequence = Number(url.searchParams.get("sinceSequence") ?? "0");
        socket.onopen = () => {
          sockets.push(socket);
          broadcast ??= (frame) => {
            for (const target of sockets) target.send(JSON.stringify(frame));
          };
          broadcast({ type: "hello", session: { id: "a".repeat(32), turns: 0 } });
          const connection: MockEventsConnection = {
            sinceSequence,
            send: (frame) => socket.send(JSON.stringify(frame)),
            close: () => socket.close(),
          };
          eventConnections.push(connection);
          onEventsConnection?.(connection);
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
    eventConnections: () => [...eventConnections],
    onEventsConnection: (handler) => {
      onEventsConnection = handler;
    },
    objectHits: () => Object.fromEntries(objectFetches),
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

Deno.test("stored sessions carry display metadata and capabilities carry the workspace", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const capabilities = await client.capabilities();
    assertEquals(capabilities.workspaceRoot, "/repos/alpha");

    const [session] = await client.listSessions();
    assertEquals(session.title, "Design review");
    assertEquals(session.preview, "First question");
    assertEquals(session.workspaceRoot, "/repos/alpha");
  } finally {
    await shutdown();
  }
});

Deno.test("rename and maintenance endpoints answer with typed shapes", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const title = await client.renameSession("a".repeat(32), "Design review");
    assertEquals(title, "Design review");
    // An empty title clears; the server answers with an omitted (undefined) title.
    const cleared = await client.renameSession("a".repeat(32), " ");
    assertEquals(cleared, undefined);

    const gc = await client.pruneObjects("a".repeat(32), 12);
    assert(gc.includes("GC"));
    const view = await client.repairObjectView("a".repeat(32));
    assert(view.includes("View"));
  } finally {
    await shutdown();
  }
});

Deno.test("rename over the cap surfaces the typed invalid request", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const failure = await client.renameSession("a".repeat(32), "x".repeat(201))
      .then(() => null, (error: unknown) => error);
    assert(failure instanceof FrontendError);
    assertEquals(failure.code, "invalid_request");
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

Deno.test("events resume passes the live run's last sequence, not an earlier run's", async () => {
  const { discovery, shutdown, eventConnections } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const iterator = client.events("a".repeat(32), { signal: controller.signal })
      [Symbol.asyncIterator]();
    assertEquals((await iterator.next()).value?.type, "hello");

    // Run A streamed to sequence 60, then run B replaced it and is at
    // sequence 5 (the session's single-run gate keeps one run in flight).
    const runA = "1".repeat(32);
    const runB = "2".repeat(32);
    eventConnections()[0].send({ type: "run.started", runId: runA });
    for (let sequence = 58; sequence <= 60; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: runA, sequence, text: "x" });
    }
    eventConnections()[0].send({ type: "run.started", runId: runB });
    for (let sequence = 1; sequence <= 5; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: runB, sequence, text: "y" });
    }
    for (let i = 0; i < 10; i++) await iterator.next();

    // The server drops the connection; pulling drives the reconnect and the
    // next frame is the new connection's hello.
    eventConnections()[0].close();
    assertEquals((await iterator.next()).value?.type, "hello");
    assertEquals(eventConnections().length, 2);
    // The offset is run B's mark (5), never run A's high-water (60): one
    // monotonic offset would filter every remaining frame of the live run.
    assertEquals(eventConnections()[1].sinceSequence, 5);
    controller.abort();
    await iterator.next();
  } finally {
    await shutdown();
  }
});

Deno.test("events resume starts from zero when no run is still live", async () => {
  const { discovery, shutdown, eventConnections } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const iterator = client.events("a".repeat(32), { signal: controller.signal })
      [Symbol.asyncIterator]();
    assertEquals((await iterator.next()).value?.type, "hello");

    // Run A settled: its terminal frame ends its sequence space, so nothing
    // observed for it is a meaningful offset for the next connection.
    const runA = "1".repeat(32);
    eventConnections()[0].send({ type: "run.started", runId: runA });
    eventConnections()[0].send({ type: "text.delta", runId: runA, sequence: 1, text: "x" });
    eventConnections()[0].send({ type: "text.delta", runId: runA, sequence: 2, text: "y" });
    eventConnections()[0].send({ type: "run.completed", runId: runA, sequence: 3 });
    for (let i = 0; i < 4; i++) await iterator.next();

    eventConnections()[0].close();
    assertEquals((await iterator.next()).value?.type, "hello");
    assertEquals(eventConnections().length, 2);
    assertEquals(eventConnections()[1].sinceSequence, 0);
    controller.abort();
    await iterator.next();
  } finally {
    await shutdown();
  }
});

Deno.test("replayed frames within a run are delivered exactly once", async () => {
  const { discovery, shutdown, onEventsConnection } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const runB = "2".repeat(32);
    // The first connection streams the run to sequence 3 and drops; the
    // reconnect's replay window overlaps (2 and 3 again) before continuing.
    onEventsConnection((connection) => {
      if (connection.sinceSequence === 0) {
        connection.send({ type: "run.started", runId: runB });
        for (let sequence = 1; sequence <= 3; sequence++) {
          connection.send({ type: "text.delta", runId: runB, sequence, text: "x" });
        }
        setTimeout(() => connection.close(), 25);
        return;
      }
      for (let sequence = 2; sequence <= 4; sequence++) {
        connection.send({ type: "text.delta", runId: runB, sequence, text: "x" });
      }
    });

    const sequences: number[] = [];
    for await (const frame of client.events("a".repeat(32))) {
      if (typeof frame.sequence !== "number") continue;
      sequences.push(frame.sequence);
      if (frame.sequence === 4) break;
    }
    // The overlap (2, 3) is deduplicated against the run's high-water mark.
    assertEquals(sequences, [1, 2, 3, 4]);
  } finally {
    await shutdown();
  }
});

Deno.test("events tolerate a failed open without unhandled rejections", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  // The server is gone: every open attempt fails. The open promise's
  // rejection is consumed by the generator (reconnect is driven by the
  // message loop ending), so nothing escapes as an unhandled rejection.
  await shutdown();
  const controller = new AbortController();
  const iterator = client.events("a".repeat(32), { signal: controller.signal })
    [Symbol.asyncIterator]();
  await new Promise((resolve) => setTimeout(resolve, 400));
  controller.abort();
  const rest = await iterator.next();
  assert(rest.done ?? true);
});

Deno.test("immutable object fetches are cached per URL", async () => {
  const { discovery, shutdown, objectHits } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const first = await client.fetchObject("/v1/objects/one");
    assertEquals(first, new Uint8Array([1, 2, 3]));
    assertEquals(await client.fetchObject("/v1/objects/one"), first);

    // Concurrent paints of the same object collapse into one fetch.
    const [a, b] = await Promise.all([
      client.fetchObject("/v1/objects/two"),
      client.fetchObject("/v1/objects/two"),
    ]);
    assertEquals(a, new Uint8Array([4, 5, 6]));
    assertEquals(b, a);

    // A failed fetch is not pinned: the next call retries against the server.
    const failure = await client.fetchObject("/v1/objects/missing")
      .then(() => null, (error: unknown) => error);
    assert(failure instanceof FrontendError);
    await assertRejects(
      () => client.fetchObject("/v1/objects/missing"),
      FrontendError,
    );

    const hits = objectHits();
    assertEquals(hits["/v1/objects/one"], 1);
    assertEquals(hits["/v1/objects/two"], 1);
    assertEquals(hits["/v1/objects/missing"], 2);
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

Deno.test("forkSession posts the branch point and carries the head", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const head = await client.forkSession("a".repeat(32), { runId: "b".repeat(32) });
    assertEquals(head.id, "c".repeat(32));
    assertEquals(head.title, "first · branch @ turn 1");

    const error = await client.forkSession("a".repeat(32), { runId: "f".repeat(32) })
      .catch((e: unknown) => e);
    assertEquals(error instanceof FrontendError, true);
    assertEquals((error as FrontendError).code, "not_found");
  } finally {
    await shutdown();
  }
});

Deno.test("forkSession without a branch point surfaces the typed error", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const error = await client.forkSession("a".repeat(32), {}).catch((e: unknown) => e);
    assertEquals(error instanceof FrontendError, true);
    assertEquals((error as FrontendError).code, "invalid_request");
  } finally {
    await shutdown();
  }
});

Deno.test("model profiles list answer is typed", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const profiles = await client.modelProfiles();
    assertEquals(profiles.length, 2);
    assertEquals(profiles[0], {
      id: "default",
      provider: "OpenAI",
      model: "test-model",
      selected: true,
    });
  } finally {
    await shutdown();
  }
});

Deno.test("forkSession forwards a profile override and surfaces unknown profiles", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const head = await client.forkSession("a".repeat(32), {
      runId: "b".repeat(32),
      profileId: "claude",
    });
    assertEquals(head.id, "c".repeat(32));

    const error = await client
      .forkSession("a".repeat(32), { seq: 0, profileId: "no-such-profile" })
      .catch((e: unknown) => e);
    assertEquals(error instanceof FrontendError, true);
    assertEquals((error as FrontendError).code, "invalid_request");
  } finally {
    await shutdown();
  }
});
