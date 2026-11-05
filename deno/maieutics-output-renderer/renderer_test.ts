import { assert, assertEquals, assertRejects, assertStringIncludes } from "@std/assert";
import { createOutputRenderer, OutputRenderingError } from "./mod.ts";
import { TemplateInputError } from "./template.ts";
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

Deno.test("public TOC and attrs plugins share scoped explicit anchors and heading metadata", async () => {
  using renderer = await createOutputRenderer();
  const source =
    "[[toc]]\n\n# Title\n\n## **Ready** `value` {#result}\n\n### 中文 {#结果}\n\n[Jump](#result)";
  const output = await renderer.render("outline", source);
  assertStringIncludes(output.html, '<nav class="table-of-contents">');
  assertStringIncludes(output.html, 'id="outline-result"');
  assertStringIncludes(output.html, 'href="#outline-result">Ready value</a>');
  assertStringIncludes(output.html, 'id="outline-结果"');
  assertStringIncludes(output.html, 'href="#outline-结果">中文</a>');
  assertStringIncludes(output.html, 'href="#outline-result">Jump</a>');
  assertEquals(output.headings[0].children[0].slug, "outline-result");
  assertEquals(output.headings[0].children[0].children[0].slug, "outline-结果");
  assertEquals(await renderer.render("outline", source), output);
  assert(!((await renderer.render("other", source)).html.includes("outline-result")));
});

Deno.test("later headings refresh earlier TOCs without keeping another render's outline", async () => {
  using renderer = await createOutputRenderer();
  const source = "[[toc]]\n\n## Earlier {#first}\n";
  const pending = await renderer.render("stream", source);
  assert(!pending.html.includes("stream-second"));
  const resolved = await renderer.render("stream", source + "\n## Later {#second}\n");
  assertStringIncludes(resolved.html, 'href="#stream-second">Later</a>');
  assertStringIncludes(resolved.html, 'id="stream-first"');
  assertEquals(await renderer.render("stream", source), pending);
});

Deno.test("duplicate explicit and generated IDs remain renderable and cannot claim code controls", async () => {
  using renderer = await createOutputRenderer();
  const source = [
    "[[toc]]",
    "",
    "## Same",
    "",
    "## Again {#same}",
    "",
    "## Same",
    "",
    "## Other {#same-1}",
    "",
    "## Control {#code-group-0-tab-0}",
    "",
    "::: code-group",
    "```ts [one.ts]",
    "const one = 1;",
    "```",
    ":::",
  ].join("\n");
  const output = await renderer.render("duplicates", source);
  for (const id of ["same", "same-1", "same-2", "same-1-1", "code-group-0-tab-0-1"]) {
    assertStringIncludes(output.html, `id="duplicates-${id}"`);
    assertStringIncludes(output.html, `href="#duplicates-${id}"`);
  }
  const ids = [...output.html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]);
  assertEquals(
    new Set(ids).size,
    ids.length,
    "Heading IDs cannot collide with native code controls.",
  );
});

Deno.test("attribute syntax only admits bounded safe heading IDs and cannot add handlers or styles", async () => {
  using renderer = await createOutputRenderer();
  const { html } = await renderer.render(
    "attrs",
    [
      "[[toc]]",
      "",
      '## Safe {#good onclick=run() style="display:none" .hidden}',
      "",
      "## Unsafe {id='bad\"onmouseover=\"run()'}",
      "",
      `[link](https://example.test){#outside onclick=run()}`,
      "",
      `## Long {#${"x".repeat(129)}}`,
    ].join("\n"),
  );
  assertStringIncludes(html, 'id="attrs-good"');
  assertStringIncludes(html, 'id="attrs-unsafe"');
  assertStringIncludes(html, 'id="attrs-long"');
  assert(!/\s(?:onclick|onmouseover|style)=/.test(html));
  assert(!html.includes('class="hidden"'));
  assert(!html.includes('id="outside"'));
  assert(!html.includes('href="#attrs-bad"'));
});

Deno.test("upstream containers render inline titles with late references and scoped local links", async () => {
  using renderer = await createOutputRenderer();
  const source = [
    "::: tip **Advice** [jump](#result) [later][r] <img src=x>",
    "Body",
    ":::",
    "",
    "::: details",
    "Details body",
    ":::",
    "",
    "## Result {#result}",
  ].join("\n");
  const pending = await renderer.render("titles", source);
  assertStringIncludes(pending.html, "<strong>Advice</strong>");
  assertStringIncludes(pending.html, 'href="#titles-result">jump</a>');
  assertStringIncludes(pending.html, "<summary>Details</summary>");
  assertStringIncludes(pending.html, "[later][r]");
  assertStringIncludes(pending.html, "&lt;img");
  assert(!pending.html.includes("<img"));
  const resolved = await renderer.render("titles", source + "\n\n[r]: https://example.test/result");
  assertStringIncludes(resolved.html, 'href="https://example.test/result">later</a>');
  assertEquals(await renderer.render("titles", source), pending);
});

Deno.test("upstream GitHub alerts recognize all markers and escape literal custom titles", async () => {
  using renderer = await createOutputRenderer();
  for (const type of ["TIP", "NOTE", "INFO", "IMPORTANT", "WARNING", "CAUTION", "DANGER"]) {
    const { html } = await renderer.render("alerts", `> [!${type}]\n> **Body**\n>\n> - Item`);
    assertStringIncludes(html, `class="custom-block ${type.toLowerCase()} github-alert"`);
    assertStringIncludes(html, `class="custom-block-title">${type}</p>`);
    assertStringIncludes(html, "<strong>Body</strong>");
    assertStringIncludes(html, "<li>Item</li>");
  }
  const source =
    '> [!tip] <img src=x onerror="run()"> **literal**\n> Body\n\n> [!unknown]\n> Ordinary quote';
  const { html } = await renderer.render("alerts", source);
  assertStringIncludes(html, "&lt;img");
  assertStringIncludes(html, "**literal**");
  assert(!html.includes("<img"));
  assertStringIncludes(html, "<blockquote>");
  assertStringIncludes(html, "[!unknown]");
  assertStringIncludes(html, "Ordinary quote");
});

