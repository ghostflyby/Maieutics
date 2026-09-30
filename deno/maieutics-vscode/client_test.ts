/// <reference lib="deno.window" />

import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import { FrontendClient } from "./client.ts";
import type { QueueState } from "./client.ts";
import { FrontendError, ProtocolVersion } from "./protocol.ts";
import { isCommandCellText, QueueWatcher } from "./queueProjection.ts";
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

/** The mock server queue's driving surface: the REST handlers mutate it and
 * every mutation broadcasts a full-state `queue.updated` frame. */
interface MockQueue {
  /** Reads the current full-state snapshot (as GET /queue answers). */
  snapshot(): QueueState;
  /** Moves the first waiting item to running under the given run id (and
   * emits the frame). Returns the item id, or undefined on an empty queue. */
  startNext(runId: string): string | undefined;
  /** Clears the running item (and emits the frame): the item is gone. */
  finishRunning(): void;
  /** Overrides the capacity (queue_full tests). */
  setCapacity(capacity: number): void;
  /** How many GET /queue requests arrived (reconcile counting). */
  getHits(): number;
  /** The text batches of every POST /queue, in arrival order. */
  postedBatches(): string[][];
}

/** Polls until the predicate holds or the deadline passes (frames and REST
 * answers race the test's assertions). */
