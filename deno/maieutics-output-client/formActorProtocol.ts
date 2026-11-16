import {
  ActorTransportError,
  createActorConnection,
  messageTransport,
} from "@maieutics/plugin-sdk/actor/browser";

export { ActorTransportError, createActorConnection, messageTransport };
export const FORM_ACTOR_SOURCE = "maieutics-form-actor";
export const FORM_ACTOR_VERSION = 1;
export const FORM_ACTOR_LIMIT = 128;
export const FORM_ACTOR_FRAME_BYTES = 1024 * 1024;

/** Bound the native value graph without serializing binary, expanding cycles or recursing. */
export function formFrameSize(value: unknown): number {
  const pending: Array<{ value: unknown; depth: number }> = [{ value, depth: 0 }];
  const seen = new Set<object>();
  let bytes = 0, nodes = 0;
  try {
    while (pending.length) {
      const item = pending.pop();
      if (!item) break;
      if (++nodes > 16384 || item.depth > 64) return Number.MAX_SAFE_INTEGER;
      const value = item.value;
      bytes += typeof value === "string" ? value.length * 2 : 8;
      if (value !== null && typeof value === "object" && !seen.has(value)) {
        seen.add(value);
        if (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) bytes += value.byteLength;
        else if (value instanceof Blob) bytes += value.size;
        else if (value instanceof Map || value instanceof Set) {
          for (const entry of value) {
            pending.push({ value: entry, depth: item.depth + 1 });
            if (pending.length + nodes > 16384) return Number.MAX_SAFE_INTEGER;
          }
        } else {
          for (const key in value) {
            if (!Object.hasOwn(value, key)) continue;
            bytes += key.length * 2;
            pending.push({ value: (value as Record<string, unknown>)[key], depth: item.depth + 1 });
            if (pending.length + nodes > 16384 || bytes > FORM_ACTOR_FRAME_BYTES) {
              return Number.MAX_SAFE_INTEGER;
            }
          }
        }
      }
      if (bytes > FORM_ACTOR_FRAME_BYTES) return Number.MAX_SAFE_INTEGER;
    }
    return bytes;
  } catch {
    return Number.MAX_SAFE_INTEGER;
  }
}

export interface FormAttachment {
  readonly modelId: string;
  readonly viewId: string;
}
export type FormActions = {
  mount(modelId: string, viewId: string): Promise<void>;
  unmount(modelId: string, viewId: string): Promise<void>;
  event(
    modelId: string,
    viewId: string,
    name: "submit" | "cancel",
    payload: unknown,
  ): Promise<void>;
};
export type FormUpdates = {
  receive(message: unknown): Promise<void>;
};

export function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
export function envelope(value: unknown): value is Record<string, unknown> {
  return record(value) && value.source === FORM_ACTOR_SOURCE &&
    value.version === FORM_ACTOR_VERSION;
}
export function identity(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= 1024;
}
export function attachment(modelId: unknown, viewId: unknown): asserts modelId is string {
  if (!identity(modelId) || !identity(viewId)) {
    throw new ActorTransportError("protocol", "Invalid form attachment identity");
  }
}
export function frame(type: string, values: Record<string, unknown> = {}): unknown {
  return { source: FORM_ACTOR_SOURCE, version: FORM_ACTOR_VERSION, type, ...values };
}

/** The embedding host owns each asynchronous post, including a false delivery result. */
export class FormPosts {
  readonly #pending = new Set<Promise<void>>();
  readonly #errors: unknown[] = [];
  #bytes = 0;

  send(
    message: unknown,
    post: (message: unknown) => void | PromiseLike<unknown>,
    failed: (error: ActorTransportError) => void,
  ): void {
    const size = formFrameSize(message);
    if (
      this.#pending.size >= FORM_ACTOR_LIMIT * 4 || this.#bytes + size > FORM_ACTOR_FRAME_BYTES * 4
    ) {
      const error = new ActorTransportError("capacity", "Form Actor delivery budget exceeded");
      if (this.#errors.length < FORM_ACTOR_LIMIT) this.#errors.push(error);
      failed(error);
      return;
    }
    const fail = (cause: unknown) => {
      const error = new ActorTransportError("send", "Form Actor message delivery failed", {
        cause,
      });
      if (this.#errors.length < FORM_ACTOR_LIMIT) this.#errors.push(error);
      failed(error);
    };
    let result: void | PromiseLike<unknown>;
    try {
      result = post(message);
    } catch (error) {
      fail(error);
      return;
    }
    this.#bytes += size;
    const task = Promise.resolve(result).then((delivered) => {
      if (delivered === false) fail(new Error("The form renderer is no longer available"));
    }, fail).finally(() => {
      this.#pending.delete(task);
      this.#bytes -= size;
    });
    this.#pending.add(task);
  }

  async join(): Promise<void> {
    while (this.#pending.size) await Promise.all([...this.#pending]);
    if (this.#errors.length) throw new AggregateError(this.#errors, "Form Actor delivery failed");
  }
}
