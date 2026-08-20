/**
 * Maieutics plugin SDK.
 *
 * A plugin is a standard Deno package (deno.json with `name`, `version`,
 * `exports`, and the standard `permissions` field). The kernel discovers a
 * plugin by the presence of a `maieutics` marker field in its deno.json and
 * creates one worker per non-`.` exports entry. Each worker imports its export
 * module and scans the top-level exports for actor surfaces and extension
 * points.
 *
 * Actor surfaces (cross-plugin interop) are declared with `defineActor`; their
 * compile-time type is worker-actor's `Remote<T>` projection, and their runtime
 * calls cross to the owning plugin through worker-actor's reference-acquire
 * machinery (`actor_ref.ts`), never through Maieutics-authored RPC frames.
 *
 * Extension points are identified by versioned global symbols
 * (`Symbol.for("maieutics/extensionPoint/v1/...")`). The host and every worker
 * isolate resolve the same symbol through the global registry, so identity
 * does not depend on module singletons. An export value belongs to an
 * extension point when it carries the marker symbol; the value is either an
 * object with a `handler` method or a callable function.
 */

import {
  actorRefCodec,
  clearActorExports,
  flattenSurface,
  registerActorExport,
  type RemoteActor,
  remoteActor,
} from "./actor_ref.ts";
import {
  connectChannel,
  type PeerRpc,
  registerControlHandler,
} from "@ghostflyby/worker-actor/codec";
import { type LinkHandle, serveWorker } from "@ghostflyby/worker-actor";

// The worker-side registry lives in the worker-actor runtime module; the SDK
// shares the worker context, so the control-plane accessor resolves the same
// instance. Exposed here for the dependency stubs' channel cleanup.
declare global {
  // deno-lint-ignore no-explicit-any
  var __workerActorRegistry: any;
}

const NAMESPACE = "maieutics/extensionPoint/v1";

/** Versioned extension point identity markers. */
export const ExtensionPoint = {
  McpDiscover: Symbol.for(`${NAMESPACE}/mcp.discover`),
  ToolPreInvoke: Symbol.for(`${NAMESPACE}/tools.preInvoke`),
  ToolPostInvoke: Symbol.for(`${NAMESPACE}/tools.postInvoke`),
} as const;

export type ExtensionPointName = keyof typeof ExtensionPoint;

/** Connection descriptor exported by `mcp.discover`; discovery only, never connection. */
export type McpDiscovery =
  | {
    module: string;
    transport: {
      type: "stdio";
      command: string;
      args?: readonly string[];
      env?: Readonly<Record<string, string>>;
    };
  }
  | {
    module: string;
    transport: {
      type: "http";
      url: string;
      headers?: Readonly<Record<string, string>>;
    };
  };

/** Why the host asked for a discovery pass. */
export interface DiscoverContext {
  readonly reason: "startup" | "config-changed";
}

/** Decision returned by a pre-invoke hook; hook chain semantics, not observation. */
export type ToolHookDecision =
  | { action: "continue" }
  | { action: "replace"; arguments?: Record<string, unknown> }
  | { action: "reject"; error: { code: string; message: string } };

/** Tool invocation as seen by a hook. */
export interface ToolInvokeContext {
  readonly tool: string;
  readonly arguments: Record<string, unknown>;
  readonly callId: string;
}

/** Tool invocation result as seen by a post hook; observation only. */
export interface ToolPostInvokeContext extends ToolInvokeContext {
  readonly status: "ok" | "error" | "cancelled";
  readonly result: unknown;
}

export interface McpDiscoverObjectInput {
  handler(context: DiscoverContext): McpDiscovery[] | Promise<McpDiscovery[]>;
}

export interface McpDiscoverObject extends McpDiscoverObjectInput {
  readonly [ExtensionPoint.McpDiscover]: true;
}

export type McpDiscoverFunctionInput = (
  context: DiscoverContext,
) => McpDiscovery[] | Promise<McpDiscovery[]>;

export type McpDiscoverFunction = McpDiscoverFunctionInput & {
  readonly [ExtensionPoint.McpDiscover]: true;
};

export type McpDiscoverInput =
  | McpDiscoverObjectInput
  | McpDiscoverFunctionInput;
export type McpDiscover = McpDiscoverObject | McpDiscoverFunction;

