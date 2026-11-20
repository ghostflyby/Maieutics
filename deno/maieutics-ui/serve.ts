/** Loopback-only fixture host. This is not the authenticated product frontend API. */
const controller = new AbortController();
const stop = () => controller.abort();
Deno.addSignalListener("SIGINT", stop);
const assets: Record<string, [string, string]> = {
  "/": ["index.html", "text/html; charset=utf-8"],
  "/demo.js": ["demo.js", "text/javascript; charset=utf-8"],
  "/styles.css": ["styles.css", "text/css; charset=utf-8"],
};
const server = Deno.serve({
  hostname: "127.0.0.1",
  port: 0,
  signal: controller.signal,
  onListen(address) {
    console.log(`Conversation prototype: http://127.0.0.1:${address.port}/`);
  },
}, async (request) => {
  const asset = assets[new URL(request.url).pathname];
  if (!asset || request.method !== "GET") return new Response("Not found", { status: 404 });
  return new Response(await Deno.readFile(new URL(`dist/${asset[0]}`, import.meta.url)), {
    headers: { "content-type": asset[1], "x-content-type-options": "nosniff" },
  });
});
try {
  await server.finished;
} finally {
  Deno.removeSignalListener("SIGINT", stop);
}
