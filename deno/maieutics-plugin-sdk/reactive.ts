/**
 * Reactive extension-point core: a contract-mode extension-point identity plus
 * a per-worker provider registry on top of @preact/signals-core.
 *
 * An extension point is a contract value (`defineExtensionPoint(name)`) owned
 * by the module that declares it: the identity carries the defining module's
 * URL (`import.meta.url`) as its owner. Providers cannot conjure the same
 * extension point by writing the same name elsewhere — the owner differs, so
 * the identities do not join the same collection. Sharing an extension point
 * therefore requires importing the contract module that exports its identity.
 *
 * The owner is the module, not the worker: every worker that imports the same
 * contract module resolves the same identity, so cross-worker collections
 * aggregate providers that all imported the contract.
 *
 * The collection of an extension point is the aggregate of every provider's
 * current value; a provider whose value is `undefined` does not contribute
 * (the convention for "currently not providing").
 *
 * The registry is purely distributed: each worker keeps its own providers and
 * peers reach each other through the worker-actor acquire machinery. The host
 * never sees extension-point identity or providers; it only routes the
 * `__acquire-actor` frames that let a consumer resolve a provider's surface.
 */

import { computed, effect, type Signal, signal } from "@preact/signals-core";

/** A reactive value: a signals-core signal holding `T`. */
export type ReactiveValue<T> = Signal<T>;

/** Re-export the signals-core primitives the SDK builds on. */
export { computed, effect, signal };

/** Brand attached to an extension-point identity value. */
const EXTENSION_POINT_BRAND = Symbol.for("maieutics/extensionPoint/v1/identity");

/**
 * A stable identity for one extension point. Two identities are the same
 * extension point when they carry the same owner (defining module URL) and
 * name; the registry keys on a symbol derived from both, so providers that
 * import the same contract module join the same collection.
 *
 * `defSpecifier` is the canonical specifier of the defining worker (the worker
 * whose entry module contains the contract). It is filled in by the SDK during
 * worker init (scanExports) for locally-defined identities, and by the
 * dependency stub for identities imported from another worker. It is the
 * routeable address used to reach the defining worker's remote collection.
 */
export interface ExtensionPointIdentity<T = unknown> {
  readonly name: string;
  /** URL of the module that declared this identity (`import.meta.url`). */
  readonly owner: string;
  /** Canonical specifier of the defining worker; set after init. */
  readonly defSpecifier?: string;
  readonly [EXTENSION_POINT_BRAND]: true;
}

/** A provider registration: one worker's contribution of a reactive value. */
export interface ProviderRegistration<T> {
  readonly extensionPoint: ExtensionPointIdentity<T>;
  readonly value: ReactiveValue<T | undefined>;
  /** Local identity of this provider within its owning worker. */
  readonly providerId: string;
  /** Remote registrations additionally carry the defining worker specifier,
   * so {@link unprovide} can route the withdrawal back to it. */
  readonly remoteSpecifier?: string;
  /** The contribution key the defining worker tracks (remote registrations). */
  readonly remoteKey?: string;
}

const providersByPoint = new Map<
  symbol,
  Map<string, ReactiveValue<unknown>>
>();

// Monotonic version bumped on every provide/unprovide, so a collection signal
// can depend on provider-set membership as well as on individual values.
const registryVersion = signal(0);

/** Resolves the registry symbol for an extension point's owner + name. */
function symbolFor(owner: string, name: string): symbol {
  return Symbol.for(`maieutics/extensionPoint/v1/${owner}/${name}`);
}

/** True when `value` is an extension-point identity created here. */
export function isExtensionPoint(value: unknown): value is ExtensionPointIdentity {
  return (
    typeof value === "object" &&
    value !== null &&
    (value as Record<symbol, unknown>)[EXTENSION_POINT_BRAND] === true
  );
}

/**
 * Global key under which the worker's load hook records the URL of the module
 * currently being evaluated. The SDK's `initPluginWorker` installs a load hook
 * that sets `globalThis[CURRENT_MODULE]` to each module's URL before its
 * top-level code runs, so `defineExtensionPoint` can read the defining module's
 * URL from the loader — objective and unforgeable. `Symbol.for` keeps the key
 * shared between the SDK (writer) and any module (reader) across isolates.
 */
