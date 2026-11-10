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

Deno.test("template clients select the explicit endpoint and preserve the undefined discriminator", async () => {
  let received: unknown;
  const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    assertEquals(new URL(request.url).pathname, "/v1/render/template");
    received = await request.json();
    return Response.json({ version: 1, documentId: "doc", html: "", css: "" });
  });
  try {
    const client = FrontendClient.fromDiscovery({
      version: 1,
      url: `http://127.0.0.1:${server.addr.port}`,
      token: "test",
    });
    const template = {
      version: 1 as const,
      bindings: [{ name: "empty", state: "ready" as const, undefined: true }],
    };
    await client.renderMarkdown("doc", "{{ empty }}", undefined, template);
    assertEquals(received, { version: 1, documentId: "doc", source: "{{ empty }}", template });
  } finally {
    await server.shutdown();
  }
});

Deno.test("Vue client selects the live endpoint and rejects a component for another scope", async () => {
  let scopeId = "data-v-doc";
  const received: unknown[] = [];
  const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    assertEquals(new URL(request.url).pathname, "/v1/render/vue");
    received.push(await request.json());
    return Response.json({
      version: 1,
      documentId: "doc",
      html: "<p>0</p>",
      css: "",
      vue: { version: 1, code: "return {};", scopeId },
    });
  });
  try {
    const client = FrontendClient.fromDiscovery({
      version: 1,
      url: `http://127.0.0.1:${server.addr.port}`,
      token: "test",
    });
    const vue = { version: 1 as const, format: "sfc" as const };
    const result = await client.renderMarkdown("doc", "source", undefined, undefined, vue);
    assertEquals(result.vue?.scopeId, "data-v-doc");
    assertEquals(received, [{ version: 1, documentId: "doc", source: "source", vue }]);
    scopeId = "data-v-another";
    const failure = await assertRejects(
      () => client.renderMarkdown("doc", "source", undefined, undefined, vue),
      FrontendError,
    );
    assertEquals(failure.code, "protocol_error");
  } finally {
    await server.shutdown();
  }
});

Deno.test("assistant client explicitly selects the live route and accepts optional compiled Vue", async () => {
  let component = true;
  const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    assertEquals(new URL(request.url).pathname, "/v1/render/assistant");
    assertEquals(await request.json(), { version: 1, documentId: "doc", source: "source" });
    return Response.json({
      version: 1,
      documentId: "doc",
      html: "<p>0</p>",
      css: "",
      ...(component ? { vue: { version: 1, code: "return {};", scopeId: "data-v-doc" } } : {}),
    });
  });
  try {
    const client = FrontendClient.fromDiscovery({
      version: 1,
      url: `http://127.0.0.1:${server.addr.port}`,
      token: "test",
    });
    assertEquals(
      (await client.renderMarkdown("doc", "source", undefined, undefined, undefined, true)).vue
        ?.version,
      1,
    );
    component = false;
    assertEquals(
      (await client.renderMarkdown("doc", "source", undefined, undefined, undefined, true)).vue,
      undefined,
    );
  } finally {
    await server.shutdown();
  }
});

Deno.test("progressive wire fixtures preserve render-only code and reject unrelated source prefixes", async () => {
  let accepted = "source";
  let received: unknown;
  const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    assertEquals(new URL(request.url).pathname, "/v1/render/assistant");
    received = await request.json();
    return Response.json({
      version: 1,
      documentId: "doc",
      html: "",
      css: "",
      renderedSource: accepted,
      vue: {
        version: 1,
        scopeId: "data-v-doc",
        code: "",
        setupKey: "a".repeat(64),
        renderCode: "return function(){};",
      },
    });
  });
  try {
    const client = FrontendClient.fromDiscovery({
      version: 1,
      url: `http://127.0.0.1:${server.addr.port}`,
      token: "test",
    });
    const result = await client.renderMarkdown(
      "doc",
      "source extended",
      undefined,
      undefined,
      undefined,
      true,
      { compileOnly: true },
    );
    assertEquals(received, {
      version: 1,
      documentId: "doc",
      source: "source extended",
      progressive: true,
      compileOnly: true,
    });
    assertEquals(result.vue?.code, "");
    assertEquals(result.renderedSource, "source");
    accepted = "other document";
    await assertRejects(
      () =>
        client.renderMarkdown("doc", "source extended", undefined, undefined, undefined, true, {
          compileOnly: true,
        }),
      FrontendError,
    );
  } finally {
    await server.shutdown();
  }
});
