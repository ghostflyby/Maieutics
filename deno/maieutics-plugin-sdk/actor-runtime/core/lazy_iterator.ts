/** Project an RPC promise as a lazy, single-consumer iterable without host dependencies. */
export function attachLazyIterator(promise: Promise<unknown>): void {
  // Bare AsyncIterable callers do not have a Promise.catch surface until they iterate.
  void promise.catch(() => {});
  let acquired = false;
  Object.defineProperty(promise, Symbol.asyncIterator, {
    configurable: true,
    value() {
      if (acquired) throw new Error("Actor stream has a single consumer");
      acquired = true;
      let closed = false;
      let pending: Promise<AsyncIterator<unknown>> | undefined;
      const iterator = (): Promise<AsyncIterator<unknown>> => {
        pending ??= promise.then((value) => {
          if (value === null || typeof value !== "object" || !(Symbol.asyncIterator in value)) {
            throw new TypeError("Actor method did not return an AsyncIterable");
          }
          return (value as AsyncIterable<unknown>)[Symbol.asyncIterator]();
        });
        return pending;
      };
      return {
        async next(): Promise<IteratorResult<unknown>> {
          if (closed) return { done: true, value: undefined };
          const inner = await iterator();
          return closed ? { done: true, value: undefined } : inner.next();
        },
        async return(value?: unknown): Promise<IteratorResult<unknown>> {
          closed = true;
          // Release a result that arrives after abandonment without starting enumeration.
          const inner = await iterator();
          return inner.return ? inner.return(value) : { done: true, value };
        },
      };
    },
  });
}
