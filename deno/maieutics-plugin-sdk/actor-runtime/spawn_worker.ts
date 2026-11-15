import { ActorTransportError } from "./core/lifetime.ts";
import { frameRecord, isResponse, isSerializedError } from "./core/validation.ts";
import { handshakeError } from "./core/handshake.ts";
import { attachLazyIterator } from "./core/lazy_iterator.ts";
/**
 * Main-thread entry point: wraps a Worker into a type-safe Actor proxy.
 *
 * Usage:
 *   // worker.ts —— export the agreed-upon rpc object; serveWorker() registers it
 *   export const rpc = { add(a: number, b: number) { return a + b; } };
 *
 *   // main.ts —— import type only grabs types, no worker module side effects
 *   import type * as WorkerModule from "./worker.ts";
 *   const actor = await spawn<typeof WorkerModule.rpc>(new Worker(url, { type: "module" }));
 *   const sum = await actor.add(1, 2); // sum: number, type-safe
 */

import { ActorDiedError, type Frame } from "./core/protocol.ts";
import { type Codec, PayloadCodecRegistry } from "./core/codec.ts";
import { createRpcProxy, requestFrame, type RpcMethod } from "./core/rpc.ts";
import type { CodecValueTypes, TransformCallbacks } from "./core/type-utils.ts";
import type { TransferArgs } from "./core/transfer.ts";
import { type ControlFrame, dispatchControlFrame, setMainAcquire } from "./core/worker-context.ts";
import { iterableCodec } from "./core/codecs/iterable.ts";
import { errorCodec } from "./core/codecs/error.ts";
import { abortSignalCodec } from "./core/codecs/abort_signal.ts";
import { callbackCodec } from "./core/codecs/callback.ts";
import { fromMessagePort, type Transport } from "./core/transport.ts";

/** Return values are normalized to promises (already-promises are not re-wrapped). */
type Resolved<T> = T extends Promise<unknown> ? Awaited<T> : T;

/**
 * Derives the remote proxy type from the worker-side API shape. Each method's
 * return type is projected so the caller sees an honest shape for the value
 * that actually crosses the boundary — the proxy call is always a native
 * Promise at runtime, and the RPC handler `await`s the worker method's return
 * value before encoding it. Rules, in order:
 *
 *   1. never-returning (throwing) methods → Promise<never> (must come first:
 *      never structurally matches PromiseLike).
 *   2. A return type covered by a runtime codec (`Pass`, derived from the
 *      const `codecs` tuple via CodecValueTypes) → kept exactly as declared:
 *      the codec rebuilds that type on the far side, so full identity survives.
 *   3. A PromiseLike<X> return type (native Promise, a custom thenable, an
 *      explicit Promise<AsyncIterable<E>>, or a thenable-stream hybrid) →
 *      Promise<X>: the handler awaits the value, so what crosses is the
 *      resolution X — never a nested Promise.
 *   4. A bare AsyncIterable<E> return type → AsyncIterable<E>: the iterable
 *      codec rebuilds a local stream (lazy: the first next() triggers the
 *      remote call). Custom AsyncIterable subclasses flatten to the interface.
 *   5. Every other function member → Promise<Resolved<R>> (sync returns
 *      normalize to a Promise).
 *   6. Non-function members (constants, classes, ...) → never, failing at
 *      compile time.
 *
 * Parameters are projected through TransformCallbacks in every branch: a
 * Promise-returning function parameter (the worker's honest declaration)
 * widens to `R | Promise<R>` on the calling side, so BOTH sync and async
 * functions can be passed.
 */
export type Remote<T, Pass extends unknown = never> = {
  [K in keyof T]: T[K] extends (...args: infer A) => never ? (...args: A) => Promise<never>
    : T[K] extends (...args: infer A) => infer R
      ? R extends Pass ? (...args: TransformCallbacks<A>) => R // codec rebuilds R on the far side: keep identity
      : R extends PromiseLike<infer X> ? (...args: TransformCallbacks<A>) => Promise<X> // handler awaits: crossing value is X
      : R extends AsyncIterable<infer E> ? (...args: TransformCallbacks<A>) => AsyncIterable<E>
      : T[K] extends RpcMethod ? (
          ...args: TransformCallbacks<Parameters<T[K]>>
        ) => Promise<Resolved<ReturnType<T[K]>>>
      : never
    : never;
};

export interface SpawnOptions<
  C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
