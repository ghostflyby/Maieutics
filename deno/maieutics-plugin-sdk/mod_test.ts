import { assertEquals, assertThrows } from "jsr:@std/assert";
import { defineExtensionPoint, ExtensionPoint } from "./mod.ts";

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
  assertEquals((impl as Record<symbol, unknown>)[ExtensionPoint.McpDiscover], true);
  assertEquals(typeof (impl as { handler(): unknown }).handler, "function");
});

Deno.test("defineExtensionPoint accepts a callable function", () => {
  const fn = () => [];
  const impl = defineExtensionPoint("McpDiscover", fn);
  assertEquals((impl as Record<symbol, unknown>)[ExtensionPoint.McpDiscover], true);
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