async function waitFor(predicate: () => boolean, timeoutMs = 5000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error("waitFor timed out");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
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
  /** The object uploads the mock received, in arrival order (`name` is the
   * raw query parameter, null when the request carried none). */
  uploadedObjects(): { bytes: Uint8Array; mediaType: string; name: string | null }[];
  /** The server-side turn queue the /queue endpoints serve. */
  queue: MockQueue;
}> {
  let sockets: WebSocket[] = [];
  let broadcast: ((frame: unknown) => void) | null = null;
  let commsSockets: WebSocket[] = [];
  const commsReceived: { sequence: number; message: CommMessage }[] = [];
  const eventConnections: MockEventsConnection[] = [];
  let onEventsConnection: ((connection: MockEventsConnection) => void) | undefined;
  const objectFetches = new Map<string, number>();
  const uploadedObjects: { bytes: Uint8Array; mediaType: string; name: string | null }[] = [];
  const abort = new AbortController();

  // The server-owned turn queue: REST mutations move it and every mutation
  // broadcasts a full-state queue.updated frame to the events sockets.
  const sessionId = "a".repeat(32);
  const queueItems: { id: string; text: string }[] = [];
  let queueRunning: { itemId: string; runId: string } | null = null;
  let queueCapacity = 8;
  let nextItemId = 0;
  let queueGetHits = 0;
  const queuePostedBatches: string[][] = [];

  // The session task plane's fixture list: a full agent snapshot (working),
  // a settled one-shot terminal, and a catalog-identity entry of an unknown
  // authority with no status — the three shapes the list must tolerate.
  const agentSession = "a".repeat(32);
  const foreignTaskUri = `task://agent/${"f".repeat(32)}/0123456789abcdef0123456789abcdef`;
  const stuckTaskUri = `task://future/${agentSession}/task-stuck`;
  const mockTaskList = [
    {
      uri: `task://agent/${agentSession}/0123456789abcdef0123456789abcdef`,
      kind: "agent",
      status: "working",
      agent: {
        agentSessionId: agentSession,
        runId: "0123456789abcdef0123456789abcdef",
      },
    },
    {
      uri: `task://terminal/${agentSession}/ts-9e58c0d2`,
      kind: "terminal",
      status: "fail",
      terminal: {
        agentSessionId: agentSession,
        sessionId: "ts-9e58c0d2",
        state: "completed",
        exitCode: 2,
      },
    },
    {
      uri: stuckTaskUri,
      kind: "future",
      description: "A running future task.",
    },
  ];
  const queueSnapshot = (): QueueState => ({
    sessionId,
    running: queueRunning,
    items: queueItems.map((item) => ({
      id: item.id,
      text: item.text,
      enqueuedAt: "2026-09-12T00:00:00Z",
    })),
    capacity: queueCapacity,
  });
  const broadcastQueueUpdate = () => {
    broadcast?.({ type: "queue.updated", queue: queueSnapshot() });
  };
  const queue: MockQueue = {
    snapshot: queueSnapshot,
    startNext: (runId) => {
      const item = queueItems.shift();
      if (item === undefined) return undefined;
      queueRunning = { itemId: item.id, runId };
      broadcastQueueUpdate();
      return item.id;
    },
    finishRunning: () => {
      queueRunning = null;
      broadcastQueueUpdate();
    },
    setCapacity: (capacity) => {
      queueCapacity = capacity;
    },
    getHits: () => queueGetHits,
    postedBatches: () => queuePostedBatches.map((batch) => [...batch]),
  };

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

      // The attachment ingest endpoint: raw binary body, the object's media
      // type as Content-Type, optional ?name= display name. The mock answers
      // the real content address of the received bytes, so a test can prove
      // the body arrived as native binary (same bytes → same sha256).
      const objectUpload = url.pathname.match(/^\/v1\/agent\/sessions\/([^/]+)\/objects$/);
      if (objectUpload !== null && request.method === "POST") {
        if (objectUpload[1] === "f".repeat(32)) {
          return json(404, { code: "not_found", message: "no such session" });
        }
        const bytes = new Uint8Array(await request.arrayBuffer());
        if (bytes.byteLength > 8) {
          return json(400, { code: "invalid_request", message: "oversized object payload" });
        }
        const name = url.searchParams.get("name");
        if (name === "__malformed__") {
          return json(200, { unexpected: true });
        }
        const digest = await crypto.subtle.digest("SHA-256", bytes);
        const sha256 = [...new Uint8Array(digest)]
          .map((byte) => byte.toString(16).padStart(2, "0"))
          .join("");
        uploadedObjects.push({
          bytes,
          mediaType: request.headers.get("Content-Type") ?? "",
          name,
        });
        return json(200, { sha256, byteLength: bytes.byteLength });
      }

      const queueMatch = url.pathname.match(/^\/v1\/agent\/sessions\/[^/]+\/queue(\/([^/]+))?$/);
      if (queueMatch !== null) {
        if (request.method === "GET") {
          queueGetHits++;
          return json(200, queueSnapshot());
        }
        if (request.method === "POST") {
          const body = await request.json() as { items?: { text?: string }[] };
          if (!Array.isArray(body.items)) {
            return json(400, { code: "invalid_request", message: "items required" });
          }
          const texts = body.items.map((item) => String(item.text ?? ""));
          if (texts.some((text) => isCommandCellText(text))) {
            return json(400, { code: "invalid_request", message: "command text is not queueable" });
          }
          if (texts.some((text) => text.trim().length === 0)) {
            return json(400, { code: "invalid_request", message: "empty text" });
          }
          if (queueItems.length + texts.length > queueCapacity) {
            return json(409, { code: "queue_full", message: "the queue is full" });
          }
          const created = texts.map((text) => {
            const id = `item-${++nextItemId}`;
            queueItems.push({ id, text });
            return { id, position: queueItems.length };
          });
          queuePostedBatches.push(texts);
          broadcastQueueUpdate();
          return json(202, { items: created });
        }
        if (request.method === "DELETE") {
          const itemId = queueMatch[2];
          if (itemId === undefined) {
            queueItems.length = 0;
            broadcastQueueUpdate();
            return new Response(null, { status: 204 });
          }
          if (queueRunning?.itemId === itemId) {
            return json(409, { code: "item_running", message: "cancel the run instead" });
          }
          const at = queueItems.findIndex((item) => item.id === itemId);
          if (at === -1) return json(404, { code: "not_found", message: itemId });
          queueItems.splice(at, 1);
          broadcastQueueUpdate();
          return new Response(null, { status: 204 });
        }
      }

      // The session task plane: list, single read, cancel by URI. The mock
      // mirrors the planned Frontend routes (`/v1/agent/sessions/{sid}/tasks…`)
      // and the control channel's error mapping (task_forbidden → 403).
      const tasksMatch = url.pathname.match(
        /^\/v1\/agent\/sessions\/([^/]+)\/tasks(?:\/cancel|(?:\/([^/]+)\/([^/]+)))?$/,
      );
      if (tasksMatch !== null) {
        if (request.method === "POST") {
          const body = await request.json() as { uri?: string };
          if (typeof body.uri !== "string" || !body.uri.startsWith("task://")) {
            return json(400, { code: "resource_invalid_uri", message: "not a task URI" });
          }
          if (body.uri === foreignTaskUri) {
            return json(403, { code: "task_forbidden", message: "not your session's task" });
          }
          if (body.uri === stuckTaskUri) {
            return json(502, { code: "task_cancel_failed", message: "the source refused" });
          }
          const found = mockTaskList.find((task) => task.uri === body.uri);
          if (found === undefined) {
            return json(404, { code: "resource_not_found", message: body.uri });
          }
          return json(200, { ...found, status: "cancel" });
        }
        if (request.method === "GET" && tasksMatch[2] === undefined) {
          return json(200, mockTaskList);
        }
        if (request.method === "GET") {
          const [kind, id] = [tasksMatch[2], tasksMatch[3]];
          const found = mockTaskList.find((task) =>
            task.kind === kind && (task.uri.endsWith(id ?? "") || task.terminal?.sessionId === id)
          );
          if (found === undefined) {
            return json(404, { code: "resource_not_found", message: url.pathname });
          }
          return json(200, { ...found, status: found.status ?? "working" });
        }
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
    uploadedObjects: () => uploadedObjects.map((entry) => ({ ...entry })),
    queue,
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

Deno.test("uploadObject ingests native binary bytes and answers the content address", async () => {
  const { discovery, shutdown, uploadedObjects } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const bytes = new Uint8Array([0, 1, 2, 250, 255]);
    const answer = await client.uploadObject(
      "a".repeat(32),
      bytes,
      "image/png",
      "diagram.png",
    );
    // The answer is a content address: 64 lowercase hex characters.
    assert(/^[0-9a-f]{64}$/.test(answer.sha256), `not a sha256: ${answer.sha256}`);
    assertEquals(answer.byteLength, bytes.byteLength);

    // The body arrived as the native bytes (not base64) with the media type
    // and the display name.
    const [received] = uploadedObjects();
    assertEquals(received.mediaType, "image/png");
    assertEquals(received.name, "diagram.png");
    assertEquals([...received.bytes], [...bytes]);

    // Re-ingesting identical bytes answers the same address (the store is
    // content-addressed), and omitting the name sends no name parameter.
    const again = await client.uploadObject("a".repeat(32), bytes, "image/png");
    assertEquals(again.sha256, answer.sha256);
    assertEquals(uploadedObjects()[1].name, null);
  } finally {
    await shutdown();
  }
});

Deno.test("uploadObject failures are typed: oversized, unknown session, malformed answer", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const oversized = await client.uploadObject(
      "a".repeat(32),
      new Uint8Array(9),
      "application/octet-stream",
      "toobig.bin",
    ).then(() => null, (error: unknown) => error);
    assert(oversized instanceof FrontendError);
    assertEquals(oversized.code, "invalid_request");

    const unknownSession = await client.uploadObject(
      "f".repeat(32),
      new Uint8Array(1),
      "text/plain",
    ).then(() => null, (error: unknown) => error);
    assert(unknownSession instanceof FrontendError);
    assertEquals(unknownSession.code, "not_found");

    const malformed = await client.uploadObject(
      "a".repeat(32),
      new Uint8Array(1),
      "text/plain",
      "__malformed__",
    ).then(() => null, (error: unknown) => error);
    assert(malformed instanceof FrontendError);
    assertEquals(malformed.code, "protocol_error");
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

    // Run A streamed to sequence 60 and settled (its terminal frame retires
    // its sequence space — wire order puts it before the next run starts);
    // run B replaced it and is at sequence 5 (the session's single-run gate
    // keeps one run in flight).
    const runA = "1".repeat(32);
    const runB = "2".repeat(32);
    eventConnections()[0].send({ type: "run.started", runId: runA });
    for (let sequence = 58; sequence <= 60; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: runA, sequence, text: "x" });
    }
    eventConnections()[0].send({ type: "run.completed", runId: runA, sequence: 61 });
    eventConnections()[0].send({ type: "run.started", runId: runB });
    for (let sequence = 1; sequence <= 5; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: runB, sequence, text: "y" });
    }
    for (let i = 0; i < 11; i++) await iterator.next();

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