> {
  /**
   * Interruption policy for creation (the handshake):
   *   - omitted (undefined): default 10s timeout
   *   - null: no interruption at all
   *   - AbortSignal: creation is aborted when the signal fires; the rejection
   *     reason is signal.reason (a TimeoutError from AbortSignal.timeout(n)
   *     keeps the "did it call serveWorker()?" diagnostic)
   * The signal only governs creation: after spawn() resolves, the actor's
   * lifecycle is owned by dispose(). An already-aborted signal fails spawn()
   * immediately.
   */
  signal?: AbortSignal | null;
  /**
   * Extra codecs to register, matched before the built-ins.
   * Built-ins: iterable / error / abort-signal / callback. Both sides must
   * register the same tag list; the handshake validates it and a mismatch
   * kills the actor.
   *
   * The const tuple also drives the type projection: a return type covered by
   * a registered codec (CodecValueTypes<C>) is kept exactly as declared on
   * the Remote<T> proxy, because that codec rebuilds it on the far side.
   * Pass an `as const` tuple to opt in; a plain array or Codec<unknown>
   * entries fall back to the built-in async projection rules.
   */
  codecs?: C;
  /**
   * Fired when the actor dies through the kill() path: a worker crash, a
   * handshake failure (version/codec mismatch, timeout, interrupted creation).
   * NOT fired by dispose() — a deliberate shutdown is not a death. The pool
   * uses this to remove/replace a member; spawn's own onerror/onmessage
   * handlers are assigned internally, so this is the only way to observe a
   * crash without breaking spawn.
   */
  onDeath?: (reason: unknown) => void;
  /**
   * Argument-side transfer policy: which parameters are moved (zero-copy,
   * sender detaches) instead of cloned. Keyed by method name:
   *   { upload: { "params[0]": "move" }, download: "move" }
   *   { default: "move" }  // every parameter of every method
   * Only top-level parameter positions are considered; a value must be
   * transferable (ArrayBuffer / TypedArray / MessagePort). On non-messageport
   * transports move is ignored and the transport's normal clone behavior is
   * preserved.
   */
  transferArgs?: TransferArgs;
}

const HANDSHAKE_TIMEOUT = 10_000;

// Reference-acquire routing: each spawned actor gets a stable transport id
// (embedded in refIds as a prefix), so the coordinator can resolve
// "refId → owner transport" and bootstrap an owner↔requester channel on
// demand. This is the pluggable ActorRegistry — the minimal discovery layer.
import { type ActorRegistry, createActorRegistry } from "./core/registry.ts";

const actorRegistry = createActorRegistry();

/**
 * The coordinator's actor registry (exported for diagnostics / advanced
 * routing). The registry resolves "transport id → Transport", which is how
 * acquire frames and cross-connection connects route to the right peer.
 */
export function getActorRegistry(): ActorRegistry {
  return actorRegistry;
}
// Established (owner, holder) pairs: the liveness channel is created once per
// pair, on the first serve; later serves reuse it on both sides.
const livenessPairs = new Set<string>();

/**
 * Validate a peer handshake (the spawn/spawnNode side) against this end's
 * expectations. Returns the mismatch error, or undefined when the handshake
 * is acceptable: same PROTOCOL_VERSION, identical codec tag list, and — when
 * the peer reports one — the same transport kind.
 */

