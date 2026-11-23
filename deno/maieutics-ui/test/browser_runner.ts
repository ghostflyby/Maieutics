import { bundleBrowserEntry, runInBrowser } from "../../test/browser_runner.ts";
import { applyMutation, type ScrollMutationCase } from "./evidence.ts";

/** Exercise the standalone component artifact; mutations use a temporary module overlay. */
export function runBrowserSuite(
  options: { mutation?: ScrollMutationCase; signal?: AbortSignal } = {},
): Promise<string[]> {
  return runInBrowser({
    executable: Deno.env.get("MAIEUTICS_UI_BROWSER"),
    signal: options.signal,
    async prepare(directory, signal) {
      const importArgs: string[] = [];
      if (options.mutation) {
        const root = new URL("../", import.meta.url);
        const original = new URL(options.mutation.mutation.file, root);
        const overlayRoot = new URL("file:///");
        overlayRoot.pathname = `${directory.replaceAll("\\", "/")}/overlay/`;
        await Deno.mkdir(new URL("vue/", overlayRoot), { recursive: true });
        const overlay = new URL(options.mutation.mutation.file, overlayRoot);
        await Deno.writeTextFile(
          overlay,
          applyMutation(await Deno.readTextFile(original), options.mutation.mutation),
        );
        const config = JSON.parse(await Deno.readTextFile(new URL("deno.json", root)));
        const map = `${directory}/import-map.json`;
        await Deno.writeTextFile(
          map,
          JSON.stringify({
            imports: {
              ...config.imports,
              [original.href]: overlay.href,
              [overlayRoot.href]: root.href,
            },
          }),
        );
        importArgs.push("--import-map", map);
      }
      const bundle = (entry: string, output: string) =>
        bundleBrowserEntry(
          new URL(entry, import.meta.url),
          `${directory}/${output}`,
          signal,
          importArgs,
        );
      const scenario = options.mutation?.scenario;
      const code = await bundle("browser_conformance.ts", "test.js");
      const conversationCode = scenario === "following"
        ? undefined
        : await bundle("../mod.ts", "conversation.js");
      const demoCode = scenario ? undefined : await bundle("../demo/main.ts", "demo.js");
      const style = await Deno.readFile(new URL("../styles.css", import.meta.url));
      const demoStyle = scenario
        ? undefined
        : await Deno.readFile(new URL("../demo/styles.css", import.meta.url));
      return {
        code,
        head: '<link rel="stylesheet" href="/styles.css">' +
          (scenario ? `<meta name="scroll-scenario" content="${scenario}">` : ""),
        route(request) {
          const path = new URL(request.url).pathname;
          if (path === "/conversation.js" && conversationCode) {
            return new Response(conversationCode, {
              headers: { "content-type": "text/javascript" },
            });
          }
          if (path === "/demo.js" && demoCode) {
            return new Response(demoCode, { headers: { "content-type": "text/javascript" } });
          }
          if (path === "/demo" && demoCode) {
            return new Response(
              '<!doctype html><html><head><link rel="stylesheet" href="/styles.css"><link rel="stylesheet" href="/demo.css"></head><body><div id="app"></div><script type="module" src="/demo.js"></script></body></html>',
              {
                headers: { "content-type": "text/html" },
              },
            );
          }
          if (path === "/styles.css") {
            return new Response(style, { headers: { "content-type": "text/css" } });
          }
          if (path === "/demo.css" && demoStyle) {
            return new Response(demoStyle, { headers: { "content-type": "text/css" } });
          }
          return new Response("not found", { status: 404 });
        },
      };
    },
  });
}

if (import.meta.main) {
  const passed = await runBrowserSuite();
  for (const scenario of passed) console.log(`ok - ${scenario}`);
  console.log(`${passed.length} real browser conversation scenarios passed.`);
}