export interface ToolPreInvokeObjectInput {
  handler(
    context: ToolInvokeContext,
  ): ToolHookDecision | Promise<ToolHookDecision>;
}

export interface ToolPreInvokeObject extends ToolPreInvokeObjectInput {
  readonly [ExtensionPoint.ToolPreInvoke]: true;
}

export type ToolPreInvokeFunctionInput = (
  context: ToolInvokeContext,
) => ToolHookDecision | Promise<ToolHookDecision>;

export type ToolPreInvokeFunction = ToolPreInvokeFunctionInput & {
  readonly [ExtensionPoint.ToolPreInvoke]: true;
};

export type ToolPreInvokeInput =
  | ToolPreInvokeObjectInput
  | ToolPreInvokeFunctionInput;
export type ToolPreInvoke = ToolPreInvokeObject | ToolPreInvokeFunction;

export interface ToolPostInvokeObjectInput {
  handler(context: ToolPostInvokeContext): void | Promise<void>;
}

export interface ToolPostInvokeObject extends ToolPostInvokeObjectInput {
  readonly [ExtensionPoint.ToolPostInvoke]: true;
}

export type ToolPostInvokeFunctionInput = (
  context: ToolPostInvokeContext,
) => void | Promise<void>;

export type ToolPostInvokeFunction = ToolPostInvokeFunctionInput & {
  readonly [ExtensionPoint.ToolPostInvoke]: true;
};

export type ToolPostInvokeInput =
  | ToolPostInvokeObjectInput
  | ToolPostInvokeFunctionInput;
export type ToolPostInvoke = ToolPostInvokeObject | ToolPostInvokeFunction;

interface ExtensionPointShape<K extends ExtensionPointName> {
  context: K extends "McpDiscover" ? DiscoverContext
    : K extends "ToolPreInvoke" ? ToolInvokeContext
    : ToolPostInvokeContext;
  input: K extends "McpDiscover" ? McpDiscoverInput
    : K extends "ToolPreInvoke" ? ToolPreInvokeInput
    : ToolPostInvokeInput;
  impl: K extends "McpDiscover" ? McpDiscover
    : K extends "ToolPreInvoke" ? ToolPreInvoke
    : ToolPostInvoke;
}

export type ExtensionPointInput<K extends ExtensionPointName> = ExtensionPointShape<K>["input"];

export type ExtensionPointImpl<K extends ExtensionPointName> = ExtensionPointShape<K>["impl"];

/**
 * Declares an extension point implementation by attaching the versioned marker
 * symbol and validating the invocation shape. The returned value keeps the
 * original runtime identity.
 */
export function defineExtensionPoint<K extends ExtensionPointName>(
  name: K,
  impl: ExtensionPointInput<K>,
): ExtensionPointImpl<K> {
  const symbol = ExtensionPoint[name];
  const kind = typeof impl === "function" ? "function" : "object";
  if (kind === "function") {
    if (typeof impl !== "function") {
      throw new TypeError(
        `Extension point '${name}' must be a function or an object with a handler.`,
      );
    }
  } else {
    const object = impl as { handler?: unknown };
    if (typeof object.handler !== "function") {
      throw new TypeError(
        `Extension point '${name}' object must expose a handler function.`,
      );
    }
  }
  Object.defineProperty(impl, symbol, {
    value: true,
    enumerable: false,
    configurable: false,
  });
  if ((impl as unknown as Record<symbol, unknown>)[symbol] !== true) {
    throw new TypeError(
      `Extension point '${name}' marker could not be attached.`,
    );
  }
  return impl as ExtensionPointImpl<K>;
}

// —— Actor surfaces (cross-plugin interop) ——

const ACTOR_MARKER = Symbol.for("maieutics/actor/v1/surface");

export type { RemoteActor };

/**
 * Declares an actor surface. The returned value is typed as worker-actor's
 * `Remote<T>` projection (methods → promises; non-function members → `never`),
 * which is exactly the shape a dependency's runtime stub implements, so
 * compile-time types and runtime calls agree. The surface is exposed to
 * dependencies under this plugin's canonical specifier.
 */
