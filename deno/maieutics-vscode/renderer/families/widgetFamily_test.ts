/// <reference lib="deno.ns" />
/** jupyter.widget family tests: dispatch parity, refs/deps, vnode trees. */

import { assertEquals } from "@std/assert";
import type { VNode } from "preact";
import type { ViewProps } from "../registry.ts";
import { collectWidgetDeps, ipyModelRef, kindOf, WidgetView } from "./widgetFamily.tsx";

interface TestView {
  modelId: string;
  state: Record<string, unknown>;
  models?: Map<string, Record<string, unknown>>;
  hasState?: boolean;
}

function renderView(view: TestView): VNode {
  const props: ViewProps = {
    modelId: view.modelId,
    state: view.state,
    models: view.models ?? new Map<string, Record<string, unknown>>(),
    post: () => {},
    hasState: view.hasState ?? true,
  };
  // FunctionalComponent may return any ComponentChildren; the widget family
  // always returns a vnode, so assert that at the test seam.
  return WidgetView(props) as VNode;
}

/** Depth-first vnode search by predicate over preact children shapes. */
function find(node: unknown, predicate: (node: VNode) => boolean): VNode | undefined {
  if (node === null || node === undefined || typeof node !== "object") return undefined;
  const vnode = node as { type?: unknown; props?: { children?: unknown } };
  if (vnode.type === undefined || vnode.props === undefined) return undefined;
  const current = vnode as VNode;
  if (predicate(current)) return current;
  const children = vnode.props.children;
  const list: unknown[] = Array.isArray(children) ? children : [children];
  for (const child of list) {
    if (
      child !== null && typeof child === "object" &&
      typeof (child as { type?: unknown }).type === "function"
    ) {
      // Functional child component: evaluate it with its props.
      const evaluated = (child as { type: (p: unknown) => unknown }).type(
        (child as { props: unknown }).props,
      );
      const found = find(evaluated, predicate);
      if (found !== undefined) return found;
      continue;
    }
    const found = find(child, predicate);
    if (found !== undefined) return found;
  }
  return undefined;
}

Deno.test("kindOf keeps the hand-written renderer's dispatch (quirks included)", () => {
  assertEquals(kindOf({ _model_name: "IntSliderModel" }), "int-slider");
  assertEquals(kindOf({ _model_name: "FloatSliderModel" }), "float-slider");
  // Parity quirk: TextModel does not match "TextInput", so text controls
  // degrade to the state JSON view — same as the hand-written renderer.
  assertEquals(kindOf({ _model_name: "TextModel" }), "unknown");
  assertEquals(kindOf({ _model_name: "ToggleButtonModel" }), "toggle");
  assertEquals(kindOf({ _model_name: "ButtonModel" }), "button");
  assertEquals(kindOf({ _model_name: "VBoxModel" }), "box");
  assertEquals(kindOf({ _model_name: "SomethingElse" }), "unknown");
  assertEquals(kindOf({}), "unknown");
});

Deno.test("ipyModelRef parses child references only", () => {
  assertEquals(ipyModelRef("IPY_MODEL_abc"), "abc");
  assertEquals(ipyModelRef("IPY_MODEL_"), undefined);
  assertEquals(ipyModelRef("plain"), undefined);
  assertEquals(ipyModelRef(7), undefined);
});

Deno.test("collectWidgetDeps walks nested children", () => {
  const models = new Map<string, Record<string, unknown>>([
    ["outer", { _model_name: "BoxModel", children: ["IPY_MODEL_inner", "not-a-ref"] }],
    ["inner", { _model_name: "VBoxModel", children: ["IPY_MODEL_leaf"] }],
    ["leaf", { _model_name: "ButtonModel" }],
  ]);
  const deps = new Set<string>();
  collectWidgetDeps(deps, "outer", models);
  assertEquals([...deps].sort(), ["inner", "leaf"]);
});

Deno.test("a slider renders a labeled range input with the current value", () => {
  const vnode = renderView({
    modelId: "s1",
    state: { _model_name: "IntSliderModel", description: "rate", value: 5, min: 0, max: 10 },
  });
  const input = find(vnode, (n) => n.type === "input");
  if (input === undefined) throw new Error("slider input not rendered");
  const props = input.props as Record<string, unknown>;
  assertEquals(props.type, "range");
  assertEquals(props.min, "0");
  assertEquals(props.max, "10");
  assertEquals(props.value, "5");
});

Deno.test("a slider posts an update when the input commits", () => {
  const posted: unknown[] = [];
  const props: ViewProps = {
    modelId: "s1",
    state: { _model_name: "IntSliderModel", value: 5, min: 0, max: 10 },
    models: new Map(),
    post: (m) => posted.push(m),
    hasState: true,
  };
  const vnode = WidgetView(props);
  const input = find(vnode, (n) => n.type === "input");
  if (input === undefined) throw new Error("slider input not rendered");
  const onInput = (input.props as { onInput?: (e: unknown) => void }).onInput;
  if (onInput === undefined) throw new Error("slider input has no handler");
  onInput({ currentTarget: { value: "7" } });
  assertEquals(posted, [{
    source: "maieutics-widget",
    type: "update",
    modelId: "s1",
    state: { value: 7 },
  }]);
});

Deno.test("a button click posts an incremented clicks counter", () => {
  const posted: unknown[] = [];
  const vnode = WidgetView({
    modelId: "b1",
    state: { _model_name: "ButtonModel", description: "Go", clicks: 2 },
    models: new Map(),
    post: (m) => posted.push(m),
    hasState: true,
  });
  const button = find(vnode, (n) => n.type === "button");
  if (button === undefined) throw new Error("button not rendered");
  const onClick = (button.props as { onClick?: () => void }).onClick;
  if (onClick === undefined) throw new Error("button has no handler");
  onClick();
  assertEquals(posted, [{
    source: "maieutics-widget",
    type: "update",
    modelId: "b1",
    state: { clicks: 3 },
  }]);
});

Deno.test("a box renders nested IPY_MODEL children recursively", () => {
  const models = new Map<string, Record<string, unknown>>([
    ["parent", { _model_name: "BoxModel", children: ["IPY_MODEL_child", "IPY_MODEL_missing"] }],
    ["child", { _model_name: "ButtonModel", description: "Inner" }],
  ]);
  const parentState = models.get("parent");
  if (parentState === undefined) throw new Error("test fixture missing parent state");
  const vnode = renderView({ modelId: "parent", state: parentState, models });
  const button = find(vnode, (n) => n.type === "button");
  assertEquals(button !== undefined, true);
  const text = find(vnode, (n) => typeof n.props?.children === "string" &&
    (n.props.children as string).includes("waiting"));
  assertEquals(text !== undefined, true);
});

Deno.test("unknown models degrade to the live state JSON view", () => {
  const vnode = renderView({
    modelId: "u1",
    state: { _model_name: "DropdownModel", options: [["a", 1]] },
  });
  const state = find(vnode, (n) => (n.props as { class?: string }).class === "state");
  assertEquals(state !== undefined, true);
  const text = (state?.props as { children?: unknown }).children;
  assertEquals((text as string).includes("DropdownModel"), true);
});

Deno.test("without live state the view waits for the model", () => {
  const vnode = renderView({ modelId: "m", state: {}, hasState: false });
  const waiting = find(
    vnode,
    (n) => typeof n.props?.children === "string" &&
      (n.props.children as string).includes("Connecting"),
  );
  assertEquals(waiting !== undefined, true);
});
