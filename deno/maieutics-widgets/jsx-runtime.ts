/**
 * Zero-dependency JSX runtime for the REPL's tsx transform.
 *
 * The transform compiles JSX with `jsxImportSource: "maieutics-widgets"`,
 * so every JSX element becomes a call to this module's `jsx`/`jsxs`.
 * Known classic-controls tags are intercepted and turned directly into
 * widget models (displayable, comm-registered); every other tag becomes a
 * plain vnode `{type, props, key}` so composite/HTML fragments stay inert
 * until a renderer walks them.
 *
 * Keeping this module dependency-free means the REPL child needs no npm
 * resolution for the generated import — the import map entry in the REPL
 * config maps `maieutics-widgets/jsx-runtime` to this file.
 */

import { controlTemplate } from "./controls.ts";
import { createWidget, normalizeSelectionOptions, useWidgetRuntime } from "./index.ts";
import { WidgetRuntime } from "./runtime.ts";
import { bindLayoutModel, bindStyleModel, bindStyleProps, isStyleBlock } from "./style.ts";

/** Fragment marker for JSX fragment syntax (`<>...</>`). */
export const Fragment = Symbol("WidgetFragment");

interface JsxProps {
  children?: unknown;
  onChange?: (key: string, value: unknown) => void;
  [name: string]: unknown;
}

/** Automatic-runtime jsx factory: control elements become widget models. */
export function jsx(
  type: unknown,
  props: JsxProps | null,
  key?: unknown,
): unknown {
  return create(type, props, key);
}

/** Automatic-runtime jsxs factory (multiple children): same behavior as jsx. */
export function jsxs(
  type: unknown,
  props: JsxProps | null,
  key?: unknown,
): unknown {
  return create(type, props, key);
}

function create(type: unknown, props: JsxProps | null, key?: unknown): unknown {
  if (type === Fragment) {
    throw new Error(
      "JSX fragments (`<>...</>`) are not supported for ipywidgets controls. " +
        "Wrap the children in a Box/VBox/HBox instead.",
    );
  }
  if (typeof type === "string") {
    const template = controlTemplate(type);
    if (template !== undefined) {
      const { onChange, children, ...stateProps } = props ?? {};
      const nested: Record<string, string> = {};
      const selection = normalizeSelectionOptions(template, stateProps);
      const style = isStyleBlock(stateProps.style) ? stateProps.style : undefined;
      if (style !== undefined) {
        const split = bindStyleProps(useWidgetRuntime(), type, style);
        if (split.style !== undefined) nested.style = split.style;
        if (split.layout !== undefined) nested.layout = split.layout;
      }
      if (isStyleBlock(stateProps.styleModel)) {
        nested.style = bindStyleModel(useWidgetRuntime(), type, stateProps.styleModel);
      }
      if (isStyleBlock(stateProps.layoutModel)) {
        nested.layout = bindLayoutModel(useWidgetRuntime(), stateProps.layoutModel);
      }
      // Layout widgets (Box, VBox, HBox) carry their children as IPY_MODEL_
      // references; other controls ignore children. Each child element is
      // instantiated here so its comm_open is broadcast and its model_id
      // becomes the reference the frontend unpack_models resolves.
      const childrenState = children === undefined ? {} : { children: toModelRefs(children) };
      const state = {
        ...WidgetRuntime.identityFields(template.modelName, template.viewName),
        ...template.defaults,
        ...stateProps,
        // Normalize AFTER stateProps so the derived options/_options_labels
        // win over a raw `options` array (see controlFactory).
        ...selection,
        ...nested,
        ...childrenState,
      };
      return createWidget(
        state,
        onChange === undefined ? undefined : (k, v) => onChange(k, v),
      );
    }
  }
  return { type, props: props ?? {}, key: key ?? null };
}

/**
 * Convert JSX children (a vnode, a model, or an array) into `IPY_MODEL_<id>`
 * reference strings. Control children are instantiated through the jsx
 * factory so their comm_open is broadcast; the returned refs are what the
 * frontend's `unpack_models` resolves.
 */
function toModelRefs(children: unknown): string[] {
  const nodes = Array.isArray(children) ? children : [children];
  const refs: string[] = [];
  for (const node of nodes) {
    const value = createFromValue(node);
    if (value !== null && typeof value === "object" && "commId" in value) {
      refs.push(WidgetRuntime.modelRef((value as { commId: string }).commId));
    }
  }
  return refs;
}

/** Re-enter the factory for a vnode/model value (used by toModelRefs). */
function createFromValue(value: unknown): unknown {
  if (
    value !== null && typeof value === "object" &&
    typeof (value as { type?: unknown }).type === "string"
  ) {
    const { type, props } = value as { type: string; props: JsxProps | null };
    return create(type, props);
  }
  return value;
}
