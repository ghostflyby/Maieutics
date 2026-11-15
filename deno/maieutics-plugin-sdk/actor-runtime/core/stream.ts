/** Credit-driven streams: one next() authorizes one producer iteration. */
import { ActorDiedError, RemoteError, type SerializedError, serializeError } from "./protocol.ts";
import { ActorTransportError } from "./lifetime.ts";
import type { Channel } from "./channel.ts";

type StreamFrame =
  | { type: "start" | "next" | "release" }
  | { type: "item"; value: unknown }
  | { type: "done" }
  | { type: "error"; error: SerializedError };
export interface StreamProducerStop {
  (): void;
  readonly completion: Promise<void>;
}

/** Own the producer and its asynchronous generator cleanup until completion settles. */
export function startStreamProducer(
  channel: Channel,
  iterable: AsyncIterable<unknown>,
  onStopped?: () => void,
): StreamProducerStop {
  const finished = Promise.withResolvers<void>();
  void finished.promise.catch(() => {}); // the completion remains observable to the owning registry
  let iterator: AsyncIterator<unknown> | undefined;
  let stopped = false;
  let running = false;
  let credits = 0;
  const stop = (() => {
    if (stopped) return;
    stopped = true;
    channel.close();
    const cleanup = async () => {
      try {
        await iterator?.return?.();
        onStopped?.();
        finished.resolve();
      } catch (error) {
        finished.reject(error);
      }
    };
    void cleanup();
  }) as StreamProducerStop;
  Object.defineProperty(stop, "completion", { value: finished.promise });
  const pump = async (): Promise<void> => {
    running = true;
    try {
      iterator ??= iterable[Symbol.asyncIterator]();
      while (credits > 0 && !stopped) {
        credits--;
        const result = await iterator.next();
        if (stopped) return;
        if (result.done) {
          channel.send({ type: "done" } satisfies StreamFrame);
          stop();
          return;
        }
        channel.send({ type: "item", value: result.value } satisfies StreamFrame);
      }
    } catch (error) {
      if (!stopped) {
        channel.send({ type: "error", error: serializeError(error) } satisfies StreamFrame);
      }
      stop();
    } finally {
      running = false;
    }
  };
  channel.onClose(stop);
  channel.onMessage((message) => {
    const frame = message as StreamFrame;
    if (frame.type === "release") {
      stop();
      return;
    }
    if (frame.type !== "start" && frame.type !== "next") return;
    if (credits >= 128) {
      channel.close(new ActorTransportError("capacity", "Actor stream demand budget exceeded"));
      return;
    }
    credits++;
    if (!running && !stopped) {
      void pump().catch((error) => {
        finished.reject(error);
        stop();
      });
    }
  });
  return stop;
}

/** Rebuild a single-consumer stream without a producer-controlled item queue. */
export function createRemoteIterable(
  channel: Channel,
  onReleased?: () => void,
): { iterable: AsyncIterable<unknown>; fail: () => void; detach: () => void } {
  const waiters: Array<{
    resolve: (value: IteratorResult<unknown>) => void;
    reject: (reason: unknown) => void;
  }> = [];
  let closed = false;
  let acquired = false;
  let released = false;
  let failure: unknown;
  const detach = () => {
    if (!released) {
      released = true;
      onReleased?.();
    }
  };
  const finish = (reason?: unknown) => {
    if (closed) return;
    closed = true;
    failure = reason;
    for (const waiter of waiters.splice(0)) {
      if (reason !== undefined) waiter.reject(reason);
      else waiter.resolve({ done: true, value: undefined });
    }
    channel.close(reason);
    detach();
  };
  channel.onClose((reason) => {
    if (!closed) finish(reason ?? new ActorDiedError());
  });
  channel.onMessage((message) => {
    if (closed) return;
    const frame = message as StreamFrame;
    if (frame.type === "item") {
      const waiter = waiters.shift();
      if (!waiter) {
        finish(new ActorTransportError("protocol", "Actor stream item arrived without demand"));
        return;
      }
      waiter.resolve({ done: false, value: frame.value });
    } else if (frame.type === "done") finish();
    else if (frame.type === "error") finish(new RemoteError(frame.error));
  });
  const iterable: AsyncIterable<unknown> = {
    [Symbol.asyncIterator]() {
      if (acquired) throw new Error("Actor stream has a single consumer");
      acquired = true;
      let started = false;
      return {
        next() {
          if (failure !== undefined) return Promise.reject(failure);
          if (closed) return Promise.resolve({ done: true, value: undefined });
          if (waiters.length >= 128) {
            return Promise.reject(
              new ActorTransportError("capacity", "Actor stream pending demand exceeded"),
            );
          }
          const result = new Promise<IteratorResult<unknown>>((resolve, reject) =>
            waiters.push({ resolve, reject })
          );
          channel.send({ type: started ? "next" : "start" } satisfies StreamFrame);
          started = true;
          return result;
        },
        return() {
          if (!closed) channel.send({ type: "release" } satisfies StreamFrame);
          finish();
          return Promise.resolve({ done: true, value: undefined });
        },
      };
    },
  };
  return {
    iterable,
    fail: () => finish(new RemoteError(serializeError(new ActorDiedError()))),
    detach,
  };
}
