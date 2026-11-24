import { bundleBrowserEntry, runInBrowser } from "../../test/browser_runner.ts";

const owner = new AbortController();
const cancelled = Promise.withResolvers<void>();
let release: (() => void) | undefined;
const pluginPath = "/scoped/plugins/probe";
const server = Deno.serve({
  hostname: "127.0.0.1",
  port: 0,
  signal: owner.signal,
  onListen() {},
}, async (request) => {
  const headers = {
    "access-control-allow-origin": "*",
    "access-control-allow-headers": "content-type",
  };
  const response = (body?: BodyInit | null, status = 200) =>
    new Response(body, { headers, status });
  const path = new URL(request.url).pathname;
  if (!path.startsWith(pluginPath + "/")) return response(null, 404);
  if (request.method === "OPTIONS") return response(null, 204);
  switch (path.slice(pluginPath.length)) {
    case "/echo":
      return response(request.body);
    case "/stream":
      return response(
        new ReadableStream({
          start(controller) {
            controller.enqueue(new Uint8Array([0, 255]));
            release = () => {
              controller.enqueue(new Uint8Array([128]));
              controller.close();
            };
          },
          cancel() {
            release = undefined;
          },
        }),
      );
    case "/release": {
      const send = release;
      release = undefined;
      if (!send) return response(null, 409);
      send();
      return response("released");
    }
    case "/pending":
      return response(
        new ReadableStream({
          start(controller) {
            controller.enqueue(new Uint8Array([42]));
          },
          cancel() {
            cancelled.resolve();
          },
        }),
      );
    case "/cancelled":
      await cancelled.promise;
      return response("cancelled");
    case "/api":
      return response("ok");
    default:
      return response(null, 404);
  }
});
const close = () => {
  cancelled.resolve();
  owner.abort();
};
try {
  const address = server.addr;
  if (address.transport !== "tcp") throw new Error("Plugin HTTP fixture requires TCP");
  const origin = `http://127.0.0.1:${address.port}`;
  for (
    const scenario of await runInBrowser({
      executable: Deno.env.get("MAIEUTICS_UI_BROWSER"),
      async prepare(directory, signal) {
        signal.addEventListener("abort", close, { once: true });
        const code = await bundleBrowserEntry(
          new URL("browser_conformance.ts", import.meta.url),
          `${directory}/test.js`,
          signal,
        );
        const artifact = await bundleBrowserEntry(
          new URL("../mod.ts", import.meta.url),
          `${directory}/outputClient.js`,
          signal,
        );
        return {
          code,
          head: `<meta name="fixture-origin" content="${origin}">`,
          policy:
            `default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; connect-src 'self' ${origin}`,
          route(request: Request) {
            const path = new URL(request.url).pathname;
            if (path === "/fixture-output.css") {
              return new Response("h1, pre.shiki { color: rgb(10, 40, 90); }", {
                headers: { "content-type": "text/css" },
              });
            }
            return path === "/outputClient.js"
              ? new Response(artifact, { headers: { "content-type": "text/javascript" } })
              : new Response(null, { status: 404 });
          },
        };
      },
    })
  ) console.log(`ok - ${scenario}`);
} finally {
  close();
  await server.finished;
}