export function defineActor<T extends Record<string, unknown>>(
  surface: T,
): RemoteActor<T> {
  for (const [name, value] of Object.entries(surface)) {
    if (typeof value !== "function") {
      throw new TypeError(
        `Actor surface member '${name}' must be a function; constants are not exposed.`,
      );
    }
  }
  Object.defineProperty(surface, ACTOR_MARKER, {
    value: true,
    enumerable: false,
    configurable: false,
  });
  return remoteActor(surface, ownSpecifier(), "actor") as RemoteActor<T>;
}

/**
 * Declares a dependency on another plugin's export module. Returns a lazy
 * reference proxy to the dependency's whole actor surface: `math.double(21)`
 * on the returned object triggers the worker-actor acquire against the owning
 * plugin, after which calls flow over a direct channel. `specifier` is the
 * canonical specifier of the dependency export (`@maieutics/dep/main`).
 *
 * The proxy is a single nested surface (per-export sub-proxies sharing one
 * acquire); there is no per-name runtime stub, so the dependency's export
 * names are never extracted. Compile-time types come from the real module via
 * `depActor<T>` — see below.
 */
export function defineDependency(specifier: string): RemoteActor<Record<string, unknown>> {
  return createDependencyStub(specifier);
}

/**
 * Typed bridge from the real dependency module to its runtime stub. The
 * runtime callable for `math.double` has the same shape
 * (`(n: number) => Promise<number>`) as the type `Remote<typeof real["math"]>`
 * gives at compile time, so bridging through `unknown` keeps the two sides in
 * agreement without any runtime extraction:
 *
 * ```ts
 * import type { math as MathSurface } from "@maieutics/dep/main";
 * const math = depActor<typeof MathSurface>("@maieutics/dep/main", "math");
 * await math.double(21); // typed, runtime stub call
 * ```
 */
export function depActor<T>(
  specifier: string,
  exportName: string,
): RemoteActor<T> {
  const surface = createDependencyStub(specifier);
  return (surface as unknown as Record<string, RemoteActor<T>>)[exportName];
}

// —— Worker-side initialization (initPluginWorker) ——

/** Host config the kernel writes into the worker's environment. */
interface WorkerInitConfig {
  /** Canonical specifier of this worker's plugin export. */
  specifier: string;
  /**
   * Actor-entry registry of the declared dependencies: the canonical specifier
   * and entry file URL of each dependency worker this plugin may call. The
   * load hook redirects only import edges that resolve to one of these.
   */
  actorEntries: readonly { specifier: string; entryUrl: string }[];
}

let ownSpecifierValue = "";
let servingApi: Record<string, (...args: unknown[]) => unknown> = {};
let configResolve: ((config: WorkerInitConfig) => void) | undefined;
let configReject: ((error: Error) => void) | undefined;
let configPromise: Promise<WorkerInitConfig> | undefined;

function ownSpecifier(): string {
  if (ownSpecifierValue.length === 0) {
    throw new Error("The plugin worker is not initialized yet (no specifier).");
  }
  return ownSpecifierValue;
}

function awaitConfig(): Promise<WorkerInitConfig> {
  if (configPromise === undefined) {
    configPromise = new Promise<WorkerInitConfig>((resolve, reject) => {
      configResolve = resolve;
      configReject = reject;
    });
  }
  return configPromise;
}

/**
 * Initializes the plugin worker runtime. Called once from the worker entry:
 * waits for the host's per-worker config (`maieutics-config`), registers the
 * worker-actor runtime (`serveWorker`) with the actor-ref codec, wires the
 * host `init`/`dispose` control frames, and installs the load hook that
 * redirects dependency specifiers to synthesized stubs.
 */
