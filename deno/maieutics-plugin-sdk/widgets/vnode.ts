/**
 * TSX vnode -> widget model tree.
 *
 * The REPL's tsx transform compiles JSX with the preact automatic runtime
 * (`jsx`/`jsxs` factories producing `{ type, props, key }` vnodes — verified
 * against esbuild's tsx loader). This walker interprets that vnode tree as a
 * *model* tree for the classic ipywidgets controls: an element whose `type`
 * is a known control name becomes a model instance; unknown/component types
 * are rejected so a typo fails loudly instead of silently rendering nothing.
 *
 * The walker produces an ordered list of created models; the host owns the
 * comm ids and the runtime registration.
 */

import { controlTemplate } from "./controls.ts";

/** A preact-style vnode (the shape esbuild's automatic-jsx output produces). */
export interface WidgetVNode {
  type: string;
  props: Record<string, unknown>;
  key?: string | number | null;
  children?: WidgetVNode[];
}

/** One control instance produced by the walker. */
export interface WidgetInstance {
  readonly kind: string;
  readonly props: Record<string, unknown>;
  /** Child models, in document order (for layout widgets such as Box). */
  readonly children: WidgetInstance[];
}

/**
 * Walk a TSX vnode tree and produce the ordered list of widget instances.
 * `children` props may be a single vnode, an array, or absent; non-vnode
 * children (text/numbers) are ignored.
 */
export function walkWidgets(node: unknown): WidgetInstance[] {
  const out: WidgetInstance[] = [];
  visit(node, out);
  return out;
}

function visit(node: unknown, out: WidgetInstance[]): void {
  if (Array.isArray(node)) {
    for (const child of node) visit(child, out);
    return;
  }
  if (!isVNode(node)) return;

  const template = controlTemplate(node.type);
  if (template === undefined) {
    throw new Error(
      `Unknown ipywidgets control '<${node.type}>'. ` +
        `Known controls: ${[...controlNames()].join(", ")}.`,
    );
  }

  const { children, ...props } = node.props;
  const childModels: WidgetInstance[] = [];
  if (children !== undefined) visit(children, childModels);

  out.push({
    kind: node.type,
    props,
    children: childModels,
  });
  // Layout widgets carry their children as props["children"] too; the walker
  // visits them once (above) and records them on the instance.
  for (const child of childModels) {
    if (!out.includes(child)) out.push(child);
  }
}

function isVNode(value: unknown): value is WidgetVNode {
  return typeof value === "object" && value !== null &&
    typeof (value as { type?: unknown }).type === "string" &&
    typeof (value as { props?: unknown }).props === "object";
}

function controlNames(): string[] {
  return [...controlTemplateNames()];
}

function* controlTemplateNames(): Iterable<string> {
  yield* ["IntSlider", "FloatSlider", "Button", "Text", "ToggleButton", "IntRangeSlider", "Box"];
}