function routeAcquire(refId: string, requester: Transport): void {
  // refId format: "<ownerId>:<localCount>"
  const ownerId = refId.slice(0, refId.indexOf(":"));
  const owner = actorRegistry.resolve(ownerId);
  if (!owner) return; // unknown or dead owner: nothing to bootstrap
  const requesterId = actorRegistry.idOf(requester);
  if (owner.kind !== "messageport" || requester.kind !== "messageport") {
    // Mux involved: a MessagePort cannot be transferred over a Mux connection.
    // Bootstrap one channel end on the owner's transport and another on the
    // requester's transport, then relay frames between them here on the main
    // thread. Each side gets its native channel form: a transferred port on a
    // messageport transport, a Mux token otherwise. The liveness plane is
    // messageport-only; Mux acquires skip it (see the remote-ref codec docs).
    const ownerEnd = owner.openChannel();
    const requesterEnd = requester.openChannel();
    // Bridge first so no frame is dropped: owner calls/results and the requester
    // proxy's calls/results flow through the relay, both directions.
    ownerEnd.channel.onMessage((m) => requesterEnd.channel.send(m));
    requesterEnd.channel.onMessage((m) => ownerEnd.channel.send(m));
    if (owner.kind === "messageport") {
      // Owner is a messageport transport: hand over the port by transfer.
      const port = ownerEnd.token as MessagePort;
      owner.send(
        {
          type: "__serve-ref",
          refId,
          port,
          holderId: requesterId,
        } satisfies Frame,
        [port],
      );
    } else {
      const token = ownerEnd.token;
      owner.send(token);
      owner.send(
        {
          type: "__serve-ref",
          refId,
          token,
          holderId: requesterId,
        } satisfies Frame,
      );
    }
    if (requester.kind === "messageport") {
      const port = requesterEnd.token as MessagePort;
      requester.send(
        {
          type: "__ref-acquired",
          refId,
          port,
          ownerId,
        } satisfies Frame,
        [port],
      );
    } else {
      const token = requesterEnd.token;
      requester.send(token);
      requester.send(
        {
          type: "__ref-acquired",
          refId,
          token,
          ownerId,
        } satisfies Frame,
      );
    }
    return;
  }
  const { port1, port2 } = new MessageChannel();
  const transfer1: Transferable[] = [port1];
  const transfer2: Transferable[] = [port2];
  // Liveness plane: the owner pulls heartbeats over one channel per worker pair,
  // so a dead holder is detected once and its refs are released in a batch.
  let livenessPort1: MessagePort | undefined;
  let livenessPort2: MessagePort | undefined;
  const pairKey = requesterId === undefined ? undefined : `${ownerId}:${requesterId}`;
  if (pairKey !== undefined && !livenessPairs.has(pairKey)) {
    livenessPairs.add(pairKey);
    const lc = new MessageChannel();
    livenessPort1 = lc.port1;
    livenessPort2 = lc.port2;
    transfer1.push(lc.port1);
    transfer2.push(lc.port2);
  }
  owner.send(
    {
      type: "__serve-ref",
      refId,
      port: port1,
      holderId: requesterId,
      livenessPort: livenessPort1,
    } satisfies Frame,
    transfer1,
  );
  requester.send(
    {
      type: "__ref-acquired",
      refId,
      port: port2,
      ownerId,
      livenessPort: livenessPort2,
    } satisfies Frame,
    transfer2,
  );
}

/** Main-side acquire: the main thread itself is the requester (materialize locally). */
function routeAcquireMain(refId: string): void {
  const ownerId = refId.slice(0, refId.indexOf(":"));
  const owner = actorRegistry.resolve(ownerId);
  if (!owner) return;
  if (owner.kind !== "messageport") {
    // Mux owner: open a channel on the owner's transport and materialize the
    // main-side pending using the LOCAL channel end directly (no relay needed —
    // the main thread owns one end and the owner serves the other).
    const opened = owner.openChannel();
    owner.send(opened.token);
    owner.send(
      { type: "__serve-ref", refId, token: opened.token } satisfies Frame,
    );
    dispatchControlFrame({
      type: "__ref-acquired",
      refId,
      channel: opened.channel,
    });
    return;
  }
  const { port1, port2 } = new MessageChannel();
  owner.send(
    { type: "__serve-ref", refId, port: port1 } satisfies Frame,
    [port1],
  );
  dispatchControlFrame({ type: "__ref-acquired", refId, port: port2 });
}

// Registered once per module load: any main-side pending ref (created while
// decoding responses) triggers direct routing + local materialization.
setMainAcquire(routeAcquireMain);

/**
 * Make a stream-returning method's promise directly consumable via `for await`
 * (the Remote<T> special case types it as AsyncIterable<E>). The promise stays
 * a real promise (await/.catch/.finally all work); the attached iterator
 * resolves it on the first next() and forwards the inner remote iterable.
 * Element-level laziness is preserved by the iterable codec itself: the
 * worker-side generator only runs after the first next() (the start frame).
 *
 * Exported so an actor pool (which reuses the Remote<T> shape) applies the
 * same laziness to its routed calls.
 */
export { attachLazyIterator } from "./core/lazy_iterator.ts";

/** Lifecycle methods attached to the proxy (not part of Remote<T> itself). */
export interface ActorHandle {
  /** Settles after termination and all owned codec/transport cleanup. */
  readonly completion: Promise<void>;
  /** Sends a dispose frame and terminates the worker; all in-flight calls reject. */
  dispose(): Promise<void>;
}

/**
 * Establish a direct, bidirectional link channel between two workers, bypassing
 * the main thread. Values sent over the link are encoded/decoded through each
 * worker's own codec registry, so references and streams hand directly between
 * the two workers without the main thread in the path. Both workers must expose
 * the link via serveWorker's onLink option (and register compatible codecs).
 *
 * Returns a tear-down function that tells both workers to close the link.
 */