Deno.test("child run frames never raise the reconnect offset", async () => {
  const { discovery, shutdown, eventConnections } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const iterator = client.events("a".repeat(32), { signal: controller.signal })
      [Symbol.asyncIterator]();
    assertEquals((await iterator.next()).value?.type, "hello");

    // The parent run is live at mark 5 while its subagent child (ADR 0030)
    // streams its own run-local sequence space up to 30. The child's
    // run-local numbers must never claim the reconnect offset: the offset
    // feeds the server's sinceSequence filter, and a raised offset would
    // silently drop every remaining parent frame.
    const parent = "1".repeat(32);
    const child = "3".repeat(32);
    eventConnections()[0].send({ type: "run.started", runId: parent });
    for (let sequence = 1; sequence <= 5; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: parent, sequence, text: "p" });
    }
    eventConnections()[0].send({ type: "run.started", runId: child });
    for (let sequence = 1; sequence <= 30; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: child, sequence, text: "c" });
    }
    for (let i = 0; i < 37; i++) await iterator.next();

    eventConnections()[0].close();
    assertEquals((await iterator.next()).value?.type, "hello");
    assertEquals(eventConnections().length, 2);
    // The offset is the live PARENT's mark (5), never the child's 30.
    assertEquals(eventConnections()[1].sinceSequence, 5);
    controller.abort();
    await iterator.next();
  } finally {
    await shutdown();
  }
});

