import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import {
  createActorCaller,
  defineExtensionPoint,
  ExtensionPoint,
  serveActor,
} from "./mod.ts";

Deno.test("global symbols are shared across any module instance", () => {
  assertEquals(
    Symbol.for("maieutics/extensionPoint/v1/tools.preInvoke"),
    ExtensionPoint.ToolPreInvoke,
  );
});

Deno.test("defineExtensionPoint accepts an object with a handler", () => {
  const impl = defineExtensionPoint("McpDiscover", {
    handler: () => [],
  });
  assertEquals(
    (impl as Record<symbol, unknown>)[ExtensionPoint.McpDiscover],
    true,
  );
  assertEquals(typeof (impl as { handler(): unknown }).handler, "function");
});

Deno.test("defineExtensionPoint accepts a callable function", () => {
  const fn = () => [];
  const impl = defineExtensionPoint("McpDiscover", fn);
  assertEquals(
    (impl as Record<symbol, unknown>)[ExtensionPoint.McpDiscover],
    true,
  );
  assertEquals(typeof impl, "function");
});

Deno.test("defineExtensionPoint rejects a bare object without a handler", () => {
  assertThrows(
    () => defineExtensionPoint("ToolPreInvoke", { marker: true } as never),
    TypeError,
    "handler",
  );
});

Deno.test("the sdk module is self-contained and imports nothing", async () => {
  const source = await Deno.readTextFile(new URL("./mod.ts", import.meta.url));
  const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) => match[1]);
  assertEquals(imports, []);
});

Deno.test("serveActor dispatches calls and values over a port", async () => {
  const channel = new MessageChannel();
  const surface = {
    add: (a: number, b: number) => a + b,
    greeting: "hello",
  };
  const detach = serveActor(channel.port1, surface);
  try {
    const port = channel.port2;
    port.start();
    const responses: unknown[] = [];
    port.onmessage = (event: MessageEvent) => responses.push(event.data);
    port.postMessage({
      __maieuticsRpc: "call",
      id: 1,
      path: ["add"],
      args: [2, 3],
    });
    port.postMessage({
      __maieuticsRpc: "call",
      id: 2,
      path: ["greeting"],
      args: [],
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assertEquals(responses.length, 2);
    const call = responses[0] as { __maieuticsRpc: string; id: number; value: unknown };
    assertEquals(call.__maieuticsRpc, "return");
    assertEquals(call.id, 1);
    assertEquals(call.value, 5);
    const value = responses[1] as { __maieuticsRpc: string; value: unknown };
    assertEquals(value.value, "hello");
  } finally {
    detach();
    channel.port1.close();
    channel.port2.close();
  }
});

Deno.test("serveActor reports handler failures as throw frames", async () => {
  const channel = new MessageChannel();
  const detach = serveActor(channel.port1, {
    boom: () => {
      throw new Error("kaboom");
    },
  });
  try {
    const port = channel.port2;
    port.start();
    const responses: unknown[] = [];
    port.onmessage = (event: MessageEvent) => responses.push(event.data);
    port.postMessage({
      __maieuticsRpc: "call",
      id: 7,
      path: ["boom"],
      args: [],
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    const frame = responses[0] as { __maieuticsRpc: string; id: number; message: string };
    assertEquals(frame.__maieuticsRpc, "throw");
    assertEquals(frame.id, 7);
    assertEquals(frame.message, "kaboom");
  } finally {
    detach();
    channel.port1.close();
    channel.port2.close();
  }
});

Deno.test("createActorCaller resolves values and rejects errors", async () => {
  const channel = new MessageChannel();
  const detach = serveActor(channel.port1, {
    add: (a: number, b: number) => a + b,
    fail: () => {
      throw new Error("nope");
    },
  });
  try {
    const caller = createActorCaller(async () => channel.port2);
    assertEquals(await caller(["add"], [1, 2]), 3);
    await assertRejects(() => caller(["fail"], []), Error, "nope");
  } finally {
    detach();
    channel.port1.close();
    channel.port2.close();
  }
});