export async function initPluginWorker(): Promise<void> {
  if (initStarted) return;
  initStarted = true;

  const scope = self as unknown as {
    addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  };
  scope.addEventListener("message", (event: MessageEvent): void => {
    const frame = event.data as { type?: string; payload?: unknown };
    if (frame?.type !== "maieutics-config") return;
    const parsed = frame.payload as Partial<WorkerInitConfig>;
    if (typeof parsed.specifier !== "string" || parsed.specifier.length === 0) {
      configReject?.(new Error("The host config is missing the worker specifier."));
      return;
    }
    configResolve?.({
      specifier: parsed.specifier,
      actorEntries: Array.isArray(parsed.actorEntries) ? parsed.actorEntries : [],
    });
  });

  const config = await awaitConfig();
  ownSpecifierValue = config.specifier;

  // The dependency stubs need this worker's id to build routeable refIds;
  // expose the worker-actor runtime's registry to them via the global slot.
  registerControlHandler("__worker-id", (frame) => {
    if (typeof frame.refId !== "string") return;
    (globalThis as unknown as { __maieuticsWorkerId: string }).__maieuticsWorkerId = frame.refId;
  });

  installDependencyLoadHook(config.actorEntries);

  serveWorker(servingApi, {
    codecs: [actorRefCodec],
    onLink(link: LinkHandle): void {
      // A peer linked directly (host or another worker). Expose the flattened
      // plugin namespace over the link; the peer calls through link.rpc.
      const surface = flattenSurface(linkedSurface);
      link.serve(surface);
      link.rpc as PeerRpc<object>;
    },
  });

  // The init/dispose listener is separate from the config listener above; both
  // coexist on the same worker (serveWorker uses its own onmessage property).
  scope.addEventListener("message", (event: MessageEvent): void => {
    const frame = event.data as { type?: string; entryUrl?: string };
    if (frame?.type === "init") {
      initialize(frame.entryUrl ?? "").catch((error) => {
        scopePostMessage({
          type: "init-error",
          message: error instanceof Error ? error.message : String(error),
        });
      });
    } else if (frame?.type === "dispose") {
      disposePlugin();
    }
  });
}

let initStarted = false;

interface InitFrame {
  type: "init";
  entryUrl: string;
}

interface Disposed {}

let linkedSurface: Record<string, unknown> = {};

async function initialize(entryUrl: string): Promise<void> {
  if (entryUrl.length === 0) {
    throw new Error("The init frame is missing the plugin entry URL.");
  }
  const namespace = (await import(entryUrl)) as Record<string, unknown>;
  linkedSurface = namespace;
  scanExports(namespace);
  // serveWorker resolved methods at call time through the api object; the
  // extension points are known only after init, so repopulate the object now.
  for (const key of Object.keys(servingApi)) delete servingApi[key];
  for (const [name, impl] of extensionPoints) {
    const invoke = async (request: unknown): Promise<unknown> => {
      const value = typeof impl === "function"
        ? await (impl as (context: unknown) => unknown)(request)
        : await (impl as { handler(context: unknown): unknown }).handler(request);
      return value;
    };
    servingApi[name] = invoke;
  }
  scopePostMessage({
    type: "ready",
    specifier: ownSpecifierValue,
    extensionPoints: [...extensionPoints.keys()],
  });
}

function scanExports(namespace: Record<string, unknown>): void {
  const extensions = new Map<string, unknown>();
  const actors = new Map<string, object>();
  for (const [name, value] of Object.entries(namespace)) {
    if (typeof value === "function" || (typeof value === "object" && value !== null)) {
      for (
        const [extensionName, symbol] of Object.entries(ExtensionPoint as Record<string, symbol>)
      ) {
        if ((value as Record<symbol, unknown>)[symbol] === true) {
          extensions.set(extensionName, value);
        }
      }
      if ((value as Record<symbol, unknown>)[ACTOR_MARKER] === true) actors.set(name, value);
    }
  }
  extensionPoints = extensions;
  for (const [name, surface] of actors) {
    // The defineActor proxy exposes the real surface via __surface so the
    // owner can flatten it (a Proxy cannot be Object.entries-enumerated).
    const realSurface = (surface as { __surface?: object }).__surface ?? surface;
    registerActorExport(name, ownSpecifierValue, realSurface);
  }
}

let extensionPoints = new Map<string, unknown>();

function disposePlugin(): void {
  clearActorExports();
  extensionPoints.clear();
  linkedSurface = {};
  for (const close of closeHandlers) close();
  closeHandlers.length = 0;
}

/** Registered close hooks (link cleanup etc.) run on dispose. */
const closeHandlers: Array<() => void> = [];

// —— Dependency stubs (the load hook + lazy acquire) ——

/** The SDK entry URL, used by synthesized stubs to import the acquire machinery. */
const SDK_ENTRY_URL = import.meta.url;

const stubBySpecifier = new Map<string, RemoteActor<Record<string, unknown>>>();

