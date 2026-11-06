import { createOutputRenderer } from "./mod.ts";
import { createRenderHandler } from "./service.ts";

async function main(): Promise<void> {
  const callback = Deno.env.get("MAIEUTICS_RENDERER_CALLBACK");
  const token = Deno.env.get("MAIEUTICS_RENDERER_TOKEN");
  const nonce = Deno.env.get("MAIEUTICS_RENDERER_NONCE");
  if (!callback || !token || !nonce) throw new Error("renderer_configuration_missing");
  using renderer = await createOutputRenderer();
  const server = Deno.serve(
    { hostname: "127.0.0.1", port: 0, onListen() {} },
    createRenderHandler(renderer, nonce),
  );
  try {
    const ready = await fetch(callback, {
      method: "POST",
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: JSON.stringify({ version: 1, port: server.addr.port, nonce }),
    });
    await ready.body?.cancel();
    if (!ready.ok) throw new Error("renderer_registration_failed");
    await server.finished;
  } finally {
    await server.shutdown();
  }
}

try {
  await main();
} catch {
  console.error("Output renderer stopped before normal completion.");
  Deno.exitCode = 1;
}
