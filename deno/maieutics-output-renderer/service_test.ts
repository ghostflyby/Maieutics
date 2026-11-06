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

Deno.test("Vue evaluation requires the explicit endpoint and a valid versioned mode", async () => {
  using renderer = await createOutputRenderer();
  const handler = createRenderHandler(renderer, "secret");
  const source =
    '<script setup>import { ref } from "vue"; const count = ref(0)</script><template><p>{{ count }}</p></template>';
  const request = (path: string, vue: unknown) =>
    new Request(`http://localhost/v1/${path}`, {
      method: "POST",
      headers: { authorization: "Bearer secret" },
      body: JSON.stringify({ version: 1, documentId: "counter", source, vue }),
    });
  const vue = { version: 1, format: "sfc" };
  const passive = await (await handler(request("markdown", vue))).json();
  assertStringIncludes(passive.html, "&lt;script setup&gt;");
  assertEquals(passive.vue, undefined);
  const response = await handler(request("vue", vue));
  assertEquals(response.status, 200);
  const active = await response.json();
  assertStringIncludes(active.html, ">0</p>");
  assertEquals(active.vue.scopeId, "data-v-counter");
  for (
    const invalid of [undefined, null, { version: 2, format: "sfc" }, {
      version: 1,
      format: "other",
    }]
  ) {
    assertEquals((await handler(request("vue", invalid))).status, 400);
  }
});

Deno.test("sealed assistant API derives component metadata without changing the passive endpoint", async () => {
  using renderer = await createOutputRenderer();
  const handler = createRenderHandler(renderer, "secret");
  const source =
    "<script setup>import { ref } from 'vue'; const count = ref(0);</script>\n\n<button @click=\"count++\">+</button><span>{{ count }}</span>";
  const request = (path: string, text: string) =>
    new Request(`http://localhost/v1/${path}`, {
      method: "POST",
      headers: { authorization: "Bearer secret" },
      body: JSON.stringify({ version: 1, documentId: "assistant", source: text }),
    });
  const live = await (await handler(request("assistant", source))).json();
  assertEquals(live.vue.version, 1);
  assertStringIncludes(live.html, ">0</span>");
  const fenced = await (await handler(request("assistant", "```vue\n" + source + "\n```"))).json();
  assertEquals(fenced.vue, undefined);
  const passive = await (await handler(request("markdown", source))).json();
  assertEquals(passive.vue, undefined);
  assertStringIncludes(passive.html, "&lt;script setup&gt;");
});
