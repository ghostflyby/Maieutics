import { assertEquals, assertRejects } from "@std/assert";
import * as api from "./index.ts";
import { FORM_MIME, FORM_TARGET, FormController, JUPYTER_DISPLAY, type UiHost } from "./index.ts";

function fixture(fail = false) {
  const calls: { kind: string; content: Record<string, unknown> }[] = [];
  const handlers = new Map<string, (message: { commId: string; data?: unknown }) => void>();
  const host: UiHost = {
    broadcast(kind, content) {
      calls.push({ kind, content });
      return fail ? Promise.reject(new Error("delivery failed")) : Promise.resolve();
    },
    onComm(event, handler) {
      handlers.set(event, handler);
    },
  };
  return { controller: FormController.bind(host), calls, handlers };
}
Deno.test("form producers expose Vue state and publish the plain versioned form contract", async () => {
  const { controller, calls } = fixture();
  const form = await controller.form({
    title: "Approve",
    fields: [{ name: "q", type: "text", default: "hi" }],
  });
  const bundle = await form[JUPYTER_DISPLAY]();
  assertEquals(bundle[FORM_MIME], { version: 1, modelId: form.commId, state: form.state.value });
  assertEquals(form.state.value.values, { q: "hi" });
  assertEquals(calls[0].content.target_name, FORM_TARGET);
  await form.update({ title: "Updated" });
  assertEquals(form.state.value.title, "Updated");
  assertEquals(calls[1].kind, "comm_msg");
  await form.close();
  await form.close();
  assertEquals(calls.filter((call) => call.kind === "comm_close").length, 1);
  await assertRejects(() => form.update({ title: "late" }), Error, "form_closed");
});
Deno.test("form actions route only to their model and close releases handlers", async () => {
  const { controller, handlers } = fixture();
  const events: unknown[] = [];
  const form = await controller.form({ fields: [] }, {
    onSubmit: (values) => events.push(values),
    onCancel: () => events.push("cancel"),
  });
  handlers.get("msg")?.({ commId: "unknown", data: { method: "event", name: "cancel" } });
  handlers.get("msg")?.({
    commId: form.commId,
    data: { method: "event", name: "submit", payload: { values: { q: "typed" } } },
  });
  handlers.get("msg")?.({ commId: form.commId, data: { method: "event", name: "cancel" } });
  handlers.get("close")?.({ commId: form.commId });
  handlers.get("msg")?.({ commId: form.commId, data: { method: "event", name: "cancel" } });
  assertEquals(events, [{ q: "typed" }, "cancel"]);
  await assertRejects(() => form.update({}), Error, "form_closed");
});
Deno.test("failed form publication is awaited rather than becoming detached work", async () => {
  await assertRejects(
    () => fixture(true).controller.form({ fields: [] }),
    Error,
    "delivery failed",
  );
});
Deno.test("SDK UI exports data forms without the retired custom component authoring APIs", () => {
  for (const key of ["registerFamily", "model", "UiModelRuntime", "NativeFamilyContract"]) {
    assertEquals(key in api, false);
  }
  assertEquals(
    api.formState({
      fields: [{ name: "ok", type: "boolean" }, { name: "pick", type: "choice", choices: ["a"] }],
    }).values,
    { ok: false, pick: "" },
  );
});

Deno.test("concurrent form updates serialize against committed state instead of losing independent changes", async () => {
  const started = Promise.withResolvers<void>(), release = Promise.withResolvers<void>();
  let messages = 0;
  const controller = FormController.bind({
    onComm() {},
    async broadcast(kind) {
      if (kind === "comm_msg" && messages++ === 0) {
        started.resolve();
        await release.promise;
      }
    },
  });
  const form = await controller.form({ fields: [] });
  const first = form.update({ title: "First" });
  await started.promise;
  const second = form.update({ submitLabel: "Second" });
  release.resolve();
  await Promise.all([first, second]);
  assertEquals(form.state.value.title, "First");
  assertEquals(form.state.value.submitLabel, "Second");
  await form.close();
});
