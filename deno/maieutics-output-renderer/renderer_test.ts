import { assert, assertEquals, assertRejects, assertStringIncludes } from "@std/assert";
import { createOutputRenderer, OutputRenderingError } from "./mod.ts";
import { MaximumSourceCharacters } from "./renderer.ts";

Deno.test("SSR preserves content structure and scopes heading references per document", async () => {
  using renderer = await createOutputRenderer();
  const output = await renderer.render("turn1", "# Result\n\n## Detail\n\n[Jump](#detail)");
  assertStringIncludes(output.html, 'class="maieutics-output vp-doc"');
  assertStringIncludes(output.html, 'id="turn1-result"');
  assertStringIncludes(output.html, 'id="turn1-detail"');
  assertStringIncludes(output.html, 'href="#turn1-detail"');
  assertEquals(output.headings, [{
    level: 1,
    title: "Result",
    slug: "turn1-result",
    children: [{ level: 2, title: "Detail", slug: "turn1-detail", children: [] }],
  }]);
});

Deno.test("late reference definitions re-render earlier text without leaking to another document", async () => {
  using renderer = await createOutputRenderer();
  const source = "[Result][r]\n\nLater prose.\n";
  const pending = await renderer.render("turn1", source);
  assert(!pending.html.includes("<a "), "A missing reference must remain unresolved.");
  const resolved = await renderer.render("turn1", source + "\n[r]: https://example.test/result\n");
  assertStringIncludes(resolved.html, 'href="https://example.test/result"');
  assertStringIncludes(resolved.html, "Later prose.");
  assertEquals(await renderer.render("turn1", source), pending);
  const other = await renderer.render("turn2", source);
  assert(!other.html.includes("<a "), "Reference state must belong to one render call.");
});

Deno.test("raw HTML, scripts, Vue directives and expressions stay passive", async () => {
  using renderer = await createOutputRenderer();
  const source = [
    "<script>globalThis.renderingExecuted = true</script>",
    '<img src="x" onerror="globalThis.renderingExecuted = true">',
    '<button @click="run()">Click</button>',
    '{{ (() => { throw new Error("executed"); })() }}',
    "<style>body { display: none }</style>",
  ].join("\n\n");
  const { html } = await renderer.render("passive", source);
  assert(!html.includes("<script"));
  assert(!html.includes("<img"));
  assert(!html.includes("<button"));
  assert(!html.includes("<style"));
  assertStringIncludes(html, "&lt;script&gt;");
  assertStringIncludes(html, "{{ (() =&gt;");
});

Deno.test("executable link schemes stay inert while product resource links survive", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "links",
    [
      "[JS](javascript:alert(1))",
      "[Command](command:workbench.action.closeWindow)",
      "[VSCode](vscode:extension/action)",
      "[Skill](skill://local/test)",
      "[File](workspace://local/readme.md)",
    ].join("\n\n"),
  );
  assert(!html.includes('href="javascript:'));
  assert(!html.includes('href="command:'));
  assert(!html.includes('href="vscode:'));
  assertStringIncludes(html, 'href="skill://local/test"');
  assertStringIncludes(html, 'href="workspace://local/readme.md"');
});

Deno.test("document text cannot read snippets or include Markdown files", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "files",
    [
      "<<< /must-not-open/private.ts",
      "<!-- @include: /must-not-open/private.md -->",
    ].join("\n\n"),
  );
  assertStringIncludes(html, "&lt;&lt;&lt; /must-not-open/private.ts");
  assertStringIncludes(html, "&lt;!-- @include:");
});

Deno.test("containers escape titles and nest normal Markdown content", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "blocks",
    [
      '::: warning <img src=x onerror="run()">',
      "**Careful**",
      ":::",
      "",
      "::: details More",
      "| Key | Value |",
      "| --- | --- |",
      "| a | 1 |",
      ":::",
    ].join("\n"),
  );
  assertStringIncludes(html, 'class="custom-block warning"');
  assertStringIncludes(html, "&lt;img");
  assert(!html.includes("<img"));
  assertStringIncludes(html, "<strong>Careful</strong>");
  assertStringIncludes(html, '<details class="custom-block details"><summary>More</summary>');
  assertStringIncludes(html, "<table>");
});

Deno.test("code highlighting uses bundled grammars and unknown languages fall back to text", async () => {
  using renderer = await createOutputRenderer();
  const source = "```ts\nconst total: number = 42;\n```\n\n```unknown-language\n<tag>\n```";
  const output = await renderer.render("code", source);
  assertStringIncludes(output.html, "shiki-themes github-light github-dark");
  assertStringIncludes(output.html, "--shiki-light:");
  assertStringIncludes(output.html, "--shiki-dark:");
  assert(!output.html.includes("<tag>"), "Unknown-language code must still escape HTML.");
  assertStringIncludes(output.html, "tag");
  assertEquals(await renderer.render("code", source), output);
});

Deno.test("each renderer owns its highlighter independently and disposal is idempotent", async () => {
  using first = await createOutputRenderer();
  using second = await createOutputRenderer();
  first[Symbol.dispose]();
  first[Symbol.dispose]();
  const error = await assertRejects(() => first.render("closed", "text"), OutputRenderingError);
  assertEquals(error.code, "disposed");
  assertStringIncludes((await second.render("open", "```ts\nconst n = 1;\n```")).html, "shiki");
});

Deno.test("source and document identity limits fail before rendering and leave the owner usable", async () => {
  using renderer = await createOutputRenderer();
  const idError = await assertRejects(
    () => renderer.render('bad"id', "text"),
    OutputRenderingError,
  );
  assertEquals(idError.code, "invalid_document_id");
  const sourceError = await assertRejects(
    () => renderer.render("large", "x".repeat(MaximumSourceCharacters + 1)),
    OutputRenderingError,
  );
  assertEquals(sourceError.code, "source_too_large");
  assertStringIncludes((await renderer.render("valid", "Still usable")).html, "Still usable");
});

Deno.test("syntax highlighting expansion is bounded independently of source size", async () => {
  using renderer = await createOutputRenderer();
  const source = `\`\`\`ts\n${"const a = 1;\n".repeat(12_000)}\`\`\``;
  assert(source.length <= MaximumSourceCharacters);
  const error = await assertRejects(
    () => renderer.render("expanded", source),
    OutputRenderingError,
  );
  assertEquals(error.code, "output_too_large");
});

Deno.test("cancelling an in-flight SSR rejects its result before returning the projection", async () => {
  using renderer = await createOutputRenderer();
  const cancellation = new AbortController();
  const rendering = renderer.render("inflight", "# Pending", cancellation.signal);
  cancellation.abort(new DOMException("cancelled", "AbortError"));
  await assertRejects(() => rendering, DOMException);
  assertStringIncludes((await renderer.render("next", "Next request")).html, "Next request");
});

Deno.test("cancellation and concurrent disposal prevent publication of an in-flight render", async () => {
  using renderer = await createOutputRenderer();
  const cancellation = new AbortController();
  cancellation.abort(new DOMException("cancelled", "AbortError"));
  await assertRejects(
    () => renderer.render("cancelled", "text", cancellation.signal),
    DOMException,
  );
  const rendering = renderer.render("inflight", "# Pending");
  renderer[Symbol.dispose]();
  const error = await assertRejects(() => rendering, OutputRenderingError);
  assertEquals(error.code, "disposed");
});
