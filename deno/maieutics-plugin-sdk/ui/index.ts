/**
 * Maieutics UI module public surface (custom-UI framework, ADR 0038).
 *
 * Bind the runtime to the REPL's comm transport, then author native view
 * models from cells:
 *
 * ```ts
 * const { form } = maieutics.ui;
 * form({ fields: [{ name: "q", type: "text" }] }, (values) => { /* … *&#47; });
 * ```
 *
 * The `jupyter.widget` family keeps its own runtime and API
 * (`maieutics.widgets`); this module serves native families only. The REPL
 * worker binds the transport at bootstrap — importing the module outside a
 * bound host leaves the runtime unbound and `useUiRuntime()` throws.
 */

import { createForm, FormFamilyContract } from "./form.ts";
import { JUPYTER_DISPLAY, type UiBroadcast, type UiIncomingMessage } from "./family.ts";
import { UiModelRuntime } from "./runtime.ts";
import type { UiModel } from "./runtime.ts";

export type { UiBroadcast, UiIncomingMessage, UiIncomingDispatch, ViewFamilyContract } from "./family.ts";
export { JUPYTER_DISPLAY, NativeFamilyContract, NATIVE_DISPLAY_MIME, nativeTarget } from "./family.ts";
export { UiModelRuntime } from "./runtime.ts";
export type { UiModel, UiModelHandlers } from "./runtime.ts";
export {
  createForm,
  FORM_FAMILY,
  formState,
  initialValues,
  normalizeChoices,
  type FormDef,
  type FormFieldDef,
  type FormFieldType,
  type FormHandlers,
  type FormState,
} from "./form.ts";

/** The host surface the REPL worker injects (broadcast + comm subscription). */
export interface UiHost {
  broadcast: UiBroadcast;
  onComm: (
    event: "open" | "msg" | "close",
    handler: (message: {
      commId: string;
      targetName?: string;
      data?: unknown;
      buffers: Uint8Array[];
    }) => void,
  ) => void;
}

let runtime: UiModelRuntime | undefined;

/** The runtime bound to the REPL transport; throws before bindUiHost. */
export function useUiRuntime(): UiModelRuntime {
  if (runtime === undefined) {
    throw new Error(
      "The UI runtime is not bound. Import the Maieutics UI module inside " +
        "the REPL so the worker can inject the comm transport.",
    );
  }
  return runtime;
}

/**
 * Bind the runtime to a host transport (REPL worker bootstrap) and
 * pre-register the built-in native families. Mirrors `bindWidgetHost`:
 * each call binds a fresh runtime against the given host (tests bind fakes;
 * production binds once).
 */
export function bindUiHost(host: UiHost): UiModelRuntime {
  const bound = new UiModelRuntime(host.broadcast);
  bound.registerFamily(new FormFamilyContract());
  host.onComm("msg", (message) => {
    bound.handleIncoming({
      kind: 1,
      commId: message.commId,
      targetName: message.targetName,
      data: message.data,
      buffers: message.buffers,
    });
  });
  // Release the model registry when the frontend closes a comm; the kernel
  // keeps running (invariant 18) and a later create registers a fresh model.
  host.onComm("close", (message) => {
    bound.remove(message.commId);
  });
  runtime = bound;
  return bound;
}

/** Create a live form model (convenience over `useUiRuntime` + `createForm`). */
export function form(
  def: Parameters<typeof createForm>[1],
  handlers: Parameters<typeof createForm>[2] = {},
) {
  return displayable(createForm(useUiRuntime(), def, handlers));
}

/** Register a custom view-family contract on the bound runtime. */
export function registerFamily(contract: Parameters<UiModelRuntime["registerFamily"]>[0]): void {
  useUiRuntime().registerFamily(contract);
}

/** Create a model of any registered native family. */
export function model<State extends Record<string, unknown>>(
  family: string,
  state: State,
  handlers: Parameters<UiModelRuntime["create"]>[2] = {},
) {
  return displayable(useUiRuntime().create<State>(family, state, handlers));
}

/**
 * Make a model displayable as a cell result: evaluating it in a cell emits
 * the family's display-mime announcement (same contract as `createWidget`).
 */
export function displayable<State extends Record<string, unknown>>(
  model: UiModel<State>,
): UiModel<State> & { [JUPYTER_DISPLAY]: () => Promise<Record<string, unknown>> } {
  return {
    ...model,
    // announce is sync; the display contract expects a thenable.
    [JUPYTER_DISPLAY]: () => Promise.resolve(model.announce()),
  };
}
