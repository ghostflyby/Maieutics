import type { ViewAddress, ViewRequest, ViewResponse } from "../conversationProtocol.ts";

/** A timer returns its cancellation function; tests can advance deadlines without sleeping. */
export type ViewRequestScheduler = (expire: () => void, milliseconds: number) => () => void;
const defaultSchedule: ViewRequestScheduler = (expire, milliseconds) => {
  const timer = setTimeout(expire, milliseconds);
  return () => clearTimeout(timer);
};

/** Document-owned request correlation. Kernel startup belongs to the extension, so ready
 * has no view deadline; owner/caller cancellation still releases every pending request. */
export class ViewRequests {
  private nextId = 0;
  private readonly pending = new Map<number, { resolve(value: unknown): void; reject(error: unknown): void }>();
  constructor(private readonly address: ViewAddress, private readonly post: (message: ViewRequest) => void, private readonly owner: AbortSignal, private readonly schedule: ViewRequestScheduler = defaultSchedule) {}

  send(method: ViewRequest["method"], params?: Record<string, unknown>): number {
    const id = ++this.nextId;
    this.post({ ...this.address, id, method, params });
    return id;
  }

  request<T>(method: ViewRequest["method"], params?: Record<string, unknown>, caller?: AbortSignal): Promise<T> {
    const signal = caller ? AbortSignal.any([this.owner, caller]) : this.owner;
    if (signal.aborted) return Promise.reject(signal.reason);
    if (this.pending.size >= 64) return Promise.reject(failure("request_capacity", "Too many pending view requests"));
    return new Promise<T>((resolve, reject) => {
      const id = this.nextId + 1;
      let cancelTimer = () => {};
      const finish = (value: unknown, error?: unknown) => {
        if (!this.pending.delete(id)) return;
        cancelTimer();
        signal.removeEventListener("abort", abort);
        if (error !== undefined) reject(error);
        else resolve(value as T);
      };
      const stop = (error: unknown) => {
        try {
          this.send("abort", { requestId: id });
        } catch (postError) {
          finish(undefined, new AggregateError([error, postError], "View cancellation delivery failed"));
          return;
        }
        finish(undefined, error);
      };
      const abort = () => stop(signal.reason);
      this.pending.set(id, { resolve: (value) => finish(value), reject: (error) => finish(undefined, error) });
      if (method !== "ready") cancelTimer = this.schedule(() => stop(failure("request_timeout", method === "submit" ? "无法确认提交结果，请检查历史后再决定是否重发。" : "请求超时，请重新打开会话。")), 15_000);
      signal.addEventListener("abort", abort, { once: true });
      try {
        this.send(method, params);
      } catch (error) {
        finish(undefined, error);
      }
    });
  }

  receive(message: ViewResponse): void {
    if (
      message.version !== 1 || message.sessionId !== this.address.sessionId ||
      message.viewId !== this.address.viewId || message.generation !== this.address.generation
    ) return;
    const target = this.pending.get(message.id);
    if (message.error) target?.reject(failure(message.error.code, message.error.message));
    else target?.resolve(message.result);
  }
}

function failure(code: string, message: string): Error & { code: string } {
  return Object.assign(new Error(message), { code });
}
