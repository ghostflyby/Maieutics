/** Explicit, readonly Vue state capabilities over Actor channels (ADR 0056). */
import {
  computed,
  type DeepReadonly,
  effectScope,
  isRef,
  onScopeDispose,
  readonly,
  type Ref,
  shallowRef,
  toRaw,
  watch,
} from "./vue_reactivity.ts";
import {
  type Codec,
  CODEC_PLACEHOLDER_KEY,
  type DecodeContext,
  type EncodeContext,
  getCodecState,
} from "./actor-runtime/core/codec.ts";
import {
  type Channel,
  connectChannel,
  connectToken,
  openChannel,
} from "./actor-runtime/core/channel.ts";
import { ActorTransportError } from "./actor-runtime/core/lifetime.ts";
import { createBinaryGraphCodec } from "./actor-runtime/core/binary_graph.ts";

/** Terminal failures of a shared state capability, independent of business-action errors. */
export class ReactiveRefError extends Error {
  constructor(
    readonly code: "closed" | "snapshot_failed" | "capacity" | "protocol",
    message: string,
    options?: ErrorOptions,
  ) {
    super(message, options);
    this.name = "ReactiveRefError";
  }
}

/** An initial value is always ready, including undefined; closure retains the last snapshot. */
export type ReactiveRefStatus =
  | { readonly kind: "live"; readonly revision: number }
  | { readonly kind: "closed"; readonly revision: number; readonly error: ReactiveRefError };

/** A disposable state capability. Mutations belong to explicit, awaitable Actor actions. */
export interface ReactiveRef<T> extends Readonly<Ref<DeepReadonly<T>>>, Disposable {
  readonly status: Readonly<Ref<ReactiveRefStatus>>;
}

interface Model {
  id: string;
  handle: ReactiveRef<unknown>;
  snapshot: Uint8Array;
  revision: number;
  closed: boolean;
  listeners: Set<() => void>;
  update(snapshot: Uint8Array, revision: number): void;
  close(error: ReactiveRefError): void;
}
const models = new WeakMap<object, Model>();
const graph = createBinaryGraphCodec();
const MAX_REFS = 1024;
export const REACTIVE_REF_TAG = "maieutics/reactive-ref/1";

/** Detach Vue proxies while tracking the actual nested reads; never evaluate data accessors. */
function snapshotOf(input: unknown): Uint8Array {
  const seen = new Map<object, object>();
  let items = 0;
  function visit(value: unknown, depth: number): unknown {
    if (++items > 65536 || depth > 64) {
      throw new ReactiveRefError("capacity", "Reactive Ref snapshot budget exceeded");
    }
    if (value === null || typeof value !== "object") return value;
    if (isRef(value)) throw new TypeError("Nested refs require an explicit data projection");
    const raw = toRaw(value);
    const previous = seen.get(raw);
    if (previous) return previous;
    if (seen.size >= 16384) {
      throw new ReactiveRefError("capacity", "Reactive Ref snapshot object budget exceeded");
    }
    let out: object;
    if (Array.isArray(value)) {
      if (value.length > 65536) {
        throw new ReactiveRefError("capacity", "Reactive Ref array budget exceeded");
      }
      out = new Array(value.length);
    } else if (value instanceof Map) out = new Map();
    else if (value instanceof Set) out = new Set();
    else if (Object.getPrototypeOf(raw) === Object.prototype) out = {};
    else if (Object.getPrototypeOf(raw) === null) out = Object.create(null);
    else return raw; // The bounded binary graph codec validates native types and rejects custom ones.
    seen.set(raw, out);
    if (value instanceof Map && out instanceof Map) {
      for (const [key, item] of value) out.set(visit(key, depth + 1), visit(item, depth + 1));
    } else if (value instanceof Set && out instanceof Set) {
      for (const item of value) out.add(visit(item, depth + 1));
    } else {
      for (const key of Object.keys(value)) {
        const descriptor = Object.getOwnPropertyDescriptor(raw, key);
        if (!descriptor || !("value" in descriptor)) {
          throw new TypeError("Reactive Ref snapshots require data properties");
        }
        Object.defineProperty(out, key, {
          value: visit(Reflect.get(value, key), depth + 1),
          enumerable: true,
          configurable: true,
          writable: true,
        });
      }
    }
    return out;
  }
  return graph.encode(visit(input, 0));
}

function readSnapshot(snapshot: unknown): unknown {
  if (!(snapshot instanceof Uint8Array)) {
    throw new ReactiveRefError("protocol", "Reactive Ref snapshot must be native bytes");
  }
  try {
    return graph.decode(snapshot);
  } catch (cause) {
    throw new ReactiveRefError("protocol", "Invalid Reactive Ref snapshot", { cause });
  }
}

