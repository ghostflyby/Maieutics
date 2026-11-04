/// <reference lib="deno.window" />
import { assertEquals, assertRejects } from "@std/assert";
import { FrontendClient } from "./client.ts";
import { FrontendError } from "./protocol.ts";

Deno.test("render client correlates and validates optional SSR snapshots independently of turns", async () => {
  let malformed = false;
  const received: unknown[] = [];
  const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    assertEquals(request.headers.get("authorization"), "Bearer test-token");
    assertEquals(new URL(request.url).pathname, "/v1/render/markdown");
    received.push(await request.json());
    return Response.json(
      malformed ? null : {
        version: 1,
        documentId: "doc",
        html: "<p>SSR</p>",
        css: ".maieutics-output {}",
        future: true,
      },
    );
  });
  try {
    const client = FrontendClient.fromDiscovery({
      version: 1,
      url: `http://127.0.0.1:${server.addr.port}`,
      token: "test-token",
    });
    assertEquals(await client.renderMarkdown("doc", "**SSR**"), {
      version: 1,
      documentId: "doc",
      html: "<p>SSR</p>",
      css: ".maieutics-output {}",
    });
    assertEquals(received, [{ version: 1, documentId: "doc", source: "**SSR**" }]);
    malformed = true;
    const error = await assertRejects(() => client.renderMarkdown("doc", "next"), FrontendError);
    assertEquals(error.code, "protocol_error");
  } finally {
    await server.shutdown();
  }
});
