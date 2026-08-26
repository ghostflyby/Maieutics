/**
 * Maieutics actor-reference codec and the main-thread acquire router.
 *
 * Cross-plugin calls reuse worker-actor's reference-acquire semantics
 * (`examples/remote_ref/ref_codec.ts` + `ref_test.ts`): a live surface is
 * wrapped into a transmittable reference token, the first call on the far side
 * triggers an acquire, and the main thread bootstraps a direct owner↔holder
 * channel. Maieutics writes no RPC machinery of its own — everything here is
 * built on the public worker-actor primitives (`registerControlHandler`,
 * `triggerAcquire`, `dispatchControlFrame`, `openChannel`, `connectChannel`,
 * `makeRpcHandler`, `createRpcProxy`, `setMainAcquire`).
 *
 * Two differences from the object-level example are required by the plugin
 * model (specifier identity, not object identity):
 *
 * - **Specifier-resolved acquire.** A consumer's stub knows only the dependency
 *   specifier (`@maieutics/dep/main`), never the owner's worker id. The worker
 *   tracks its own worker id (from the `__worker-id` frame) and posts
 *   `__acquire-actor { specifier, refId }`; the main thread maps the specifier
 *   to the owner worker id, rewrites the refId prefix, and runs the standard
 *   acquire path. Rewriting is safe: the refId is owned by the consumer and its
 *   prefix is only a routing hint; after the rewrite it embeds the true owner
 *   id so worker-actor's main-side `routeAcquire` resolves it.
 * - **No liveness plane.** Main is the lifecycle authority (`spawn`'s
 *   `onDeath`); the ref codec skips the heartbeat pairs the object example
 *   needs.
 *
 * The codec tag `maieutics/actor` is registered on the worker side via
 * `serveWorker(api, { codecs: [...] })`; the host thread registers the same
 * codec in `spawn()`'s codec list so every spawn registry can decode incoming
 * actor references. Main-side registries cannot create fresh references (a
 * worker owns its surfaces), so the codec is effectively single-registered per
 * worker — the object tables below are worker-scoped.
 */

import {
  type Channel,
  type Codec,
  CODEC_PLACEHOLDER_KEY,
  connectChannel,
  type ControlFrame,
  type DecodeContext,
  type EncodeContext,
  getActiveRegistry,
  getActiveTransport,
  openChannel,
  registerControlHandler,
  registerRelease,
  setMainAcquire,
  triggerAcquire,
} from "@ghostflyby/worker-actor/codec";
import { makeRpcHandler } from "@ghostflyby/worker-actor/codec";
import { RemoteError } from "@ghostflyby/worker-actor";

export const ACTOR_CODEC_TAG = "maieutics/actor";
export const ACTOR_BRAND = Symbol.for("maieutics/actor/v1/surface");
const REF_TOKEN_BRAND = Symbol.for("maieutics/actor/v1/ref-token");
/** Brand on received reference proxies (pending or materialized). */
export const REF_PROXY_BRAND = Symbol.for("maieutics/actor/v1/ref-proxy");

/**
 * The proxy type: methods returning an AsyncIterable keep it lazy (matching
 * worker-actor's `Remote<T>` projection, so streaming methods transport over
 * the iterable codec); other methods return a Promise; non-functions are
 * `never`.
 */
export type RemoteActor<T> = {
  [K in keyof T]: T[K] extends (...args: infer A) => infer R
    ? R extends AsyncIterable<infer E> ? (...args: A) => AsyncIterable<E>
    : (...args: A) => Promise<Awaited<R>>
    : never;
};

// —— Owner-side identity tables (one per worker; single-threaded) ——

/** Surfaces registered by name; the flattened peer surface is derived on serve. */
const surfaces = new Map<string, object>();
/** Specifiers (of this worker) served by name; matches how consumers request them. */
const specifierBySurface = new Map<string, string>();
let localRefCount = 0;
let workerIdPrefix = Math.random().toString(36).slice(2);

// —— Main-thread acquire router (registered via setMainAcquire) ——