/**
 * The runtime stub for one dependency specifier: a single lazy surface whose
 * members are method proxies sharing one acquire. `math.double(21)` on the
 * returned object flattens to a call of method `double` over the acquired
 * channel — the same shape `Remote<typeof real["math"]>` gives at compile
 * time. No export names are extracted anywhere; the surface is shape-agnostic.
 * The refId embeds this worker's id and a fresh counter; the main thread maps
 * the specifier to the owner and answers with `__serve-actor` /
 * `__ref-acquired`.
 */
export function createDependencyStub(
  specifier: string,
): RemoteActor<Record<string, unknown>> {
  const existing = stubBySpecifier.get(specifier);
  if (existing) return existing;

  let workerIdPrefix = globalWorkerId();
  let refId = "";
  const queue: Array<{
    method: string;
    args: unknown[];
    resolve: (value: unknown) => void;
    reject: (reason: unknown) => void;
  }> = [];
  let materialized: RemoteActor<Record<string, unknown>> | undefined;

  // One acquire per specifier; every member proxy routes through it.
  const acquireAndCall = (
    method: string,
    args: unknown[],
  ): Promise<unknown> => {
    if (materialized) {
      return (materialized as unknown as Record<
        string,
        (...a: unknown[]) => Promise<unknown>
      >)[method](...args);
    }
    if (refId.length === 0) {
      refId = `${workerIdPrefix}:${specifier}:${++dependencyCallCount}`;
    }
    postAcquireActor(specifier, refId);
    return new Promise<unknown>((resolve, reject) => {
      queue.push({ method, args, resolve, reject });
    });
  };

  const surface = new Proxy({} as RemoteActor<Record<string, unknown>>, {
    get(_target, prop) {
      if (prop === "then") return undefined;
      if (prop === "dispose") {
        return () => {
          stubBySpecifier.delete(specifier);
          (materialized as { dispose?(): Promise<void> } | undefined)?.dispose?.();
          return Promise.resolve();
        };
      }
      if (typeof prop === "string") {
        // A member proxy for one export of the dependency. Calling
        // `math.double(21)` reaches here with prop === "math"; the member's own
        // method calls forward through the shared acquire as "math.double" —
        // the same flattened method name the owner serves over the channel.
        const member = new Proxy({} as Record<string, (...a: unknown[]) => Promise<unknown>>, {
          get(_m, method) {
            if (method === "then") return undefined;
            if (method === "dispose") {
              return () => acquireAndCall(`${String(prop)}.dispose`, []);
            }
            if (typeof method === "string") {
              return (...args: unknown[]) => acquireAndCall(`${String(prop)}.${method}`, args);
            }
            return undefined;
          },
        });
        return member;
      }
      return undefined;
    },
  });
  stubBySpecifier.set(specifier, surface);

  // The worker id arrives via the standard worker-actor frame; the surface's
  // first call before that would use the placeholder prefix, so re-route any
  // already-built refId when the id lands.
  registerControlHandler("__worker-id", (frame) => {
    if (typeof frame.refId !== "string") return;
    workerIdPrefix = frame.refId;
    if (refId.length === 0) return;
    const separator = refId.indexOf(":");
    if (separator <= 0) return;
    refId = `${workerIdPrefix}${refId.slice(separator)}`;
  });

  // Acquire completion: the main thread handed this worker the other end of
  // the owner↔holder channel. Materialize the real proxy and flush queued calls.
  registerControlHandler("__ref-acquired", (frame) => {
    if (frame.port === undefined || frame.refId !== refId) {
      return;
    }
    const channel = connectChannel(frame.port);
    const registry = activeRegistry();
    if (registry) registry.registerChannel(channel);
    const real = createRefProxyForStub(channel, refId, registry);
    materialized = real;
    // Defer the flush one microtask: the owner's __serve-ref handler binds its
    // channel in the same message-dispatch turn; a same-turn send on the fresh
    // port pair is still delivered (MessagePort queues), but this keeps the
    // trace ordering deterministic.
    const calls = queue.splice(0);
    for (const call of calls) {
      queueMicrotask(() => {
        (real as unknown as Record<string, (...a: unknown[]) => Promise<unknown>>)[
          call.method
        ](...call.args).then(call.resolve, call.reject);
      });
    }
  });

  return surface;
}