Deno.test("child run.started keeps the parent's dedupe mark across reconnects", async () => {
  const { discovery, shutdown, onEventsConnection } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const parent = "1".repeat(32);
    const child = "3".repeat(32);
    onEventsConnection((connection) => {
      if (connection.sinceSequence === 0) {
        connection.send({ type: "run.started", runId: parent });
        for (let sequence = 1; sequence <= 3; sequence++) {
          connection.send({ type: "text.delta", runId: parent, sequence, text: "x" });
        }
        // The child announces itself mid-parent: this frame must not wipe
        // the parent's high-water mark.
        connection.send({ type: "run.started", runId: child });
        setTimeout(() => connection.close(), 25);
        return;
      }
      // The reconnect replays the parent's whole window and continues.
      for (let sequence = 1; sequence <= 5; sequence++) {
        connection.send({ type: "text.delta", runId: parent, sequence, text: "x" });
      }
    });

    const sequences: number[] = [];
    for await (const frame of client.events("a".repeat(32))) {
      if (frame.runId !== parent || typeof frame.sequence !== "number") continue;
      sequences.push(frame.sequence);
      if (frame.sequence === 5) break;
    }
    // A wiped mark would re-deliver 1..3 on every reconnect (the parent
    // answer text rendering three times over).
    assertEquals(sequences, [1, 2, 3, 4, 5]);
  } finally {
    await shutdown();
  }
});

Deno.test("child replay dedupes against the child's own mark", async () => {
  const { discovery, shutdown, onEventsConnection } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const parent = "1".repeat(32);
    const child = "3".repeat(32);
    onEventsConnection((connection) => {
      if (connection.sinceSequence === 0) {
        connection.send({ type: "run.started", runId: parent });
        connection.send({ type: "run.started", runId: child });
        for (let sequence = 1; sequence <= 3; sequence++) {
          connection.send({ type: "text.delta", runId: child, sequence, text: "c" });
        }
        setTimeout(() => connection.close(), 25);
        return;
      }
      // The child's replay window overlaps (2 and 3 again): its own mark
      // dedupes it without dragging the offset up (child marks never claim
      // the reconnect offset, so the reconnect restarts from 0 here).
      for (let sequence = 2; sequence <= 4; sequence++) {
        connection.send({ type: "text.delta", runId: child, sequence, text: "c" });
      }
    });

    const sequences: number[] = [];
    for await (const frame of client.events("a".repeat(32))) {
      if (frame.runId !== child || typeof frame.sequence !== "number") continue;
      sequences.push(frame.sequence);
      if (frame.sequence === 4) break;
    }
    assertEquals(sequences, [1, 2, 3, 4]);
  } finally {
    await shutdown();
  }
});