/**
 * The main thread has no `__worker-id` frame, so the router is a plain closure
 * invoked for main-side pending proxies; the SDK never creates those (fresh
 * references are worker-owned), but worker-actor calls it when a main-side
 * decode hands the main thread a reference. Nothing to do: the acquire was
 * already routed by the worker's `__acquire-actor` frame.
 */
setMainAcquire(() => {});

// —— Control frames (worker side) ——

interface AcquireActorFrame extends ControlFrame {
  specifier: string;
  /** Optional surface name for name-addressed acquire (remote collections). */
  name?: string;
}

registerControlHandler("__acquire-actor", () => {
  // Consumer-worker → main acquire request. The main thread maps the specifier
  // to its owner and answers with `__serve-actor` / `__ref-acquired`; this
  // worker has nothing to serve for its own acquire.
});

/**
 * Adopt a refId (replacing the random prefix with the main-assigned worker id).
 * The first `__worker-id` frame re-targets every refId created before it;
 * acquire-serve adopts the incoming refId directly, so both sides agree on the
 * prefix without racing the worker-id frame.
 */
function adoptRefId(refId: string): void {
  const separator = refId.indexOf(":");
  if (separator <= 0) return;
  const count = refId.slice(separator + 1);
  if (!/^\d+$/.test(count)) return;
  workerIdPrefix = refId.slice(0, separator);
  localRefCount = Math.max(localRefCount, Number(count));
}

// —— Surface bookkeeping ——

/**
 * Register a named actor surface so dependencies can acquire it by specifier.
 * Called by the worker-side `init` handler for every top-level actor export.
 */
export function registerActorExport(
  name: string,
  specifier: string,
  surface: object,
): void {
  surfaces.set(name, surface);
  specifierBySurface.set(name, specifier);
}

/** Clear all registered surfaces; called on dispose and before a re-init. */
export function clearActorExports(): void {
  surfaces.clear();
  specifierBySurface.clear();
}

/** The worker's entry-module namespace, set by the SDK after init. Served to
 * dependency acquires so every actor export's methods are reachable (nested
 * `exportName.method`), not just the first surface matching a specifier. */
let namespaceSurface: Record<string, unknown> = {};

/** Sets the namespace surface served to acquiring peers. */
export function setNamespaceSurface(surface: Record<string, unknown>): void {
  namespaceSurface = surface;
}

/** Clears the namespace surface (dispose/re-init). */
export function clearNamespaceSurface(): void {
  namespaceSurface = {};
}

/** Marker on remote-collection surfaces (set by the SDK entry); the acquire
 * router prefers ordinary actor surfaces when both share a specifier. */
const COLLECTION_SURFACE_MARKER = Symbol.for("maieutics/extensionPoint/v1/collectionSurface");

function findSurfaceBySpecifier(
  specifier: string,
  name?: string,
): { name: string; surface: object } | undefined {
  // Name-addressed acquire (remote collections): resolve the exact named
  // surface this worker serves under the specifier, ordinary or collection.
  if (name !== undefined) {
    const surface = surfaces.get(name);
    if (surface !== undefined && specifierBySurface.get(name) === specifier) {
      return { name, surface };
    }
    return undefined;
  }
  // Specifier-only acquire (actor interop): prefer an ordinary actor surface
  // over a remote-collection surface when a worker exports both under the
  // same specifier (the collection is addressed by extension-point name, not
  // by this acquire path).
  let collection: { name: string; surface: object } | undefined;
  for (const [surfaceName, surface] of surfaces) {
    if (specifierBySurface.get(surfaceName) !== specifier) continue;
    if ((surface as Record<symbol, unknown>)[COLLECTION_SURFACE_MARKER] === true) {
      collection ??= { name: surfaceName, surface };
      continue;
    }
    return { name: surfaceName, surface };
  }
  return collection;
}

/**
 * The peer-facing surface: an actor export `math` becomes `math.double`,
 * `math.add`, ...; a plain function export `helper` becomes `helper`;
 * non-function plain exports are ignored (constants are not exposed).
 */
