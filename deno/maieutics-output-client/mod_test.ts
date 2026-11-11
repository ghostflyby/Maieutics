import { assertEquals, assertRejects } from "@std/assert";
import { createOutputRenderer, renderTurn } from "./mod.ts";

Deno.test("standalone imports and scope disposal require no DOM, VS Code or transport globals", async () => {
  const sent: unknown[] = [];
  const client = createOutputRenderer({ sendMessage: (message) => sent.push(message) });
  await client.receive({ source: "unknown" });
  client.disposeOutput("absent");
  client.dispose();
  client.dispose();
  await client.receive({ source: "maieutics-output", type: "rendered", requestId: "late" });
  await assertRejects(
    () => client.render({ id: "closed", kind: "turn", value: {} }, {} as HTMLElement),
    DOMException,
  );
  assertEquals(sent, []);
});

Deno.test("standalone timeline helpers keep untrusted content inert without a host Markdown service", () => {
  const html = renderTurn({ text: "<script>alert(1)</script>" });
  assertEquals(html.includes("<script>"), false);
  assertEquals(html.includes("&lt;script&gt;"), true);
});
