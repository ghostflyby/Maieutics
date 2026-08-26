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
import { createWidget, useWidgetRuntime } from "./index.ts";
import { WidgetRuntime } from "./runtime.ts";
import { bindNestedStyle, isCssBlock } from "./style.ts";

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
  if (typeof type === "string") {
    const template = controlTemplate(type);
    if (template !== undefined) {
      const { onChange, ...stateProps } = props ?? {};
      const nested: Record<string, string> = {};
      if (isCssBlock(stateProps.style)) {
        nested.style = bindNestedStyle(useWidgetRuntime(), "StyleModel", stateProps.style);
      }
      if (isCssBlock(stateProps.layout)) {
        nested.layout = bindNestedStyle(useWidgetRuntime(), "LayoutModel", stateProps.layout);
      }
      const state = {
        ...WidgetRuntime.identityFields(template.modelName, template.viewName),
        ...template.defaults,
        ...stateProps,
        ...nested,
      };
      return createWidget(
        state,
        onChange === undefined ? undefined : (k, v) => onChange(k, v),
      );
    }
  }
  return { type, props: props ?? {}, key: key ?? null };
}
