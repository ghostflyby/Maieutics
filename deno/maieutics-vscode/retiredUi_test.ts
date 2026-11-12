/// <reference lib="deno.ns" />
import { assertEquals, assertRejects } from "@std/assert";

Deno.test("the shipped UI graph has one Vue client and no retired component engine or authoring imports", async () => {
  const workspace = JSON.parse(await Deno.readTextFile(new URL("../deno.json", import.meta.url)));
  const sdk = JSON.parse(
    await Deno.readTextFile(new URL("../maieutics-plugin-sdk/deno.json", import.meta.url)),
  );
  const manifest = JSON.parse(await Deno.readTextFile(new URL("package.json", import.meta.url)));
  const lock = JSON.parse(await Deno.readTextFile(new URL("../deno.lock", import.meta.url)));
  assertEquals(sdk.version, "0.3.0");
  for (const key of ["preact", "maieutics-widgets", "maieutics-widgets/jsx-runtime"]) {
    assertEquals(key in workspace.imports, false);
  }
  for (const key of Object.keys(sdk.exports)) assertEquals(key.startsWith("./widgets"), false);
  assertEquals(Object.keys(lock.specifiers).some((key) => /preact|signals-core/.test(key)), false);
  assertEquals(manifest.contributes.notebookRenderer.map((entry: { id: string }) => entry.id), [
    "maieutics-turn-renderer",
    "maieutics-form-renderer",
    "maieutics-markdown-safety",
  ]);
  for (
    const path of [
      "renderer",
      "media/viewRenderer.js",
      "media/widgetRenderer.js",
      "../maieutics-plugin-sdk/widgets",
      "../maieutics-plugin-sdk/ui/family.ts",
      "../maieutics-plugin-sdk/ui/runtime.ts",
    ]
  ) {
    await assertRejects(() => Deno.stat(new URL(path, import.meta.url)), Deno.errors.NotFound);
  }
  const bundle = await Deno.readTextFile(new URL("media/outputClient.js", import.meta.url));
  assertEquals(bundle.includes("registerViewFamily"), false);
  assertEquals(bundle.includes("collectDeps"), false);
});