export function flattenSurface(
  surface: Record<string, unknown>,
): Record<string, (...args: unknown[]) => unknown> {
  const flat: Record<string, (...args: unknown[]) => unknown> = {};
  for (const [key, value] of Object.entries(surface)) {
    if (typeof value === "function") {
      flat[key] = value as (...args: unknown[]) => unknown;
      continue;
    }
    if (typeof value !== "object" || value === null) continue;
    if (isActorSurface(value)) {
      // The defineActor value is a proxy (its get trap answers method calls);
      // Object.entries on the proxy is empty, so unwrap the real surface via
      // __surface before enumerating its methods.
      const real = (value as { __surface?: object }).__surface ?? value;
      for (const [method, fn] of Object.entries(real)) {
        if (typeof fn === "function") {
          flat[`${key}.${method}`] = fn as (...args: unknown[]) => unknown;
        }
      }
    }
  }
  return flat;
}

/** Flatten one actor surface under a fixed export name (used by the serve path). */
function flattenActorSurface(
  name: string,
  surface: object,
): Record<string, (...args: unknown[]) => unknown> {
  const flat: Record<string, (...args: unknown[]) => unknown> = {};
  for (const [method, fn] of Object.entries(surface)) {
    if (typeof fn === "function") {
      flat[`${name}.${method}`] = fn as (...args: unknown[]) => unknown;
    }
  }
  return flat;
}

// —— The reference codec ——

interface RefToken {
  [REF_TOKEN_BRAND]: true;
  surface: object;
  specifier: string;
  name: string;
}

interface RefHandle {
  [CODEC_PLACEHOLDER_KEY]: typeof ACTOR_CODEC_TAG;
  refId: string;
  port?: MessagePort;
  /** The owning worker's canonical specifier, carried so a consumer can route
   * the acquire through Maieutics' __acquire-actor (which is specifier-based;
   * worker-actor's __acquire-ref is not handled by the host). */
  specifier?: string;
  /** The registered surface name (a service's internal `__svc:` name), so a
   * consumer's acquire resolves exactly that surface. */
  name?: string;
}

type RefFrame =
  | { type: "call"; id: number; method: string; args: unknown[] }
  | { type: "result"; id: number; ok: true; value: unknown }
  | {
    type: "result";
    id: number;
    ok: false;
    error: { name: string; message: string; stack?: string };
  }
  | { type: "released" };

/**
 * Wrap a live surface (owned by this worker) into a transmittable reference.
 * A plain object wrapper carries the surface by identity — it never crosses a
 * thread directly; the codec encodes it into a refId token plus a fresh
 * channel port.
 */
export function remoteActor(surface: object, specifier: string, name: string): RemoteActor<object> {
  const token: RefToken = { [REF_TOKEN_BRAND]: true, surface, specifier, name };
  const refId = refIdFor(specifier);
  const proxy = new Proxy({} as RemoteActor<object>, {
    get(_target, prop) {
      if (prop === REF_TOKEN_BRAND) return true;
      if (prop === ACTOR_BRAND) return true;
      if (prop === "__surface") return surface;
      if (prop === "__refId") return refId;
      if (prop === "dispose") return () => releaseToken(token);
      if (prop === Symbol.dispose) return () => void releaseToken(token);
      if (prop === "then") return undefined;
      // The owner's own token: calls run locally with zero indirection.
      if (typeof prop === "string") {
        return (...args: unknown[]) => {
          const fn = (token.surface as Record<string, unknown>)[prop];
          if (typeof fn !== "function") {
            return Promise.reject(new Error(`No such method: "${String(prop)}"`));
          }
          return Promise.resolve((fn as (...a: unknown[]) => unknown).apply(token.surface, args));
        };
      }
      return undefined;
    },
  });
  (proxy as unknown as { __refId: string }).__refId = refId;
  tokenProxies.set(token, proxy);
  proxyTokens.set(proxy, token);
  return proxy;
}

function releaseToken(token: RefToken): Promise<void> {
  tokenProxies.delete(token);
  return Promise.resolve();
}

/**
 * Owner-side tokens keyed by identity so re-encoding keeps the same refId.
 * A WeakMap is not iterable, so tokenByProxy scans its entries through the
 * reverse proxy map (proxies are uniquely registered per token).
 */
