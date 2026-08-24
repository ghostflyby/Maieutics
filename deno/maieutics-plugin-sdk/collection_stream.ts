/**
 * Maieutics collection-stream codec.
 *
 * Transports an AsyncIterable whose elements may be arbitrary codec values
 * (actor references / services, nested AsyncIterables) — not just structured
 * clone data. The worker-actor iterable codec sends each stream item via
 * structured clone directly, so a service proxy (or any codec value) in a
 * stream element fails to clone. This codec encodes every element through the
 * registry on the producer side and decodes it on the consumer side, so a
 * contributed service arrives as a live `Remote<T>` proxy.
 *
 * The stream protocol (lazy start, backpressure, release, error, death) is
 * implemented here on top of the generic Channel, mirroring worker-actor's
 * stream semantics. The element codec wiring is the only difference.
 */

import {
  type Channel,
  type Codec,
  CODEC_PLACEHOLDER_KEY,
  connectChannel,
  connectToken,
  type DecodeContext,
  type EncodeContext,
  openChannel,
  registerRelease,
} from "@ghostflyby/worker-actor/codec";

export const COLLECTION_STREAM_TAG = "maieutics/collection-stream";
const COLLECTION_STREAM_BRAND = Symbol.for("maieutics/collectionStream/v1");

type StreamFrame =
  | { type: "start" }
  | { type: "item"; value: unknown }
  | { type: "done" }
  | { type: "error"; error: { name: string; message: string; stack?: string } }
  | { type: "release" };

interface StreamHandle {
  [CODEC_PLACEHOLDER_KEY]: typeof COLLECTION_STREAM_TAG;
  /** Messageport transports: the transferred peer port. */
  port?: MessagePort;
  /** Mux transports: the channel-establishment token. */
  token?: unknown;
}

interface CollectionStreamState {
  producerStops: Set<() => void>;
  consumerFails: Set<() => void>;
}

/** Marks an AsyncIterable as a collection stream (element-codec transport). */
export function markCollectionStream<T>(
  iterable: AsyncIterable<T>,
): AsyncIterable<T> {
  (iterable as unknown as Record<symbol, unknown>)[COLLECTION_STREAM_BRAND] = true;
  return iterable;
}

function matches(v: unknown): v is AsyncIterable<unknown> {
  return (
    v !== null &&
    typeof v === "object" &&
    (v as Record<symbol, unknown>)[COLLECTION_STREAM_BRAND] === true &&
    typeof (v as { [Symbol.asyncIterator]?: unknown })[Symbol.asyncIterator] ===
      "function"
  );
}

function getState(
  ctx: { codecState: Map<Codec, unknown> },
): CollectionStreamState {
  let state = ctx.codecState.get(collectionStreamCodec) as
    | CollectionStreamState
    | undefined;
  if (state === undefined) {
    state = { producerStops: new Set(), consumerFails: new Set() };
    ctx.codecState.set(collectionStreamCodec, state);
  }
  return state;
}

/** Producer: pump an AsyncIterable, encoding each element via the registry. */
function startEncodedProducer(
  channel: Channel,
  iterable: AsyncIterable<unknown>,
  registry: EncodeContext["registry"],
  onStopped: () => void,
): () => void {
  const iterator = iterable[Symbol.asyncIterator]();
  let started = false;
  let stopped = false;

  const stop = (): void => {
    if (stopped) return;
    stopped = true;
    channel.close();
    const ret = iterator.return?.();
    if (ret) void ret.catch(() => {});
    onStopped();
  };

  channel.onMessage((message) => {
    const frame = message as StreamFrame;
    if (frame.type === "start") {
      if (!started) {
        started = true;
        void pump();
      }
    } else if (frame.type === "release") {
      stop();
    }
  });

  async function pump(): Promise<void> {
    try {
      while (!stopped) {
        const result = await iterator.next();
        if (stopped) break;
        if (result.done) {
          channel.send({ type: "done" } satisfies StreamFrame);
          break;
        }
        const transfer: Transferable[] = [];
        const encoded = registry.encode(result.value, transfer);
        channel.send(
          { type: "item", value: encoded } satisfies StreamFrame,
          transfer,
        );
      }
    } catch (error) {
      if (!stopped) {
        channel.send(
          {
            type: "error",
            error: {
              name: error instanceof Error ? error.name : "Error",
              message: error instanceof Error ? error.message : String(error),
              ...(error instanceof Error && error.stack !== undefined
                ? { stack: error.stack }
                : {}),
            },
          } satisfies StreamFrame,
        );
      }
    } finally {
      stop();
    }
  }

  return stop;
}

