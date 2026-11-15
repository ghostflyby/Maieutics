/** Synchronous, observable terminal state shared by transports and channels. */
export interface Lifetime {
  readonly closed: boolean;
  readonly reason: unknown;
  /** Reports close-observer and owned cleanup failures after the terminal transition. */
  readonly completion?: Promise<void>;
  onClose(listener: (reason: unknown) => void): () => void;
  close(reason?: unknown): void;
}

/** A typed failure at an Actor transport boundary. */
export class ActorTransportError extends Error {
  constructor(
    readonly code: "closed" | "protocol" | "capacity" | "decode" | "send",
    message: string,
    options?: ErrorOptions,
  ) {
    super(message, options);
    this.name = "ActorTransportError";
  }
}

interface OwnedLifetime extends Lifetime {
  readonly completion: Promise<void>;
  own(task: Promise<void> | undefined): void;
}

/** Close synchronously without throwing; completion joins cleanup and reports observer failures. */
export function createLifetime(): OwnedLifetime {
  let closed = false;
  let reason: unknown;
  const listeners = new Set<(reason: unknown) => void>();
  const tasks = new Set<Promise<void>>();
  const errors: unknown[] = [];
  let errorCount = 0;
  const finished = Promise.withResolvers<void>();
  void finished.promise.catch(() => {}); // The owning channel/transport exposes the rejection.
  const record = (error: unknown) => {
    errorCount++;
    if (errors.length < 128) errors.push(error);
  };
  const own = (task: Promise<void> | undefined): void => {
    if (!task) return;
    const observed = task.catch(record).finally(() => tasks.delete(observed));
    tasks.add(observed);
  };
  return {
    completion: finished.promise,
    own,
    get closed() {
      return closed;
    },
    get reason() {
      return reason;
    },
    onClose(listener) {
      if (closed) listener(reason);
      else listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    close(cause = new ActorTransportError("closed", "Actor channel closed")) {
      if (closed) return;
      closed = true;
      reason = cause;
      for (const listener of listeners) {
        try {
          listener(cause);
        } catch (error) {
          record(error);
        }
      }
      listeners.clear();
      const join = async () => {
        while (tasks.size) await Promise.all([...tasks]);
        if (errorCount) {
          finished.reject(
            new AggregateError(
              errors,
              `Actor close cleanup failed (${errorCount} failures; at most 128 retained)`,
            ),
          );
        } else finished.resolve();
      };
      void join();
    },
  };
}

export function positiveLimit(value: number | undefined, fallback: number): number {
  const limit = value ?? fallback;
  if (!Number.isSafeInteger(limit) || limit < 1) {
    throw new RangeError("Actor limit must be a positive safe integer");
  }
  return limit;
}

/** Stop waiting on a cooperative operation while still observing its eventual rejection. */
export function waitWithSignal<T>(operation: T | PromiseLike<T>, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const abort = () => reject(signal.reason);
    if (signal.aborted) reject(signal.reason);
    else signal.addEventListener("abort", abort, { once: true });
    Promise.resolve(operation).then(
      (value) => {
        signal.removeEventListener("abort", abort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener("abort", abort);
        reject(error);
      },
    );
  });
}
