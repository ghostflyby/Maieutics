/**
 * Maieutics widget runtime public surface.
 *
 * Bind ipywidgets model instances to TSX values and receive frontend-driven
 * changes:
 *
 * ```tsx
 * import { IntSlider, useWidgetRuntime } from "maieutics:widgets";
 * const widgets = useWidgetRuntime();
 * const slider = IntSlider({ value: 5, min: 0, max: 10,
 *   onChange: (key, value) => { /* update your data model *&#47; } });
 * slider; // displays the slider
 * ```
 *
 * The runtime is bound to the REPL worker's broadcast + comm subscription
 * when the module is imported (the worker injects the wiring). Rendering of
 * the control itself is the frontend's job (classic `@jupyter-widgets/controls`
 * via `@jupyter-widgets/jupyterlab-manager`); this module only maintains the
 * kernel-side model state and routes updates both ways.
 */

import { type WidgetModel, WidgetRuntime } from "./runtime.ts";
import { controlTemplate } from "./controls.ts";
import { walkWidgets, type WidgetInstance } from "./vnode.ts";
import { bindNestedStyle, isCssBlock } from "./style.ts";

/** Injected by the REPL worker (see installMaieuticsNamespace). */
interface WidgetHost {
  broadcast: (messageType: string, content: Record<string, unknown>, extra?: {
    metadata?: Record<string, unknown>;
    buffers?: Uint8Array[];
  }) => Promise<void>;
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

const DISPLAY = Symbol.for("Jupyter.display");

let host: WidgetHost | undefined;
let runtime: WidgetRuntime | undefined;

/** The REPL worker calls this once at bootstrap to bind the transport. */
export function bindWidgetHost(widgetHost: WidgetHost): void {
  host = widgetHost;
  runtime = new WidgetRuntime(widgetHost.broadcast);
  widgetHost.onComm("msg", (message) => {
    runtime?.handleIncoming({
      kind: 1,
      commId: message.commId,
      targetName: message.targetName,
      data: message.data,
      buffers: message.buffers,
    });
  });
  // Release the model registry when the frontend closes a comm; the kernel
  // keeps running (invariant 18) and a later re-open registers a fresh model.
  widgetHost.onComm("close", (message) => {
    runtime?.remove(message.commId);
  });
}

/** The runtime bound to the REPL transport; throws before bindWidgetHost. */
export function useWidgetRuntime(): WidgetRuntime {
  if (runtime === undefined) {
    throw new Error(
      "The widget runtime is not bound. Import the Maieutics widget module " +
        "inside the REPL so the worker can inject the comm transport.",
    );
  }
  return runtime;
}

/**
 * Create a widget model from a control factory's props. Returns a model that
 * displays as `application/vnd.jupyter.widget-view+json` and syncs changes
 * with the frontend.
 */
export function createWidget<State extends Record<string, unknown>>(
  state: State,
  onChange?: (key: string & keyof State, value: State[keyof State]) => void,
): WidgetModel<State> & { [DISPLAY]: () => Promise<Record<string, unknown>> } {
  const rt = useWidgetRuntime();
  const commId = crypto.randomUUID();
  const model = rt.init(commId, state, onChange);
  return {
    ...model,
    [DISPLAY]: async () => model.mimeBundle(),
  };
}

/** The control factories exposed to TSX cells. */
export const IntSlider = controlFactory("IntSlider");
export const FloatSlider = controlFactory("FloatSlider");
export const Button = controlFactory("Button");
export const Text = controlFactory("Text");
export const ToggleButton = controlFactory("ToggleButton");
export const IntRangeSlider = controlFactory("IntRangeSlider");
export const Box = controlFactory("Box");

/** A widget model factory: props (initial state + optional onChange) -> model. */
export type ControlFactory = (
  props?: Record<string, unknown> & { onChange?: (key: string, value: unknown) => void },
) => WidgetModel<Record<string, unknown>>;

function controlFactory(kind: string): ControlFactory {
  const template = controlTemplate(kind);
  if (template === undefined) {
    throw new Error(`Unknown control '${kind}'.`);
  }
  return (props = {}): WidgetModel<Record<string, unknown>> => {
    const { onChange, ...stateProps } = props;
    const state = {
      ...WidgetRuntime.identityFields(template.modelName, template.viewName),
      ...template.defaults,
      ...stateProps,
    };
    // style={{}} / layout={{}} become nested Layout/Style models referenced by
    // IPY_MODEL_<commId> in the control's state (frontend unpack_models).
    const nested = bindNestedProps(useWidgetRuntime(), stateProps);
    const model = createWidget(
      { ...state, ...nested },
      onChange === undefined
        ? undefined
        : (key: string, value: unknown) =>
          (onChange as (key: string, value: unknown) => void)(key, value),
    );
    // The model itself is the JSX value; rendering to the frontend happens
    // through $display when the cell evaluates it.
    return model;
  };
}

/**
 * Convert style/layout props into nested-model references. `style={{...}}`
 * binds a StyleModel, `layout={{...}}` binds a LayoutModel; the returned
 * object maps `style`/`layout` to their `IPY_MODEL_<commId>` strings.
 */
function bindNestedProps(
  rt: WidgetRuntime,
  props: Record<string, unknown>,
): Partial<Record<"style" | "layout", string>> {
  const result: Partial<Record<"style" | "layout", string>> = {};
  if (isCssBlock(props.style)) {
    result.style = bindNestedStyle(rt, "StyleModel", props.style);
  }
  if (isCssBlock(props.layout)) {
    result.layout = bindNestedStyle(rt, "LayoutModel", props.layout);
  }
  return result;
}

/**
 * Render a TSX vnode tree into widget models. Each control element becomes a
 * registered model (comm_open broadcast, display-ready); children of layout
 * widgets are nested through their `children` state.
 */
export function renderWidgets(root: unknown): unknown {
  const instances = walkWidgets(root);
  return instances.map((instance) => instantiate(instance));
}

function instantiate(instance: WidgetInstance): unknown {
  const template = controlTemplate(instance.kind);
  if (template === undefined) {
    throw new Error(`Unknown ipywidgets control '${instance.kind}'.`);
  }
  const childModels = instance.children.map((child) => instantiate(child));
  const { onChange, ...stateProps } = instance.props;
  const childrenState = childModels.length > 0 ? { children: childModels } : {};
  const state = {
    ...WidgetRuntime.identityFields(template.modelName, template.viewName),
    ...template.defaults,
    ...stateProps,
    ...childrenState,
  };
  return createWidget(state, onChange as never);
}

/** Widget runtime re-exports. */
export type { WidgetModel } from "./runtime.ts";
export { WidgetRuntime } from "./runtime.ts";
