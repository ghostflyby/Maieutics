/// <reference lib="deno.window" />
import { assert } from "@std/assert";
import MarkdownIt from "markdown-it";
import * as renderer from "./media/turnRenderer.js";
const markdown = new MarkdownIt({ html: true });
renderer.protectMarkdownHtml(markdown);
const markdownHtml = (text: string): string =>
  markdown.render(text, {
    outputItem: renderer.markdownOutputItem({ id: "test" }, text, 0),
  });

Deno.test("streamed Vue source cannot swallow independent Markdown into an HTML block", () => {
  const source = "# Before\n\n<div>\n## Inside\n\n<script setup>\nconst count =";
  const html = markdown.render(source, {
    outputItem: renderer.markdownOutputItem({ id: "stream" }, source, 0),
  });
  assert(html.includes("<h1>Before</h1>"), html);
  assert(html.includes("<h2>Inside</h2>"), html);
  assert(!html.includes("<script") && !html.includes("<div>"), html);
  const ordinary = markdown.render("<div>\n## Literal example\n</div>");
  assert(!ordinary.includes("<h2>Literal example</h2>"), ordinary);
});

Deno.test("native Markdown guard blocks executable HTML and links without changing other notebook Markdown", () => {
  const html = markdownHtml(
    '<img src=x onerror="alert(1)">\n\n<script>alert(2)</script>\n\n[bad](javascript:alert%281%29)\n\n[run](command:evil)',
  );
  assert(!html.includes("<img") && !html.includes("<script>"), html);
  assert(!html.includes('href="javascript:') && !html.includes('href="command:'), html);
  assert(markdown.render("<b>ordinary notebook HTML</b>").includes("<b>"));
});