function makeModel(id: string, snapshot: Uint8Array, revision: number): Model {
  const slot = shallowRef<{ value: unknown; status: ReactiveRefStatus }>({
    value: readSnapshot(snapshot),
    status: { kind: "live", revision },
  });
  const status = computed(() => readonly(slot.value.status));
  const handle = computed(() => {
    const value = slot.value.value;
    return value !== null && typeof value === "object" ? readonly(value) : value;
  }) as unknown as ReactiveRef<unknown>;
  const model: Model = {
    id,
    handle,
    snapshot,
    revision,
    closed: false,
    listeners: new Set(),
    update(bytes, nextRevision) {
      if (model.closed) return;
      const next = readSnapshot(bytes);
      model.snapshot = bytes;
      model.revision = nextRevision;
      // Invalidation callbacks run synchronously; publish a complete snapshot before notifying peers.
      try {
        slot.value = { value: next, status: { kind: "live", revision: nextRevision } };
      } finally {
        for (const notify of [...model.listeners]) notify();
      }
    },
    close(error) {
      if (model.closed) return;
      model.closed = true;
      try {
        slot.value = {
          value: slot.value.value,
          status: { kind: "closed", revision: model.revision, error },
        };
      } finally {
        try {
          for (const notify of [...model.listeners]) notify();
        } finally {
          model.listeners.clear();
        }
      }
    },
  };
  Object.defineProperties(handle, {
    status: { value: status },
    [Symbol.dispose]: {
      value: () => model.close(new ReactiveRefError("closed", "Reactive Ref released")),
    },
  });
  models.set(handle, model);
  return model;
}

/**
 * Publish a detached, readonly projection of a Vue ref/computed. Nested reactive reads are observed.
 * State updates may coalesce; use an event stream when every transition matters. Dispose explicitly
 * or create inside an effectScope. Native mutable values in snapshots never mutate the owning state.
 */
export function shareRef<T>(source: Readonly<Ref<T>>): ReactiveRef<T> {
  if (!isRef(source)) throw new TypeError("shareRef requires a Vue Ref or ComputedRef");
  const scope = effectScope();
  let model: Model | undefined;
  try {
    scope.run(() => {
      onScopeDispose(() =>
        model?.close(new ReactiveRefError("closed", "Reactive Ref scope stopped"))
      );
      watch(() => {
        try {
          return snapshotOf(source.value);
        } catch (cause) {
          const error = cause instanceof ReactiveRefError ? cause : new ReactiveRefError(
            cause instanceof ActorTransportError && cause.code === "capacity"
              ? "capacity"
              : "snapshot_failed",
            "Cannot snapshot Reactive Ref source",
            { cause },
          );
          if (!model) throw error;
          model.close(error);
          return model.snapshot;
        }
      }, (bytes) => {
        if (!model) model = makeModel(crypto.randomUUID(), bytes, 0);
        else if (model.revision === Number.MAX_SAFE_INTEGER) {
          model.close(new ReactiveRefError("capacity", "Reactive Ref revision budget exceeded"));
        } else model.update(bytes, model.revision + 1);
      }, { immediate: true });
    });
    if (!model) {
      throw new ReactiveRefError("snapshot_failed", "Reactive Ref initial snapshot missing");
    }
    model.listeners.add(() => {
      if (model?.closed) scope.stop();
    });
    return model.handle as ReactiveRef<T>;
  } catch (error) {
    scope.stop();
    throw error;
  }
}

interface RefHandle {
  [CODEC_PLACEHOLDER_KEY]: typeof REACTIVE_REF_TAG;
  id: string;
  revision: number;
  snapshot: Uint8Array;
  port?: MessagePort;
  token?: unknown;
}
interface CodecState {
  imports: Map<string, Model>;
  exports: Set<() => void>;
}
function stateOf(ctx: EncodeContext | DecodeContext): CodecState {
  return getCodecState(ctx, reactiveRefCodec, () => ({ imports: new Map(), exports: new Set() }));
}
function protocol(message: string): never {
  throw new ReactiveRefError("protocol", message);
}
function frameOf(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object") return protocol("Invalid Reactive Ref frame");
  return value as Record<string, unknown>;
}
function revisionOf(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) {
    return protocol("Invalid Reactive Ref revision");
  }
  return value;
}
function closeError(cause: unknown): ReactiveRefError {
  return cause instanceof ReactiveRefError ? cause : new ReactiveRefError(
    cause instanceof ActorTransportError && (cause.code === "protocol" || cause.code === "capacity")
      ? cause.code
      : "closed",
    "Reactive Ref connection closed",
    { cause },
  );
}

