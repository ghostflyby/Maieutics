import { ConversationError } from "./types.ts";

interface Job {
  readonly signal: AbortSignal;
  readonly start: () => void;
  readonly cancel: () => void;
}

/** Bounded source work has one owner and a joined, idempotent disposal path. */
export class OwnedTasks {
  readonly #owner = new AbortController();
  readonly #queue: Job[] = [];
  readonly #running = new Set<Promise<void>>();
  #disposal: Promise<void> | undefined;
  constructor(readonly concurrency: number, readonly capacity: number) {}
  get signal(): AbortSignal {
    return this.#owner.signal;
  }
  get active(): number {
    return this.#running.size;
  }
  get queued(): number {
    return this.#queue.length;
  }
  run<T>(work: (signal: AbortSignal) => Promise<T>, caller?: AbortSignal): Promise<T> {
    if (this.signal.aborted) return Promise.reject(this.signal.reason);
    if (this.#queue.length >= this.capacity) {
      return Promise.reject(new ConversationError("request_capacity"));
    }
    const signal = caller ? AbortSignal.any([this.signal, caller]) : this.signal;
    if (signal.aborted) return Promise.reject(signal.reason);
    const result = Promise.withResolvers<T>();
    const job: Job = {
      signal,
      cancel: () => {
        const index = this.#queue.indexOf(job);
        if (index >= 0) {
          this.#queue.splice(index, 1);
          result.reject(signal.reason);
        }
      },
      start: () => {
        signal.removeEventListener("abort", job.cancel);
        const completion = (async () => {
          try {
            signal.throwIfAborted();
            const value = await work(signal);
            signal.throwIfAborted();
            result.resolve(value);
          } catch (error) {
            result.reject(error);
          }
        })().finally(() => {
          this.#running.delete(completion);
          this.#pump();
        });
        this.#running.add(completion);
      },
    };
    signal.addEventListener("abort", job.cancel, { once: true });
    this.#queue.push(job);
    this.#pump();
    return result.promise;
  }
  #pump(): void {
    while (!this.signal.aborted && this.#running.size < this.concurrency) {
      const job = this.#queue.shift();
      if (!job) break;
      job.start();
    }
  }
  dispose(): Promise<void> {
    if (!this.#disposal) {
      this.#owner.abort(new DOMException("Conversation closed", "AbortError"));
      this.#disposal = Promise.all([...this.#running]).then(() => {});
    }
    return this.#disposal;
  }
}