const tokenProxies = new WeakMap<RefToken, RemoteActor<object>>();
const proxyTokens = new WeakMap<object, RefToken>();

function tokenByProxy(proxy: unknown): RefToken {
  const token = proxyTokens.get(proxy as object);
  if (token === undefined) {
    throw new Error("Cannot encode an actor reference without its surface.");
  }
  return token;
}

function refIdFor(specifier: string): string {
  return `${workerIdPrefix}:${specifier}:${++localRefCount}`;
}

function serveRefOwner(
  channel: Channel,
  _refId: string,
  handler: ReturnType<typeof makeRpcHandler>,
  _registry: EncodeContext["registry"],
): void {
  channel.onMessage(async (message) => {
    const frame = message as RefFrame;
    if (frame.type === "released") {
      channel.close();
      return;
    }
    if (frame.type !== "call") return;
    // makeRpcHandler decodes the args itself (its signature takes the encoded
    // request). Passing already-decoded args would decode twice: plain values
    // survive, but a codec value like an AsyncIterable placeholder loses its
    // symbol-keyed methods on the second plain-object pass and becomes {}.
    const result = await handler({
      id: frame.id,
      method: frame.method,
      args: frame.args,
    });
    if (result.ok) {
      // makeRpcHandler already encoded the value and collected the transferable
      // ports (an AsyncIterable return travels over an iterable-codec channel).
      // Re-encoding would turn the placeholder back into a plain object and drop
      // the ports, so send the handler's own value and transfer list.
      channel.send(
        { type: "result", id: result.id, ok: true, value: result.value },
        result.transfer,
      );
    } else {
      channel.send({ type: "result", id: result.id, ok: false, error: result.error });
    }
  });
}

function createRefProxy(
  channel: Channel,
  registry: DecodeContext["registry"],
  _refId: string,
): RemoteActor<object> {
  const pending = new Map<
    number,
    { resolve: (value: unknown) => void; reject: (reason: unknown) => void }
  >();
  let nextCallId = 1;
  let closed = false;
  let unregisterRelease: () => void = () => {};

  const fail = (reason: unknown): void => {
    if (closed) return;
    closed = true;
    for (const call of pending.values()) call.reject(reason);
    pending.clear();
    channel.close();
  };

  channel.onMessage((message) => {
    const frame = message as RefFrame;
    if (frame.type === "result") {
      const call = pending.get(frame.id);
      if (!call) return;
      pending.delete(frame.id);
      if (frame.ok) call.resolve(registry.decode(frame.value));
      else call.reject(new RemoteError(frame.error));
    } else if (frame.type === "released") {
      fail(new Error("Actor reference released by its owner"));
    }
  });

  const call = (method: string, args: unknown[]): Promise<unknown> => {
    if (closed) return Promise.reject(new Error("Actor reference is disposed"));
    return new Promise((resolve, reject) => {
      const id = nextCallId++;
      pending.set(id, { resolve, reject });
      const transfer: Transferable[] = [];
      channel.send(
        { type: "call", id, method, args: registry.encode(args, transfer) as unknown[] },
        transfer,
      );
    });
  };

  const dispose = (): void => {
    if (closed) return;
    closed = true;
    channel.send({ type: "released" } satisfies RefFrame);
    channel.close();
    for (const call of pending.values()) call.reject(new Error("Actor reference disposed"));
    pending.clear();
    unregisterRelease();
  };

  const proxy = new Proxy({} as RemoteActor<object>, {
    get(_target, prop) {
      if (prop === REF_PROXY_BRAND) return true;
      if (prop === "dispose") return dispose;
      if (prop === Symbol.dispose) return () => void dispose();
      // The proxy must not be detected as a thenable, or await behavior breaks.
      if (prop === "then") return undefined;
      if (typeof prop === "string") return (...args: unknown[]) => call(prop, args);
      return undefined;
    },
  });

  unregisterRelease = registerRelease(proxy, () => {
    channel.send({ type: "released" } satisfies RefFrame);
    fail(new Error("Actor reference garbage-collected"));
  });

  return proxy;
}

// —— Pending (refId-only) arrivals: the stub's first call triggers the acquire ——

interface PendingCall {
  method: string;
  args: unknown[];
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
}

