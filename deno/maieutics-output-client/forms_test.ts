import { assertEquals, assertThrows } from "@std/assert";
import { createFormRenderer, readFormState } from "./forms.ts";

Deno.test("forms decode bounded data without an executable component contract", () => {
  const state = readFormState({
    fields: [null, "invalid", { name: "q", choices: ["a", { value: "b", label: "B" }, 42] }, {
      name: "q",
    }],
    values: false,
    esmSource: "throw new Error('inert')",
  });
  assertEquals(state.fields.length, 1);
  assertEquals(state.fields[0].choices, [{ value: "a", label: "a" }, { value: "b", label: "B" }]);
  assertEquals(state.values, {});
  assertEquals("esmSource" in state, false);
});
Deno.test("oversized form fields and choices fail explicitly instead of hiding required controls", () => {
  assertThrows(
    () => readFormState({ fields: Array.from({ length: 129 }, (_, i) => ({ name: String(i) })) }),
    Error,
    "form_fields_limit",
  );
  assertThrows(
    () => readFormState({ fields: [{ name: "pick", choices: Array(129).fill("x") }] }),
    Error,
    "form_options_limit",
  );
});
Deno.test("form client import and terminal disposal require neither DOM nor host globals", async () => {
  const sent: unknown[] = [];
  const client = createFormRenderer({ sendMessage: (message) => sent.push(message) });
  await client.receive({ source: "other" });
  client.dispose();
  client.dispose();
  await client.receive({ source: "maieutics-form", type: "state", modelId: "late", state: {} });
  assertThrows(() => client.render("late", {}, {} as HTMLElement), Error, "form_renderer_disposed");
  assertEquals(sent, []);
});
