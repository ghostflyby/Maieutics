/// <reference lib="deno.ns" />

import { assert, AssertionError, assertThrows } from "@std/assert";
import { parse } from "acorn";
import { fileURLToPath } from "node:url";

/**
 * Structural regression gate for the notebook-renderer entrypoints (the
 * renderer ESM incident): VS Code loads a renderer entrypoint with a native
 * dynamic `import()`, so every media path declared under
 * `contributes.notebookRenderer` in package.json must export `activate` as
 * an ES module. Inspect syntax without evaluating browser code: substrings
 * such as `module.exports` or a comment mentioning `export` prove nothing.
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

function assertRendererModule(source: string, label: string): void {
  const module = parse(source, { ecmaVersion: "latest", sourceType: "module" });
  const exportsActivate = module.body.some((node) => {
    if (node.type !== "ExportNamedDeclaration") return false;
    if (
      node.specifiers.some(({ exported }) =>
        exported.type === "Identifier"
          ? exported.name === "activate"
          : exported.value === "activate"
      )
    ) return true;
    const declaration = node.declaration;
    if (declaration?.type === "FunctionDeclaration") return declaration.id?.name === "activate";
    return declaration?.type === "VariableDeclaration" &&
      declaration.declarations.some(({ id }) => id.type === "Identifier" && id.name === "activate");
  });
  assert(exportsActivate, `${label} has an ESM named export 'activate'`);
}

Deno.test("renderer gate accepts direct and aliased activate exports", () => {
  for (
    const source of [
      "export function activate() {}",
      "export const activate = () => {};",
      "function a() {} export { a as activate };",
      'function a() {} export { a as "activate" };',
      'export function activate() {} const text = "exports.activate";',
    ]
  ) {
    assertRendererModule(source, "ESM fixture");
  }
});

Deno.test("renderer gate rejects CJS, misleading text, and unrelated exports", () => {
  for (
    const source of [
      "exports.activate = function () {};",
      "module.exports = { activate() {} };",
      "// export function activate() {}\nconst activate = () => {};",
      'const text = "export { activate }";',
      "function activate() {} export const other = 1;",
      "export default function activate() {}",
      "function activate() {} export { activate as other };",
    ]
  ) {
    assertThrows(
      () => assertRendererModule(source, "non-renderer fixture"),
      AssertionError,
      "has an ESM named export 'activate'",
    );
  }
});

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
    assertRendererModule(source, `${renderer.id} (${entrypoint})`);
  }
});

Deno.test("renderer gate rejects real minified CJS bundles", async () => {
  const moduleRoot = new URL(".", import.meta.url);
  const scratch = await Deno.makeTempDir({ prefix: "maieutics-renderer-cjs-" });
  try {
    for (const entry of ["widgetEntry", "viewEntry"]) {
      const outputPath = `${scratch}/${entry}.cjs`;
      const output = await new Deno.Command(Deno.execPath(), {
        args: [
          "bundle",
          "--format",
          "cjs",
          "--minify",
          "--output",
          outputPath,
          `renderer/${entry}.ts`,
        ],
        cwd: fileURLToPath(moduleRoot),
        stdin: "null",
        stdout: "piped",
        stderr: "piped",
      }).output();
      assert(output.success, new TextDecoder().decode(output.stderr));
      const source = await Deno.readTextFile(outputPath);
      assertThrows(
        () => assertRendererModule(source, entry),
        AssertionError,
        "has an ESM named export 'activate'",
      );
    }
  } finally {
    await Deno.remove(scratch, { recursive: true });
  }
});
