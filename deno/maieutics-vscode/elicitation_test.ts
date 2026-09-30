/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import { coerceField, planElicitation } from "./elicitation.ts";

const Schema = {
  type: "object",
  required: ["model", "count"],
  properties: {
    model: { type: "string", title: "Model", enum: ["small", "large"] },
    count: { type: "integer", title: "Count", description: "How many", default: 3 },
    ratio: { type: "number", title: "Ratio" },
    verbose: { type: "boolean", title: "Verbose" },
    note: { type: "string", title: "Note" },
    ignored: { type: "object", title: "Ignored" },
  },
};

Deno.test("planElicitation builds typed fields from a form schema", () => {
  const plan = planElicitation("Pick:", Schema, "srv-1");
  assert(plan !== null);
  assertEquals(plan.prompt, "Pick:");
  assertEquals(plan.serverId, "srv-1");
  assertEquals(plan.fields.length, 5, "the non-primitive property is dropped");
  const model = plan.fields[0];
  assertEquals(model.name, "model");
  assertEquals(model.required, true);
  assertEquals(model.enumValues, ["small", "large"]);
  const count = plan.fields[1];
  assertEquals(count.type, "integer");
  assertEquals(count.required, true);
  assertEquals(count.defaultValue, "3");
  const note = plan.fields[4];
  assertEquals(note.required, false);
});

Deno.test("planElicitation returns null for unusable schemas", () => {
  assertEquals(planElicitation("p", undefined), null);
  assertEquals(planElicitation("p", "string"), null);
  assertEquals(planElicitation("p", { type: "object" }), null);
  assertEquals(planElicitation("p", { type: "object", properties: {} }), null);
  assertEquals(
    planElicitation("p", { type: "object", properties: { deep: { type: "object" } } }),
    null,
  );
});

Deno.test("coerceField coerces strings to the declared types", () => {
  const fields = planElicitation("p", Schema)!.fields;
  const [model, count, ratio, verbose] = fields;

  assertEquals(coerceField(model, "large"), { ok: true, value: "large" });
  assertEquals(coerceField(count, "7"), { ok: true, value: 7 });
  assertEquals(coerceField(count, "7.5").ok, false, "integer rejects fractions");
  assertEquals(coerceField(ratio, "0.25"), { ok: true, value: 0.25 });
  assertEquals(coerceField(ratio, "abc").ok, false);
  assertEquals(coerceField(verbose, "yes"), { ok: true, value: true });
  assertEquals(coerceField(verbose, "No"), { ok: true, value: false });
});

Deno.test("coerceField handles empty optional and required fields", () => {
  const fields = planElicitation("p", Schema)!.fields;
  const [, , , , note] = fields;
  assertEquals(coerceField(note, ""), { ok: true, value: undefined });

  const required = fields[0];
  const missing = coerceField(required, "");
  assertEquals(missing.ok, false, "a required field with no default refuses empty");

  // An optional field WITH a default falls back to the default when skipped.
  const withDefault = fields[1];
  assertEquals(coerceField(withDefault, ""), { ok: true, value: "3" });
});