export function link(a: Worker, b: Worker, label: string): () => void {
  const { port1, port2 } = new MessageChannel();
  a.postMessage({ type: "__link", label, port: port1 } satisfies Frame, {
    transfer: [port1],
  });
  b.postMessage({ type: "__link", label, port: port2 } satisfies Frame, {
    transfer: [port2],
  });
  return () => {
    a.postMessage({ type: "__link-close", label } satisfies Frame);
    b.postMessage({ type: "__link-close", label } satisfies Frame);
  };
}

export function spawn<
  T,
  const C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
>(
  source: Worker | Transport,
  options: SpawnOptions<C> = {},
): Promise<Remote<T, CodecValueTypes<C>> & ActorHandle> {
  // Worker is the structured-clone message-port world: convert it to a
  // messageport-type Transport and recurse on the unified abstraction. The raw
  // Worker is kept alongside for crash detection and terminate() (the Transport
  // abstraction has no worker error event or termination primitive).
  if (source instanceof Worker) {
    const rawWorker = source;
    return spawnOnTransport<T, C>(
      fromMessagePort(rawWorker),
      options,
      rawWorker,
    );
  }
  return spawnOnTransport<T, C>(source, options);
}

async function spawnOnTransport<
  T,
  const C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
>(
  worker: Transport,
  options: SpawnOptions<C> = {},
  rawWorker?: Worker,
): Promise<Remote<T, CodecValueTypes<C>> & ActorHandle> {
  // Constraint: the worker handshake is not buffered — call spawn() right after
  // `new Worker(...)`. If messages arrive before the onmessage handler below is
  // set (e.g. another await in between), the handshake is lost and spawn()
  // waits until the handshake timeout.
  const registry = new PayloadCodecRegistry();
  // User codecs register first (can override a built-in of the same tag); built-ins fill in after.
  for (const codec of options.codecs ?? []) registry.register(codec);
  for (
    const codec of [
      iterableCodec,
      errorCodec,
      abortSignalCodec,
      callbackCodec,
    ]
  ) {
    if (!registry.has(codec.tag)) registry.register(codec);
  }
  // The RPC machinery is channel-agnostic (core/rpc.ts): the main channel is
  // just another adapter, identical to a worker-to-worker link.
  const rpcProxy = createRpcProxy(registry, {
    send: (request, transfer) => worker.send(requestFrame(request), transfer),
    isDead: () => dead,
    deadReason: () => deathReason instanceof Error ? deathReason : new ActorDiedError(),
    transport: worker,
    transferArgs: options.transferArgs,
  });
  // Assign a stable transport id (embedded in refIds) and register it for
  // acquire routing. The id is sent after the handshake so the worker is
  // definitely ready; FIFO ordering on the channel guarantees it arrives
  // before any user request.
  const workerId = actorRegistry.register(worker);
  let dead = false;
  let deathReason: unknown;
  const finished = Promise.withResolvers<void>();
  void finished.promise.catch(() => {}); // ActorHandle.completion preserves failures for the owner.
  let resolveHandshake: (() => void) | undefined;
  let rejectHandshake: ((reason: unknown) => void) | undefined;

  // The handshake can succeed or fail: version/codec mismatches reject it on the kill path
  const handshake = new Promise<void>((resolve, reject) => {
    resolveHandshake = resolve;
    rejectHandshake = reject;
  });

  const stage = (action: () => void): void => {
    try {
      action();
    } catch (error) {
      registry.trackTask(Promise.reject(error));
    }
  };
  function finish(reason: unknown, deliberate: boolean): void {
    if (dead) return;
    dead = true;
    deathReason = reason;
    rpcProxy.rejectAll(reason);
    rejectHandshake?.(reason);
    registry.failAll();
    if (deliberate && !worker.closed) stage(() => worker.send({ type: "dispose" } satisfies Frame));
    stage(() => worker.close(reason));
    if (rawWorker) {
      rawWorker.onerror = null;
      stage(() => rawWorker.terminate());
    }
    actorRegistry.unregister(workerId);
    if (!deliberate) stage(() => options.onDeath?.(reason));
    void Promise.allSettled([registry.join(), worker.completion]).then((results) => {
      const errors = results.flatMap((r) => r.status === "rejected" ? [r.reason] : []);
      if (errors.length) finished.reject(new AggregateError(errors, "Actor cleanup failed"));
      else finished.resolve();
    });
  }
  function kill(reason: unknown): void {
    finish(reason, false);
  }
  worker.onClose(kill);

  // Transport onMessage delivers { data, ports } wrappers (MessageEvent-like),
  // carrying any transferred MessagePorts (e.g. reference-acquire handshakes).
  worker.onMessage((ev) => {
    const checked = frameRecord(ev.data);
    if (checked.type === "__actor-failed") {
      if (
        !isSerializedError(checked.error) ||
        !["closed", "protocol", "capacity", "decode", "send"].includes(String(checked.code))
      ) {
        throw new ActorTransportError("protocol", "Invalid Actor terminal failure frame");
      }
      kill(
        new ActorTransportError(
          checked.code as ActorTransportError["code"],
          `Actor peer failed: ${checked.error.message}`,
        ),
      );
      return;
    }
    const frame = checked as unknown as Frame;
    if (frame.type === "handshake") {
      const mismatch = handshakeError(frame, registry, worker.kind);
      if (mismatch) {
        kill(mismatch);
        return;
      }
      // Tell the worker its id (for refIds); FIFO after the handshake.
      worker.send({ type: "__worker-id", id: workerId } satisfies Frame);
      resolveHandshake?.();
      return;
    }
    if (frame.type === "response") {
      if (!isResponse(frame)) {
        throw new ActorTransportError("protocol", "Invalid Actor response frame");
      }
      rpcProxy.deliver(frame);
      return;
    }
    if (frame.type === "__acquire-ref") {
      // A worker requests a channel to the owner of a reference; this worker
      // is the requester.
      routeAcquire(frame.refId, worker);
      return;
    }
    // Remaining worker→main control frames (e.g. __holder-dead death notices
    // from the liveness sweep) go to registered codec control handlers.
    dispatchControlFrame(frame as ControlFrame);
  });

  // Worker crash detection: only the raw Worker path has error events.
  // Mux transports surface death through closed channels (wired via onDeath /
  // onClose at the caller, e.g. spawnProcess).
  if (rawWorker && !dead) {
    rawWorker.onerror = (ev) =>
      kill(
        new Error(
          `Worker crashed: ${ev.message}`,
          { cause: ev.error },
        ),
      );
    rawWorker.onmessageerror = () => kill(new Error("Worker deserialization error"));
  }

  const invoke = (method: string, args: unknown[]): Promise<unknown> => rpcProxy.call(method, args);
  const dispose = (): Promise<void> => {
    finish(new ActorDiedError(), true);
    return finished.promise;
  };

  const actor = new Proxy({} as Remote<T> & ActorHandle, {
    get(_target, prop) {
      if (prop === "dispose") return dispose;
      if (prop === "completion") return finished.promise;
      if (prop === Symbol.dispose) return () => void dispose();
      // The proxy must not be detected as a thenable, or await behavior breaks.
      if (prop === "then") return undefined;
      if (typeof prop === "string") {
        return (...args: unknown[]) => {
          const p = invoke(prop, args);
          attachLazyIterator(p);
          return p;
        };
      }
      return undefined;
    },
  });

  // Interruption for creation: undefined → default timeout, null → never,
  // AbortSignal → user-controlled. A TimeoutError keeps the diagnostic hint
  // for the most common failure (serveWorker() never called).
  const interrupt = options.signal === undefined
    ? AbortSignal.timeout(HANDSHAKE_TIMEOUT)
    : options.signal;

  const reasonOf = (r: unknown): unknown => {
    if (r instanceof Error) {
      if (r.name === "TimeoutError") {
        return new Error(
          "Worker handshake timed out: the worker never reported ready " +
            "(did it call serveWorker()?)",
        );
      }
      return r;
    }
    return new Error(String(r));
  };

  const onInterruptAbort = (): void => {
    if (interrupt) kill(reasonOf(interrupt.reason));
  };

  if (interrupt?.aborted) {
    kill(reasonOf(interrupt.reason));
  } else {
    interrupt?.addEventListener("abort", onInterruptAbort, { once: true });
  }

  try {
    await handshake;
  } catch (error) {
    try {
      await finished.promise;
    } catch (cleanup) {
      throw new AggregateError([error, cleanup], "Actor startup and cleanup failed", {
        cause: error,
      });
    }
    throw error;
  } finally {
    interrupt?.removeEventListener("abort", onInterruptAbort);
  }
  return actor;
}