function encode(handle: ReactiveRef<unknown>, ctx: EncodeContext): RefHandle {
  const model = models.get(handle);
  if (!model || model.closed) throw new ReactiveRefError("closed", "Reactive Ref is closed");
  const state = stateOf(ctx);
  if (state.exports.size >= MAX_REFS) {
    throw new ReactiveRefError("capacity", "Reactive Ref export budget exceeded");
  }
  const { channel, peerPort, token } = openChannel(ctx);
  ctx.registry.registerChannel(channel, ctx.transport);
  let requested: number | undefined;
  let stopped = false;
  const stop = () => {
    if (stopped) return;
    stopped = true;
    model.listeners.delete(notify);
    state.exports.delete(stop);
    channel.close();
  };
  const notify = () => {
    if (stopped) return;
    try {
      if (model.closed) {
        const status = model.handle.status.value;
        if (status.kind === "closed") {
          channel.send({ type: "closed", code: status.error.code, message: status.error.message });
        }
        stop();
      } else if (requested !== undefined && model.revision > requested) {
        requested = undefined;
        channel.send({ type: "state", revision: model.revision, snapshot: model.snapshot });
      }
    } catch (error) {
      // A watcher must never throw transport failures into an unrelated business action.
      ctx.registry.trackTask(Promise.reject(error));
      stop();
    }
  };
  state.exports.add(stop);
  model.listeners.add(notify);
  channel.onClose(stop);
  channel.onMessage((raw) => {
    const frame = frameOf(raw);
    if (frame.type === "release") return stop();
    if (frame.type !== "pull") return protocol("Invalid Reactive Ref producer frame");
    if (requested !== undefined) return protocol("Concurrent Reactive Ref pulls");
    const revision = revisionOf(frame.revision);
    if (revision > model.revision) return protocol("Reactive Ref pull is ahead of source");
    requested = revision;
    notify();
  });
  return {
    [CODEC_PLACEHOLDER_KEY]: REACTIVE_REF_TAG,
    id: model.id,
    revision: model.revision,
    snapshot: model.snapshot,
    ...(peerPort === undefined ? { token } : { port: peerPort }),
  };
}

function decode(raw: unknown, ctx: DecodeContext): ReactiveRef<unknown> {
  const handle = frameOf(raw);
  if (typeof handle.id !== "string" || !handle.id.length || handle.id.length > 128) {
    return protocol("Invalid Reactive Ref identity");
  }
  const revision = revisionOf(handle.revision);
  if (!(handle.snapshot instanceof Uint8Array)) return protocol("Invalid Reactive Ref snapshot");
  const channel: Channel = handle.port instanceof MessagePort
    ? connectChannel(handle.port)
    : connectToken(ctx.transport, handle.token as { __mux: "open"; ch: number });
  ctx.registry.registerChannel(channel, ctx.transport);
  const state = stateOf(ctx);
  const existing = state.imports.get(handle.id);
  if (existing && !existing.closed) {
    channel.send({ type: "release" });
    channel.close();
    return existing.handle;
  }
  if (state.imports.size >= MAX_REFS) {
    try {
      // Returning a typed closed capability avoids making RPC result decoding fatal to its owner.
      const rejected = makeModel(handle.id, handle.snapshot, revision);
      rejected.close(new ReactiveRefError("capacity", "Reactive Ref import budget exceeded"));
      return rejected.handle;
    } finally {
      channel.send({ type: "release" });
      channel.close();
    }
  }
  let model: Model;
  try {
    model = makeModel(handle.id, handle.snapshot, revision);
  } catch (error) {
    channel.send({ type: "release" });
    channel.close();
    throw error;
  }
  state.imports.set(handle.id, model);
  let stopped = false;
  const stop = () => {
    if (stopped) return;
    stopped = true;
    state.imports.delete(model.id);
    channel.send({ type: "release" });
    channel.close();
  };
  model.listeners.add(() => {
    if (model.closed) stop();
  });
  channel.onClose((reason) => model.close(closeError(reason)));
  channel.onMessage((raw) => {
    const frame = frameOf(raw);
    if (frame.type === "closed") {
      const code = frame.code;
      if (
        (code !== "closed" && code !== "snapshot_failed" && code !== "capacity" &&
          code !== "protocol") ||
        typeof frame.message !== "string" || frame.message.length > 1024
      ) return protocol("Invalid Reactive Ref terminal frame");
      model.close(new ReactiveRefError(code, frame.message));
    } else if (frame.type === "state") {
      const revision = revisionOf(frame.revision);
      if (revision <= model.revision) return protocol("Reactive Ref revisions must increase");
      model.update(frame.snapshot as Uint8Array, revision);
      if (!model.closed) channel.send({ type: "pull", revision });
    } else return protocol("Invalid Reactive Ref consumer frame");
  });
  channel.send({ type: "pull", revision });
  return model.handle;
}

/** Register on both peers; ordinary Vue refs remain local unless explicitly shared. */
export const reactiveRefCodec: Codec<ReactiveRef<unknown>> = {
  tag: REACTIVE_REF_TAG,
  matches(value): value is ReactiveRef<unknown> {
    return typeof value === "object" && value !== null && models.has(value);
  },
  encode,
  decode,
};
