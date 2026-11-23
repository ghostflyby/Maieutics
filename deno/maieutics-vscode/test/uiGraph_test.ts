/// <reference lib="deno.ns" />
import { assertEquals } from "@std/assert";
import { join } from "node:path";

const deno = new URL("../../", import.meta.url);
const retiredPackage = /(?:^|\/)(?:preact|@preact\/signals-core|maieutics-widgets)(?:\/|@|$)/;
const retiredSource =
  /\/(?:media\/(?:view|widget)Renderer\.js|maieutics-plugin-sdk\/(?:widgets\/|ui\/(?:family|runtime)\.ts))/;

for (
  const entry of [
    "maieutics-vscode/webview/conversation.ts",
    "maieutics-output-client/mod.ts",
  ]
) {
  Deno.test(`bundled browser graph contains one shared Vue runtime: ${entry}`, async () => {
    const directory = await Deno.makeTempDir();
    try {
      const output = join(directory, "probe.js");
      const command = await new Deno.Command(Deno.execPath(), {
        args: [
          "bundle",
          "--platform=browser",
          "--minify",
          "--sourcemap=external",
          "-o",
          output,
          new URL(entry, deno).href,
        ],
        signal: AbortSignal.timeout(15_000),
      }).output();
      assertEquals(command.success, true, new TextDecoder().decode(command.stderr));
      const map = JSON.parse(await Deno.readTextFile(output + ".map")) as { sources: string[] };
      const sources = map.sources.map((source) => source.replaceAll("\\", "/"));
      const renderer = sources.filter((source) =>
        source.endsWith("/maieutics-output-client/renderer.js")
      );
      assertEquals(renderer.length, 1, "Browser output must compile the shared renderer source");
      assertEquals(
        sources.filter((source) => source.endsWith("/media/outputClient.js")),
        [],
        "A prepackaged output client would hide an extra Vue runtime in this bundle",
      );
      for (const name of ["runtime-dom", "runtime-core", "reactivity", "shared"]) {
        const modules = sources.filter((source) => source.includes(`/@vue/${name}/`));
        assertEquals(modules.length, 1, `Expected one Vue ${name} module: ${modules.join(", ")}`);
      }
      assertEquals(
        sources.filter((source) => retiredPackage.test(source) || retiredSource.test(source)),
        [],
        "Retired renderer dependencies are present in the bundle",
      );
    } finally {
      await Deno.remove(directory, { recursive: true });
    }
  });
}

Deno.test("public UI entry points exclude the retired rendering engine", async () => {
  const json = async <T>(path: string): Promise<T> =>
    JSON.parse(await Deno.readTextFile(new URL(path, deno))) as T;
  const [workspace, sdk, extension] = await Promise.all([
    json<{ imports: Record<string, string> }>("deno.json"),
    json<{ exports: Record<string, string> }>("maieutics-plugin-sdk/deno.json"),
    json<{
      contributes: {
        notebookRenderer: { entrypoint: string | { path: string } }[];
      };
    }>("maieutics-vscode/package.json"),
  ]);
  assertEquals(Object.keys(workspace.imports).filter((key) => retiredPackage.test(key)), []);
  assertEquals(Object.keys(sdk.exports).filter((key) => /^\.\/widgets(?:\/|$)/.test(key)), []);
  assertEquals(
    extension.contributes.notebookRenderer.map(({ entrypoint }) =>
      typeof entrypoint === "string" ? entrypoint : entrypoint.path
    ).filter((path) => retiredSource.test(path)),
    [],
  );
});
