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

Deno.test("code fence metadata retains syntax highlighting, escaped titles and bounded line ranges", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "metadata",
    [
      '```ts:line-numbers=7 {2-999999999999} [<img src=x onerror="run()">]',
      "const first = 1;",
      "const second = 2;",
      "```",
    ].join("\n"),
  );
  assertStringIncludes(html, 'class="code-title"');
  assertStringIncludes(html, "&lt;img");
  assert(!html.includes("<img"));
  assertStringIncludes(html, 'data-line="7"');
  assertStringIncludes(html, 'class="line highlighted" data-line="8"');
  assertStringIncludes(html, "--shiki-light:");
});

Deno.test("VitePress comment notations decorate code and remain local to each fence", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "notations",
    [
      "```ts",
      "const oldValue = 1; // [!code --]",
      "const newValue = 2; // [!code ++]",
      "const focus = 3; // [!code focus]",
      "const highlighted = 4; // [!code highlight]",
      "const error = 5; // [!code error]",
      "const warning = 6; // [!code warning]",
      "```",
      "",
      "```ts",
      "const plain = 7;",
      "```",
    ].join("\n"),
  );
  for (
    const marker of [
      "diff remove",
      "diff add",
      "has-focus",
      "highlighted",
      "highlighted error",
      "highlighted warning",
    ]
  ) {
    assertStringIncludes(html, marker);
  }
  assert(!html.includes("[!code"), "Shiki must remove recognized presentation comments.");
  assertEquals(html.match(/has-focused-lines/g)?.length, 1);
});

Deno.test("removed notation-only comments do not leave gaps in displayed line numbers", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "numbered",
    [
      "```ts:line-numbers=7",
      "// [!code highlight:999999999999]",
      "const first = 1;",
      "const second = 2;",
      "```",
    ].join("\n"),
  );
  assertStringIncludes(html, 'data-line="7"');
  assertStringIncludes(html, 'data-line="8"');
  assert(
    !html.includes('data-line="9"'),
    "Line numbers count visible code after notation removal.",
  );
  assertEquals(html.match(/class="line highlighted"/g)?.length, 2);
});

Deno.test("code groups produce deterministic document-scoped native controls with escaped labels", async () => {
  using renderer = await createOutputRenderer();
  const source = [
    "::: code-group",
    "```ts [one.ts]",
    "const one = 1;",
    "```",
    "",
    '```json [<img src=x onerror="run()">]',
    '{ "two": 2 }',
    "```",
    ":::",
  ].join("\n");
  const output = await renderer.render("groups", source);
  assertStringIncludes(output.html, '<fieldset class="vp-code-group"');
  assertStringIncludes(output.html, 'type="radio"');
  assertStringIncludes(output.html, 'name="groups-code-group-0"');
  assertStringIncludes(output.html, "&lt;img");
  assert(!output.html.includes("<img"));
  assertEquals(output.html.match(/ checked/g)?.length, 1);
  assertEquals(await renderer.render("groups", source), output);
  const other = await renderer.render("other", source);
  assert(!other.html.includes('name="groups-code-group-0"'));
  const partial = await renderer.render("groups", source.slice(0, source.lastIndexOf(":::")));
  assertStringIncludes(partial.html, "one.ts");
  assertEquals(partial.html, output.html);
});

Deno.test("mixed code-group content stays visible instead of disappearing behind tabs", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "mixed",
    "::: code-group\nVisible prose\n\n```ts\nconst n = 1;\n```\n:::",
  );
  assertStringIncludes(html, "Visible prose");
  assertStringIncludes(html, "const");
  assert(!html.includes('type="radio"'));
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
