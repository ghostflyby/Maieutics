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
  ACTOR_CODEC_TAG,
  actorRefCodec,
  clearActorExports,
  clearNamespaceSurface,
  decodeActorValue,
  encodeActorValue,
  flattenSurface,
  REF_PROXY_BRAND,
  registerActorExport,
  type RemoteActor,
  remoteActor,
  setNamespaceSurface,
  setSpecifierAcquire,
} from "./actor_ref.ts";
import {
  CODEC_PLACEHOLDER_KEY,
  connectChannel,
  type DecodeContext,
  getActiveRegistry,
  type PeerRpc,
  registerControlHandler,
} from "@ghostflyby/worker-actor/codec";
import { attachLazyIterator, type LinkHandle, serveWorker } from "@ghostflyby/worker-actor";
import { collectionStreamCodec, markCollectionStream } from "./collection_stream.ts";
import {
  type AdmissionContext,
  type AdmissionHook,
  admissionMailboxFor,
  type AdmissionRequestFrame,
  answerAdmission,
} from "./admission.ts";
import { http, HTTP_AGGREGATOR_SPECIFIER } from "./http.ts";
import { httpCodec } from "./http_codec.ts";
import {
  bindDefiningWorker,
  collection,
  type CollectionStream,
  type CollectionValue,
  createRemoteIdentity,
  CURRENT_MODULE,
  defineExtensionPoint as defineReactiveExtensionPoint,
  defineServiceExtensionPoint as defineServiceExtensionPointReactive,
  evaluateAdmissionHook,
  type ExtensionPointIdentity,
  isExtensionPoint,
  isLocalExtensionPoint,
  isRemoteExtensionPoint,
  provide,
  providerCount,
  type ProviderRegistration,
  type ReactiveValue,
  type Remote,
  setRemoteProvide,
  setRemoteUnprovide,
  setValueTransformer,
  setValueUntransformer,
  setWorkerSpecifier,
  signal,
  snapshot,
  subscribe,
  unprovide,
  values,
} from "./reactive.ts";

const NAMESPACE = "maieutics/extensionPoint/v1";

/** Versioned extension point identity markers. */
export const ExtensionPoint: {
  readonly McpDiscover: symbol;
  readonly ToolPreInvoke: symbol;
  readonly ToolPostInvoke: symbol;
} = {
  McpDiscover: Symbol.for(`${NAMESPACE}/mcp.discover`),
  ToolPreInvoke: Symbol.for(`${NAMESPACE}/tools.preInvoke`),
  ToolPostInvoke: Symbol.for(`${NAMESPACE}/tools.postInvoke`),
};

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
 * original runtime identity. This is the host-extension-point form; the
 * reactive contract-identity form (single-argument) lives on the `./reactive`
 * SDK path.
 */
export function defineExtensionPoint<K extends ExtensionPointName>(
  name: K,
  impl: ExtensionPointInput<K>,
): ExtensionPointImpl<K>;
export function defineExtensionPoint(
  name: string,
  impl: unknown,
): ExtensionPointImpl<ExtensionPointName> {
  const symbol = ExtensionPoint[name as ExtensionPointName];
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
  return impl as ExtensionPointImpl<ExtensionPointName>;
}

/**
 * Declares a service extension-point identity. The element type `T` is the
 * service's original type; a provider contributes a live service instance
 * (no export, no handle) and consumers receive a `Remote<T>` proxy — every
 * method becomes a Promise-returning callable. Provided values are converted
 * to remote references automatically, whether the provider and the defining
 * worker are the same worker or different workers.
 *
 * ```ts
 * const services = defineServiceExtensionPoint<{ hello(): string }>("services");
 * provide(services, signal({ hello() { return "hi"; } }));
 * // consumer: for await (const svc of values(services)) await svc.hello();
 * ```
 */
export function defineServiceExtensionPoint<T = unknown>(
  name: string,
): ExtensionPointIdentity<T> {
  return defineServiceExtensionPointReactive<T>(name);
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
): RemoteActor<T>;