Deno.test("child terminal frames retire the child; the next run starts clean", async () => {
  const { discovery, shutdown, eventConnections } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const iterator = client.events("a".repeat(32), { signal: controller.signal })
      [Symbol.asyncIterator]();
    assertEquals((await iterator.next()).value?.type, "hello");

    const parent = "1".repeat(32);
    const child = "3".repeat(32);
    const next = "4".repeat(32);
    eventConnections()[0].send({ type: "run.started", runId: parent });
    eventConnections()[0].send({ type: "text.delta", runId: parent, sequence: 1, text: "p" });
    eventConnections()[0].send({ type: "run.started", runId: child });
    for (let sequence = 1; sequence <= 30; sequence++) {
      eventConnections()[0].send({ type: "text.delta", runId: child, sequence, text: "c" });
    }
    eventConnections()[0].send({ type: "run.completed", runId: child });
    eventConnections()[0].send({ type: "run.completed", runId: parent, sequence: 2 });
    // The next TOP-LEVEL run starts: it must be classified as top-level
    // (not as a child of the retired parent) and the child's high-water 30
    // must not leak into its resume request.
    eventConnections()[0].send({ type: "run.started", runId: next });
    eventConnections()[0].send({ type: "text.delta", runId: next, sequence: 1, text: "n" });
    eventConnections()[0].send({ type: "text.delta", runId: next, sequence: 2, text: "n" });
    for (let i = 0; i < 38; i++) await iterator.next();

    eventConnections()[0].close();
    assertEquals((await iterator.next()).value?.type, "hello");
    assertEquals(eventConnections().length, 2);
    assertEquals(eventConnections()[1].sinceSequence, 2);
    controller.abort();
    await iterator.next();
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

Deno.test("queue endpoints round-trip snapshots, one-batch enqueues, and deletions", async () => {
  const { discovery, shutdown, queue } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  const sessionId = "a".repeat(32);
  try {
    assertEquals(await client.getQueue(sessionId), {
      sessionId,
      running: null,
      items: [],
      capacity: 8,
    });

    // One POST for the whole batch; the answer ids and positions are the
    // projection keys.
    const created = await client.enqueueTurns(sessionId, ["first turn", "second turn"]);
    assertEquals(created, [
      { id: "item-1", position: 1 },
      { id: "item-2", position: 2 },
    ]);
    assertEquals(queue.postedBatches(), [["first turn", "second turn"]]);

    const queued = await client.getQueue(sessionId);
    assertEquals(queued.items.map((item) => [item.id, item.text]), [
      ["item-1", "first turn"],
      ["item-2", "second turn"],
    ]);

    // A waiting item is removable; a running item refuses with the typed
    // error (cancel the run instead).
    assertEquals(queue.startNext("b".repeat(32)), "item-1");
    assertEquals((await client.getQueue(sessionId)).running, {
      itemId: "item-1",
      runId: "b".repeat(32),
    });
    await client.dequeueItem(sessionId, "item-2");
    const running = await client.dequeueItem(sessionId, "item-1")
      .then(() => null, (error: unknown) => error);
    assert(running instanceof FrontendError);
    assertEquals(running.code, "item_running");
    queue.finishRunning();
    await client.clearQueue(sessionId);
    assertEquals((await client.getQueue(sessionId)).items.length, 0);
  } finally {
    await shutdown();
  }
});

Deno.test("enqueue rejects command text and full queues with typed errors", async () => {
  const { discovery, shutdown, queue } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  const sessionId = "a".repeat(32);
  try {
    const command = await client.enqueueTurns(sessionId, ["%status"])
      .then(() => null, (error: unknown) => error);
    assert(command instanceof FrontendError);
    assertEquals(command.code, "invalid_request");

    queue.setCapacity(1);
    const full = await client.enqueueTurns(sessionId, ["one", "two"])
      .then(() => null, (error: unknown) => error);
    assert(full instanceof FrontendError);
    assertEquals(full.code, "queue_full");
    assertEquals(full.status, 409);
  } finally {
    await shutdown();
  }
});

Deno.test("task list, single read, and cancel round-trip the task plane", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  const sessionId = "a".repeat(32);
  try {
    const tasks = await client.listTasks(sessionId);
    assertEquals(tasks.length, 3);
    // The full snapshot shape parses through; the unknown authority's
    // catalog-identity entry has no status and the type leaves it optional.
    assertEquals(tasks[0].kind, "agent");
    assertEquals(tasks[0].status, "working");
    assertEquals(tasks[0].agent?.runId, "0123456789abcdef0123456789abcdef");
    assertEquals(tasks[1].terminal?.exitCode, 2);
    assertEquals(tasks[2].kind, "future");
    assertEquals(tasks[2].status, undefined);

    // Single read by authority and path id; a missed one is the typed 404.
    const terminal = await client.readTask(sessionId, "terminal", "ts-9e58c0d2");
    assertEquals(terminal.status, "fail");
    const missing = await client.readTask(sessionId, "agent", "e".repeat(32))
      .then(() => null, (error: unknown) => error);
    assert(missing instanceof FrontendError);
    assertEquals(missing.code, "resource_not_found");
    assertEquals(missing.status, 404);

    // Cancel posts the URI and answers the terminal snapshot.
    const cancelled = await client.cancelTask(sessionId, tasks[1].uri);
    assertEquals(cancelled.status, "cancel");
    const gone = await client.cancelTask(sessionId, "task://agent/x/not-here")
      .then(() => null, (error: unknown) => error);
    assert(gone instanceof FrontendError);
    assertEquals(gone.code, "resource_not_found");
  } finally {
    await shutdown();
  }
});

Deno.test("task cancel maps ownership and source failures to typed errors", async () => {
  const { discovery, shutdown } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  const sessionId = "a".repeat(32);
  try {
    // Another session's task: denial wins, always 403 task_forbidden.
    const foreign = await client.cancelTask(
      sessionId,
      `task://agent/${"f".repeat(32)}/0123456789abcdef0123456789abcdef`,
    )
      .then(() => null, (error: unknown) => error);
    assert(foreign instanceof FrontendError);
    assertEquals(foreign.code, "task_forbidden");
    assertEquals(foreign.status, 403);

    // A source that could not settle its task: 502, still typed.
    const failed = await client.cancelTask(sessionId, `task://future/${sessionId}/task-stuck`)
      .then(() => null, (error: unknown) => error);
    assert(failed instanceof FrontendError);
    assertEquals(failed.code, "task_cancel_failed");
    assertEquals(failed.status, 502);

    // A body that is not a task URI: 400.
    const invalid = await client.cancelTask(sessionId, "https://example.invalid/not-a-task")
      .then(() => null, (error: unknown) => error);
    assert(invalid instanceof FrontendError);
    assertEquals(invalid.code, "resource_invalid_uri");
    assertEquals(invalid.status, 400);
  } finally {
    await shutdown();
  }
});

Deno.test("queue.updated frames flow through the events stream unfiltered", async () => {
  const { discovery, shutdown, eventConnections } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  const sessionId = "a".repeat(32);
  try {
    const controller = new AbortController();
    const frames: { type: string; queue?: QueueState }[] = [];
    const pump = (async () => {
      for await (const frame of client.events(sessionId, { signal: controller.signal })) {
        frames.push(frame);
        if (frame.type === "queue.updated") return;
      }
    })();
    await waitFor(() => eventConnections().length === 1);
    // Unknown fields on the frame and inside the queue payload are tolerated
    // and pass through untouched.
    eventConnections()[0].send({
      type: "queue.updated",
      future: "field",
      queue: {
        sessionId,
        running: null,
        items: [{ id: "item-1", future: 7 }],
        capacity: 8,
        futureCapacity: "x",
      },
    });
    await pump;
    const frame = frames.find((candidate) => candidate.type === "queue.updated");
    assertEquals(frame?.queue?.sessionId, sessionId);
    assertEquals(frame?.queue?.capacity, 8);
    // The unknown fields ride the raw parsed value untouched (tolerated, not
    // stripped): compare the untyped items value the wire delivered.
    assertEquals(frame?.queue?.items as unknown, [{ id: "item-1", future: 7 }]);
    controller.abort();
    await new Promise((resolve) => setTimeout(resolve, 25));
  } finally {
    await shutdown();
  }
});

/** Drives one watcher over the mock's event stream until aborted. */
function pumpWatcher(
  client: FrontendClient,
  watcher: QueueWatcher,
  signal: AbortSignal,
): Promise<void> {
  return (async () => {
    for await (const frame of client.events("a".repeat(32), { signal })) {
      if (await watcher.handleFrame(frame)) continue;
    }
  })();
}

Deno.test("queue watcher reconciles once per (re)connect and applies snapshots", async () => {
  const { discovery, shutdown, eventConnections, queue } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const snapshots: QueueState[] = [];
    const watcher = new QueueWatcher(client, "a".repeat(32), (snapshot) => {
      snapshots.push(snapshot);
    }, () => {});
    const pump = pumpWatcher(client, watcher, controller.signal);

    // The connect hello reconciles with exactly one GET /queue.
    await waitFor(() => queue.getHits() === 1);
    assertEquals(watcher.lastSnapshot?.sessionId, "a".repeat(32));

    // A scripted mutation frame applies as full state.
    eventConnections()[0].send({
      type: "queue.updated",
      queue: {
        sessionId: "a".repeat(32),
        running: null,
        items: [{ id: "item-1" }, { id: "item-2" }],
        capacity: 8,
      },
    });
    await waitFor(() => watcher.lastSnapshot?.items.length === 2);

    // The reconnect hello reconciles again: one GET per (re)connect, and the
    // GET applies the server's truth — the scripted frame (as if its frame
    // had been missed) is replaced wholesale.
    eventConnections()[0].close();
    await waitFor(() => queue.getHits() === 2 && eventConnections().length === 2);
    assertEquals(eventConnections()[1].sinceSequence, 0);
    assertEquals(snapshots.at(-1)?.items.length, 0);

    controller.abort();
    await pump;
  } finally {
    await shutdown();
  }
});

