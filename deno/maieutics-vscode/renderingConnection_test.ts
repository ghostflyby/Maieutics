/// <reference lib="deno.ns" />
import { assertEquals, assertRejects } from "@std/assert";
import { type Connection, renderingConnection } from "./connection.ts";
import { FrontendClient } from "./client.ts";

function connection(): Connection {
  return {
    client: FrontendClient.fromDiscovery({
      version: 1,
      url: "http://127.0.0.1:9",
      token: "fixture",
    }),
    isDisposed: false,
    capabilities: () =>
      Promise.resolve({
        protocolVersion: 1,
        serverVersion: "test",
        session: { id: "a".repeat(32), turns: 0, persistenceEnabled: false },
      }),
    syncProjects: () => Promise.resolve(),
    dispose: () => Promise.resolve(),
  };
}

Deno.test("rendering joins existing startup and keeps offline opening disconnected", async () => {
  const lifetime = new AbortController();
  assertEquals(await renderingConnection(undefined, undefined, lifetime.signal), undefined);
  const starting = Promise.withResolvers<Connection>();
  const ready = connection();
  const rendering = renderingConnection(undefined, starting.promise, lifetime.signal);
  starting.resolve(ready);
  assertEquals(await rendering, ready);
  assertEquals(await renderingConnection(ready, undefined, lifetime.signal), ready);
});

Deno.test("cancelling rendering leaves connection startup owned and observes its failure", async () => {
  const lifetime = new AbortController();
  const starting = Promise.withResolvers<Connection>();
  const waiting = renderingConnection(undefined, starting.promise, lifetime.signal);
  const cancelled = assertRejects(() => waiting, DOMException, "cancelled rendering");
  lifetime.abort(new DOMException("cancelled rendering", "AbortError"));
  await cancelled;
  starting.reject(new Error("startup failed after cancelled rendering"));
  await assertRejects(() => starting.promise, Error, "startup failed");
  await assertRejects(
    () => renderingConnection(undefined, undefined, lifetime.signal),
    DOMException,
    "cancelled rendering",
  );
});

Deno.test("rendering propagates an existing startup failure to its fallback boundary", async () => {
  const lifetime = new AbortController();
  await assertRejects(
    () =>
      renderingConnection(undefined, Promise.reject(new Error("startup failed")), lifetime.signal),
    Error,
    "startup failed",
  );
});
