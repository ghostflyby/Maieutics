/** Real headless Chromium conformance with an owned profile, process, server and verdict. */
import { createActorConnection, webSocketTransport } from "../browser.ts";
import { testWireCodec } from "./browser_wire_codec.ts";

const roots: Record<string, string[]> = {
  darwin: [
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  ],
  linux: ["/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"],
  windows: [
    `${Deno.env.get("PROGRAMFILES(X86)")}\\Microsoft\\Edge\\Application\\msedge.exe`,
    `${Deno.env.get("PROGRAMFILES")}\\Google\\Chrome\\Application\\chrome.exe`,
  ],
};
const configured = Deno.env.get("MAIEUTICS_ACTOR_BROWSER");
let browser: string | undefined;
for (const path of configured ? [configured] : roots[Deno.build.os] ?? []) {
  try {
    if ((await Deno.stat(path)).isFile) {
      browser = path;
      break;
    }
  } catch (error) {
    if (!(error instanceof Deno.errors.NotFound)) throw error;
  }
}
if (!browser) {
  throw new Error(
    "Set MAIEUTICS_ACTOR_BROWSER to a Chromium browser executable to run Actor browser conformance",
  );
}
const directory = await Deno.makeTempDir({ prefix: "maieutics-actor-browser-" });
const verdict = Promise.withResolvers<string[]>();
const sessions: ReturnType<typeof createActorConnection>[] = [];
let server: Deno.HttpServer | undefined;
let child: Deno.ChildProcess | undefined;
let logs: Promise<void> | undefined;
let diagnostics = "";
let timer: ReturnType<typeof setTimeout> | undefined;
let passed: string[] = [];
let failure: unknown;
async function cleanup(): Promise<void> {
  const errors: unknown[] = [];
  const attempt = async (work: Promise<unknown>) => {
    try {
      await work;
    } catch (error) {
      errors.push(error);
    }
  };
  if (child) {
    try {
      child.kill("SIGTERM");
    } catch (error) {
      if (!(error instanceof Deno.errors.NotFound)) errors.push(error);
    }
    await attempt(child.status);
    if (logs) await attempt(logs);
  }
  for (const connection of sessions) {
    try {
      connection.close();
    } catch (error) {
      errors.push(error);
    }
  }
  await attempt(Promise.all(sessions.map((connection) => connection.completion)));
  if (server) await attempt(server.shutdown());
  await attempt(Deno.remove(directory, { recursive: true }));
  if (errors.length) throw new AggregateError(errors, "Actor browser runner cleanup failed");
}
try {
  for (
    const [source, name] of [["browser_conformance.ts", "test.js"], [
      "browser_worker.ts",
      "worker.js",
    ]]
  ) {
    const output = await new Deno.Command(Deno.execPath(), {
      args: [
        "bundle",
        "--no-config",
        "--platform=browser",
        "--output",
        `${directory}/${name}`,
        new URL(source, import.meta.url).href,
      ],
      stdout: "piped",
      stderr: "piped",
    }).output();
    if (!output.success) throw new Error(new TextDecoder().decode(output.stderr));
    const code = await Deno.readTextFile(`${directory}/${name}`);
    if (code.includes("node:") || /\bDeno\./.test(code)) {
      throw new Error("Actor browser artifact retained a host runtime dependency");
    }
  }
  const resultPath = `/result/${crypto.randomUUID()}`;
  server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    const url = new URL(request.url);
    if (url.pathname === "/test.js" || url.pathname === "/worker.js") {
      return new Response(await Deno.readFile(`${directory}${url.pathname}`), {
        headers: { "content-type": "text/javascript" },
      });
    }
    if (url.pathname === resultPath && request.method === "POST") {
      const result = await request.json();
      if (result.ok === true && Array.isArray(result.passed)) verdict.resolve(result.passed);
      else verdict.reject(new Error(String(result.error)));
      return new Response("received");
    }
    if (url.pathname === "/ws" && request.headers.get("upgrade")?.toLowerCase() === "websocket") {
      const { socket, response } = Deno.upgradeWebSocket(request);
      socket.addEventListener("open", () => {
        const transport = webSocketTransport(socket, { codec: testWireCodec });
        let count = 0;
        let produced = 0;
        const released = Promise.withResolvers<void>();
        let signal = new AbortController().signal;
        const api = {
          echo: (bytes: Uint8Array) => bytes,
          increment: () => ++count,
          async *numbers() {
            try {
              for (let i = 0; i < 1000; i++) {
                produced++;
                yield i;
              }
            } finally {
              released.resolve();
            }
          },
          produced: () => produced,
          released: () => released.promise,
          apply: (callback: (bytes: Uint8Array) => Promise<Uint8Array>, bytes: Uint8Array) =>
            callback(bytes),
          askUi: () => connection.actor("ui").describe(),
          async cancel(remote: AbortSignal, started: () => Promise<void>) {
            await started();
            if (!remote.aborted) {
              await new Promise<void>((resolve) =>
                remote.addEventListener("abort", () => resolve(), { once: true })
              );
            }
            return remote.aborted;
          },
          async wait(started: () => Promise<void>) {
            await started();
            if (!signal.aborted) {
              await new Promise<void>((resolve) =>
                signal.addEventListener("abort", () => resolve(), { once: true })
              );
            }
          },
          fail(): never {
            throw new RangeError("deliberate conformance failure");
          },
        };
        const connection = createActorConnection<{ ui: { describe(): string } }>(transport, {
          actors: { service: api },
        });
        signal = connection.signal;
        sessions.push(connection);
      }, { once: true });
      return response;
    }
    if (url.pathname === "/") {
      return new Response(
        `<!doctype html><script type="module">
      const report = (value) => fetch(${
          JSON.stringify(resultPath)
        }, {method:'POST', headers:{'content-type':'application/json'}, body:JSON.stringify(value)});
      try {
        const {runBrowserConformance} = await import('/test.js');
        const passed = await runBrowserConformance(location.origin.replace('http:','ws:') + '/ws', location.origin + '/worker.js');
        await report({ok:true, passed});
      } catch(error) { await report({ok:false, error:error.stack??String(error)}); }
    </script>`,
        { headers: { "content-type": "text/html" } },
      );
    }
    return new Response("not found", { status: 404 });
  });
  const environment: Record<string, string> = {};
  for (
    const key of [
      "PATH",
      "HOME",
      "TMPDIR",
      "SYSTEMROOT",
      "WINDIR",
      "DISPLAY",
      "WAYLAND_DISPLAY",
      "XDG_RUNTIME_DIR",
    ]
  ) {
    const value = Deno.env.get(key);
    if (value !== undefined) environment[key] = value;
  }
  const address = server.addr;
  if (address.transport !== "tcp") throw new Error("Actor test server must use TCP");
  child = new Deno.Command(browser, {
    args: [
      "--headless=new",
      "--disable-gpu",
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-background-networking",
      `--user-data-dir=${directory}/profile`,
      `http://127.0.0.1:${address.port}/`,
    ],
    stdin: "null",
    stdout: "null",
    stderr: "piped",
    clearEnv: true,
    env: environment,
  }).spawn();
  logs = child.stderr.pipeTo(
    new WritableStream({
      write(bytes) {
        diagnostics = (diagnostics + new TextDecoder().decode(bytes)).slice(-12000);
      },
    }),
  );
  timer = setTimeout(
    () => verdict.reject(new Error("Actor browser conformance exceeded its 60s completion budget")),
    60_000,
  );
  passed = await Promise.race([
    verdict.promise,
    child.status.then((status) => {
      throw new Error(`Actor browser exited before its verdict (${status.code})`);
    }),
  ]);
} catch (error) {
  failure = new Error(
    `${error instanceof Error ? error.stack : error}\nBrowser diagnostics:\n${diagnostics}`,
  );
} finally {
  clearTimeout(timer);
  try {
    await cleanup();
  } catch (error) {
    failure = failure === undefined
      ? error
      : new AggregateError([failure, error], "Actor browser conformance and cleanup failed");
  }
}
if (failure !== undefined) throw failure;
for (const test of passed) console.log(`ok - ${test}`);
console.log(`${passed.length} real browser Actor scenarios passed.`);
