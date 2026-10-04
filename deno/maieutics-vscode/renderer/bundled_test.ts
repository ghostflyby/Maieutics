/// <reference lib="deno.ns" />
/** Bundled-family materialization tests: registration, memoization, degradation. */

import { assertEquals } from "@std/assert";
import type { VNode } from "preact";
import {
  materializeBundledFamily,
  resetBundledMaterialization,
} from "./bundled.ts";
import { viewFamily } from "./registry.ts";

Deno.test("a bundled module registers its family through the injected API", () => {
  resetBundledMaterialization();
  const family = "test/spike-one";
  assertEquals(viewFamily(family), undefined);
  const result = materializeBundledFamily(family, {
    esmSource:
      `registerViewFamily(${JSON.stringify(family)}, { component: (props) => h("div", { class: "spike" }, props.state.title) });`,
  });
  assertEquals(result, { ok: true });
  const module = viewFamily(family);
  assertEquals(module !== undefined, true);
});

Deno.test("a module that throws is a typed failure and never retried", () => {
  resetBundledMaterialization();
  const family = "test/spike-broken";
  const source = "throw new Error('boom');";
  const first = materializeBundledFamily(family, { esmSource: source });
  assertEquals(first.ok, false);
  assertEquals((first as { error: string }).error.includes("boom"), true);
  // Memoized: the same source reports the same outcome without re-evaluating.
  const second = materializeBundledFamily(family, { esmSource: source });
  assertEquals(second, first);
  assertEquals(viewFamily(family), undefined);
});

Deno.test("a module that does not register the announced family fails loudly", () => {
  resetBundledMaterialization();
  const result = materializeBundledFamily("test/spike-absent", {
    esmSource: "registerViewFamily('test/somewhere-else', { component: () => null });",
  });
  assertEquals(result.ok, false);
  assertEquals(
    (result as { error: string }).error.includes("did not register family"),
    true,
  );
});

Deno.test("the injected h builds preact vnodes inside a bundled module", () => {
  resetBundledMaterialization();
  const family = "test/spike-h";
  materializeBundledFamily(family, {
    esmSource:
      `registerViewFamily(${JSON.stringify(family)}, { component: (props) => h("span", { class: "x" }, props.state.text) });`,
  });
  const module = viewFamily(family);
  if (module === undefined) throw new Error("family not registered");
  const component = module.component as (props: unknown) => VNode;
  const vnode = component({ state: { text: "hi" }, modelId: "m", models: new Map(), post: () => {}, hasState: true });
  assertEquals(vnode.type, "span");
  assertEquals((vnode.props as { class?: string }).class, "x");
  assertEquals((vnode.props as { children?: unknown }).children, "hi");
});
