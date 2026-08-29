import { assertEquals, assertInstanceOf } from "@std/assert";
import { httpCodec } from "./http_codec.ts";

Deno.test("http codec round-trips a request and preserves projected signal", () => {
  const controller = new AbortController();
  const encoded = httpCodec.encode(new Request("https://plugin.invalid/a", {
    method: "POST",
    headers: { "x-test": "yes" },
    body: "payload",
    signal: controller.signal,
  }), {
    transport: { kind: "messageport" } as never,
    transfer: [],
    seen: new WeakMap(),
    codecState: new Map(),
    registry: {
      encode(value: unknown): unknown {
        return value;
      },
    } as never,
  });
  const decoded = httpCodec.decode(encoded, {
    transport: { kind: "messageport" } as never,
    seen: new WeakMap(),
    codecState: new Map(),
    registry: {
      decode(value: unknown): unknown {
        return value;
      },
    } as never,
  });
  assertInstanceOf(decoded, Request);
  assertEquals(decoded.method, "POST");
  assertEquals(decoded.headers.get("x-test"), "yes");
  assertEquals(decoded.signal.aborted, false);
});

Deno.test("http codec round-trips response metadata", () => {
  const encoded = httpCodec.encode(new Response("ok", {
    status: 201,
    statusText: "Created",
    headers: { "content-type": "text/plain" },
  }), {
    transport: { kind: "messageport" } as never,
    transfer: [],
    seen: new WeakMap(),
    codecState: new Map(),
    registry: { encode(value: unknown): unknown { return value; } } as never,
  });
  const decoded = httpCodec.decode(encoded, {
    transport: { kind: "messageport" } as never,
    seen: new WeakMap(),
    codecState: new Map(),
    registry: { decode(value: unknown): unknown { return value; } } as never,
  });
  assertInstanceOf(decoded, Response);
  assertEquals(decoded.status, 201);
  assertEquals(decoded.statusText, "Created");
});