export const CURRENT_MODULE = Symbol.for("maieutics/extensionPoint/v1/currentModule");

/** Reads the currently-evaluating module URL, or a fallback when no load hook
 * is installed (host process, modules imported before init). */
function currentModuleUrl(): string {
  const url = (globalThis as Record<symbol, unknown>)[CURRENT_MODULE];
  return typeof url === "string" && url.length > 0 ? url : import.meta.url;
}

/**
 * Declares an extension-point identity owned by the calling module. This is a
 * pure contract value: it carries no implementation. Any worker that imports
 * this module can later `provide(ep, value)` to contribute to the extension
 * point's collection; a worker declaring the same name in a different module
 * gets a different identity and does not join this collection.
 *
 * `owner` is the defining module's URL, taken from the loader via
 * {@link CURRENT_MODULE}: the load hook records each module's URL before its
 * top-level code runs, so the owner is the module that actually declared the
 * identity and cannot be forged by the caller. When no load hook is installed
 * (the host process, or modules loaded before worker init) the owner falls
 * back to the SDK module's URL.
 */
export function defineExtensionPoint<T = unknown>(name: string): ExtensionPointIdentity<T> {
  if (typeof name !== "string" || name.length === 0) {
    throw new TypeError("An extension point name must be a non-empty string.");
  }

  return {
    name,
    owner: currentModuleUrl(),
    [EXTENSION_POINT_BRAND]: true,
  } as ExtensionPointIdentity<T>;
}

/**
 * Builds the remote identity value a dependency stub exports for a contract
 * module: the same brand and fields as a locally-declared identity, with the
 * defining worker's specifier filled in. `provide(ep, ...)` on this value
 * routes to the defining worker's remote collection. Only the SDK's stub
 * synthesis calls this; a plugin never constructs it directly.
 */
export function createRemoteIdentity<T = unknown>(
  name: string,
  owner: string,
  defSpecifier: string,
): ExtensionPointIdentity<T> {
  return {
    name,
    owner,
    defSpecifier,
    [EXTENSION_POINT_BRAND]: true,
  } as ExtensionPointIdentity<T>;
}

/** Mutable view of an identity used by the SDK to attach the defining worker
 * specifier after init. */
export interface MutableExtensionPointIdentity<T = unknown> {
  name: string;
  owner: string;
  defSpecifier?: string;
}

/** Sets the defining worker's canonical specifier on a locally-declared identity. */
export function bindDefiningWorker<T>(
  extensionPoint: ExtensionPointIdentity<T>,
  specifier: string,
): void {
  (extensionPoint as unknown as MutableExtensionPointIdentity<T>).defSpecifier = specifier;
}

// —— Remote routing (injected by mod.ts to avoid a reactive→actor_ref cycle) ——

/** Remote-provide hook: contributes a reactive value to a defining worker's
 * remote collection. Set by the SDK entry; undefined means remote routing is
 * unavailable (host process or pre-init). */
export interface RemoteProvideFn {
  (
    specifier: string,
    name: string,
    initial: unknown,
    changes: AsyncIterable<unknown>,
    providerKey: string,
  ): Promise<void>;
}

/** Remote-unprovide hook: withdraws a remote contribution from a defining
 * worker's collection. Set by the SDK entry alongside {@link RemoteProvideFn}. */
export type RemoteUnprovideFn = (
  specifier: string,
  name: string,
  providerKey: string,
) => Promise<void>;

/**
 * Value transformer: converts a provided value before it enters a collection.
 * The SDK entry installs one that turns a `defineService`-marked object into a
 * remote actor reference (a cloneable token), so services travel across workers
 * as references and consumers receive `Remote<T>` proxies. Plain data passes
 * through unchanged.
 */
export type ValueTransformer = (value: unknown) => unknown;

let valueTransformer: ValueTransformer | undefined;

/** Installed by mod.ts: converts service values to remote references. */
export function setValueTransformer(fn: ValueTransformer | undefined): void {
  valueTransformer = fn;
}

/**
 * Value untransformer: converts a received collection element back to its
 * consumer-side form. The SDK entry installs one that decodes actor reference
 * placeholders (a service contributed by another worker) into a `Remote<T>`
 * proxy. Plain data passes through unchanged.
 */
export type ValueUntransformer = (value: unknown) => unknown;

let valueUntransformer: ValueUntransformer | undefined;

