/// <reference lib="deno.ns" />
/** maieutics/form family tests: tolerant state reads, field rendering, envelopes. */

import { assertEquals } from "@std/assert";
import type { VNode } from "preact";
import type { ViewProps } from "../registry.ts";
import { fieldValue, FormView, readFormDef } from "./formFamily.tsx";

function renderForm(state: Record<string, unknown>, overrides: Partial<ViewProps> = {}): VNode {
  const props: ViewProps = {
    modelId: "f1",
    state,
    models: new Map(),
    post: () => {},
    hasState: true,
    ...overrides,
  };
  return FormView(props) as VNode;
}

/** Depth-first vnode walk that flattens nested arrays (JSX map children). */
function* walk(node: unknown): Generator<VNode> {
  if (node === null || node === undefined || typeof node !== "object") return;
  if (Array.isArray(node)) {
    for (const item of node) yield* walk(item);
    return;
  }
  const vnode = node as { type?: unknown; props?: { children?: unknown } };
  if (vnode.type === undefined || vnode.props === undefined) return;
  yield vnode as VNode;
  yield* walk(vnode.props.children);
}

function find(node: unknown, predicate: (node: VNode) => boolean): VNode | undefined {
  for (const vnode of walk(node)) {
    if (predicate(vnode)) return vnode;
  }
  return undefined;
}

function collect(node: unknown, predicate: (node: VNode) => boolean): VNode[] {
  return [...walk(node)].filter(predicate);
}

Deno.test("readFormDef tolerates malformed state", () => {
  const def = readFormDef({
    fields: ["junk", { name: "ok", type: "text" }, { type: "text" }, null],
    values: "not-an-object",
  });
  assertEquals(def.fields.length, 1);
  assertEquals(def.fields[0].name, "ok");
  assertEquals(def.fields[0].label, "ok");
  assertEquals(def.values, {});
  assertEquals(def.submitLabel, "Submit");
  assertEquals(def.cancelLabel, "Cancel");
});

Deno.test("readFormDef normalizes choices and reads labels", () => {
  const def = readFormDef({
    title: "Approve",
    submitLabel: "Go",
    cancelLabel: "Stop",
    fields: [{
      name: "pick",
      label: "Pick one",
      type: "choice",
      choices: ["a", { value: "b", label: "Bee" }, 7],
      required: true,
    }],
    values: { pick: "b" },
  });
  assertEquals(def.title, "Approve");
  assertEquals(def.submitLabel, "Go");
  assertEquals(def.cancelLabel, "Stop");
  assertEquals(def.fields[0].choices, [{ value: "a", label: "a" }, { value: "b", label: "Bee" }]);
  assertEquals(def.fields[0].required, true);
  assertEquals(def.values, { pick: "b" });
});

Deno.test("fieldValue converts raw element readings per field type", () => {
  const text = { name: "q", label: "q", type: "text", choices: [], required: false };
  const number = { name: "n", label: "n", type: "number", choices: [], required: false };
  const bool = { name: "b", label: "b", type: "boolean", choices: [], required: false };
  assertEquals(fieldValue(text, { text: "hi", checked: false }), "hi");
  assertEquals(fieldValue(number, { text: "3.5", checked: false }), 3.5);
  assertEquals(fieldValue(number, { text: "", checked: false }), "");
  assertEquals(fieldValue(number, { text: "NaN-ish", checked: true }), "NaN-ish");
  assertEquals(fieldValue(bool, { text: "", checked: true }), true);
});

Deno.test("the form renders typed fields and actions from live state", () => {
  const vnode = renderForm({
    title: "Approve deployment?",
    fields: [
      { name: "note", type: "text", placeholder: "why" },
      { name: "count", type: "number" },
      { name: "ok", type: "boolean" },
      { name: "pick", type: "choice", choices: [{ value: "a", label: "A" }] },
    ],
    values: { note: "", count: "", ok: false, pick: "a" },
  });
  const title = find(vnode, (n) => (n.props as { class?: string }).class === "title");
  assertEquals(title !== undefined, true);
  assertEquals(
    find(vnode, (n) => n.type === "input" && (n.props as Record<string, unknown>).type === "text") !==
      undefined,
    true,
  );
  assertEquals(
    find(vnode, (n) => n.type === "input" && (n.props as Record<string, unknown>).type === "number") !==
      undefined,
    true,
  );
  assertEquals(
    find(vnode, (n) => n.type === "input" && (n.props as Record<string, unknown>).type === "checkbox") !==
      undefined,
    true,
  );
  assertEquals(find(vnode, (n) => n.type === "select") !== undefined, true);
  const buttons = collect(vnode, (n) => n.type === "button");
  assertEquals(buttons.length, 2);
});

Deno.test("without live state the stale marker shows", () => {
  const vnode = renderForm({ fields: [], values: {} }, { hasState: false });
  const stale = find(
    vnode,
    (n) => (n.props as { class?: string }).class === "stale",
  );
  assertEquals(stale !== undefined, true);
});

Deno.test("cancel posts the native event envelope", () => {
  const posted: unknown[] = [];
  const vnode = renderForm({ fields: [], values: {} }, { post: (m) => posted.push(m) });
  const cancel = find(
    vnode,
    (n) => n.type === "button" && (n.props as { class?: string }).class === "cancel",
  );
  const onClick = (cancel?.props as { onClick?: () => void }).onClick;
  if (onClick === undefined) throw new Error("cancel button has no handler");
  onClick();
  assertEquals(posted, [{ source: "maieutics-ui", type: "event", modelId: "f1", name: "cancel" }]);
});

Deno.test("submit posts the gathered values (registered elements only)", () => {
  const posted: unknown[] = [];
  const vnode = renderForm(
    { fields: [{ name: "q", type: "text" }], values: { q: "" } },
    { post: (m) => posted.push(m) },
  );
  const submit = find(
    vnode,
    (n) => n.type === "button" && (n.props as { class?: string }).class === "submit",
  );
  const onClick = (submit?.props as { onClick?: () => void }).onClick;
  if (onClick === undefined) throw new Error("submit button has no handler");
  // Without mounted DOM (refs never ran) submit carries no field values —
  // the DOM-path is exercised by the bundled-renderer verification instead.
  onClick();
  assertEquals(posted, [{
    source: "maieutics-ui",
    type: "event",
    modelId: "f1",
    name: "submit",
    payload: { values: {} },
  }]);
});
