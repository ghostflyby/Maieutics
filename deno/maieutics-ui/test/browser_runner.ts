/** An owned browser/profile/loopback server runs deterministic UI conformance. */
const candidates: Record<string, string[]> = {
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
const configured = Deno.env.get("MAIEUTICS_UI_BROWSER") ?? Deno.env.get("MAIEUTICS_ACTOR_BROWSER");
let browser: string | undefined;
for (const path of configured ? [configured] : candidates[Deno.build.os] ?? []) {
  try {
    if ((await Deno.stat(path)).isFile) {
      browser = path;
      break;
    }
  } catch (error) {
    if (!(error instanceof Deno.errors.NotFound)) throw error;
  }
}
if (!browser) throw new Error("Set MAIEUTICS_UI_BROWSER to a Chromium browser executable");
const directory = await Deno.makeTempDir({ prefix: "maieutics-ui-browser-" });
const verdict = Promise.withResolvers<string[]>();
let server: Deno.HttpServer | undefined;
let child: Deno.ChildProcess | undefined;
let logs: Promise<void> | undefined;
let diagnostics = "";
let failure: unknown;
let passed: string[] = [];
let timer: ReturnType<typeof setTimeout> | undefined;

async function wait<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
  signal.throwIfAborted();
  return await new Promise<T>((resolve, reject) => {
    const abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    work.then((value) => {
      signal.removeEventListener("abort", abort);
      resolve(value);
    }, (error) => {
      signal.removeEventListener("abort", abort);
      reject(error);
    });
  });
}
async function cleanup(): Promise<void> {
  const budget = AbortSignal.timeout(10_000);
  const errors: unknown[] = [];
  const attempt = async (work: Promise<unknown>) => {
    try {
      await wait(work, budget);
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
    try {
      await wait(child.status, AbortSignal.any([budget, AbortSignal.timeout(2000)]));
    } catch {
      try {
        child.kill("SIGKILL");
      } catch (error) {
        if (!(error instanceof Deno.errors.NotFound)) errors.push(error);
      }
      await attempt(child.status);
    }
  }
  if (logs) await attempt(logs);
  if (server) await attempt(server.shutdown());
  await attempt(Deno.remove(directory, { recursive: true }));
  if (errors.length) throw new AggregateError(errors, "UI browser cleanup failed");
}
try {
  const bundle = await new Deno.Command(Deno.execPath(), {
    args: [
      "bundle",
      "--platform=browser",
      "--format=esm",
      "--minify",
      "-o",
      `${directory}/test.js`,
      new URL("browser_conformance.ts", import.meta.url).pathname,
    ],
    stdout: "piped",
    stderr: "piped",
  }).output();
  if (!bundle.success) throw new Error(new TextDecoder().decode(bundle.stderr));
  const code = await Deno.readFile(`${directory}/test.js`);
  const artifact = await new Deno.Command(Deno.execPath(), {
    args: [
      "bundle",
      "--platform=browser",
      "--format=esm",
      "--minify",
      "-o",
      `${directory}/conversation.js`,
      new URL("../mod.ts", import.meta.url).pathname,
    ],
    stdout: "piped",
    stderr: "piped",
  }).output();
  if (!artifact.success) throw new Error(new TextDecoder().decode(artifact.stderr));
  const conversationCode = await Deno.readFile(`${directory}/conversation.js`);
  const style = await Deno.readFile(new URL("../demo/styles.css", import.meta.url));
  const resultPath = `/result/${crypto.randomUUID()}`;
  server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
    const path = new URL(request.url).pathname;
    if (path === "/test.js") {
      return new Response(code, { headers: { "content-type": "text/javascript" } });
    }
    if (path === "/conversation.js") {
      return new Response(conversationCode, { headers: { "content-type": "text/javascript" } });
    }
    if (path === "/styles.css") {
      return new Response(style, { headers: { "content-type": "text/css" } });
    }
    if (path === resultPath && request.method === "POST") {
      const body = await request.text();
      if (body.length > 32_000) {
        verdict.reject(new Error("Browser verdict too large"));
        return new Response("too large", { status: 413 });
      }
      const result = JSON.parse(body);
      if (result.ok && Array.isArray(result.passed)) verdict.resolve(result.passed);
      else verdict.reject(new Error(String(result.error)));
      return new Response("received");
    }
    if (path !== "/") return new Response("not found", { status: 404 });
    return new Response(
      `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><link rel="stylesheet" href="/styles.css"></head><body><script type="module">
      const report = value => fetch(${
        JSON.stringify(resultPath)
      }, {method:'POST',body:JSON.stringify(value)});
      window.addEventListener('unhandledrejection', event => report({ok:false,error:String(event.reason?.stack ?? event.reason)}));
      window.addEventListener('error', event => report({ok:false,error:String(event.error?.stack ?? event.message)}));
      try { const {runBrowserConformance} = await import('/test.js'); await report({ok:true,passed:await runBrowserConformance()}); }
      catch(error) { await report({ok:false,error:error.stack ?? String(error)}); }
    </script></body></html>`,
      {
        headers: {
          "content-type": "text/html",
          "content-security-policy":
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; object-src 'none'; frame-src 'none'",
        },
      },
    );
  });
  const environment: Record<string, string> = {};
  for (
    const name of [
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
    const value = Deno.env.get(name);
    if (value !== undefined) environment[name] = value;
  }
  const address = server.addr;
  if (address.transport !== "tcp") {
    throw new Error("Browser conformance requires a loopback TCP listener");
  }
  child = new Deno.Command(browser, {
    args: [
      "--headless=new",
      ...(Deno.env.get("CI") === "true" && Deno.build.os === "linux" ? ["--no-sandbox"] : []),
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
    () => verdict.reject(new Error("UI browser conformance exceeded its 60s total budget")),
    60_000,
  );
  passed = await Promise.race([
    verdict.promise,
    child.status.then((status) => {
      throw new Error(`Browser exited before its verdict (${status.code})`);
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
      : new AggregateError([failure, error], "Browser conformance and cleanup failed");
  }
}
if (failure !== undefined) throw failure;
for (const scenario of passed) console.log(`ok - ${scenario}`);
console.log(`${passed.length} real browser conversation scenarios passed.`);