/** Installed by mod.ts: decodes received actor reference placeholders. */
export function setValueUntransformer(fn: ValueUntransformer | undefined): void {
  valueUntransformer = fn;
}

/** Applies the untransformer to a single value (no-op when not installed). */
function untransformValue(value: unknown): unknown {
  const untransform = valueUntransformer;
  return untransform === undefined ? value : untransform(value);
}

let remoteProvide: RemoteProvideFn | undefined;
let remoteUnprovide: RemoteUnprovideFn | undefined;

/** Installed by mod.ts: routes provide() to a defining worker's collection. */
export function setRemoteProvide(fn: RemoteProvideFn | undefined): void {
  remoteProvide = fn;
}

/** Installed by mod.ts: routes unprovide() of a remote contribution. */
export function setRemoteUnprovide(fn: RemoteUnprovideFn | undefined): void {
  remoteUnprovide = fn;
}

/** True when the identity's defining worker is not this worker (remote). */
export function isRemoteExtensionPoint(extensionPoint: ExtensionPointIdentity): boolean {
  const def = extensionPoint.defSpecifier;
  if (def === undefined) return false;
  return def !== currentWorkerSpecifier();
}

let currentWorkerSpecifierValue = "";

/** Set by mod.ts during init: this worker's canonical specifier. */
export function setWorkerSpecifier(specifier: string): void {
  currentWorkerSpecifierValue = specifier;
}

function currentWorkerSpecifier(): string {
  return currentWorkerSpecifierValue;
}

/** True when the identity's defining worker is this worker (local). */
export function isLocalExtensionPoint(extensionPoint: ExtensionPointIdentity): boolean {
  const def = extensionPoint.defSpecifier;
  return def === undefined || def === currentWorkerSpecifier();
}

/**
 * An AsyncIterable of a signal's subsequent changes (excluding the current
 * value, which travels as the initial value). Used to transmit a reactive
 * value to a remote collection: the consumer observes the initial value, then
 * each change as it happens. Returning (abandoning) the iteration stops the
 * subscription.
 */
export function changesOf<T>(value: ReactiveValue<T | undefined>): AsyncIterable<T | undefined> {
  return {
    [Symbol.asyncIterator]() {
      let stopped = false;
      let pending: ((v: T | undefined) => void) | undefined;
      let queue: (T | undefined)[] = [];
      // The initial value travels separately; the effect's first run must not
      // enqueue it again.
      let first = true;
      const stop = effect(() => {
        const current = value.value;
        if (first) {
          first = false;
          return;
        }
        if (pending !== undefined) {
          const resolve = pending;
          pending = undefined;
          resolve(current);
        } else {
          queue.push(current);
        }
      });
      return {
        next(): Promise<IteratorResult<T | undefined>> {
          if (stopped) return Promise.resolve({ done: true, value: undefined });
          if (queue.length > 0) {
            // The length guard guarantees shift() returns an element; the
            // element itself may legitimately be undefined (a signal change).
            return Promise.resolve({ done: false, value: queue.shift() as T | undefined });
          }
          return new Promise<IteratorResult<T | undefined>>((resolve) => {
            pending = (v: T | undefined) => resolve({ done: false, value: v });
          });
        },
        return(): Promise<IteratorResult<T | undefined>> {
          stopped = true;
          stop();
          return Promise.resolve({ done: true, value: undefined });
        },
      };
    },
  };
}

/**
 * Contributes a reactive value to an extension point from the current worker.
 * The provider joins the extension point's collection; while its value is
 * `undefined` it does not contribute. Returns a handle that can be passed to
 * {@link unprovide} to withdraw the contribution.
 *
 * When the extension point is owned by another worker (`defSpecifier` points
 * elsewhere), the contribution is routed to that worker's remote collection:
 * the signal is transmitted as its current value plus an AsyncIterable of
 * changes, and the defining worker aggregates it with its local providers.
 */