/**
 * Declares a single-function actor surface: the function becomes the `call`
 * member of the surface, so consumers call it as `dep.<export>.call(...)`.
 * This lets a bare function export be converted into a dedicated export.
 */
export function defineActor<T extends (...args: unknown[]) => unknown>(
  fn: T,
): RemoteActor<{ call: T }>;

export function defineActor(
  surfaceOrFn: Record<string, unknown> | ((...args: unknown[]) => unknown),
): RemoteActor<Record<string, unknown>> {
  const surface = typeof surfaceOrFn === "function" ? { call: surfaceOrFn } : surfaceOrFn;
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
  return remoteActor(surface, ownSpecifier(), "actor") as RemoteActor<Record<string, unknown>>;
}

// —— Transparent services (original-type instances in collections) ——

// The service brand is an explicit marker: a `defineService`-marked value is
// converted to a remote actor reference placeholder before it enters a service
// extension point's collection. The conversion is NOT gated on the brand — a
// service extension point converts every provided value (see
// convertServiceValue) — but marking makes the intent explicit and lets a
// provider opt a value into reference semantics on a data extension point too.
const SERVICE_BRAND = "maieutics/service/v1";

/**
 * Marks an object as a service: a live instance that should be exposed across
 * workers as a remote reference rather than serialized as data. The returned
 * value is the same object (type `T` unchanged).
 *
 * On a service extension point (`defineServiceExtensionPoint`) the conversion
 * happens automatically for every provided value, so `defineService` is
 * optional there. On a data extension point it has no effect: values are
 * structured-cloned as-is.
 *
 * ```ts
 * const service = { hello(): string { return "hi"; } };
 * provide(ep, signal(defineService(service))); // original instance, no export
 * ```
 */
export function defineService<T extends object>(service: T): T {
  if (typeof service !== "object" || service === null) {
    throw new TypeError("defineService() expects an object.");
  }
  (service as Record<string, unknown>)[SERVICE_BRAND] = true;
  return service;
}

/** True when `value` is a service marked with {@link defineService}. */
export function isService(value: unknown): value is object {
  return (
    typeof value === "object" &&
    value !== null &&
    (value as Record<string, unknown>)[SERVICE_BRAND] === true
  );
}

let serviceRefCount = 0;

/** Service object → its transmittable placeholder. Each service instance is
 * converted once and registered under one service surface name; repeated
 * provide/snapshot reads reuse the same placeholder instead of growing the
 * surface registry. */
let servicePlaceholderByObject = new WeakMap<object, unknown>();

/** Converts a service value to a transmittable remote reference: the service
 * becomes an actor proxy registered under a service surface name, so a
 * consumer's specifier-based acquire resolves it. Already-converted values
 * (placeholders, including services forwarded from another worker) pass
 * through unchanged; plain data also passes through unchanged. Called only for
 * service extension points, whose values are live instances by contract. */
