/// <reference lib="deno.ns" />

import { assert } from "@std/assert";

/**
 * Structural regression gate for the notebook-renderer entrypoints (the
 * renderer ESM incident): VS Code loads a renderer entrypoint with a native
 * dynamic `import()`, so every media path declared under
 * `contributes.notebookRenderer` in package.json must be a real ES module —
 * its source carries `export` statements — and must never be a CJS bundle
 * that assigns `exports.activate` (that shape loads but activates nothing).
 * This runs with the ordinary `deno task test` suite, so the accident fails
 * before any real VS Code has to load the renderer.
 */

interface NotebookRendererContribution {
  id: string;
  entrypoint: unknown;
}

interface ExtensionManifest {
  contributes?: { notebookRenderer?: NotebookRendererContribution[] };
}

Deno.test("notebook renderer entrypoints are ESM modules, not CJS bundles", async () => {
  const moduleRoot = new URL(".", import.meta.url);
  const manifest = JSON.parse(
    await Deno.readTextFile(new URL("package.json", moduleRoot)),
  ) as ExtensionManifest;
  const renderers = manifest.contributes?.notebookRenderer ?? [];
  assert(renderers.length > 0, "package.json contributes at least one notebookRenderer");

  for (const renderer of renderers) {
    assert(
      typeof renderer.entrypoint === "string",
      `${renderer.id} declares a string entrypoint`,
    );
    const entrypoint = renderer.entrypoint as string;
    const source = await Deno.readTextFile(new URL(entrypoint, moduleRoot));
    assert(
      source.includes("export"),
      `${renderer.id} (${entrypoint}) contains an export statement (ESM)`,
    );
    assert(
      !source.includes("exports.activate"),
      `${renderer.id} (${entrypoint}) is not a CJS bundle assigning exports.activate`,
    );
  }
});