export function provide<T>(
  extensionPoint: ExtensionPointIdentity<T>,
  value: ReactiveValue<T | undefined>,
): ProviderRegistration<T> {
  if (!isExtensionPoint(extensionPoint)) {
    throw new TypeError("provide() expects an extension point identity.");
  }

  // Remote identity: route to the defining worker's collection. The providerKey
  // (generated here) identifies this contribution so a later unprovide can
  // withdraw exactly it from the defining worker. It carries the provider's
  // own specifier prefix, so the defining worker can drop every contribution
  // from a dead provider (host `__provider-dead` notification).
  if (isRemoteExtensionPoint(extensionPoint) && remoteProvide !== undefined) {
    // isRemoteExtensionPoint guarantees defSpecifier is set (a remote identity
    // always carries it); bind it to satisfy the compiler without `!`.
    const defSpecifier = extensionPoint.defSpecifier;
    if (defSpecifier === undefined) {
      throw new Error(
        "A remote extension point identity is missing its defining worker specifier.",
      );
    }
    const specifier = defSpecifier;
    const providerKey = `${currentWorkerSpecifier()}:${crypto.randomUUID()}`;
    void remoteProvide(
      specifier,
      extensionPoint.name,
      value.value,
      changesOf(value),
      providerKey,
    ).catch((error: unknown) => {
      // The contribution failed to reach the defining worker; log instead of
      // surfacing an unhandled rejection in this worker.
      console.error(
        `[plugin-sdk] remote provide to '${specifier}/${extensionPoint.name}' failed: ` +
          `${error instanceof Error ? error.message : String(error)}`,
      );
    });
    return {
      extensionPoint,
      value,
      providerId: `remote:${specifier}:${extensionPoint.name}:${providerKey}`,
      remoteSpecifier: specifier,
      remoteKey: providerKey,
    };
  }

  const symbol = symbolFor(extensionPoint.owner, extensionPoint.name);
  let providers = providersByPoint.get(symbol);
  if (providers === undefined) {
    providers = new Map();
    providersByPoint.set(symbol, providers);
  }

  const providerId = crypto.randomUUID();
  // A service value (defineService-marked) is converted to a remote actor
  // reference token before entering the collection, so it travels as a
  // cloneable placeholder and consumers decode it to a Remote<T> proxy.
  // Plain data passes through unchanged. The transformer is applied on every
  // read, so a provider switching a signal from data to a service (or vice
  // versa) is handled consistently.
  const stored = valueTransformer === undefined ? value : (() => {
    const transform = valueTransformer;
    if (transform === undefined) return value;
    return computed(() => transform(value.value));
  })();
  providers.set(providerId, stored as ReactiveValue<unknown>);
  registryVersion.value += 1;
  return { extensionPoint, value, providerId };
}

/** Withdraws a provider contribution made by {@link provide}. For a remote
 * registration the withdrawal is routed back to the defining worker, which
 * unregisters its local contribution and stops the change stream. */
export function unprovide<T>(registration: ProviderRegistration<T>): void {
  if (
    registration.remoteSpecifier !== undefined &&
    registration.remoteKey !== undefined &&
    remoteUnprovide !== undefined
  ) {
    void remoteUnprovide(
      registration.remoteSpecifier,
      registration.extensionPoint.name,
      registration.remoteKey,
    ).catch((error: unknown) => {
      console.error(
        `[plugin-sdk] remote unprovide of '${registration.remoteSpecifier}/` +
          `${registration.extensionPoint.name}' failed: ` +
          `${error instanceof Error ? error.message : String(error)}`,
      );
    });
    return;
  }
  const symbol = symbolFor(registration.extensionPoint.owner, registration.extensionPoint.name);
  const providers = providersByPoint.get(symbol);
  providers?.delete(registration.providerId);
  if (providers !== undefined && providers.size === 0) {
    providersByPoint.delete(symbol);
  }
  registryVersion.value += 1;
}

/**
 * The current collection snapshot of an extension point: every provider value
 * that is not `undefined`, in registration order. Non-reactive read.
 *
 * Values are returned as stored: a service contribution is a cloneable actor
 * reference placeholder (see {@link provide}); consumers decode it with the
 * untransformer, while the provider's own snapshot keeps the placeholder so it
 * can travel across workers.
 */
export function snapshot<T>(extensionPoint: ExtensionPointIdentity<T>): T[] {
  const providers = providersByPoint.get(symbolFor(extensionPoint.owner, extensionPoint.name));
  if (providers === undefined) return [];
  const values: T[] = [];
  for (const value of providers.values()) {
    const current = value.value;
    if (current !== undefined) values.push(current as T);
  }
  return values;
}