interface PendingEntry {
  proxy: RemoteActor<object>;
  calls: PendingCall[];
  registry: DecodeContext["registry"];
  refId: string;
  real?: RemoteActor<object>;
  /** Owning worker's specifier, when the reference carries one (services in
   * collections). Preserved so a re-encode keeps routing through the host's
   * specifier-based __acquire-actor. */
  specifier?: string;
  /** The registered surface name (a service's internal `__svc:` name). */
  name?: string;
}

const pendingByRefId = new Map<string, PendingEntry>();

/**
 * Hook installed by the SDK entry: routes a specifier-addressed acquire to the
 * host via Maieutics' `__acquire-actor` frame (the host does not handle
 * worker-actor's `__acquire-ref`). Used when decoding an actor reference that
 * carries the owning worker's specifier (e.g. a service in a collection).
 */
let specifierAcquire:
  | ((specifier: string, refId: string, name?: string) => void)
  | undefined;

/** Installed by mod.ts: routes specifier-addressed acquires through the host. */
export function setSpecifierAcquire(
  fn: ((specifier: string, refId: string, name?: string) => void) | undefined,
): void {
  specifierAcquire = fn;
}

function createPendingProxy(
  refId: string,
  ctx: DecodeContext,
  specifier?: string,
  name?: string,
): RemoteActor<object> {
  const existing = pendingByRefId.get(refId);
  if (existing) return existing.proxy;
  const entry: PendingEntry = {
    proxy: undefined as never,
    calls: [],
    registry: ctx.registry,
    refId,
    ...(specifier === undefined ? {} : { specifier }),
    ...(name === undefined ? {} : { name }),
  };
  const proxy = new Proxy({} as RemoteActor<object>, {
    get(_target, prop) {
      if (prop === REF_PROXY_BRAND) return true;
      if (prop === "dispose") return () => disposePending(entry);
      if (prop === Symbol.dispose) return () => void disposePending(entry);
      if (prop === "then") return undefined;
      if (typeof prop === "string") {
        return (...args: unknown[]) => {
          if (entry.real) {
            return (entry.real as unknown as Record<string, (...a: unknown[]) => Promise<unknown>>)
              [prop](...args);
          }
          // First call triggers the acquire; subsequent calls queue too. A
          // specifier-addressed reference routes through the host's
          // __acquire-actor (specifier-based), not worker-actor's __acquire-ref.
          if (specifier !== undefined && specifierAcquire !== undefined) {
            specifierAcquire(specifier, refId, name);
          } else {
            triggerAcquire(refId);
          }
          return new Promise<unknown>((resolve, reject) => {
            entry.calls.push({ method: prop, args, resolve, reject });
          });
        };
      }
      return undefined;
    },
  });
  entry.proxy = proxy;
  pendingByRefId.set(refId, entry);
  return proxy;
}

function disposePending(entry: PendingEntry): Promise<void> {
  if (entry.real) (entry.real as unknown as { dispose(): Promise<void> }).dispose();
  pendingByRefId.delete(entry.refId);
  for (const call of entry.calls) {
    call.reject(new Error("Actor reference disposed before acquire completed"));
  }
  entry.calls.length = 0;
  return Promise.resolve();
}

function materialize(entry: PendingEntry, port: MessagePort): void {
  if (entry.real) {
    port.close();
    return;
  }
  const registry = entry.registry;
  const channel = connectChannel(port);
  registry.registerChannel(channel);
  const real = createRefProxy(channel, registry, entry.refId);
  entry.real = real;
  pendingByRefId.delete(entry.refId);
  const calls = entry.calls;
  entry.calls = [];
  for (const call of calls) {
    const promise = (real as unknown as Record<string, (...a: unknown[]) => Promise<unknown>>)
      [call.method](...call.args);
    promise.then(call.resolve, call.reject);
  }
}

registerControlHandler("__ref-acquired", (frame: ControlFrame) => {
  if (frame.port === undefined) return;
  const entry = pendingByRefId.get(frame.refId);
  if (!entry) {
    // Not ours: another consumer-side handler (the dependency stub) owns this
    // refId and will materialize the port. Never close a port we don't own.
    return;
  }
  materialize(entry, frame.port);
});

