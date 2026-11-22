import { assertEquals, assertRejects } from "@std/assert";
import { FrontendClient } from "./client.ts";
import { FrontendError } from "./protocol.ts";

Deno.test("conversation client keeps credentials in the host, addresses real pages and closes its stream on abort", async () => {
  const closed = Promise.withResolvers<void>();
  const owner = new AbortController();
  const requests: string[] = [];
  const snapshot = { sessionId: "session", epoch: "epoch", revision: "7", total: 100, entries: [] };
  const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, (request) => {
    const url = new URL(request.url);
    if (url.pathname.endsWith("/events")) {
      assertEquals(url.searchParams.get("token"), "host-secret");
      const { socket, response } = Deno.upgradeWebSocket(request);
      socket.onopen = () => socket.send(JSON.stringify({ type: "view.snapshot", page: snapshot }));
      socket.onclose = () => closed.resolve();
      return response;
    }
    assertEquals(request.headers.get("Authorization"), "Bearer host-secret");
    requests.push(url.pathname + url.search);
    return Response.json(
      url.pathname.endsWith("/entries")
        ? snapshot
        : { entryId: "run:message", revision: "8", text: "中文正文" },
    );
  });
  const client = FrontendClient.fromDiscovery({
    version: 1,
    url: `http://127.0.0.1:${server.addr.port}`,
    token: "host-secret",
    pid: 1,
  });
  const events = client.conversationEvents("session", owner.signal);
  try {
    assertEquals((await events.next()).value, { type: "view.snapshot", page: snapshot });
    assertEquals(await client.conversationPage("session", "epoch", 40, 80, owner.signal), snapshot);
    assertEquals(
      await client.conversationContent("session", "epoch", "run:message", "8", owner.signal),
      { entryId: "run:message", revision: "8", text: "中文正文" },
    );
    assertEquals(requests, [
      "/v1/agent/sessions/session/view/entries?epoch=epoch&limit=80&offset=40",
      "/v1/agent/sessions/session/view/blocks/run%3Amessage?epoch=epoch&revision=8",
    ]);
    const next = events.next();
    owner.abort(new FrontendError("view_closed", 0, "View closed"));
    await assertRejects(() => next, FrontendError, "View closed");
    await closed.promise;
  } finally {
    owner.abort();
    await events.return(undefined);
    await server.shutdown();
  }
});
