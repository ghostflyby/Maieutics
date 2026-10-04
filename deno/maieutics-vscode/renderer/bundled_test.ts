/// <reference lib="deno.ns" />
/** Bundled-family materialization tests: registration, memoization, degradation. */

import { assertEquals } from "@std/assert";
import type { VNode } from "preact";
import { materializeBundledFamily, resetBundledMaterialization } from "./bundled.ts";
import { FALLBACK_FAMILY, viewFamily } from "./registry.ts";

Deno.test("a bundled module registers its family through the injected API", () => {
  resetBundledMaterialization();
  const family = "test/spike-one";
  assertEquals(viewFamily(family), undefined);
  const result = materializeBundledFamily(family, {
    esmSource: `registerViewFamily(${
      JSON.stringify(family)
    }, { component: (props) => h("div", { class: "spike" }, props.state.title) });`,
  });
  assertEquals(result, { ok: true });
  const module = viewFamily(family);
  assertEquals(module !== undefined, true);
});

Deno.test("a module that throws is a typed failure and never retried", () => {
  resetBundledMaterialization();
  const family = "test/spike-broken";
  // Side-effect counter proves the memoized second call never re-evaluates.
  const counter = globalThis as { __spikeEvaluations?: number };
  counter.__spikeEvaluations = 0;
  const source = "globalThis.__spikeEvaluations += 1; throw new Error('boom');";
  const first = materializeBundledFamily(family, { esmSource: source });
  assertEquals(first.ok, false);
  assertEquals((first as { error: string }).error.includes("boom"), true);
  const second = materializeBundledFamily(family, { esmSource: source });
  assertEquals(second.ok, false);
  assertEquals(counter.__spikeEvaluations, 1);
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

Deno.test("a module shape without a functional component is rejected", () => {
  resetBundledMaterialization();
  const result = materializeBundledFamily("test/spike-shape", {
    esmSource: "registerViewFamily('test/spike-shape', { component: 42 });",
  });
  assertEquals(result.ok, false);
  assertEquals(
    (result as { error: string }).error.includes("functional 'component'"),
    true,
  );
  assertEquals(viewFamily("test/spike-shape"), undefined);
});

Deno.test("a module cannot override an already-registered (built-in) family", () => {
  resetBundledMaterialization();
  const result = materializeBundledFamily("test/spike-clobber", {
    esmSource: `registerViewFamily(${JSON.stringify(FALLBACK_FAMILY)}, {
      component: () => null,
    });`,
  });
  assertEquals(result.ok, false);
  assertEquals(
    (result as { error: string }).error.includes("refuses to override"),
    true,
  );
});

Deno.test("an oversized source is refused before evaluation", () => {
  resetBundledMaterialization();
  const result = materializeBundledFamily("test/spike-huge", {
    esmSource: "x".repeat(1024 * 1024 + 1),
  });
  assertEquals(result.ok, false);
  assertEquals(
    (result as { error: string }).error.includes("the ceiling is"),
    true,
  );
});

Deno.test("the injected h builds preact vnodes inside a bundled module", () => {
  resetBundledMaterialization();
  const family = "test/spike-h";
  materializeBundledFamily(family, {
    esmSource: `registerViewFamily(${
      JSON.stringify(family)
    }, { component: (props) => h("span", { class: "x" }, props.state.text) });`,
  });
  const module = viewFamily(family);
  if (module === undefined) throw new Error("family not registered");
  const component = module.component as (props: unknown) => VNode;
  const vnode = component({
    state: { text: "hi" },
    modelId: "m",
    models: new Map(),
    post: () => {},
    hasState: true,
  });
  assertEquals(vnode.type, "span");
  assertEquals((vnode.props as { class?: string }).class, "x");
  assertEquals((vnode.props as { children?: unknown }).children, "hi");
});
