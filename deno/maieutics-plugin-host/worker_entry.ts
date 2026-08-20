/**
 * Plugin worker entry, materialized by the kernel and passed to each worker as
 * its module URL. The host sends an `init` frame with the plugin export module
 * URL and the redirect table of the plugin's declared dependencies; this entry
 * installs module resolution hooks for those dependency specifiers, then
 * dynamically imports the plugin module. The plugin module itself loads the
 * real files; a dependency specifier imported by the plugin resolves to a
 * synthesized stub that binds a remote callable for every top-level export the
 * owner reported (type-identical to the real module).
 *
 * The same entry also serves cross-plugin actor channels: when the host sends
 * a `serve-actor` frame with a MessagePort, the worker serves its own module
 * namespace over that port so a dependency plugin can call into it.
 */

import { createActorCaller, serveActor } from "../maieutics-plugin-sdk/mod.ts";
import { registerHooks } from "node:module";

interface InitFrame {
  type: "init";
  entryUrl: string;
  /** Declared dependency specifier → real entry URL and known export names. */
  redirectTable?: Record<string, { entryUrl: string; exportNames: string[] }>;
  /** Package names of every plugin known to the host, to reject undeclared cross-plugin imports. */
  knownPluginSpecifiers?: string[];
}

interface InvokeFrame {
  type: "invoke";
  id: string;
  extensionPoint: string;
  request: unknown;
}

interface ServeFrame {
  type: "serve-actor";
  refId: string;
  port: MessagePort;
}

const scope = self as unknown as {
  postMessage(message: unknown, transfer?: Transferable[]): void;
  onmessage: ((event: MessageEvent) => void) | null;
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent) => void): void;
};

function fail(id: string | null, code: string, message: string): void {
  scope.postMessage({ type: "error", id, code, message });
}

const SDK_MODULE_URL = import.meta.resolve("../maieutics-plugin-sdk/mod.ts");

/**
 * Synthesizes the stub module for one declared dependency specifier. The stub
 * binds one remote callable per reported top-level export name; all bindings
 * share one lazily acquired actor channel. The stub is plain JavaScript — its
 * type identity with the real module comes from the kernel-generated
 * import map used by `deno check` and editors, not from runtime type queries.
 */
function makeStubSource(
  specifier: string,
  exportNames: readonly string[],
): string {
  const lines = [
    `import { createActorCaller } from ${JSON.stringify(SDK_MODULE_URL)};`,
    `const caller = createActorCaller(() => globalThis.__maieuticsAcquire(${JSON.stringify(specifier)}));`,
  ];
  for (const name of exportNames) {
    if (name === "default") {
      lines.push(
        `export default (...args) => caller(["default"], args);`,
      );
      continue;
    }
    const safe = /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name)
      ? name
      : JSON.stringify(name);
    lines.push(
      `export const ${safe} = (...args) => caller([${JSON.stringify(name)}], args);`,
    );
  }
  return `${lines.join("\n")}\n`;
}

let initialized: Promise<void> | undefined;
let extensions = new Map<string, unknown>();
let moduleNamespace: Record<string, unknown> = {};
let acquire: (specifier: string) => Promise<MessagePort> = async () => {
  throw new Error("No actor can be acquired before the plugin has initialized.");
};

