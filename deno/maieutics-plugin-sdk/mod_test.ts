import { assert, assertEquals, assertThrows } from "@std/assert";
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

Deno.test("the sdk module imports only worker-actor, signals-core and its local modules", async () => {
  const source = await Deno.readTextFile(new URL("./mod.ts", import.meta.url));
  const imports = [...source.matchAll(/^import[^\n]*?from\s+"([^"]+)"/gm)].map((match) => match[1]);
  assert(imports.length > 0, "expected at least one import in the sdk module");
  for (const specifier of imports) {
    assert(
      specifier === "./actor_ref.ts" ||
        specifier === "./collection_stream.ts" ||
        specifier === "./reactive.ts" ||
        specifier === "@ghostflyby/worker-actor" ||
        specifier.startsWith("@ghostflyby/worker-actor/") ||
        specifier === "@preact/signals-core",
      `unexpected import '${specifier}' in the sdk module`,
    );
  }
});