function globalWorkerId(): string {
  return (globalThis as unknown as { __maieuticsWorkerId?: string }).__maieuticsWorkerId ??
    "w?";
}

/** Posts the Maieutics acquire request; the host's router answers with __serve-actor/__ref-acquired. */
function postAcquireActor(specifier: string, refId: string): void {
  (self as unknown as { postMessage(m: unknown): void }).postMessage({
    type: "__acquire-actor",
    specifier,
    refId,
  });
}

let dependencyCallCount = 0;

/** The stub's received channels must join the worker's registry for failAll cleanup. */
function activeRegistry(): { registerChannel(channel: unknown): void } | undefined {
  // The worker-actor runtime registers its per-worker registry on the worker
  // context; the SDK entry shares that context, so the accessor returns the
  // same instance the serveWorker runtime uses.
  return (globalThis as unknown as {
    __workerActorRegistry?: { registerChannel(channel: unknown): void };
  }).__workerActorRegistry;
}

function createRefProxyForStub(
  channel: ReturnType<typeof connectChannel>,
  refId: string,
  registry: { registerChannel(channel: unknown): void } | undefined,
): RemoteActor<Record<string, unknown>> {
  const pending = new Map<number, {
    resolve: (value: unknown) => void;
    reject: (reason: unknown) => void;
  }>();
  let nextCallId = 1;
  let closed = false;
  channel.onMessage((message) => {
    const frame = message as {
      type?: string;
      id?: number;
      ok?: boolean;
      value?: unknown;
      error?: { name: string; message: string; stack?: string };
    };
    if (frame?.type !== "result") return;
    const call = pending.get(frame.id ?? -1);
    if (!call) return;
    pending.delete(frame.id ?? -1);
    if (frame.ok) call.resolve(frame.value);
    else call.reject(new Error(frame.error?.message ?? "Actor call failed"));
  });
  const proxy = new Proxy({} as RemoteActor<Record<string, unknown>>, {
    get(_target, prop) {
      if (prop === "then") return undefined;
      if (prop === "dispose") {
        return () => {
          if (!closed) {
            closed = true;
            channel.close();
            for (const call of pending.values()) call.reject(new Error("Actor reference disposed"));
            pending.clear();
          }
          return Promise.resolve();
        };
      }
      if (typeof prop === "string") {
        return (...args: unknown[]) => {
          if (closed) return Promise.reject(new Error("Actor reference is disposed"));
          return new Promise<unknown>((resolve, reject) => {
            const id = nextCallId++;
            pending.set(id, { resolve, reject });
            try {
              channel.send({ type: "call", id, method: prop, args });
            } catch (error) {
              reject(error);
            }
          });
        };
      }
      return undefined;
    },
  });
  void refId;
  void registry;
  return proxy;
}

/**
 * Load hook: intercepts declared dependency specifiers inside the worker's
 * module graph and serves a synthesized stub that binds every known export to
 * a remote callable. The import map used for `deno check`/editors resolves the
 * same specifiers to the real module (compile-time `Remote<T>`); this hook is
 * the only runtime resolver.
 */
/**
 * Load hook: intercepts import edges inside the worker's module graph that
 * resolve to a registered actor entry, and serves a synthesized stub whose
 * default export is the lazy acquire surface. Only edges that hit the
 * registry (two-way match below) are redirected; every other import —
 * local files, third-party jsr/npm packages — passes through unchanged. The
 * import map used for `deno check`/editors resolves the same specifiers to
 * the real module (compile-time `Remote<T>`); this hook is the only runtime
 * resolver.
 *
 * The two-way match covers the forms a consumer may write:
 *   - canonical: the raw specifier, normalized (strip `jsr:` prefix, strip the
 *     `@version` segment after the package name) equals a registry specifier;
 *   - resolved: `nextResolve` (which applies the import map) yields a URL that
 *     equals a registry entry URL (import-map aliases, relative paths, ...).
 */
