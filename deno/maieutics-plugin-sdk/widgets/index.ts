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
import { type ControlTemplate, controlTemplate } from "./controls.ts";
import { walkWidgets, type WidgetInstance } from "./vnode.ts";
import {
  bindLayoutModel,
  bindStyleModel,
  bindStyleProps,
  isStyleBlock,
  type StyleProps,
} from "./style.ts";

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

// Pinned so the host transport stays reachable for the widget lifetime.
let _host: WidgetHost | undefined;
let runtime: WidgetRuntime | undefined;

/** The REPL worker calls this once at bootstrap to bind the transport. */
export function bindWidgetHost(widgetHost: WidgetHost): void {
  _host = widgetHost;
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
    // mimeBundle is sync; the display contract expects a thenable.
    [DISPLAY]: () => Promise.resolve(model.mimeBundle()),
  };
}

/** The control factories exposed to TSX cells. */
export const IntSlider: ControlFactory = controlFactory("IntSlider");
export const FloatSlider: ControlFactory = controlFactory("FloatSlider");
export const Button: ControlFactory = controlFactory("Button");
export const Text: ControlFactory = controlFactory("Text");
export const ToggleButton: ControlFactory = controlFactory("ToggleButton");
export const IntRangeSlider: ControlFactory = controlFactory("IntRangeSlider");
export const Box: ControlFactory = controlFactory("Box");
export const VBox: ControlFactory = controlFactory("VBox");
export const HBox: ControlFactory = controlFactory("HBox");
export const Checkbox: ControlFactory = controlFactory("Checkbox");
export const Label: ControlFactory = controlFactory("Label");
export const HTML: ControlFactory = controlFactory("HTML");
export const Textarea: ControlFactory = controlFactory("Textarea");
export const Password: ControlFactory = controlFactory("Password");
export const IntText: ControlFactory = controlFactory("IntText");
export const FloatText: ControlFactory = controlFactory("FloatText");
export const BoundedIntText: ControlFactory = controlFactory("BoundedIntText");
export const IntProgress: ControlFactory = controlFactory("IntProgress");
export const FloatProgress: ControlFactory = controlFactory("FloatProgress");
export const DatePicker: ControlFactory = controlFactory("DatePicker");
export const Dropdown: ControlFactory = controlFactory("Dropdown");
export const Select: ControlFactory = controlFactory("Select");
export const ToggleButtons: ControlFactory = controlFactory("ToggleButtons");
export const RadioButtons: ControlFactory = controlFactory("RadioButtons");

/** A widget model factory: props (initial state + optional onChange) -> model. */
export type ControlFactoryProps = Record<string, unknown> & {
  onChange?: (key: string, value: unknown) => void;
  /** Unified web-CSS block, split into LayoutModel + per-control StyleModel. */
  style?: StyleProps;
  /** Bound verbatim to the control's StyleModel subclass (ipywidgets-literate). */
  styleModel?: Record<string, unknown>;
  /** Bound verbatim to the LayoutModel (ipywidgets-literate). */
  layoutModel?: Record<string, unknown>;
};

export type ControlFactory = (props?: ControlFactoryProps) => WidgetModel<Record<string, unknown>>;

/**
 * Normalize a selection control's `options` prop into the frontend's expected
 * shape: `options` as label-value pairs plus `_options_labels` (the labels).
 * Accepts `["a","b"]` (label = value), `[["a",1],["b",2]]` (pairs), or
 * `[{label, value}]` objects; an explicitly supplied `_options_labels` wins.
 */
export function normalizeSelectionOptions(
  template: ControlTemplate,
  props: Record<string, unknown>,
): Record<string, unknown> {
  if (!template.selection) return {};
  const raw = props.options;
  if (!Array.isArray(raw)) return {};
  const pairs: unknown[] = [];
  const labels: unknown[] = [];
  for (const entry of raw) {
    if (typeof entry === "string" || typeof entry === "number") {
      pairs.push([entry, entry]);
      labels.push(entry);
    } else if (
      Array.isArray(entry) && entry.length >= 1
    ) {
      pairs.push(entry);
      labels.push(entry[0]);
    } else if (
      typeof entry === "object" && entry !== null &&
      "label" in entry
    ) {
      pairs.push([entry.label, "value" in entry ? entry.value : entry.label]);
      labels.push(entry.label);
    }
  }
  return {
    options: pairs,
    _options_labels: labels,
  };
}

function controlFactory(kind: string): ControlFactory {
  const template = controlTemplate(kind);
  if (template === undefined) {
    throw new Error(`Unknown control '${kind}'.`);
  }
  // Tag the factory with its control kind so the jsx-runtime can recognize
  // component-identifier elements like `<IntSlider />` (the factory itself is
  // the component function; calling it yields the widget model).
  const factory = (props: ControlFactoryProps = {}): WidgetModel<Record<string, unknown>> => {
    const { onChange, ...stateProps } = props;
    const state = {
      ...WidgetRuntime.identityFields(template.modelName, template.viewName),
      ...template.defaults,
      ...stateProps,
      // Normalize AFTER stateProps so the derived options/_options_labels
      // win over a raw `options` array (an explicit _options_labels in props
      // still overrides because it lives in stateProps).
      ...normalizeSelectionOptions(template, stateProps),
    };
    // style={{...}} (unified) / styleModel={{...}} / layoutModel={{...}} become
    // nested Layout/Style model references in the control's state (IPY_MODEL_<id>).
    const nested = bindNestedProps(useWidgetRuntime(), kind, stateProps);
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
  return Object.assign(factory, { kind }) as ControlFactory;
}

/**
 * Convert style/styleModel/layoutModel props into nested-model references.
 * The unified `style` block is split by trait ownership (layout traits ->
 * LayoutModel, style traits -> the control's style subclass);
 * `styleModel`/`layoutModel` bind directly to their models. Returns
 * `{style, layout}` IPY_MODEL_ refs.
 */
function bindNestedProps(
  rt: WidgetRuntime,
  kind: string,
  props: Record<string, unknown>,
): Partial<Record<"style" | "layout", string>> {
  const result: Partial<Record<"style" | "layout", string>> = {};
  const style = isStyleBlock(props.style) ? props.style : undefined;
  if (style !== undefined) {
    Object.assign(result, bindStyleProps(rt, kind, style));
  }
  if (isStyleBlock(props.styleModel)) {
    result.style = bindStyleModel(rt, kind, props.styleModel);
  }
  if (isStyleBlock(props.layoutModel)) {
    result.layout = bindLayoutModel(rt, props.layoutModel);
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