function convertServiceValue(value: unknown): unknown {
  if (typeof value !== "object" || value === null) return value;
  if ((value as Record<string, unknown>)[CODEC_PLACEHOLDER_KEY] === ACTOR_CODEC_TAG) {
    return value;
  }
  // A received reference proxy (a service forwarded from another worker,
  // decoded at an RPC boundary) is already a remote reference — keep it as-is;
  // re-registering it under a new surface would orphan the routing identity.
  if ((value as Record<symbol, unknown>)[REF_PROXY_BRAND] === true) {
    return value;
  }
  const cached = servicePlaceholderByObject.get(value as object);
  if (cached !== undefined) return cached;
  const name = `__svc:${++serviceRefCount}`;
  // Register the service so a consumer's acquire (specifier + name) can serve
  // it; the surface is the service object itself.
  registerActorExport(name, ownSpecifier(), value);
  const proxy = remoteActor(value, ownSpecifier(), name);
  const placeholder = encodeActorValue(proxy);
  servicePlaceholderByObject.set(value as object, placeholder);
  return placeholder;
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

/**
 * Resolves a runtime-computed specifier through the same pipeline as static
 * imports: plugin actor specifiers load the synthesized acquire stub (the load
 * hook's canonical match), bare aliases resolve via the process import map
 * (the kernel materializes the merged plugin entries), and self-contained
 * jsr:/npm: specifiers resolve via the registry. The
 * unanalyzable-dynamic-import warning at publish is expected and benign — the
 * specifier is provided at runtime and is not rewritten by JSR. For actor
 * targets prefer {@link defineDependency} / {@link depActor}, which skip
 * module loading entirely and share the stub cache with static imports.
 */
export function dynamicImport<T = Record<string, unknown>>(specifier: string): Promise<T> {
  return import(specifier) as Promise<T>;
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
  actorEntries: readonly {
    specifier: string;
    entryUrl: string;
    /**
     * Contract identities (extension points) the dependency worker exports,
     * reported by the host from its ready frame. The load hook synthesizes
     * stub exports for these, so `import { ep } from "contract"` yields a
     * remote identity carrying the defining worker's specifier.
     */
    identities?: readonly { exportName: string; name: string; owner: string }[];
  }[];
}

let ownSpecifierValue = "";
const servingApi: Record<string, (...args: unknown[]) => unknown> = {};
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
  setWorkerSpecifier(config.specifier);
  setRemoteProvide(remoteProvideImpl);
  setRemoteUnprovide(remoteUnprovideImpl);
  // Specifier-addressed acquires (decoded service references in collections)
  // route through the host's __acquire-actor frame.
  setSpecifierAcquire(postAcquireActor);
  // Service values (defineService-marked) become remote actor references; the
  // actorRefCodec encodes them to cloneable placeholders on the wire. Received
  // placeholders decode back to Remote<T> proxies.
  setValueTransformer(convertServiceValue);
  setValueUntransformer(decodeActorValue);

  // The dependency stubs need this worker's id to build routeable refIds;
  // expose the worker-actor runtime's registry to them via the global slot.
  registerControlHandler("__worker-id", (frame) => {
    if (typeof frame.refId !== "string") return;
    (globalThis as unknown as { __maieuticsWorkerId: string }).__maieuticsWorkerId = frame.refId;
  });

  installDependencyLoadHook(config.actorEntries);

  serveWorker(servingApi, {
    codecs: [actorRefCodec, collectionStreamCodec, httpCodec],
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

  // Admission verdicts (ADR 0021 decision 9): when this worker defines a
  // contract, the host relays providers' `__admit` frames here. The hook runs
  // synchronously inside the message handler — the providing worker is parked
  // on the shared buffer. Without a hook the contract accepts everything, but
  // the verdict is always written so the provider never waits out its timeout.
  scope.addEventListener("message", (event: MessageEvent): void => {
    const frame = event.data as Partial<AdmissionRequestFrame>;
    if (frame?.type !== "__admit" || !(frame.sab instanceof SharedArrayBuffer)) {
      return;
    }
    answerAdmission(admissionMailboxFor(frame.sab), () =>
      evaluateAdmissionHook(frame.ep ?? "", {
        extensionPoint: frame.ep ?? "",
        providerSpecifier: contributorPrefix(frame.providerKey ?? ""),
        providerModule: frame.providerModule ?? "",
        existingProviders: existingContributorPrefixes(frame.ep ?? ""),
      }));
  });
}

let initStarted = false;

interface InitFrame {
  type: "init";
  entryUrl: string;
}

let linkedSurface: Record<string, unknown> = {};

async function initialize(entryUrl: string): Promise<void> {
  if (entryUrl.length === 0) {
    throw new Error("The init frame is missing the plugin entry URL.");
  }
  const namespace = (await import(entryUrl)) as Record<string, unknown>;
  linkedSurface = namespace;
  setNamespaceSurface(namespace);
  scanExports(namespace);
  // serveWorker resolved methods at call time through the api object; the
  // extension points are known only after init, so repopulate the object now.
  for (const key of Object.keys(servingApi)) delete servingApi[key];
  for (const [name, impl] of extensionPoints) {
    const invoke = (request: unknown): unknown => {
      const raw = typeof impl === "function"
        ? (impl as (context: unknown) => unknown)(request)
        : (impl as { handler(context: unknown): unknown }).handler(request);
      // A handler returning an AsyncIterable must be passed through untouched:
      // awaiting it would flatten the stream into an object and break the
      // worker-actor iterable codec. Only promise values are awaited.
      if (raw instanceof Promise) return raw;
      return raw;
    };
    servingApi[name] = invoke;
  }
  // Internal host hook: a declared dependency provider stopped; drop its remote
  // contributions. Exposed on the RPC surface because the host notifies through
  // the worker-actor channel (a bare postMessage frame would be ignored by the
  // worker runtime's onmessage dispatcher).
  servingApi["__maieuticsProviderDead"] = (specifier: unknown): void => {
    if (typeof specifier !== "string") return;
    removeContributionsByProvider(specifier);
  };
  scopePostMessage({
    type: "ready",
    specifier: ownSpecifierValue,
    extensionPoints: [...extensionPoints.keys()],
    // Contract identities (export key + extension point name + defining module
    // URL) are reported to the host so dependency workers can synthesize stub
    // identity exports for them (stub identity replacement).
    contractIdentities: [...contractExportIdentities],
  });
}

function scanExports(namespace: Record<string, unknown>): void {
  const extensions = new Map<string, unknown>();
  const actors = new Map<string, object>();
  const contractExports: ContractExportIdentity[] = [];
  for (const [name, value] of Object.entries(namespace)) {
    if (typeof value === "function" || (typeof value === "object" && value !== null)) {
      for (
        const [extensionName, symbol] of Object.entries(ExtensionPoint as Record<string, symbol>)
      ) {
        if ((value as Record<symbol, unknown>)[symbol] === true) {
          extensions.set(extensionName, value);
        }
      }
      // Reactive (contract-mode) extension point identities: bind the defining
      // worker specifier and register a remote collection actor under the
      // extension point's name so providers in other workers can route their
      // contributions to this worker's collection. Identities are not host
      // callable, so they stay out of extensionPoints (the servingApi surface).
      // The surface is keyed by the identity name (what providers address), not
      // the export key, so `provide(ep, ...)` on an imported identity routes
      // here regardless of the local export spelling.
      if (isExtensionPoint(value)) {
        bindDefiningWorker(value, ownSpecifierValue);
        const identityName = value.name;
        extensionPointIdentities.set(identityName, value);
        contractExports.push({
          exportName: name,
          name: identityName,
          owner: value.owner,
          serviceKind: value.serviceKind,
        });
        registerActorExport(identityName, ownSpecifierValue, collectionActorSurface(identityName));
      }
      if ((value as Record<symbol, unknown>)[ACTOR_MARKER] === true) actors.set(name, value);
    }
  }
  extensionPoints = extensions;
  contractExportIdentities = contractExports;
  for (const [name, surface] of actors) {
    // The defineActor proxy exposes the real surface via __surface so the
    // owner can flatten it (a Proxy cannot be Object.entries-enumerated).
    const realSurface = (surface as { __surface?: object }).__surface ?? surface;
    registerActorExport(name, ownSpecifierValue, realSurface);
  }
}

/**
 * The remote collection actor surface exposed by the defining worker for one
 * extension point. Providers in other workers acquire this surface (via the
 * dependency stub under the extension point's name) and call `add` to
 * contribute; `changes` streams the aggregated collection snapshots.
 *
 * The surface carries a marker so the acquire router prefers ordinary actor
 * surfaces when a worker exports both an actor and a collection under the
 * same specifier.
 */
const COLLECTION_SURFACE_MARKER = Symbol.for("maieutics/extensionPoint/v1/collectionSurface");

/** One remote contribution tracked by the defining worker: the local signal
 * plus the pull-loop stop, so `remove`/dispose can withdraw it. */
interface RemoteContribution {
  registration: ProviderRegistration<unknown>;
  stop: () => void;
}

/** Remote contributions by extension point name, keyed by provider identity
 * (the acquiring worker's refId prefix serves as the per-provider key; one
 * provider contributes once per extension point). */
const remoteContributions = new Map<string, Map<string, RemoteContribution>>();

/**
 * The contributing worker's canonical specifier: everything before the last
 * `:` of a provider key (`<specifier>:<uuid>`). Specifiers never contain `:`
 * (they are `<plugin>/<entrypoint>`), so the split is unambiguous.
 */
function contributorPrefix(providerKey: string): string {
  const cut = providerKey.lastIndexOf(":");
  return cut === -1 ? providerKey : providerKey.slice(0, cut);
}

/**
 * Specifiers of the contract's current live contributors — the aggregate's
 * `existingProviders` view for admission hooks (ADR 0021 decision 9). Remote
 * contributions come from the provider keys; a local contribution is this
 * worker itself.
 */
function existingContributorPrefixes(name: string): string[] {
  const prefixes = new Set<string>();
  for (const key of remoteContributions.get(name)?.keys() ?? []) {
    prefixes.add(contributorPrefix(key));
  }
  try {
    const identity = lookupIdentity(name);
    if (identity !== undefined && providerCount(identity) > 0) {
      prefixes.add(ownSpecifier());
    }
  } catch {
    // Pre-init (no specifier): local providers cannot exist yet.
  }
  return [...prefixes];
}

/** The provider identity of an incoming contribution: the caller's worker id
 * prefix from the acquire refId (rewritten by the host to the owner id, but
 * the original holder prefix survives in the frame's refId — see below). */
let remoteContributionCounter = 0;

function collectionActorSurface(name: string): object {
  return {
    [COLLECTION_SURFACE_MARKER]: true,
    add(
      initial: unknown,
      changes: AsyncIterable<unknown>,
      providerKey?: string,
    ): Promise<void> {
      const ep = lookupIdentity(name);
      if (ep === undefined) return Promise.resolve();
      applyRemoteContribution(ep, initial, changes, providerKey);
      return Promise.resolve();
    },
    remove(providerKey?: string): Promise<void> {
      const ep = lookupIdentity(name);
      if (ep === undefined) return Promise.resolve();
      if (providerKey === undefined) {
        removeAllRemoteContributions(name);
      } else {
        removeRemoteContribution(name, providerKey);
      }
      return Promise.resolve();
    },
    changes(): AsyncIterable<unknown[]> {
      const ep = lookupIdentity(name);
      // Mark the stream so the collection-stream codec transports its elements
      // through the registry (services arrive as Remote<T> proxies, not
      // structured-clone failures).
      return ep === undefined
        ? emptyAsyncIterable()
        : markCollectionStream(subscribe(ep) as AsyncIterable<unknown[]>);
    },
  };
}

/** True when a registered surface is a remote collection actor. */
export function isCollectionSurface(surface: object): boolean {
  return (surface as Record<symbol, unknown>)[COLLECTION_SURFACE_MARKER] === true;
}

function lookupIdentity(name: string): ExtensionPointIdentity | undefined {
  return extensionPointIdentities.get(name);
}

function emptyAsyncIterable<T>(): AsyncIterable<T> {
  return {
    async *[Symbol.asyncIterator](): AsyncIterator<T> {
      // never yields
    },
  };
}

/** Applies a remote provider's contribution to the local collection: register
 * the initial value, then keep pulling the change stream and updating the
 * signal. The pull loop runs independently of any consumer so the producer's
 * values stream continuously; the signal stays registered until the provider's
 * stream ends (undefined values simply drop the contribution from snapshots).
 *
 * `providerKey` identifies the contributing provider (a client-generated id)
 * so a later `remove`/`unprovide` can withdraw exactly this contribution and a
 * repeated `add` with the same key replaces the previous contribution instead
 * of stacking duplicates.
 *
 * The loop must never throw outward: a failed/ended stream unregisters the
 * contribution (the provider is gone or the channel closed). */
function applyRemoteContribution(
  extensionPoint: ExtensionPointIdentity,
  initial: unknown,
  changes: AsyncIterable<unknown>,
  providerKey?: string,
): void {
  const name = extensionPoint.name;
  const key = providerKey ?? `remote:${++remoteContributionCounter}`;
  // Replace a previous contribution from the same provider (idempotent add).
  removeRemoteContribution(name, key);

  const value = signal<unknown | undefined>(initial as unknown | undefined);
  const registration = provide(extensionPoint, value as ReactiveValue<unknown | undefined>);
  let stopped = false;
  const stop = (): void => {
    if (stopped) return;
    stopped = true;
    unprovide(registration);
    const byName = remoteContributions.get(name);
    if (byName?.get(key)?.registration === registration) {
      byName.delete(key);
      if (byName.size === 0) remoteContributions.delete(name);
    }
  };
  // Merge into the existing per-name map instead of replacing it: replacing
  // would drop references to contributions registered under other keys, whose
  // signals would linger unremovable in the collection.
  const byName = remoteContributions.get(name) ?? new Map<string, RemoteContribution>();
  byName.set(key, { registration, stop });
  remoteContributions.set(name, byName);

  void (async () => {
    try {
      const iterator = changes[Symbol.asyncIterator]();
      while (!stopped) {
        const next = await iterator.next();
        if (stopped) return;
        if (next.done) return stop();
        value.value = next.value as unknown | undefined;
      }
    } catch {
      // The provider's stream ended or was abandoned; withdraw the contribution.
      stop();
    }
  })();
}

/** Withdraws the remote contribution identified by `providerKey`, stopping its
 * pull loop and unregistering its signal from the collection. */
function removeRemoteContribution(name: string, providerKey: string): void {
  const byName = remoteContributions.get(name);
  if (byName === undefined) return;
  byName.get(providerKey)?.stop();
}

/** Withdraws every contribution whose providerKey carries `providerSpecifier`
 * as its prefix (the provider worker is dead or stopped). */
function removeContributionsByProvider(providerSpecifier: string): void {
  const prefix = `${providerSpecifier}:`;
  for (const byName of remoteContributions.values()) {
    for (const [key, contribution] of byName) {
      if (key.startsWith(prefix)) contribution.stop();
    }
  }
}

/** Withdraws every remote contribution of one extension point. */
function removeAllRemoteContributions(name: string): void {
  const byName = remoteContributions.get(name);
  if (byName === undefined) return;
  for (const contribution of byName.values()) contribution.stop();
}

/** Routes provide() to the defining worker's remote collection. The providerKey
 * (generated on the providing side) identifies this contribution so a later
 * unprovide can withdraw exactly it. */
async function remoteProvideImpl(
  specifier: string,
  name: string,
  initial: unknown,
  changes: AsyncIterable<unknown>,
  providerKey: string,
): Promise<void> {
  const stub = createDependencyStub(specifier, name);
  const collection = (stub as unknown as Record<string, unknown>)[name] as {
    add(
      initial: unknown,
      changes: AsyncIterable<unknown>,
      providerKey: string,
    ): Promise<void>;
  };
  await collection.add(initial, changes, providerKey);
}

/** Routes unprovide() of a remote contribution back to the defining worker,
 * which withdraws the contribution by providerKey. */
async function remoteUnprovideImpl(
  specifier: string,
  name: string,
  providerKey: string,
): Promise<void> {
  const stub = createDependencyStub(specifier, name);
  const collection = (stub as unknown as Record<string, unknown>)[name] as {
    remove(providerKey: string): Promise<void>;
  };
  await collection.remove(providerKey);
}

let extensionPoints = new Map<string, unknown>();
const extensionPointIdentities = new Map<string, ExtensionPointIdentity>();

/** One contract identity exported by this worker's entry module: the export
 * key (what a dependency imports), the extension point name (what providers
 * address), the defining module URL (the identity's owner), and the element
 * kind (data or service) so imported identities carry the same category. */
interface ContractExportIdentity {
  readonly exportName: string;
  readonly name: string;
  readonly owner: string;
  readonly serviceKind: "data" | "service";
}

/** Contract identities reported in the ready frame; cleared on dispose. */
let contractExportIdentities: ContractExportIdentity[] = [];

function disposePlugin(): void {
  // Stop every remote pull loop; each stop unregisters its contribution.
  for (const byName of remoteContributions.values()) {
    for (const contribution of byName.values()) contribution.stop();
  }
  remoteContributions.clear();
  clearActorExports();
  // Forget converted service placeholders: their surfaces were just cleared,
  // so a re-init must convert fresh (the previous placeholders would route to
  // surfaces that no longer exist).
  servicePlaceholderByObject = new WeakMap();
  extensionPoints.clear();
  extensionPointIdentities.clear();
  contractExportIdentities = [];
  linkedSurface = {};
  clearNamespaceSurface();
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
): RemoteActor<Record<string, unknown>>;
/**
 * Internal: a stub whose acquire targets a specific named surface of the
 * dependency worker (the remote collection of one extension point) instead of
 * its default actor surface. The name rides on the acquire frame so the owner
 * serves exactly that surface. Each named stub is cached independently, so a
 * worker can hold both the default surface and one named collection per
 * extension point.
 */
export function createDependencyStub(
  specifier: string,
  surfaceName: string,
): RemoteActor<Record<string, unknown>>;
export function createDependencyStub(
  specifier: string,
  surfaceName?: string,
): RemoteActor<Record<string, unknown>> {
  const cacheKey = surfaceName === undefined ? specifier : `${specifier}\u0000${surfaceName}`;
  const existing = stubBySpecifier.get(cacheKey);
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
    postAcquireActor(specifier, refId, surfaceName);
    return new Promise<unknown>((resolve, reject) => {
      queue.push({ method, args, resolve, reject });
    });
  };

  const surface = new Proxy({} as RemoteActor<Record<string, unknown>>, {
    get(_target, prop) {
      if (prop === "then") return undefined;
      if (prop === "dispose") {
        return () => {
          stubBySpecifier.delete(cacheKey);
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
              return (...args: unknown[]) => {
                const p = acquireAndCall(`${String(prop)}.${method}`, args);
                // A method may return an AsyncIterable over the wire; attach a
                // lazy iterator so `for await` works exactly like worker-actor's
                // Remote<T> projection (the first next() awaits the acquire and
                // iterates the resolved stream). Ordinary single-value methods
                // are unaffected: nobody for-await's them.
                attachLazyIterator(p);
                return p;
              };
            }
            return undefined;
          },
        });
        return member;
      }
      return undefined;
    },
  });
  stubBySpecifier.set(cacheKey, surface);

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
    const registry = getActiveRegistry();
    if (registry) registry.registerChannel(channel);
    const real = createRefProxyForStub(channel, registry);
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