async function initialize(frame: InitFrame): Promise<void> {
  const { entryUrl } = frame;
  const redirects = new Map(Object.entries(frame.redirectTable ?? {}));
  const knownPlugins = frame.knownPluginSpecifiers ?? [];

  // The entry's acquire hook is what the synthesized stubs call. It posts an
  // acquire request to the host and resolves with the direct channel port once
  // the host has wired the consumer to the owner worker.
  (globalThis as Record<string, unknown>)["__maieuticsAcquire"] = (
    specifier: string,
  ): Promise<MessagePort> => {
    const entry = redirects.get(specifier);
    if (entry === undefined) {
      return Promise.reject(
        new Error(
          `Plugin dependency '${specifier}' is not declared; cross-plugin imports require a declared dependency.`,
        ),
      );
    }
    return new Promise<MessagePort>((resolve, reject) => {
      const refId = crypto.randomUUID();
      const timer = setTimeout(() => {
        scope.removeEventListener("message", onResponse);
        reject(new Error(`Acquiring plugin dependency '${specifier}' timed out.`));
      }, 15_000);
      function onResponse(event: MessageEvent): void {
        const response = event.data as { refId?: string; port?: MessagePort; error?: string };
        if (response.refId !== refId) return;
        clearTimeout(timer);
        scope.removeEventListener("message", onResponse);
        if (response.error !== undefined) {
          reject(new Error(response.error));
          return;
        }
        if (response.port === undefined) {
          reject(new Error(`Acquiring plugin dependency '${specifier}' returned no port.`));
          return;
        }
        resolve(response.port);
      }
      scope.addEventListener("message", onResponse);
      scope.postMessage({ type: "acquire-actor", refId, specifier });
    });
  };
  acquire = (specifier) =>
    (globalThis as unknown as {
      __maieuticsAcquire(specifier: string): Promise<MessagePort>;
    }).__maieuticsAcquire(specifier);

  registerHooks({
    resolve(specifier, context, nextResolve) {
      if (redirects.has(specifier)) {
        return {
          url: `virtual:plugin/${encodeURIComponent(specifier)}`,
          shortCircuit: true,
        };
      }
      for (const plugin of knownPlugins) {
        if (specifier === plugin || specifier.startsWith(`${plugin}/`)) {
          throw new Error(
            `Plugin dependency '${specifier}' is not declared by this plugin; ` +
              `cross-plugin imports require a declared dependency (dependency_not_declared).`,
          );
        }
      }
      return nextResolve(specifier, context);
    },
    load(url, context, nextLoad) {
      if (url.startsWith("virtual:plugin/")) {
        const specifier = decodeURIComponent(url.slice("virtual:plugin/".length));
        const entry = redirects.get(specifier);
        if (entry === undefined) {
          return nextLoad(url, context);
        }
        return {
          format: "module",
          source: makeStubSource(specifier, entry.exportNames),
          shortCircuit: true,
        };
      }
      return nextLoad(url, context);
    },
  });

  const namespace = (await import(entryUrl)) as Record<string, unknown>;
  moduleNamespace = namespace;
  const found = new Map<string, unknown>();
  for (const key of Object.keys(namespace)) {
    const value = namespace[key];
    if (typeof value !== "object" && typeof value !== "function") {
      continue;
    }
    for (const [name, symbol] of Object.entries(ExtensionPointSymbols)) {
      if ((value as Record<symbol, unknown>)[symbol] === true) {
        found.set(name, value);
      }
    }
  }
  extensions = found;
  scope.postMessage({
    type: "ready",
    extensionPoints: [...found.keys()],
    exportNames: Object.keys(namespace),
  });
}

async function invoke(frame: InvokeFrame): Promise<void> {
  const { id, extensionPoint, request } = frame;
  try {
    await initialized;
    const impl = extensions.get(extensionPoint);
    if (impl === undefined) {
      fail(
        id,
        "extension_point_not_registered",
        `Extension point '${extensionPoint}' is not registered.`,
      );
      return;
    }
    const value = typeof impl === "function"
      ? await (impl as (context: unknown) => unknown)(request)
      : await (impl as { handler(context: unknown): unknown }).handler(
        request,
      );
    scope.postMessage({ type: "result", id, value });
  } catch (error) {
    fail(
      id,
      "extension_failed",
      error instanceof Error ? error.message : String(error),
    );
  }
}

const ExtensionPointSymbols: Record<string, symbol> = {
  McpDiscover: Symbol.for("maieutics/extensionPoint/v1/mcp.discover"),
  ToolPreInvoke: Symbol.for("maieutics/extensionPoint/v1/tools.preInvoke"),
  ToolPostInvoke: Symbol.for("maieutics/extensionPoint/v1/tools.postInvoke"),
};

async function serve(frame: ServeFrame): Promise<void> {
  const port = frame.port;
  try {
    await initialized;
    port.start();
    serveActor(port, moduleNamespace);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    scope.postMessage({ type: "serve-error", refId: frame.refId, message });
    port.close();
  }
}

scope.onmessage = (event: MessageEvent): void => {
  const frame = event.data as InitFrame | InvokeFrame | ServeFrame;
  if (frame?.type === "init") {
    initialized = initialize(frame as InitFrame).catch((error) => {
      fail(
        null,
        "init_failed",
        error instanceof Error ? error.message : String(error),
      );
    });
  } else if (frame?.type === "invoke") {
    void invoke(frame as InvokeFrame);
  } else if (frame?.type === "serve-actor") {
    void serve(frame as ServeFrame);
  }
};
