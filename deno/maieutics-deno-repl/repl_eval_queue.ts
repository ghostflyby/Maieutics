/** Bounded FIFO used by the REPL eval owner to apply explicit backpressure. */
export class ReplEvalQueue<T> {
  readonly #capacity: number;
  readonly #items: T[] = [];
  readonly #consumers: Array<Deferred<T>> = [];
  readonly #producers: Array<PendingProducer<T>> = [];
  #closed: unknown | undefined;

  constructor(capacity: number) {
    if (!Number.isSafeInteger(capacity) || capacity <= 0) {
      throw new RangeError("ReplEvalQueue capacity must be a positive integer.");
    }
    this.#capacity = capacity;
  }

  get size(): number {
    return this.#items.length;
  }

  enqueue(value: T): Promise<void> {
    if (this.#closed !== undefined) {
      return Promise.reject(this.#closed);
    }
    const consumer = this.#consumers.shift();
    if (consumer !== undefined) {
      consumer.resolve(value);
      return Promise.resolve();
    }
    if (this.#items.length < this.#capacity) {
      this.#items.push(value);
      return Promise.resolve();
    }
    const accepted = replEvalDeferred<void>();
    this.#producers.push({ value, accepted });
    return accepted.promise;
  }

  tryEnqueue(value: T): boolean {
    if (this.#closed !== undefined) {
      return false;
    }
    const consumer = this.#consumers.shift();
    if (consumer !== undefined) {
      consumer.resolve(value);
      return true;
    }
    if (this.#items.length >= this.#capacity) {
      return false;
    }
    this.#items.push(value);
    return true;
  }

  dequeue(): Promise<T> {
    const value = this.#items.shift();
    if (value !== undefined) {
      this.#admitProducer();
      return Promise.resolve(value);
    }
    const producer = this.#producers.shift();
    if (producer !== undefined) {
      producer.accepted.resolve();
      return Promise.resolve(producer.value);
    }
    if (this.#closed !== undefined) {
      return Promise.reject(this.#closed);
    }
    const consumer = replEvalDeferred<T>();
    this.#consumers.push(consumer);
    return consumer.promise;
  }

  close(reason: unknown = new Error("The queue is closed.")): void {
    if (this.#closed !== undefined) {
      return;
    }
    this.#closed = reason;
    for (const consumer of this.#consumers.splice(0)) {
      consumer.reject(reason);
    }
    for (const producer of this.#producers.splice(0)) {
      producer.accepted.reject(reason);
    }
  }

  #admitProducer(): void {
    const producer = this.#producers.shift();
    if (producer === undefined) {
      return;
    }
    const consumer = this.#consumers.shift();
    if (consumer === undefined) {
      this.#items.push(producer.value);
    } else {
      consumer.resolve(producer.value);
    }
    producer.accepted.resolve();
  }
}

export interface Deferred<T> {
  promise: Promise<T>;
  resolve(value: T | PromiseLike<T>): void;
  reject(reason?: unknown): void;
}

interface PendingProducer<T> {
  value: T;
  accepted: Deferred<void>;
}

export function replEvalDeferred<T>(): Deferred<T> {
  let resolve!: Deferred<T>["resolve"];
  let reject!: Deferred<T>["reject"];
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}
