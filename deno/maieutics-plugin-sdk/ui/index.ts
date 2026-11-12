/** Declarative forms. Vue components live in the shared browser output client. */
import { readonly, type Ref, shallowRef } from "../vue_reactivity.ts";
import { type FormDef, type FormHandlers, type FormState, formState } from "./form.ts";
export * from "./form.ts";

/** Form display data contains no component source or runtime registration. */
export const FORM_MIME = "application/vnd.maieutics.form+json";
/** Comm target for the version-one form data contract. */
export const FORM_TARGET = "maieutics.form";
/** Display hook supplied by the REPL. */
export const JUPYTER_DISPLAY = Symbol.for("Jupyter.display");

/** A form's transport, supplied by its owning REPL worker. */
export interface UiHost {
  broadcast(messageType: string, content: Record<string, unknown>): Promise<void>;
  onComm(
    event: "msg" | "close",
    handler: (message: { commId: string; data?: unknown }) => void,
  ): void;
}

/** A live form; updates are awaited and closing releases its handlers. */
export interface FormHandle {
  readonly commId: string;
  readonly state: Readonly<Ref<FormState>>;
  update(patch: Partial<FormState>): Promise<void>;
  close(): Promise<void>;
  [JUPYTER_DISPLAY](): Promise<Record<string, unknown>>;
}

interface Entry {
  state: Ref<FormState>;
  handlers: FormHandlers;
  tail: Promise<void>;
  pending: number;
}

/** Owns form data and comm routing, with no custom component registry. */
export class FormController {
  readonly #host: UiHost;
  readonly #forms = new Map<string, Entry>();
  private constructor(host: UiHost) {
    this.#host = host;
  }

  /** Explicitly attach one controller to its owning worker transport. */
  static bind(host: UiHost): FormController {
    const controller = new FormController(host);
    controller.#bind();
    return controller;
  }

  #bind(): void {
    const host = this.#host;
    host.onComm("close", ({ commId }) => this.#forms.delete(commId));
    host.onComm("msg", ({ commId, data }) => {
      const entry = this.#forms.get(commId);
      if (!entry || !isRecord(data) || data.method !== "event") return;
      if (data.name === "cancel") entry.handlers.onCancel?.();
      if (data.name === "submit" && isRecord(data.payload) && isRecord(data.payload.values)) {
        entry.handlers.onSubmit?.(data.payload.values);
      }
    });
  }

  /** Publish the form before returning a displayable handle; failed opens retain no entry. */
  async form(def: FormDef, handlers: FormHandlers = {}): Promise<FormHandle> {
    if (this.#forms.size >= 128) throw new Error("form_limit");
    const commId = crypto.randomUUID();
    const entry: Entry = {
      state: shallowRef(formState(def)),
      handlers,
      tail: Promise.resolve(),
      pending: 0,
    };
    this.#forms.set(commId, entry);
    try {
      await this.#host.broadcast("comm_open", {
        comm_id: commId,
        target_name: FORM_TARGET,
        data: { state: entry.state.value },
      });
    } catch (error) {
      this.#forms.delete(commId);
      throw error;
    }
    const requireOpen = () => {
      if (!this.#forms.has(commId)) throw new Error("form_closed");
    };
    requireOpen();
    return {
      commId,
      state: readonly(entry.state),
      update: async (patch) => {
        requireOpen();
        if (entry.pending >= 64) throw new Error("form_update_limit");
        const captured = structuredClone(patch);
        entry.pending++;
        const operation = entry.tail.then(async () => {
          requireOpen();
          const state = { ...entry.state.value, ...captured };
          await this.#host.broadcast("comm_msg", {
            comm_id: commId,
            data: { method: "update", state },
          });
          if (this.#forms.has(commId)) entry.state.value = state;
        }).finally(() => {
          entry.pending--;
        });
        entry.tail = operation.catch(() => {});
        await operation;
      },
      close: async () => {
        if (!this.#forms.delete(commId)) return;
        await entry.tail;
        await this.#host.broadcast("comm_close", { comm_id: commId });
      },
      [JUPYTER_DISPLAY]: () =>
        Promise.resolve({
          [FORM_MIME]: { version: 1, modelId: commId, state: entry.state.value },
          "text/plain": entry.state.value.title ?? "Form",
        }),
    };
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
