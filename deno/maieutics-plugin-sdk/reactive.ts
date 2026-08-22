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
 */
export interface ExtensionPointIdentity<T = unknown> {
  readonly name: string;
  /** URL of the module that declared this identity (`import.meta.url`). */
  readonly owner: string;
  readonly [EXTENSION_POINT_BRAND]: true;
}

/** A provider registration: one worker's contribution of a reactive value. */
export interface ProviderRegistration<T> {
  readonly extensionPoint: ExtensionPointIdentity<T>;
  readonly value: ReactiveValue<T | undefined>;
  /** Local identity of this provider within its owning worker. */
  readonly providerId: string;
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
 * Declares an extension-point identity owned by the calling module. This is a
 * pure contract value: it carries no implementation. Any worker that imports
 * this module can later `provide(ep, value)` to contribute to the extension
 * point's collection; a worker declaring the same name in a different module
 * gets a different identity and does not join this collection.
 *
 * `owner` is the defining module's URL; a contract module passes its own
 * `import.meta.url` so every worker importing the contract resolves the same
 * identity. `defineExtensionPoint` cannot read the caller's `import.meta.url`
 * itself (it lives in the SDK module), so the owner is explicit.
 */
export function defineExtensionPoint<T = unknown>(
  name: string,
  owner: string = import.meta.url,
): ExtensionPointIdentity<T> {
  if (typeof name !== "string" || name.length === 0) {
    throw new TypeError("An extension point name must be a non-empty string.");
  }
  if (typeof owner !== "string" || owner.length === 0) {
    throw new TypeError("An extension point owner must be a non-empty string.");
  }

  return {
    name,
    owner,
    [EXTENSION_POINT_BRAND]: true,
  } as ExtensionPointIdentity<T>;
}

/**
 * Contributes a reactive value to an extension point from the current worker.
 * The provider joins the extension point's collection; while its value is
 * `undefined` it does not contribute. Returns a handle that can be passed to
 * {@link unprovide} to withdraw the contribution.
 */
export function provide<T>(
  extensionPoint: ExtensionPointIdentity<T>,
  value: ReactiveValue<T | undefined>,
): ProviderRegistration<T> {
  if (!isExtensionPoint(extensionPoint)) {
    throw new TypeError("provide() expects an extension point identity.");
  }

  const symbol = symbolFor(extensionPoint.owner, extensionPoint.name);
  let providers = providersByPoint.get(symbol);
  if (providers === undefined) {
    providers = new Map();
    providersByPoint.set(symbol, providers);
  }

  const providerId = crypto.randomUUID();
  providers.set(providerId, value as ReactiveValue<unknown>);
  registryVersion.value += 1;
  return { extensionPoint, value, providerId };
}

/** Withdraws a provider contribution made by {@link provide}. */
export function unprovide<T>(registration: ProviderRegistration<T>): void {
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
        yield queue.shift()!;
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