// —— Codec entry point ——

export const actorRefCodec: Codec<RemoteActor<object>> = {
  tag: ACTOR_CODEC_TAG,
  matches(v: unknown): v is RemoteActor<object> {
    return typeof v === "object" && v !== null &&
      ((v as { [REF_TOKEN_BRAND]?: unknown })[REF_TOKEN_BRAND] === true ||
        (v as { [REF_PROXY_BRAND]?: unknown })[REF_PROXY_BRAND] === true);
  },
  encode(v: RemoteActor<object>, ctx: EncodeContext): unknown {
    const ref = v as unknown as Record<PropertyKey, unknown>;
    if (ref[REF_TOKEN_BRAND] === true) {
      // The token always comes from the reverse WeakMap. The proxy's get trap
      // answers `__refId`/`__surface` with real values and every other string
      // key with a method callable; never read arbitrary keys off it.
      const token = tokenByProxy(v);
      const refId = (v as unknown as { __refId?: string }).__refId ??
        refIdFor(token.specifier);
      const { channel, peerPort } = openChannel(ctx);
      ctx.registry.registerChannel(channel);
      const handler = makeRpcHandler(
        flattenSurface(token.surface as Record<string, unknown>),
        ctx.registry,
      );
      serveRefOwner(channel, refId, handler, ctx.registry);
      return {
        [CODEC_PLACEHOLDER_KEY]: ACTOR_CODEC_TAG,
        refId,
        port: peerPort,
        specifier: token.specifier,
      } satisfies RefHandle;
    }
    return {
      [CODEC_PLACEHOLDER_KEY]: ACTOR_CODEC_TAG,
      refId: refIdOfProxy(v),
      ...proxyIdentityOf(v),
    } satisfies RefHandle;
  },
  decode(placeholder: RefHandle, ctx: DecodeContext): RemoteActor<object> {
    const { refId, port, specifier, name } = placeholder;
    if (port === undefined) {
      return createPendingProxy(refId, ctx, specifier, name);
    }
    const channel = connectChannel(port);
    ctx.registry.registerChannel(channel);
    const proxy = createRefProxy(channel, ctx.registry, refId);
    pendingByRefId.set(refId, {
      proxy,
      calls: [],
      registry: ctx.registry,
      refId,
      real: proxy,
      ...(specifier === undefined ? {} : { specifier }),
      ...(name === undefined ? {} : { name }),
    });
    return proxy;
  },
  onRegistryFail(): void {
    for (const entry of pendingByRefId.values()) {
      for (const call of entry.calls) call.reject(new Error("Actor reference released"));
      entry.calls.length = 0;
      (entry.real as unknown as { dispose?(): void } | undefined)?.dispose?.();
    }
    pendingByRefId.clear();
  },
};

/**
 * Decodes a single value that may be an actor reference placeholder (a service
 * contributed by another worker) into a `Remote<T>` proxy. Plain data returns
 * unchanged. Uses the active worker registry so the decode context (codec
 * state, pending table) is the current worker's. The transport is the active
 * runtime transport: a process/remote actor carries a real Transport (the
 * ref-codec's dedicated channels and acquire control frames ride it); a Web
 * Worker has none, so the value is returned undecoded (its `refId`-only
 * placeholder still routes through the pending-acquire path on a later call).
 */
export function decodeActorValue(value: unknown): unknown {
  if (typeof value !== "object" || value === null) return value;
  if ((value as Record<string, unknown>)[CODEC_PLACEHOLDER_KEY] !== ACTOR_CODEC_TAG) {
    return value;
  }
  const registry = getActiveRegistry();
  const transport = getActiveTransport();
  if (registry === undefined || transport === undefined) return value;
  return actorRefCodec.decode(value as RefHandle, {
    registry,
    seen: new WeakMap(),
    codecState: new Map(),
    transport,
  });
}

