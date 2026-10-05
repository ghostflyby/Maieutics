/**
 * Plugin-owned UI models (ADR 0038 stage 3, worker side).
 *
 * A granted plugin pushes native view-family frames to the kernel through the
 * `ui.models` capability and receives frontend events on its `UiEvent`
 * extension-point export. The kernel publishes open frames into the
 * foreground session's comm plane and announces them as `maieutics.view`
 * display members; a frontend that renders the announcement talks back over
 * the same comm, and the kernel routes those frames here.
 *
 * ```ts
 * import { defineExtensionPoint, ui } from "@maieutics/plugin-sdk";
 *
 * export const UiEvent = defineExtensionPoint("UiEvent", (event) => {
 *   ui.deliver(event); // routes {commId, data|closed} to the model's handlers
 * });
 *
 * export function showApprovalForm() {
 *   const form = ui.form(
 *     { title: "Approve deploy?", fields: [{ name: "note", type: "text" }] },
 *     { onSubmit: (values) => capabilities.invokeTool("deploy", values) },
 *   );
 *   return form.commId;
 * }
 * ```
 *
 * Lifecycle mirrors the comm plane: `form()` publishes the open (and the
 * kernel announces it), `close()` publishes the close, and a frontend-driven
 * close arrives as a `closed` event that releases the model's handlers.
 * Frames are fire-and-forget with a per-call budget — a session without a
 * live run stream rejects the open with `ui_frame_rejected`, surfaced as a
 * rejected promise from `form()`.
 */

import { callCapability } from "./mod.ts";

/** One field of a `maieutics/form` model (mirrors the kernel/SDK dialect). */
export interface UiFormField {
  readonly name: string;
  readonly label?: string;
  readonly type: "text" | "number" | "boolean" | "choice" | "password";
  readonly choices?: readonly (string | { label: string; value: string })[];
  readonly required?: boolean;
  readonly placeholder?: string;
}

/** The `maieutics/form` definition a plugin authors. */
export interface UiFormDef {
  readonly title?: string;
  readonly fields: readonly UiFormField[];
  readonly submitLabel?: string;
  readonly cancelLabel?: string;
}

/** Handlers of one live form model. */
export interface UiFormHandlers {
  readonly onSubmit?: (values: Record<string, unknown>) => void;
  readonly onCancel?: () => void;
  /** The frontend closed the comm (notebook closed, session switched past the
   * retention window); the model is dead and should not be reused. */
  readonly onClosed?: () => void;
}

/** A live plugin-owned form model. */
export interface UiFormHandle {
  readonly commId: string;
  /** Syncs one state key to the frontend (the native update dialect). */
  sync(key: string, value: unknown): Promise<void>;
  /** Closes the model deterministically. */
  close(): Promise<void>;
}

/** One `UiEvent` extension-point invocation from the kernel. */
export interface UiEventMessage {
  readonly commId: string;
  /** State deltas / one-shot events, as the native dialect decodes them. */
  readonly data?: { method: "update"; state: Record<string, unknown> } | {
    method: "event";
    name: string;
    payload?: unknown;
  };
  /** Present when the frontend closed the comm. */
  readonly closed?: boolean;
}

interface ModelEntry {
  handlers: UiFormHandlers;
  values: Record<string, unknown>;
}

const models = new Map<string, ModelEntry>();

function pushFrame(
  frame: Record<string, unknown>,
  timeoutMs?: number,
): Promise<unknown> {
  return callCapability("ui.models", frame, { timeoutMs });
}

function normalizeChoices(
  choices: readonly (string | { label: string; value: string })[],
): { value: string; label: string }[] {
  return choices.map((choice) =>
    typeof choice === "string" ? { value: choice, label: choice } : choice
  );
}

function deliverToModel(commId: string, message: UiEventMessage): void {
  const model = models.get(commId);
  if (model === undefined) return;
  if (message.closed) {
    models.delete(commId);
    model.handlers.onClosed?.();
    return;
  }
  if (message.data?.method === "update") {
    for (const [key, value] of Object.entries(message.data.state)) {
      model.values[key] = value;
    }
    return;
  }
  if (message.data?.method === "event" && message.data.name === "submit") {
    const payload = message.data.payload as { values?: Record<string, unknown> } | undefined;
    model.handlers.onSubmit?.(payload?.values ?? {});
    return;
  }
  if (message.data?.method === "event" && message.data.name === "cancel") {
    model.handlers.onCancel?.();
  }
}

/** Worker-side UI surface. Requires the `ui.models` capability granted in the
 * plugin manifest; every call is kernel-gated deny-by-default. */
export const ui: {
  form(def: UiFormDef, handlers?: UiFormHandlers): Promise<UiFormHandle>;
  sync(handle: UiFormHandle, key: string, value: unknown): Promise<void>;
  close(handle: UiFormHandle): Promise<void>;
  deliver(event: UiEventMessage): void;
} = {
  /** Creates a live form model: publishes the comm open through the kernel,
   * which announces it to the frontend. Rejects when the capability is not
   * granted, no session is live, or the frame budget expires. */
  async form(def: UiFormDef, handlers: UiFormHandlers = {}): Promise<UiFormHandle> {
    const commId = crypto.randomUUID();
    const values: Record<string, unknown> = {};
    const fields = def.fields.map((field) => ({
      ...field,
      ...(field.choices === undefined ? {} : { choices: normalizeChoices(field.choices) }),
    }));
    for (const field of fields) {
      values[field.name] = field.type === "boolean" ? false : "";
    }
    models.set(commId, { handlers, values });
    try {
      await pushFrame({
        kind: "open",
        commId,
        targetName: "maieutics.view/maieutics/form",
        data: {
          state: {
            ...(def.title === undefined ? {} : { title: def.title }),
            fields,
            ...(def.submitLabel === undefined ? {} : { submitLabel: def.submitLabel }),
            ...(def.cancelLabel === undefined ? {} : { cancelLabel: def.cancelLabel }),
            values,
          },
        },
      });
    } catch (error) {
      models.delete(commId);
      throw error;
    }
    return {
      commId,
      sync: async (key, value) => {
        if (!models.has(commId)) {
          throw new Error(`The UI model '${commId}' is closed.`);
        }
        values[key] = value;
        await pushFrame({
          kind: "message",
          commId,
          data: { method: "update", state: { [key]: value } },
        });
      },
      close: async () => {
        if (!models.has(commId)) return;
        models.delete(commId);
        await pushFrame({ kind: "close", commId });
      },
    };
  },

  /** Syncs one state key on a form handle (convenience over `handle.sync`). */
  sync(handle: UiFormHandle, key: string, value: unknown): Promise<void> {
    return handle.sync(key, value);
  },

  /** Closes a form handle (convenience over `handle.close`). */
  close(handle: UiFormHandle): Promise<void> {
    return handle.close();
  },

  /** Routes one `UiEvent` invocation to the model's handlers. This is what a
   * plugin's `UiEvent` extension-point export calls; unknown comm ids are
   * ignored (the kernel keeps running, invariant 18). */
  deliver(event: UiEventMessage): void {
    if (typeof event?.commId !== "string" || event.commId.length === 0) return;
    deliverToModel(event.commId, event);
  },
};
