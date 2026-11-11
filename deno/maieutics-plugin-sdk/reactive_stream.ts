import { effectScope, onScopeDispose, watch } from "./vue_reactivity.ts";

/** Maximum retained changes per iterator; overflow terminates visibly rather than dropping data. */
export const MaximumReactiveQueueLength = 1024;

/** A bounded reactive subscription cannot continue under the reported condition. */
export class ReactiveSubscriptionError extends Error {
  constructor(
    readonly code: "reactive_backpressure" | "concurrent_next" | "reactive_observation_failed",
  ) {
    super(code);
    this.name = "ReactiveSubscriptionError";
  }
}

/** Own one synchronous Vue watcher per iterator, with bounded delivery and deterministic return. */
export function reactiveStream<T>(
  observe: (emit: (value: T) => void) => () => void,
): AsyncIterable<T> {
  return {
    [Symbol.asyncIterator](): AsyncIterableIterator<T> {
      const scope = effectScope();
      const queue: T[] = [];
      let closed = false;
      let failure: ReactiveSubscriptionError | undefined;
      let pending: {
        resolve(value: IteratorResult<T>): void;
        reject(error: Error): void;
      } | undefined;
      const finish = (error?: ReactiveSubscriptionError) => {
        if (closed) return;
        closed = true;
        failure = error;
        queue.length = 0;
        scope.stop();
        const waiting = pending;
        pending = undefined;
        if (error) waiting?.reject(error);
        else waiting?.resolve({ done: true, value: undefined });
      };
      const emit = (value: T) => {
        if (closed) return;
        const waiting = pending;
        if (waiting) {
          pending = undefined;
          waiting.resolve({ done: false, value });
        } else if (queue.length >= MaximumReactiveQueueLength) {
          finish(new ReactiveSubscriptionError("reactive_backpressure"));
        } else queue.push(value);
      };
      scope.run(() => {
        onScopeDispose(() => finish());
        const read = observe(emit);
        watch(() => {
          try {
            read();
          } catch {
            finish(new ReactiveSubscriptionError("reactive_observation_failed"));
          }
        }, null);
      });
      return {
        [Symbol.asyncIterator]() {
          return this;
        },
        next() {
          if (failure) return Promise.reject(failure);
          if (closed) return Promise.resolve({ done: true, value: undefined });
          if (pending) return Promise.reject(new ReactiveSubscriptionError("concurrent_next"));
          if (queue.length) {
            return Promise.resolve({ done: false, value: queue.shift() as T });
          }
          return new Promise<IteratorResult<T>>((resolve, reject) => {
            pending = { resolve, reject };
          });
        },
        return() {
          finish();
          return Promise.resolve({ done: true, value: undefined });
        },
      };
    },
  };
}
