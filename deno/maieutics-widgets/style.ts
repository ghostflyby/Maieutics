/**
 * style={{}} / layout={{}} prop handling for classic controls.
 *
 * ipywidgets' `style`/`layout` attributes are nested models (`StyleModel` /
 * `LayoutModel`) in `@jupyter-widgets/base`, referenced from a control's
 * state by the `IPY_MODEL_<commId>` string (see `unpack_models` in the
 * frontend `@jupyter-widgets/base`). Each nested model is registered and
 * broadcast with its own comm_open; the CSS properties are the model's
 * state keys, written in the ipywidgets snake_case form.
 */

import { WidgetRuntime } from "./runtime.ts";

/** CSS property -> ipywidgets snake_case key (subset used by LayoutModel). */
const CSS_TO_IPYWIDGETS: Readonly<Record<string, string>> = {
  alignItems: "align_items",
  alignSelf: "align_self",
  alignContent: "align_content",
  borderTop: "border_top",
  borderRight: "border_right",
  borderBottom: "border_bottom",
  borderLeft: "border_left",
  justifyContent: "justify_content",
  justifyItems: "justify_items",
  flexFlow: "flex_flow",
  maxHeight: "max_height",
  maxWidth: "max_width",
  minHeight: "min_height",
  minWidth: "min_width",
  gridGap: "grid_gap",
  gridRow: "grid_row",
  gridColumn: "grid_column",
  gridArea: "grid_area",
  gridAutoColumns: "grid_auto_columns",
  gridAutoFlow: "grid_auto_flow",
  gridAutoRows: "grid_auto_rows",
  gridTemplateColumns: "grid_template_columns",
  gridTemplateRows: "grid_template_rows",
  gridTemplateAreas: "grid_template_areas",
  objectFit: "object_fit",
  objectPosition: "object_position",
  overflow: "overflow",
  visibility: "visibility",
  // StyleModel properties (frontend widget_style.ts).
  fontFamily: "font_family",
  fontSize: "font_size",
  fontStyle: "font_style",
  fontWeight: "font_weight",
  fontVariant: "font_variant",
  color: "color",
  background: "background",
  backgroundColor: "background_color",
  borderColor: "border_color",
  borderStyle: "border_style",
  borderWidth: "border_width",
  textAlign: "text_align",
  textDecoration: "text_decoration",
  textTransform: "text_transform",
  letterSpacing: "letter_spacing",
  lineHeight: "line_height",
  opacity: "opacity",
  whiteSpace: "white_space",
  wordWrap: "word_wrap",
  cursor: "cursor",
  outline: "outline",
};

/** Keys passed through unchanged (already snake_case or single words). */
const CSS_PASSTHROUGH: ReadonlySet<string> = new Set([
  "width",
  "height",
  "min_width",
  "min_height",
  "max_width",
  "max_height",
  "display",
  "flex",
  "order",
  "margin",
  "padding",
  "top",
  "right",
  "bottom",
  "left",
]);

/**
 * Normalize a style/layout object's keys to ipywidgets snake_case. Unknown
 * camelCase CSS keys are kept as-is (the frontend ignores unknown state
 * keys, so this is safe); values pass through unchanged.
 */
export function normalizeCssProps(
  props: Record<string, unknown>,
): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(props)) {
    const mapped = CSS_TO_IPYWIDGETS[key] ?? key;
    out[mapped] = value;
  }
  return out;
}

/**
 * Create a nested LayoutModel/StyleModel for a style/layout prop object and
 * return the `IPY_MODEL_<commId>` reference string. The nested model is
 * registered and broadcast by the runtime; its state is the normalized CSS
 * properties.
 */
export function bindNestedStyle(
  runtime: WidgetRuntime,
  kind: "LayoutModel" | "StyleModel",
  props: Record<string, unknown>,
): string {
  const commId = crypto.randomUUID();
  const state = normalizeCssProps(props);
  runtime.initNested(commId, kind, state);
  return WidgetRuntime.modelRef(commId);
}

/** True when a value is a plain object (a candidate style/layout block). */
export function isCssBlock(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
