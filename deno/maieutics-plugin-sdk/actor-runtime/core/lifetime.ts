/** Synchronous, observable terminal state shared by transports and channels. */
export interface Lifetime {
  readonly closed: boolean;
  readonly reason: unknown;
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

/** Notify all owners exactly once, including observers attached after closure. */
export function createLifetime(): Lifetime {
  let closed = false;
  let reason: unknown;
  const listeners = new Set<(reason: unknown) => void>();
  return {
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
      const errors: unknown[] = [];
      for (const listener of listeners) {
        try {
          listener(cause);
        } catch (error) {
          errors.push(error);
        }
      }
      listeners.clear();
      if (errors.length) throw new AggregateError(errors, "Actor close observers failed");
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