/**
 * A reactive signal holding the extension point's collection. The signal
 * recomputes whenever any provider's value changes or a provider joins/leaves,
 * so it can drive `subscribe` and computed consumers. `undefined` provider
 * values are excluded from the snapshot.
 */
export function collection<T>(
  extensionPoint: ExtensionPointIdentity<T>,
): Signal<T[]> {
  // Reading the registry version makes the computed depend on provider-set
  // membership; the per-provider value signals are read inside the computed,
  // so a value change also recomputes.
  return computed(() => {
    registryVersion.value;
    const providers = providersByPoint.get(symbolFor(extensionPoint.owner, extensionPoint.name));
    if (providers === undefined) return [];
    const values: T[] = [];
    for (const value of providers.values()) {
      const current = value.value;
      if (current !== undefined) values.push(current as T);
    }
    return values;
  });
}

/**
 * Subscribes to an extension point's collection as an async stream of
 * snapshots. Each snapshot is the full collection at that moment; the stream
 * emits the initial snapshot, then a new snapshot on every change. The
 * returned iterable is a plain async iterable (worker-actor's iterable codec
 * transports it across workers lazily with backpressure and cancellation).
 */
export async function* subscribe<T>(
  extensionPoint: ExtensionPointIdentity<T>,
): AsyncIterable<T[]> {
  const collectionSignal = collection(extensionPoint);
  let pending: ((value: T[]) => void) | undefined;
  let queue: T[][] = [];
  // The initial snapshot is yielded explicitly below; the effect's first run
  // must not enqueue it again.
  let first = true;

  const notify = (): void => {
    const snapshot = collectionSignal.value;
    if (first) {
      first = false;
      return;
    }
    if (pending !== undefined) {
      const resolve = pending;
      pending = undefined;
      resolve(snapshot);
    } else {
      queue.push(snapshot);
    }
  };

  const stop = effect(notify);
  try {
    // Emit the current snapshot first, then every change.
    yield collectionSignal.value;
    while (true) {
      if (queue.length > 0) {
        yield queue.shift() as T[];
      } else {
        const snapshot = await new Promise<T[]>((resolve) => {
          pending = resolve;
        });
        yield snapshot;
      }
    }
  } finally {
    stop();
  }
}

/** Number of local providers currently registered for an extension point. */
export function providerCount(extensionPoint: ExtensionPointIdentity): number {
  return providersByPoint.get(symbolFor(extensionPoint.owner, extensionPoint.name))?.size ?? 0;
}

// —— CollectionStream: a lazy, single-value async stream over a collection ——

/**
 * A lazy async stream of the extension point's values. The element type is the
 * provider value itself (no event wrapper, no provider identity): the stream
 * emits every current value on subscription, then the value of each change as
 * it happens. A provider going `undefined` or leaving is silent — consumers
 * observe the values that flow, not the collection's membership.
 *
 * Every combinator returns a new stream and is lazy: nothing iterates until
 * `for await` (or `toArray`) runs. This mirrors ES `Iterator.prototype` map /
 * filter / take / drop / toArray semantics, adapted to async iteration.
 *
 * `map`/`filter` receive the single value, so `stream.map((v) => v * 2)`
 * transforms each flowing value — the collection's container shape never
 * leaks into the consumer's pipeline.
 */
export interface CollectionStream<T> {
  /** Iterate the stream: initial values, then each change. */
  [Symbol.asyncIterator](): AsyncIterator<T>;
  /** A stream of `fn(value)` for each flowing value. */
  map<U>(fn: (value: T) => U): CollectionStream<U>;
  /** A stream of the values for which `pred(value)` is true. */
  filter(pred: (value: T) => boolean): CollectionStream<T>;
  /** The first `count` values, then the stream ends. */
  take(count: number): CollectionStream<T>;
  /** The values after the first `count`, then the stream ends. */
  drop(count: number): CollectionStream<T>;
  /** Collect every value until the stream ends (finite streams only). */
  toArray(): Promise<T[]>;
}