Deno.test("drain waits resolve only from snapshots after the enqueue", async () => {
  const { discovery, shutdown, queue } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const watcher = new QueueWatcher(client, "a".repeat(32), () => {}, () => {});
    const pump = pumpWatcher(client, watcher, controller.signal);
    await waitFor(() => queue.getHits() === 1);

    // The wait is registered against the pre-enqueue snapshot version, so the
    // current (stale, empty) state cannot resolve it.
    const version = watcher.snapshotVersion;
    const wait = watcher.awaitDrainedAsync(version, ["item-1", "item-2"]);
    const stillWaiting = await Promise.race([
      wait.then(() => "drained" as const),
      new Promise<"pending">((resolve) => setTimeout(() => resolve("pending"), 50)),
    ]);
    assertEquals(stillWaiting, "pending");

    // The enqueue's own mutation frame postdates the wait and carries the
    // items: still waiting.
    await client.enqueueTurns("a".repeat(32), ["one", "two"]);
    await waitFor(() => watcher.lastSnapshot?.items.length === 2);
    const queuedStillWaiting = await Promise.race([
      wait.then(() => "drained" as const),
      new Promise<"pending">((resolve) => setTimeout(() => resolve("pending"), 50)),
    ]);
    assertEquals(queuedStillWaiting, "pending");

    // item-1 starts running (it still holds its id): the batch keeps waiting.
    assertEquals(queue.startNext("b".repeat(32)), "item-1");
    await waitFor(() => (watcher.lastSnapshot?.running ?? null) !== null);
    const runningStillWaiting = await Promise.race([
      wait.then(() => "drained" as const),
      new Promise<"pending">((resolve) => setTimeout(() => resolve("pending"), 50)),
    ]);
    assertEquals(runningStillWaiting, "pending");

    // item-1 settles (its id leaves), but item-2 is still queued.
    queue.finishRunning();
    await waitFor(() => watcher.lastSnapshot?.running === null);
    const settledStillWaiting = await Promise.race([
      wait.then(() => "drained" as const),
      new Promise<"pending">((resolve) => setTimeout(() => resolve("pending"), 50)),
    ]);
    assertEquals(settledStillWaiting, "pending");

    // item-2 is dequeued: every id has left the queue, so the wait drains.
    await client.dequeueItem("a".repeat(32), "item-2");
    assertEquals(await wait, true);

    controller.abort();
    await pump;
  } finally {
    await shutdown();
  }
});

Deno.test("drain waits evaluate a snapshot that beat the registration", async () => {
  const { discovery, shutdown, queue } = await startMockServer();
  const client = FrontendClient.fromDiscovery(discovery);
  try {
    const controller = new AbortController();
    const watcher = new QueueWatcher(client, "a".repeat(32), () => {}, () => {});
    const pump = pumpWatcher(client, watcher, controller.signal);
    await waitFor(() => queue.getHits() === 1);

    // The HTTP answer and the events frame race: here the enqueue frame and
    // the completion frames all apply BEFORE the waiter registers. The
    // captured version predates them, so the post-enqueue absence drains.
    const version = watcher.snapshotVersion;
    await client.enqueueTurns("a".repeat(32), ["fast turn"]);
    assertEquals(queue.startNext("b".repeat(32)), "item-1");
    queue.finishRunning();
    await waitFor(() => watcher.lastSnapshot?.items.length === 0);
    const wait = watcher.awaitDrainedAsync(version, ["item-1"]);
    assertEquals(await wait, true);

    controller.abort();
    await pump;
  } finally {
    await shutdown();
  }
});
