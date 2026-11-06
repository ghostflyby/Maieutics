import { assert, assertEquals, assertThrows } from "@std/assert";
import { captureTemplate, evaluateTemplate, interpolationEnd } from "./template.ts";

Deno.test("expressions read only frozen bindings and reuse Vue value formatting", () => {
  const context = captureTemplate({
    version: 1,
    bindings: [
      { name: "report", state: "ready", value: { total: 42.125, items: [2, 3] } },
    ],
  });
  assertEquals(evaluateTemplate("(report.total * 2).toFixed(2)", context), {
    state: "ready",
    text: "84.25",
    dependencies: ["report"],
  });
  assertEquals(evaluateTemplate("report.items[1] + 1", context).text, "4");
  assertEquals(
    evaluateTemplate("{ answer: report.items[0] }", context).text,
    '{\n  "answer": 2\n}',
  );
  assertEquals(evaluateTemplate("[false, 0, null]", context).text, "[\n  false,\n  0,\n  null\n]");
});

Deno.test("pending is distinct from ready undefined, null, zero and false", () => {
  const context = captureTemplate({
    version: 1,
    sourceSealed: true,
    bindings: [
      { name: "waiting", state: "pending" },
      { name: "empty", state: "ready", undefined: true },
      { name: "nothing", state: "ready", value: null },
      { name: "zero", state: "ready", value: 0 },
      { name: "no", state: "ready", value: false },
    ],
  });
  assertEquals(evaluateTemplate("waiting ?? 7", context).state, "pending");
  assertEquals(evaluateTemplate("empty", context), {
    state: "ready",
    text: "",
    dependencies: ["empty"],
  });
  assertEquals(evaluateTemplate("nothing", context).text, "");
  assertEquals(evaluateTemplate("zero", context).text, "0");
  assertEquals(evaluateTemplate("no", context).text, "false");
  assertEquals(evaluateTemplate("empty ?? 7", context).text, "7");
  assertEquals(evaluateTemplate("empty?.missing.deep", context).state, "ready");
  assertEquals(evaluateTemplate("nothing.total", context).code, "invalid_access");
});

Deno.test("short circuits track only needed branches and never treat pending as false", () => {
  const context = captureTemplate({
    version: 1,
    bindings: [
      { name: "waiting", state: "pending" },
      { name: "broken", state: "failed" },
      { name: "stopped", state: "cancelled" },
    ],
  });
  assertEquals(evaluateTemplate("false && waiting", context), {
    state: "ready",
    text: "false",
    dependencies: [],
  });
  assertEquals(evaluateTemplate("true ? 42 : broken", context).text, "42");
  assertEquals(evaluateTemplate("waiting ? 1 : 2", context).state, "pending");
  assertEquals(evaluateTemplate("broken", context).state, "failed");
  assertEquals(evaluateTemplate("stopped", context).state, "cancelled");
  assertEquals(evaluateTemplate("later.total", context).state, "pending");
  assertEquals(
    evaluateTemplate("later.total", { ...context, sourceSealed: true }).code,
    "unresolved_binding",
  );
});

Deno.test("template interpretation cannot execute assignments, callbacks, host globals or prototype paths", () => {
  const context = captureTemplate({
    version: 1,
    sourceSealed: true,
    bindings: [{ name: "report", state: "ready", value: { total: 42 } }],
  });
  for (
    const expression of [
      "globalThis.Deno.exit()",
      "(() => 42)()",
      "report.total = 7",
      "report.total++",
      "new Date()",
      "import('x')",
      "report.constructor",
      "report['__proto__']",
      "({__proto__: 1})",
      "report.total.toFixed(9999999)",
      "1 / 0",
      "2n",
      "/x/",
    ]
  ) {
    assertEquals(evaluateTemplate(expression, context).state, "failed", expression);
  }
  assertEquals(evaluateTemplate("report.total", context).text, "42");
  assertEquals(evaluateTemplate("true; globalThis.Deno.exit()", context).state, "failed");
});

Deno.test("capture rejects hostile or excessive data and never invokes value accessors", () => {
  let read = false;
  const value = {
    get secret() {
      read = true;
      return 42;
    },
  };
  assertThrows(() =>
    captureTemplate({ version: 1, bindings: [{ name: "x", state: "ready", value }] })
  );
  assert(!read);
  for (
    const bindings of [
      [{ name: "x", state: "ready" }],
      [{ name: "x", state: "pending", value: 42 }],
      [{ name: "x", state: "ready", undefined: true, value: null }],
      [{ name: "x", state: "ready", value: Number.NaN }],
      [{ name: "x", state: "ready", value: "x".repeat(64_001) }],
      [{ name: "x", state: "ready", value: new Date() }],
      [{ name: "x", state: "ready", value: new Array(5) }],
      [{ name: "x", state: "ready", value: 1 }, { name: "x", state: "pending" }],
    ]
  ) assertThrows(() => captureTemplate({ version: 1, bindings }));
  const original = { total: 1 };
  const context = captureTemplate({
    version: 1,
    bindings: [{ name: "report", state: "ready", value: original }],
  });
  original.total = 2;
  assertEquals(evaluateTemplate("report.total", context).text, "1");
});

Deno.test("delimiter recognition respects quoted braces and complete expression boundaries", () => {
  for (const source of ['"}}" }}', "report.total }}", "({x: '}'}) }}", "/}}/ }}", "`a${1}b` }}"]) {
    assertEquals(interpolationEnd(source), source.length - 2, source);
  }
  assertEquals(interpolationEnd("report."), undefined);
  assertEquals(interpolationEnd("'unfinished }}"), undefined);
});

Deno.test("expression and traversal budgets terminate locally without affecting the next evaluation", () => {
  const context = captureTemplate({ version: 1, bindings: [] });
  assertEquals(evaluateTemplate("1+".repeat(1100) + "1", context).code, "expression_too_large");
  assertEquals(evaluateTemplate("!".repeat(40) + "true", context).code, "expression_too_complex");
  assertEquals(evaluateTemplate("1+2", context).text, "3");
});
