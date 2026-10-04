/// <reference lib="deno.ns" />
/** Registry unit tests: register/resolve and the unknown-family fallback. */

import { assertEquals, assertNotEquals } from "@std/assert";
import type { VNode } from "preact";
import {
  FALLBACK_FAMILY,
  registerViewFamily,
  viewFamily,
  type ViewProps,
} from "./registry.ts";

function baseProps(overrides: Partial<ViewProps> = {}): ViewProps {
  return {
    modelId: "m1",
    state: {},
    models: new Map<string, Record<string, unknown>>(),
    post: () => {},
    hasState: true,
    ...overrides,
  };
}

/** Functional families are plain functions: call one directly for a vnode. */
function renderFallback(overrides: Partial<ViewProps>): VNode {
  const module = viewFamily(FALLBACK_FAMILY);
  if (module === undefined) throw new Error("fallback family is not registered");
  const component = module.component as (props: ViewProps) => VNode;
  return component(baseProps(overrides));
}

Deno.test("viewFamily resolves a registered family", () => {
  const sentinel = (() => null) as unknown as (props: ViewProps) => VNode;
  registerViewFamily("test/one", { component: sentinel });
  const found = viewFamily("test/one");
  assertNotEquals(found, undefined);
  assertEquals(found?.component, sentinel);
  assertEquals(viewFamily("test/missing"), undefined);
});

Deno.test("the fallback family renders state as JSON", () => {
  const vnode = renderFallback({ state: { a: 1, b: "<x>" } });
  assertEquals(vnode.type, "div");
  assertEquals((vnode.props as { class?: string }).class, "state");
  const text = (vnode.props as { children?: unknown }).children;
  assertEquals(typeof text, "string");
  assertEquals((text as string).includes("\"a\": 1"), true);
  assertEquals((text as string).includes("<x>"), true);
});

Deno.test("the fallback family degrades on malformed state without throwing", () => {
  assertEquals(renderFallback({ status: "weird" } as Partial<ViewProps>) !== null, true);
  assertEquals(renderFallback({ nested: { deep: 7 } } as Partial<ViewProps>) !== null, true);
});