/** Consumer: rebuild a local AsyncIterable, decoding each element. */
function createDecodedIterable(
  channel: Channel,
  registry: DecodeContext["registry"],
  onReleased: () => void,
): {
  iterable: AsyncIterable<unknown>;
  fail: () => void;
  detach: () => void;
} {
  const queue: StreamFrame[] = [];
  const waiters: Array<{
    resolve: (result: IteratorResult<unknown>) => void;
    reject: (reason: unknown) => void;
  }> = [];
  let closed = false;
  let released = false;

  const detach = (): void => {
    if (released) return;
    released = true;
    onReleased();
  };

  const deliver = (frame: StreamFrame): void => {
    const waiter = waiters.shift();
    if (frame.type === "item") {
      const value = registry.decode(frame.value);
      if (waiter) waiter.resolve({ done: false, value });
      else queue.push({ type: "item", value });
    } else if (frame.type === "done") {
      if (waiter) waiter.resolve({ done: true, value: undefined });
      else queue.push(frame);
      closed = true;
      channel.close();
      detach();
    } else if (frame.type === "error") {
      const err = new Error(frame.error.message);
      err.name = frame.error.name;
      if (frame.error.stack !== undefined) err.stack = frame.error.stack;
      if (waiter) waiter.reject(err);
      else queue.push(frame);
      closed = true;
      channel.close();
      detach();
    } else if (waiter) {
      waiter.resolve({ done: true, value: undefined });
    }
  };

  channel.onMessage((message) => deliver(message as StreamFrame));

  const fail = (): void => {
    if (closed) return;
    closed = true;
    channel.close();
    while (waiters.length) {
      const waiter = waiters.shift()!;
      waiter.reject(new Error("Collection stream failed: actor terminated"));
    }
    detach();
  };

  const iterable: AsyncIterable<unknown> = {
    [Symbol.asyncIterator]() {
      let started = false;
      return {
        next(): Promise<IteratorResult<unknown>> {
          if (!started) {
            started = true;
            channel.send({ type: "start" } satisfies StreamFrame);
          }
          if (closed && queue.length === 0) {
            return Promise.resolve({ done: true, value: undefined });
          }
          const frame = queue.shift();
          if (frame) return Promise.resolve(toResult(frame));
          return new Promise((resolve, reject) => waiters.push({ resolve, reject }));
        },
        return(): Promise<IteratorResult<unknown>> {
          channel.send({ type: "release" } satisfies StreamFrame);
          channel.close();
          closed = true;
          detach();
          return Promise.resolve({ done: true, value: undefined });
        },
      };
    },
  };

  function toResult(frame: StreamFrame): IteratorResult<unknown> {
    if (frame.type === "item") return { done: false, value: frame.value };
    if (frame.type === "done") return { done: true, value: undefined };
    if (frame.type === "error") {
      const err = new Error(frame.error.message);
      err.name = frame.error.name;
      if (frame.error.stack !== undefined) err.stack = frame.error.stack;
      throw err;
    }
    return { done: true, value: undefined };
  }

  return { iterable, fail, detach };
}

function encode(value: AsyncIterable<unknown>, ctx: EncodeContext): unknown {
  const { channel, peerPort, token } = openChannel(ctx);
  ctx.registry.registerChannel(channel);
  const state = getState(ctx);
  let stopFn: () => void = () => {};
  stopFn = startEncodedProducer(channel, value, ctx.registry, () => {
    state.producerStops.delete(stopFn);
  });
  state.producerStops.add(stopFn);
  // Messageport transports hand over a port; Mux transports hand over a token
  // the peer resolves back to this channel on its transport.
  return {
    [CODEC_PLACEHOLDER_KEY]: COLLECTION_STREAM_TAG,
    ...(peerPort !== undefined ? { port: peerPort } : { token }),
  } satisfies StreamHandle;
}

function decode(
  placeholder: { port?: MessagePort; token?: unknown },
  ctx: DecodeContext,
): AsyncIterable<unknown> {
  const channel = placeholder.port !== undefined ? connectChannel(placeholder.port) : connectToken(
    ctx.transport,
    placeholder.token as { __mux: "open"; ch: number },
  );
  ctx.registry.registerChannel(channel);
  const state = getState(ctx);
  let failFn: () => void = () => {};
  let unregister: () => void = () => {};
  const { iterable, fail, detach } = createDecodedIterable(
    channel,
    ctx.registry,
    () => {
      state.consumerFails.delete(failFn);
      unregister();
    },
  );
  failFn = fail;
  state.consumerFails.add(failFn);
  unregister = registerRelease(iterable as object, () => {
    channel.send({ type: "release" } satisfies StreamFrame);
    channel.close();
    detach();
  });
  return iterable;
}

function onRegistryFail(state: CollectionStreamState | undefined): void {
  if (state === undefined) return;
  for (const fail of state.consumerFails) fail();
  for (const stop of state.producerStops) stop();
  state.consumerFails.clear();
  state.producerStops.clear();
}

export const collectionStreamCodec: Codec<AsyncIterable<unknown>> = {
  tag: COLLECTION_STREAM_TAG,
  matches,
  encode,
  decode,
  onRegistryFail,
};
