import { bundleBrowserEntry, runInBrowser } from "../../../test/browser_runner.ts";
import { conversationChrome } from "../../conversationChrome.ts";

const stylesheetFiles = {
  "/ui.css": new URL("../../../maieutics-ui/styles.css", import.meta.url),
  "/theme.css": new URL("../../webview/theme.css", import.meta.url),
  "/output.css": new URL("../../webview/output.css", import.meta.url),
};
for (
  const scenario of await runInBrowser({
    executable: Deno.env.get("MAIEUTICS_UI_BROWSER"),
    async prepare(directory, signal) {
      const code = await bundleBrowserEntry(
        new URL("conversationLayout.ts", import.meta.url),
        `${directory}/test.js`,
        signal,
      );
      const chrome = conversationChrome({
        conversation: "Layout fixture",
        connecting: "Connected",
        actions: "Toolbar actions",
        reload: "Reconnect",
        stop: "Stop",
      });
      return {
        code,
        async route(request: Request) {
          const path = new URL(request.url).pathname;
          if (path === "/layout") {
            return new Response(
              `<!doctype html><html><head><link rel="stylesheet" href="/ui.css"><link rel="stylesheet" href="/theme.css"></head><body>${chrome}<script type="module">
              try {
                const {mountLayoutFixture} = await import("/test.js");
                await mountLayoutFixture();
                parent.postMessage({layoutReady:true}, location.origin);
              } catch(error) { parent.postMessage({layoutError:String(error.stack || error)}, location.origin); }
            </script></body></html>`,
              { headers: { "content-type": "text/html" } },
            );
          }
          if (path === "/ui.css" || path === "/theme.css" || path === "/output.css") {
            return new Response(await Deno.readFile(stylesheetFiles[path]), {
              headers: { "content-type": "text/css" },
            });
          }
          return new Response(null, { status: 404 });
        },
      };
    },
  })
) console.log(`ok - ${scenario}`);
