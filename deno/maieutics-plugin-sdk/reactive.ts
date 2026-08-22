/**
 * Reactive extension-point core: an open extension-point identity plus a
 * per-worker provider registry on top of @preact/signals-core.
 *
 * An extension point is a plain identity value (`defineExtensionPoint(name)`),
 * not a declaration+implementation bundle: any worker may `provide(ep, value)`
 * to contribute a reactive value to the extension point's collection. The
 * collection of a worker is the aggregate of every provider's current value;
 * a provider whose value is `undefined` does not contribute (the convention
 * for "currently not providing").
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
 * extension point when they carry the same name; the registry keys on the
 * symbol so any worker's `defineExtensionPoint("x")` joins the same collection.
 */
export interface ExtensionPointIdentity<T = unknown> {
  readonly name: string;
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

/** Resolves the registry symbol for an extension-point name. */
function symbolFor(name: string): symbol {
  return Symbol.for(`maieutics/extensionPoint/v1/${name}`);
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
 * Declares an extension-point identity. This is a pure value: it carries no
 * implementation. Any worker can later `provide(ep, value)` to contribute to
 * the extension point's collection.
 */
export function defineExtensionPoint<T = unknown>(name: string): ExtensionPointIdentity<T> {
  if (typeof name !== "string" || name.length === 0) {
    throw new TypeError("An extension point name must be a non-empty string.");
  }

  return {
    name,
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

  const symbol = symbolFor(extensionPoint.name);
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
  const symbol = symbolFor(registration.extensionPoint.name);
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
  const providers = providersByPoint.get(symbolFor(extensionPoint.name));
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
    const providers = providersByPoint.get(symbolFor(extensionPoint.name));
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
  return providersByPoint.get(symbolFor(extensionPoint.name))?.size ?? 0;
}