/** Posts the Maieutics acquire request; the host's router answers with __serve-actor/__ref-acquired.
 * An optional name addresses a specific surface (a remote collection) instead
 * of the worker's default actor surface. */
function postAcquireActor(specifier: string, refId: string, name?: string): void {
  (self as unknown as { postMessage(m: unknown): void }).postMessage({
    type: "__acquire-actor",
    specifier,
    refId,
    ...(name === undefined ? {} : { name }),
  });
}

let dependencyCallCount = 0;

/** The stub's received channels must join the worker's registry for failAll cleanup. */
function createRefProxyForStub(
  channel: ReturnType<typeof connectChannel>,
  registry: DecodeContext["registry"] | undefined,
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
    if (frame.ok) {
      // The value arrives encoded (an AsyncIterable return travels as an
      // iterable-codec placeholder with a MessagePort); decode it through the
      // worker-actor registry so the caller gets a real local stream, exactly
      // like actor_ref's createRefProxy does.
      call.resolve(registry ? registry.decode(frame.value) : frame.value);
    } else {
      call.reject(new Error(frame.error?.message ?? "Actor call failed"));
    }
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
              const transfer: Transferable[] = [];
              channel.send(
                {
                  type: "call",
                  id,
                  method: prop,
                  args: registry ? registry.encode(args, transfer) as unknown[] : args,
                },
                transfer,
              );
            } catch (error) {
              reject(error);
            }
          });
        };
      }
      return undefined;
    },
  });
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
 *
 * Two invariants this hook's shape depends on:
 *   - The unconditional `load` handler is load-bearing: static import edges of
 *     runtime-loaded modules reach the `resolve` hook only while a `load` hook
 *     is installed. Removing the pass-through `load` handler silently breaks
 *     the stub redirect for static imports.
 *   - The `resolve` fallback must stay a pass-through to `nextResolve`. Never
 *     rewrite bare aliases to `jsr:`/`npm:` specifiers here: the hooks
 *     pipeline cannot decline URLs it cannot load and its `jsr:` concretization
 *     is unreliable (bare aliases belong to the process import map, which the
 *     kernel materializes — see docs/plugin-import-resolution.md).
 */