/**
 * Encodes an actor reference (a `remoteActor` proxy) into its transmittable
 * placeholder form. Unlike relying on the codec during a stream send (stream
 * items are structured-cloned directly, so the codec never runs on them), this
 * produces the placeholder eagerly so it can be stored in a collection and
 * cloned.
 *
 * The placeholder deliberately omits the port: a port would need to be
 * transferred with the message, which stream items cannot do. The consumer
 * decodes it to a pending proxy whose first call triggers a specifier-based
 * acquire (the placeholder carries the owning worker's specifier).
 * Plain data passes through unchanged.
 */
export function encodeActorValue(value: unknown): unknown {
  if (typeof value !== "object" || value === null) return value;
  if (!actorRefCodec.matches(value)) return value;
  const token = tokenByProxy(value as RemoteActor<object>);
  const refId = (value as unknown as { __refId?: string }).__refId ??
    refIdFor(token.specifier);
  return {
    [CODEC_PLACEHOLDER_KEY]: ACTOR_CODEC_TAG,
    refId,
    specifier: token.specifier,
    ...(token.name.length > 0 ? { name: token.name } : {}),
  } satisfies RefHandle;
}

function refIdOfProxy(proxy: unknown): string {
  const ref = proxy as { __refId?: string };
  if (typeof ref.__refId === "string") return ref.__refId;
  // Re-encoding a received proxy hands off the same identity; the stub path
  // carries the refId alongside, so derive it from the pending table by proxy.
  for (const [refId, entry] of pendingByRefId) {
    if (entry.proxy === proxy || entry.real === proxy) return refId;
  }
  throw new Error("Cannot re-encode an unknown actor reference.");
}

/**
 * The routing identity (specifier + optional surface name) of a received
 * reference proxy, from the pending table. Preserved on re-encode so a
 * service reference forwarded to a third worker still routes through the
 * host's specifier-based __acquire-actor instead of falling back to
 * worker-actor's __acquire-ref (which the host does not handle).
 */
function proxyIdentityOf(proxy: unknown): { specifier?: string; name?: string } {
  for (const entry of pendingByRefId.values()) {
    if (entry.proxy === proxy || entry.real === proxy) {
      if (entry.specifier !== undefined) {
        return entry.name === undefined ? { specifier: entry.specifier } : {
          specifier: entry.specifier,
          name: entry.name,
        };
      }
      break;
    }
  }
  return {};
}

function isActorSurface(value: object): boolean {
  return (value as { [ACTOR_BRAND]?: unknown })[ACTOR_BRAND] === true;
}

// Worker-side serve: the main thread answered `__acquire-actor` with a fresh
// channel (`__serve-ref` to the owner, `__ref-acquired` to the holder);
// serveWorker dispatches both standard frames to the control handlers. The
// owner serves its flattened surface over the transferred port.
registerControlHandler("__serve-ref", (frame: ControlFrame) => {
  if (frame.port === undefined) return;
  const registry = getActiveRegistry();
  if (!registry) return;
  const acquire = frame as AcquireActorFrame;
  const specifier = acquire.specifier;
  if (typeof specifier !== "string") return;
  const name = typeof acquire.name === "string" ? acquire.name : undefined;
  const found = findSurfaceBySpecifier(specifier, name);
  if (found === undefined) {
    frame.port.close();
    return;
  }
  adoptRefId(frame.refId);
  try {
    const channel = connectChannel(frame.port);
    registry.registerChannel(channel);
    // Name-addressed acquires (remote collections) serve exactly the named
    // surface. Specifier-only acquires (actor interop) serve the whole
    // namespace — every actor export's methods nested as `exportName.method` —
    // so all of a worker's actor surfaces are reachable, not just the first
    // matching one. A service surface (internal `__svc:` name) is served as a
    // bare method set, matching the Remote<T> proxy's direct method calls.
    const api = name !== undefined
      ? (name.startsWith("__svc:")
        ? flattenSurface(found.surface as Record<string, unknown>)
        : flattenActorSurface(found.name, found.surface))
      : flattenSurface(namespaceSurface);
    const handler = makeRpcHandler(api, registry);
    serveRefOwner(channel, frame.refId, handler, registry);
  } catch {
    // An acquire frame for an unknown surface is dropped: the referencing
    // side treats the closed channel as the ref being unavailable.
  }
});
