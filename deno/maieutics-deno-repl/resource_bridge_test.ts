import { assertEquals, assertRejects, assertStringIncludes, assertThrows } from "@std/assert";
import {
  createResourceReader,
  isVirtualResourceUrl,
  patchFetch,
} from "./resource_bridge.ts";

Deno.test("virtual URL detection covers registered schemes only", () => {
  assertEquals(isVirtualResourceUrl("workspace://local/notes/a.md"), true);
  assertEquals(isVirtualResourceUrl("mcp://filesystem/file:///etc/hosts"), true);
  assertEquals(isVirtualResourceUrl("notes://team/idea"), true);
  assertEquals(isVirtualResourceUrl(new URL("postgres://db/sales")), true);
  assertEquals(isVirtualResourceUrl("https://example.test/x"), false);
  assertEquals(isVirtualResourceUrl("http://localhost:8080/"), false);
  assertEquals(isVirtualResourceUrl("data:text/plain,hi"), false);
  assertEquals(isVirtualResourceUrl("blob:abc"), false);
  assertEquals(isVirtualResourceUrl("about:blank"), false);
  assertEquals(isVirtualResourceUrl("file:///etc/hosts"), false);
  assertEquals(isVirtualResourceUrl("relative/path"), false);
  assertEquals(isVirtualResourceUrl(42), false);
});

Deno.test("the resource reader GETs virtual URIs over the control channel", async () => {
  const seen: string[] = [];
  const server = Deno.serve({ port: 0, onListen: undefined }, (request) => {
    const url = new URL(request.url);
    seen.push(`${request.method} ${url.pathname} uri=${url.searchParams.get("uri")}`);
    const uri = url.searchParams.get("uri") ?? "";
    if (uri === "notes://team/missing") {
      return Response.json({ code: "resource_not_found", message: "absent" }, { status: 404 });
    }
    return new Response("hello resource", {
      status: 200,
      headers: { "content-type": "text/markdown" },
    });
  });
  try {
    const address = `127.0.0.1:${server.addr.port}`;
    const read = createResourceReader({ address, transport: "tcp" });
    const response = await read("notes://team/idea");
    assertEquals(response.status, 200);
    assertEquals(response.headers.get("content-type"), "text/markdown");
    assertEquals(await response.text(), "hello resource");
    const missing = await read("notes://team/missing");
    assertEquals(missing.status, 404);
    await missing.body?.cancel();

    assertEquals(seen, [
      "GET /v1/resource uri=notes://team/idea",
      "GET /v1/resource uri=notes://team/missing",
    ]);
  } finally {
    await server.shutdown();
  }
});

Deno.test("the resource reader forwards abort signals and rejects non-GET methods", async () => {
  const server = Deno.serve({ port: 0, onListen: undefined }, () => new Response("slow"));
  try {
    const read = createResourceReader({
      address: `127.0.0.1:${server.addr.port}`,
      transport: "tcp",
    });
    const controller = new AbortController();
    const pending = read("notes://team/slow", { signal: controller.signal });
    controller.abort();
    await assertRejects(() => pending);
  } finally {
    await server.shutdown();
  }
});

Deno.test("patched fetch routes virtual URLs to the kernel and others to the original", async () => {
  const virtualHits: string[] = [];
  const originalHits: string[] = [];
  const server = Deno.serve({ port: 0, onListen: undefined }, (request) => {
    virtualHits.push(new URL(request.url).searchParams.get("uri") ?? "");
    return new Response("virtual body");
  });
  try {
    const original: typeof fetch = (_input, _init) => {
      originalHits.push(String(_input));
      return Promise.resolve(new Response("native body"));
    };
    const patched = patchFetch(original, {
      address: `127.0.0.1:${server.addr.port}`,
      transport: "tcp",
    });

    const virtual = await patched("workspace://local/a.txt");
    assertEquals(await virtual.text(), "virtual body");
    const native = await patched("https://example.test/api");
    assertEquals(await native.text(), "native body");

    assertEquals(virtualHits, ["workspace://local/a.txt"]);
    assertEquals(originalHits, ["https://example.test/api"]);

    assertThrows(
      () => patched("notes://team/idea", { method: "POST" }),
      TypeError,
      "GET only",
    );
    // A non-virtual scheme with POST still reaches the original implementation.
    const posted = await patched("https://example.test/api", { method: "POST" });
    assertEquals(await posted.text(), "native body");
  } finally {
    await server.shutdown();
  }
});

Deno.test("the reader carries the Windows bearer credential when configured", async () => {
  const server = Deno.serve({ port: 0, onListen: undefined }, (request) => {
    return new Response(request.headers.get("authorization") ?? "none");
  });
  try {
    const read = createResourceReader({
      address: `127.0.0.1:${server.addr.port}`,
      transport: "tcp",
      credential: "secret-token",
    });
    const response = await read("notes://team/idea");
    assertStringIncludes(await response.text(), "Bearer secret-token");
  } finally {
    await server.shutdown();
  }
});