function installDependencyLoadHook(
  actorEntries: readonly {
    specifier: string;
    entryUrl: string;
    identities?: readonly { exportName: string; name: string; owner: string }[];
  }[],
): void {
  const canonical = new Map(
    actorEntries.map((entry) => [normalizeSpecifier(entry.specifier), entry.specifier]),
  );
  const entryUrls = new Map(
    actorEntries.map((entry) => [normalizeFileUrl(entry.entryUrl), entry.specifier]),
  );
  // Contract identities per dependency specifier, used to synthesize stub
  // identity exports (stub identity replacement).
  const identitiesBySpecifier = new Map(
    actorEntries
      .filter((entry) => (entry.identities?.length ?? 0) > 0)
      .map((entry) => [entry.specifier, entry.identities!]),
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
      // Record the module URL before its top-level code runs so
      // defineExtensionPoint can read the defining module from the loader
      // (CURRENT_MODULE). This runs for every module, even with no actor
      // entries, which is why the hook installs unconditionally.
      (globalThis as Record<symbol, unknown>)[CURRENT_MODULE] = url;
      if (url.startsWith(STUB_SCHEME)) {
        const specifier = decodeURIComponent(url.slice(STUB_SCHEME.length));
        return {
          format: "module",
          source: stubSource(specifier, identitiesBySpecifier.get(specifier)),
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

function stubSource(
  specifier: string,
  identities?: readonly {
    exportName: string;
    name: string;
    owner: string;
    serviceKind?: "data" | "service";
  }[],
): string {
  // The stub imports the SDK entry module for the acquire machinery: the
  // plugin module graph shares the SDK instance with the worker entry (Deno
  // caches modules by URL per worker), so no re-initialization occurs, and the
  // load hook is installed in this same graph, so the SDK import resolves
  // normally. The default export is the single lazy surface; contract
  // identities of the dependency are exported by their export name as remote
  // identities (stub identity replacement), so `import { ep } from "contract"`
  // yields the identity with the defining worker's specifier.
  const identityExports = (identities ?? []).map((identity) => {
    const value = `createRemoteIdentity(${JSON.stringify(identity.name)}, ${
      JSON.stringify(identity.owner)
    }, ${JSON.stringify(specifier)}, ${JSON.stringify(identity.serviceKind ?? "data")})`;
    return `export const ${identity.exportName} = ${value};`;
  });
  return `
import { createDependencyStub, createRemoteIdentity } from ${JSON.stringify(SDK_ENTRY_URL)};
export default createDependencyStub(${JSON.stringify(specifier)});
${identityExports.join("\n")}
`;
}

function scopePostMessage(message: unknown): void {
  (self as unknown as { postMessage(m: unknown): void }).postMessage(message);
}

// —— Link peer surface (flattened namespace) ——

export { flattenSurface };

// —— Collection stream transport ——

export { collectionStreamCodec, markCollectionStream } from "./collection_stream.ts";

// —— Reactive extension points ——

export {
  collection,
  createRemoteIdentity,
  CURRENT_MODULE,
  defineReactiveExtensionPoint,
  isExtensionPoint,
  isLocalExtensionPoint,
  isRemoteExtensionPoint,
  provide,
  providerCount,
  snapshot,
  subscribe,
  unprovide,
  values,
};
export type {
  CollectionStream,
  CollectionValue,
  ExtensionPointIdentity,
  ProviderRegistration,
  ReactiveValue,
  Remote,
};
export { computed, effect, signal } from "./reactive.ts";
export { evaluateAdmissionHook, setAdmissionHook } from "./reactive.ts";
export type { AdmissionContext, AdmissionHook } from "./admission.ts";
export { http, HTTP_AGGREGATOR_SPECIFIER } from "./http.ts";
export { httpCodec } from "./http_codec.ts";
