import { assert, assertEquals, assertThrows } from "@std/assert";
import { defineExtensionPoint, ExtensionPoint, type McpAdjustedTool } from "./entry.ts";

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

Deno.test("defineExtensionPoint marks McpAdjust as a known extension point", () => {
  const impl = defineExtensionPoint("McpAdjust", {
    handler(): McpAdjustedTool[] {
      return [];
    },
  });
  assertEquals(
    (impl as Record<symbol, unknown>)[ExtensionPoint.McpAdjust],
    true,
  );
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

Deno.test("SystemPrompt supports typed callable and object hooks with the shared marker", async () => {
  const fn = defineExtensionPoint("SystemPrompt", ({ workspaceRoot }) => [
    { name: "project", content: `Workspace: ${workspaceRoot}` },
  ]);
  const object = defineExtensionPoint("SystemPrompt", {
    handler: async () => await Promise.resolve([{ name: "style", content: "Use concise prose." }]),
  });
  assertEquals(
    ExtensionPoint.SystemPrompt,
    Symbol.for("maieutics/extensionPoint/v1/system.prompt"),
  );
  assertEquals((fn as Record<symbol, unknown>)[ExtensionPoint.SystemPrompt], true);
  assertEquals((object as Record<symbol, unknown>)[ExtensionPoint.SystemPrompt], true);
  assert(typeof fn === "function");
  assert(typeof object !== "function");
  assertEquals(await fn({ workspaceRoot: "/workspace" }), [
    { name: "project", content: "Workspace: /workspace" },
  ]);
  assertEquals(await object.handler({ workspaceRoot: null }), [
    { name: "style", content: "Use concise prose." },
  ]);
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
        specifier === "./http.ts" ||
        specifier === "./admission.ts" ||
        specifier === "./http_codec.ts" ||
        specifier === "@ghostflyby/worker-actor" ||
        specifier.startsWith("@ghostflyby/worker-actor/") ||
        specifier === "@preact/signals-core",
      `unexpected import '${specifier}' in the sdk module`,
    );
  }
});