/** The stream's value source: one value per change, identified by provider. */
function valueChanges<T>(
  extensionPoint: ExtensionPointIdentity<T>,
): AsyncIterable<T> {
  return {
    [Symbol.asyncIterator]() {
      const providers = providersByPoint.get(
        symbolFor(extensionPoint.owner, extensionPoint.name),
      );
      let stopped = false;
      let pending: ((result: IteratorResult<T>) => void) | undefined;
      let queue: T[] = [];
      // Provider id → last emitted value; a changed or new provider emits.
      let lastByProvider = new Map<string, unknown>();

      const stop = effect(() => {
        registryVersion.value;
        const current = providersByPoint.get(
          symbolFor(extensionPoint.owner, extensionPoint.name),
        );
        const nextByProvider = new Map<string, unknown>();
        for (const [providerId, signal] of current ?? []) {
          const value = signal.value;
          nextByProvider.set(providerId, value);
          if (value === undefined) continue;
          if (
            !lastByProvider.has(providerId) ||
            lastByProvider.get(providerId) !== value
          ) {
            const out = untransformValue(value) as T;
            if (pending !== undefined) {
              const resolve = pending;
              pending = undefined;
              resolve({ done: false, value: out });
            } else {
              queue.push(out);
            }
          }
        }
        lastByProvider = nextByProvider;
      });

      return {
        next(): Promise<IteratorResult<T>> {
          if (stopped) return Promise.resolve({ done: true, value: undefined });
          if (queue.length > 0) {
            // The length guard guarantees shift() returns an element.
            return Promise.resolve({ done: false, value: queue.shift() as T });
          }
          return new Promise<IteratorResult<T>>((resolve) => {
            pending = (result: IteratorResult<T>) => resolve(result);
          });
        },
        return(): Promise<IteratorResult<T>> {
          if (stopped) return Promise.resolve({ done: true, value: undefined });
          stopped = true;
          stop();
          // A pending next() must settle or the iterator never completes.
          if (pending !== undefined) {
            const resolve = pending;
            pending = undefined;
            resolve({ done: true, value: undefined });
          }
          return Promise.resolve({ done: true, value: undefined });
        },
      };
    },
  };
}

/**
 * Subscribes to an extension point's collection as a stream of individual
 * values. The stream emits the current values, then each changed value as it
 * happens. See {@link CollectionStream} for the combinator API.
 */
export function values<T>(extensionPoint: ExtensionPointIdentity<T>): CollectionStream<T> {
  return new CollectionStreamImpl(valueChanges(extensionPoint));
}

class CollectionStreamImpl<T> implements CollectionStream<T> {
  readonly #source: AsyncIterable<T>;

  constructor(source: AsyncIterable<T>) {
    this.#source = source;
  }

  [Symbol.asyncIterator](): AsyncIterator<T> {
    return this.#source[Symbol.asyncIterator]();
  }

  map<U>(fn: (value: T) => U): CollectionStream<U> {
    return new CollectionStreamImpl(mapAsync(this.#source, fn));
  }

  filter(pred: (value: T) => boolean): CollectionStream<T> {
    return new CollectionStreamImpl(filterAsync(this.#source, pred));
  }

  take(count: number): CollectionStream<T> {
    return new CollectionStreamImpl(takeAsync(this.#source, count));
  }

  drop(count: number): CollectionStream<T> {
    return new CollectionStreamImpl(dropAsync(this.#source, count));
  }

  async toArray(): Promise<T[]> {
    const out: T[] = [];
    for await (const value of this.#source) out.push(value);
    return out;
  }
}

// —— Lazy async combinators (mirror ES Iterator.prototype semantics) ——

async function* mapAsync<T, U>(
  source: AsyncIterable<T>,
  fn: (value: T) => U,
): AsyncGenerator<U> {
  for await (const value of source) yield fn(value);
}

async function* filterAsync<T>(
  source: AsyncIterable<T>,
  pred: (value: T) => boolean,
): AsyncGenerator<T> {
  for await (const value of source) {
    if (pred(value)) yield value;
  }
}

async function* takeAsync<T>(
  source: AsyncIterable<T>,
  count: number,
): AsyncGenerator<T> {
  let seen = 0;
  if (count <= 0) return;
  for await (const value of source) {
    yield value;
    seen += 1;
    if (seen >= count) return;
  }
}

async function* dropAsync<T>(
  source: AsyncIterable<T>,
  count: number,
): AsyncGenerator<T> {
  let skipped = 0;
  for await (const value of source) {
    if (skipped < count) {
      skipped += 1;
      continue;
    }
    yield value;
  }
}
