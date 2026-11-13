/// <reference lib="deno.ns" />
/// <reference lib="deno.window" />
/// <reference lib="es2024.promise" />
import { encodeCommEnvelope } from "../../shared/comm_codec.ts";

// A real WebSocket fixture for the activated extension's per-notebook form routing.
const stop = Promise.withResolvers<void>();
const lifetime = new AbortController();
const sockets = new Map<string, WebSocket>();
const closed = new Map<string, ReturnType<typeof Promise.withResolvers<void>>>();
const openingEvents: string[] = [];
let openingReady = Promise.withResolvers<void>();
let foregroundReads = 0;
const barrier = (map: typeof closed, id: string) => {
  let value = map.get(id);
  if (!value) {
    value = Promise.withResolvers<void>();
    map.set(id, value);
  }
  return value;
};
const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
  const url = new URL(request.url);
  const json = (value: unknown) => Response.json(value);
  if (url.pathname === "/test/stop") {
    stop.resolve();
    return json({ ok: true });
  }
  if (url.pathname === "/test/stats") {
    return json({ sessions: [...sockets.keys()], foregroundReads });
  }
  if (url.pathname === "/test/wait" || url.pathname === "/test/next-open") {
    const id = url.searchParams.get("id") ?? "";
    const ready = url.pathname === "/test/next-open" ? openingReady : barrier(closed, id);
    const aborted = Promise.withResolvers<never>();
    const abort = () => aborted.reject(new DOMException("Fixture request cancelled", "AbortError"));
    request.signal.addEventListener("abort", abort, { once: true });
    lifetime.signal.addEventListener("abort", abort, { once: true });
    try {
      await Promise.race([ready.promise, aborted.promise]);
      if (url.pathname === "/test/next-open") {
        const sessionId = openingEvents.shift();
        if (openingEvents.length === 0) openingReady = Promise.withResolvers<void>();
        return json({ sessionId });
      }
      return json({ ok: true });
    } finally {
      request.signal.removeEventListener("abort", abort);
      lifetime.signal.removeEventListener("abort", abort);
    }
  }
  const comm = /^\/v1\/agent\/sessions\/([a-f\d]{32})\/comms$/.exec(url.pathname);
  if (comm) {
    const id = comm[1];
    const { socket, response } = Deno.upgradeWebSocket(request);
    socket.onopen = () => {
      console.error(`Form fixture socket opened: ${id}`);
      openingEvents.push(id);
      openingReady.resolve();
      sockets.set(id, socket);
      socket.send(JSON.stringify({ live: [], replayed: false, truncated: false }));
      socket.send(encodeCommEnvelope(1, {
        kind: 0,
        commId: "scoped-form",
        targetName: "maieutics.form",
        data: { state: { title: id, fields: [], values: {} } },
        buffers: [],
      }));
    };
    socket.onclose = () => {
      console.error(`Form fixture socket closed: ${id}`);
      if (sockets.get(id) === socket) sockets.delete(id);
      barrier(closed, id).resolve();
    };
    return response;
  }
  const session = { id: "f".repeat(32), turns: 0, persistenceEnabled: false };
  if (url.pathname === "/v1/agent/capabilities") {
    return json({
      protocolVersion: 1,
      workspaceRoot: "/fixture-agent-home",
      multiSession: true,
      comm: { version: 1 },
      ui: { version: 2, formVersion: 1 },
      session,
    });
  }
  if (url.pathname === "/v1/agent/session") {
    foregroundReads++;
    return json(session);
  }
  if (url.pathname === "/v1/agent/sessions") return json({ sessions: [] });
  if (url.pathname === "/v1/status" || url.pathname === "/v1/agent/commands") {
    return json({ markdown: "Ready" });
  }
  return new Response("Not found", { status: 404 });
});
console.log(
  JSON.stringify({
    version: 1,
    url: `http://127.0.0.1:${server.addr.port}`,
    token: "form-session-fixture",
  }),
);
try {
  await stop.promise;
} finally {
  lifetime.abort();
  for (const socket of sockets.values()) socket.close();
  await server.shutdown();
}
