/**
 * style/layout/css prop handling for classic controls.
 *
 * ipywidgets' `style` and `layout` attributes are nested models (`StyleModel`
 * / `LayoutModel`) in `@jupyter-widgets/base`, referenced from a control's
 * state by the `IPY_MODEL_<commId>` string (see `unpack_models` in the
 * frontend `@jupyter-widgets/base`). The CSS properties are the model's
 * state keys, written in the ipywidgets snake_case form.
 *
 * The Maieutics surface accepts **web (camelCase) CSS keys** and maps them to
 * the ipywidgets trait each one belongs to:
 *   - layout traits (container geometry/composition) -> LayoutModel
 *   - style traits (per-control appearance)        -> the control's style
 *     subclass (SliderStyleModel, ButtonStyleModel, ...)
 *
 * A unified `css={{...}}` block is split by trait ownership; `style={{...}}`
 * and `layout={{...}}` remain supported for ipywidgets-literate callers.
 */

import { WidgetRuntime } from "./runtime.ts";

/** LayoutModel trait names (frontend widget_layout.ts css_properties): camelCase -> snake_case. */
const LAYOUT_TO_IPYWIDGETS: Readonly<Record<string, string>> = {
  alignContent: "align_content",
  alignItems: "align_items",
  alignSelf: "align_self",
  borderTop: "border_top",
  borderRight: "border_right",
  borderBottom: "border_bottom",
  borderLeft: "border_left",
  bottom: "bottom",
  display: "display",
  flex: "flex",
  flexFlow: "flex_flow",
  height: "height",
  justifyContent: "justify_content",
  justifyItems: "justify_items",
  left: "left",
  margin: "margin",
  maxHeight: "max_height",
  maxWidth: "max_width",
  minHeight: "min_height",
  minWidth: "min_width",
  order: "order",
  overflow: "overflow",
  padding: "padding",
  right: "right",
  top: "top",
  visibility: "visibility",
  width: "width",
  objectFit: "object_fit",
  objectPosition: "object_position",
  gridAutoColumns: "grid_auto_columns",
  gridAutoFlow: "grid_auto_flow",
  gridAutoRows: "grid_auto_rows",
  gridGap: "grid_gap",
  gridTemplateRows: "grid_template_rows",
  gridTemplateColumns: "grid_template_columns",
  gridTemplateAreas: "grid_template_areas",
  gridRow: "grid_row",
  gridColumn: "grid_column",
  gridArea: "grid_area",
};

/** StyleModel trait names (frontend widget_style.ts, per-control union): camelCase -> snake_case. */
const STYLE_TO_IPYWIDGETS: Readonly<Record<string, string>> = {
  fontFamily: "font_family",
  fontSize: "font_size",
  fontStyle: "font_style",
  fontWeight: "font_weight",
  fontVariant: "font_variant",
  color: "color",
  textColor: "text_color",
  textAlign: "text_align",
  textDecoration: "text_decoration",
  textTransform: "text_transform",
  background: "background",
  whiteSpace: "white_space",
  handleColor: "handle_color",
  buttonColor: "button_color",
  barColor: "bar_color",
};

/** Web (camelCase) CSS properties accepted by the unified `css` prop. */
export type CssProps = Partial<
  Record<keyof typeof LAYOUT_TO_IPYWIDGETS | keyof typeof STYLE_TO_IPYWIDGETS, string>
>;

/** The split of one css block into layout vs style trait state. */
export interface SplitCss {
  layout: Record<string, unknown>;
  style: Record<string, unknown>;
}

/**
 * Split a `css={{...}}` block by trait ownership. Layout traits go to the
 * LayoutModel state, style traits to the StyleModel state; unknown keys are
 * kept (the frontend ignores unknown traits, so this is safe).
 */
export function splitCssProps(props: Record<string, unknown>): SplitCss {
  const layout: Record<string, unknown> = {};
  const style: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(props)) {
    const layoutKey = LAYOUT_TO_IPYWIDGETS[key];
    if (layoutKey !== undefined) {
      layout[layoutKey] = value;
      continue;
    }
    const styleKey = STYLE_TO_IPYWIDGETS[key];
    if (styleKey !== undefined) {
      style[styleKey] = value;
      continue;
    }
    layout[key] = value;
  }
  return { layout, style };
}

/** True when a value is a plain object (a candidate css/style/layout block). */
export function isCssBlock(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Per-control style subclass (frontend widget_style.ts), or undefined. */
export function styleModelFor(kind: string): { modelName: string } | undefined {
  switch (kind) {
    case "IntSlider":
    case "FloatSlider":
    case "IntRangeSlider":
      return { modelName: "SliderStyleModel" };
    case "Button":
    case "ToggleButton":
      return { modelName: "ButtonStyleModel" };
    case "Text":
      return { modelName: "TextStyleModel" };
    default:
      return undefined;
  }
}

/** The module a style model lives in: generic base models live in base, per-control subclasses in controls. */
function styleModelModule(modelName: string): string {
  return modelName === "StyleModel" || modelName === "LayoutModel"
    ? "@jupyter-widgets/base"
    : "@jupyter-widgets/controls";
}

/**
 * Create a nested model for one style/layout block and return the
 * `IPY_MODEL_<commId>` reference. The nested model is registered and
 * broadcast by the runtime; its state is the normalized CSS properties.
 */
export function bindNestedStyle(
  runtime: WidgetRuntime,
  modelName: string,
  props: Record<string, unknown>,
  modelModule: string = styleModelModule(modelName),
): string {
  const commId = crypto.randomUUID();
  runtime.initNested(commId, modelName, props, modelModule);
  return WidgetRuntime.modelRef(commId);
}

/**
 * Bind a unified `css={{...}}` block: split by trait ownership and create the
 * nested LayoutModel and/or per-control StyleModel references. `kind` selects
 * the control's style subclass.
 */
export function bindCssProps(
  runtime: WidgetRuntime,
  kind: string,
  props: Record<string, unknown>,
): { style?: string; layout?: string } {
  const { layout, style } = splitCssProps(props);
  const result: { style?: string; layout?: string } = {};
  if (Object.keys(layout).length > 0) {
    result.layout = bindNestedStyle(runtime, "LayoutModel", layout, "@jupyter-widgets/base");
  }
  if (Object.keys(style).length > 0) {
    const subclass = styleModelFor(kind);
    const modelName = subclass?.modelName ?? "StyleModel";
    result.style = bindNestedStyle(runtime, modelName, style, styleModelModule(modelName));
  }
  return result;
}