function installDependencyLoadHook(
  actorEntries: readonly { specifier: string; entryUrl: string }[],
): void {
  if (actorEntries.length === 0) return;
  const canonical = new Map(
    actorEntries.map((entry) => [normalizeSpecifier(entry.specifier), entry.specifier]),
  );
  const entryUrls = new Map(
    actorEntries.map((entry) => [normalizeFileUrl(entry.entryUrl), entry.specifier]),
  );
  // node:module is CJS; registerHooks is Deno's implemented (sync) form of
  // Node's registerHooks. `import` is hoisted to module top level (fine: the
  // worker runs after the SDK module finished evaluating).
  const { registerHooks } = importNodeModuleHooks();
  registerHooks({
    resolve(specifier: string, context: unknown, nextResolve: (s: string, c: unknown) => unknown) {
      // The stub is keyed by the registered canonical specifier, so the
      // acquire routes to the right owner regardless of the consumer's import
      // form (jsr: prefix, version segment, import-map alias).
      const canonicalSpecifier = canonical.get(normalizeSpecifier(specifier));
      if (canonicalSpecifier !== undefined) {
        return { url: stubUrl(canonicalSpecifier), shortCircuit: true };
      }
      const resolved = nextResolve(specifier, context) as { url: string };
      const byUrl = entryUrls.get(normalizeFileUrl(resolved.url));
      if (byUrl !== undefined) {
        return { url: stubUrl(byUrl), shortCircuit: true };
      }
      return resolved;
    },
    load(url: string, context: unknown, nextLoad: (u: string, c: unknown) => unknown) {
      if (url.startsWith(STUB_SCHEME)) {
        const specifier = decodeURIComponent(url.slice(STUB_SCHEME.length));
        return {
          format: "module",
          source: stubSource(specifier),
          shortCircuit: true,
        };
      }
      return nextLoad(url, context);
    },
  });
}

const STUB_SCHEME = "maieutics-stub:";

function stubUrl(specifier: string): string {
  return `${STUB_SCHEME}${encodeURIComponent(specifier)}`;
}

/**
 * Normalizes a specifier to its canonical comparison form: strip a `jsr:`
 * prefix and the `@version` segment between the package name and the first
 * subpath (`jsr:@scope/name@0.1/subpath` → `@scope/name/subpath`). Import-map
 * aliases and relative paths are left as-is; they are covered by the URL match.
 */
function normalizeSpecifier(specifier: string): string {
  let value = specifier.startsWith("jsr:") ? specifier.slice(4) : specifier;
  // jsr specifier shape: `@scope/name@version/subpath`. The version `@` is
  // the second `@` (after the leading scope `@`); strip from it up to the
  // next `/` (the subpath start). `@scope/name@0.1/main` → `@scope/name/main`.
  const at = value.indexOf("@", 1);
  const slashAfterVersion = at === -1 ? -1 : value.indexOf("/", at + 1);
  if (at > 0 && slashAfterVersion !== -1) {
    value = value.slice(0, at) + value.slice(slashAfterVersion);
  }
  return value;
}

/** Normalizes a file URL for comparison (resolves `/tmp` → `/private/tmp` symlinks). */
function normalizeFileUrl(url: string): string {
  if (!url.startsWith("file://")) return url;
  try {
    return new URL(url).href;
  } catch {
    return url;
  }
}

function importNodeModuleHooks(): { registerHooks(hooks: unknown): void } {
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  // deno-lint-ignore no-explicit-any
  const nodeModule = (globalThis as any).process?.getBuiltinModule?.("node:module") ??
    // Deno exposes node: built-ins as static imports; fall back to a no-op
    // when the runtime lacks the hooks API (never in the supported matrix).
    { registerHooks: () => {} };
  return nodeModule as { registerHooks(hooks: unknown): void };
}

function stubSource(specifier: string): string {
  // The stub imports the SDK entry module for the acquire machinery: the
  // plugin module graph shares the SDK instance with the worker entry (Deno
  // caches modules by URL per worker), so no re-initialization occurs, and the
  // load hook is installed in this same graph, so the SDK import resolves
  // normally. The default export is the single lazy surface.
  return `
import { createDependencyStub } from ${JSON.stringify(SDK_ENTRY_URL)};
export default createDependencyStub(${JSON.stringify(specifier)});
`;
}

function scopePostMessage(message: unknown): void {
  (self as unknown as { postMessage(m: unknown): void }).postMessage(message);
}

// —— Link peer surface (flattened namespace) ——

export { flattenSurface };