Deno.test("explicit template snapshots resolve earlier values while later prose remains visible", async () => {
  using renderer = await createOutputRenderer();
  const source =
    "Total {{ report.total.toFixed(2) }}.\n\nLater independent prose.\n\nFalse: {{ false }} / empty: {{ empty }}";
  const pending = await renderer.renderTemplate("template", source, {
    version: 1,
    bindings: [
      { name: "report", state: "pending" },
      { name: "empty", state: "ready", undefined: true },
    ],
  });
  assertStringIncludes(pending.html, 'data-template-state="pending"');
  assertStringIncludes(pending.html, "Later independent prose.");
  assertEquals(pending.expressions?.map((value) => value.state), ["pending", "ready", "ready"]);
  const context = {
    version: 1 as const,
    bindings: [{ name: "report", state: "ready" as const, value: { total: 42 } }, {
      name: "empty",
      state: "ready" as const,
      undefined: true,
    }],
  };
  const ready = await renderer.renderTemplate("template", source, context);
  assertStringIncludes(ready.html, ">42.00</span>");
  assertStringIncludes(ready.html, "Later independent prose.");
  assert(!ready.html.includes('data-template-state="pending"'));
  assertEquals(await renderer.renderTemplate("template", source, context), ready);
  const passive = await renderer.render("template", source);
  assertStringIncludes(passive.html, "{{ report.total.toFixed(2) }}");
  assertEquals(passive.expressions, undefined);
});

Deno.test("interpolation values stay text without being reparsed as Markdown, attributes or HTML", async () => {
  using renderer = await createOutputRenderer();
  const source =
    "[[toc]]\n\n## Result {{ title }} {#result}\n\nValue {{ html }}\n\n::: tip **{{ title }}**\nBody {{ 1 + 2 }}\n:::\n\n`{{ ignored }}`\n\n```ts\n{{ ignored }}\n```";
  const output = await renderer.renderTemplate("safe", source, {
    version: 1,
    bindings: [
      { name: "title", state: "ready", value: "**bold** {#bad}" },
      {
        name: "html",
        state: "ready",
        value: '<img src=x onerror="run()"> [evil](javascript:run())',
      },
    ],
  });
  assertStringIncludes(output.html, 'id="safe-result"');
  assertStringIncludes(output.html, 'href="#safe-result">Result **bold** {#bad}</a>');
  assertStringIncludes(output.html, "&lt;img");
  assert(!output.html.includes("<img"));
  assert(!output.html.includes('id="safe-bad"'));
  assert(!output.html.includes("<strong>bold</strong>"));
  assertStringIncludes(output.html, ">3</span>");
  assertStringIncludes(output.html, "{{ ignored }}");
  assertEquals(
    output.expressions?.length,
    4,
    "Code spans and fences must not register template dependencies.",
  );
});

Deno.test("incomplete interpolations never execute guessed source and reconcile when completed", async () => {
  using renderer = await createOutputRenderer();
  const context = {
    version: 1 as const,
    bindings: [{ name: "report", state: "ready" as const, value: { total: 42 } }],
  };
  const incomplete = await renderer.renderTemplate(
    "partial",
    "Before {{ report.\n\nLater prose",
    context,
  );
  assertStringIncludes(incomplete.html, "{{ report.");
  assertStringIncludes(incomplete.html, "Later prose");
  assertEquals(incomplete.expressions?.[0].state, "incomplete");
  const sealed = await renderer.renderTemplate("partial", "Before {{ report.", {
    ...context,
    sourceSealed: true,
  });
  assertEquals(sealed.expressions?.[0].state, "failed");
  const complete = await renderer.renderTemplate(
    "partial",
    "Before {{ report.total }}\n\nLater prose",
    context,
  );
  assertStringIncludes(complete.html, ">42</span>");
  const quoted = await renderer.renderTemplate("partial", '{{ "}}" }}', context);
  assertStringIncludes(quoted.html, ">}}</span>");
});

Deno.test("expression failures remain local and re-rendering cannot leak bindings or publish cancelled work", async () => {
  using renderer = await createOutputRenderer();
  const source = "{{ report.total }} {{ (() => 1)() }}\n\nVisible prose";
  const context = {
    version: 1 as const,
    bindings: [{ name: "report", state: "ready" as const, value: { total: 42 } }],
  };
  const first = await renderer.renderTemplate("owner", source, context);
  assertEquals(first.expressions?.map((value) => value.state), ["ready", "failed"]);
  assertStringIncludes(first.html, "Visible prose");
  const other = await renderer.renderTemplate("other", source, { version: 1, bindings: [] });
  assertEquals(other.expressions?.[0].state, "pending");
  const cancellation = new AbortController();
  const pending = renderer.renderTemplate("owner", source, context, cancellation.signal);
  cancellation.abort();
  await assertRejects(() => pending, DOMException);
  assertEquals(await renderer.renderTemplate("owner", source, context), first);
});

Deno.test("interpolation-count rejection leaves the renderer reusable", async () => {
  using renderer = await createOutputRenderer();
  await assertRejects(
    () => renderer.renderTemplate("budget", "{{ 1 }}".repeat(257), { version: 1, bindings: [] }),
    TemplateInputError,
  );
  assertStringIncludes(
    (await renderer.renderTemplate("budget", "{{ 2 }}", { version: 1, bindings: [] })).html,
    ">2</span>",
  );
});
