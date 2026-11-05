import { assertEquals, assertStringIncludes } from "@std/assert";
import { createOutputRenderer } from "./mod.ts";
import { createRenderHandler, MaximumRequestBytes } from "./service.ts";

Deno.test("internal render API authenticates, validates versions and renders without retaining documents", async () => {
  using renderer = await createOutputRenderer();
  const handle = createRenderHandler(renderer, "secret");
  const request = (body: unknown, token = "secret") =>
    new Request("http://localhost/v1/markdown", {
      method: "POST",
      headers: { authorization: `Bearer ${token}` },
      body: JSON.stringify(body),
    });
  assertEquals((await handle(request({}, "wrong"))).status, 401);
  assertEquals((await handle(request({ version: 2, documentId: "d", source: "x" }))).status, 400);
  assertEquals((await handle(request({ version: 1, documentId: "d", source: null }))).status, 400);
  const response = await handle(
    request({ version: 1, documentId: "d", source: "# Demo", future: true }),
  );
  assertEquals(response.status, 200);
  const body = await response.json();
  assertEquals(body.version, 1);
  assertEquals(body.documentId, "d");
  assertStringIncludes(body.html, 'id="d-demo"');
  const oversized = await handle(
    new Request("http://localhost/v1/markdown", {
      method: "POST",
      headers: { authorization: "Bearer secret" },
      body: "x".repeat(MaximumRequestBytes + 1),
    }),
  );
  assertEquals(oversized.status, 413);
});

Deno.test("the explicit template endpoint validates bindings and leaves Markdown expressions passive", async () => {
  using renderer = await createOutputRenderer();
  const handler = createRenderHandler(renderer, "secret");
  const request = (path: string, template: unknown) =>
    new Request(`http://localhost/v1/${path}`, {
      method: "POST",
      headers: { authorization: "Bearer secret" },
      body: JSON.stringify({
        version: 1,
        documentId: "doc",
        source: "{{ report.total * 2 }}",
        template,
      }),
    });
  const template = {
    version: 1,
    bindings: [{ name: "report", state: "ready", value: { total: 21 } }],
  };
  const ready = await (await handler(request("template", template))).json();
  assertStringIncludes(ready.html, ">42</span>");
  assertEquals(ready.expressions, [{ state: "ready", dependencies: ["report"] }]);
  const passive = await (await handler(request("markdown", template))).json();
  assertStringIncludes(passive.html, "{{ report.total * 2 }}");
  assertEquals(passive.expressions, undefined);
  for (
    const invalid of [undefined, { version: 2, bindings: [] }, {
      version: 1,
      bindings: [{ name: "report", state: "ready" }],
    }]
  ) {
    assertEquals((await handler(request("template", invalid))).status, 400);
  }
});
