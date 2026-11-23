/// <reference lib="deno.window" />
/// <reference lib="es2024.promise" />

export class BrowserScenarioFailure extends Error {
  override name = "BrowserScenarioFailure";
}

interface BrowserFixture {
  code: Uint8Array<ArrayBuffer>;
  head?: string;
  policy?: string;
  route?(request: Request): Response | Promise<Response>;
}

/** Own the Chromium profile, process, loopback server, and verdict for one browser suite. */
export async function runInBrowser(options: {
  executable?: string;
  signal?: AbortSignal;
  prepare(directory: string, signal: AbortSignal): Promise<BrowserFixture>;
}): Promise<string[]> {
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
  const configured = options.executable ??
    Deno.env.get("MAIEUTICS_ACTOR_BROWSER");
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
  if (!browser) {
    throw new Error(
      "Set MAIEUTICS_ACTOR_BROWSER or the suite browser override to a Chromium executable",
    );
  }
  const directory = await Deno.makeTempDir({ prefix: "maieutics-browser-test-" });
  const verdict = Promise.withResolvers<string[]>();
  const observedVerdict = verdict.promise.catch(() => undefined);
  const owner = new AbortController();
  const signal = options.signal ? AbortSignal.any([owner.signal, options.signal]) : owner.signal;
  const cancelled = () => verdict.reject(signal.reason);
  signal.addEventListener("abort", cancelled, { once: true });
  let server: Deno.HttpServer | undefined;
  let child: Deno.ChildProcess | undefined;
  let logs: Promise<void> | undefined;
  let logObservation: Promise<void> | undefined;
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
    if (logObservation) await attempt(logObservation);
    if (server) await attempt(server.shutdown());
    await attempt(Deno.remove(directory, { recursive: true }));
    if (errors.length) throw new AggregateError(errors, "Browser test cleanup failed");
  }
  try {
    signal.throwIfAborted();
    timer = setTimeout(
      () => owner.abort(new Error("Browser conformance exceeded its 60s total budget")),
      60_000,
    );
    const fixture = await options.prepare(directory, signal);
    const resultPath = `/result/${crypto.randomUUID()}`;
    server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, async (request) => {
      const path = new URL(request.url).pathname;
      if (path === "/test.js") {
        return new Response(fixture.code, { headers: { "content-type": "text/javascript" } });
      }
      if (path === resultPath && request.method === "POST") {
        const body = await request.text();
        if (body.length > 32_000) {
          verdict.reject(new Error("Browser verdict too large"));
          return new Response("too large", { status: 413 });
        }
        const result = JSON.parse(body);
        if (
          result.ok === true && Array.isArray(result.passed) && result.passed.length > 0 &&
          result.passed.every((name: unknown) => typeof name === "string")
        ) verdict.resolve(result.passed);
        else if (result.ok === false && typeof result.error === "string") {
          verdict.reject(new BrowserScenarioFailure(result.error));
        } else verdict.reject(new Error("Invalid browser test verdict"));
        return new Response("received");
      }
      if (path !== "/") {
        return await fixture.route?.(request) ?? new Response("not found", { status: 404 });
      }
      return new Response(
        `<!doctype html><html lang="en"><head><meta charset="utf-8">${
          fixture.head ?? ""
        }</head><body><script type="module">
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
            "content-security-policy": fixture.policy ??
              "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; object-src 'none'; frame-src 'self'",
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
    logObservation = logs.catch((error) => {
      verdict.reject(error);
    });
    passed = await Promise.race([
      verdict.promise,
      child.status.then((status) => {
        throw new Error(`Browser exited before its verdict (${status.code})`);
      }),
    ]);
  } catch (error) {
    const details = `${
      error instanceof Error ? error.stack : error
    }\nBrowser diagnostics:\n${diagnostics}`;
    failure = error instanceof BrowserScenarioFailure
      ? new BrowserScenarioFailure(details)
      : new Error(details);
  } finally {
    clearTimeout(timer);
    signal.removeEventListener("abort", cancelled);
    if (failure !== undefined) verdict.reject(failure);
    await observedVerdict;
    try {
      await cleanup();
    } catch (error) {
      failure = failure === undefined
        ? error
        : new AggregateError([failure, error], "Browser conformance and cleanup failed");
    }
  }
  if (failure !== undefined) throw failure;
  return passed;
}

/** Bundle a browser entry in its owning workspace configuration. */
export async function bundleBrowserEntry(
  entry: URL,
  output: string,
  signal: AbortSignal,
  options: string[] = [],
): Promise<Uint8Array<ArrayBuffer>> {
  const result = await new Deno.Command(Deno.execPath(), {
    args: [
      "bundle",
      "--platform=browser",
      "--format=esm",
      "--minify",
      "--frozen-lockfile",
      ...options,
      "-o",
      output,
      entry.href,
    ],
    stdout: "piped",
    stderr: "piped",
    signal,
  }).output();
  if (!result.success) throw new Error(new TextDecoder().decode(result.stderr));
  return await Deno.readFile(output);
}
