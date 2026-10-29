/// <reference lib="deno.window" />
import { assert, assertEquals } from "@std/assert";
import MarkdownIt from "markdown-it";
import * as renderer from "./media/turnRenderer.js";
const markdown = new MarkdownIt({ html: true });
renderer.protectMarkdownHtml(markdown);
const markdownHtml = (text: string): string =>
  markdown.render(text, {
    outputItem: renderer.markdownOutputItem({ id: "test" }, text, 0),
  });
const render = (turn: unknown) => renderer.render(turn, markdownHtml);

Deno.test("activity nodes collapse while assistant replies remain expanded", () => {
  const html = render({
    text: "Answer",
    tools: [{ tool: "read_file", status: "ok" }],
    reasoning: [{ messageId: "m", text: "Review evidence" }],
  });
  assertEquals(html.match(/<details /g)?.length, 2);
  assert(!html.includes(" open"), html);
  assert(!html.includes("data-maieutics-view") && !html.includes("viewtoggle"), html);
  assert(
    html.includes("思考摘要") && html.includes("read_file") && html.includes("Answer"),
    html,
  );
});

Deno.test("expanded answer keeps full native Markdown including tables, nested lists and references", () => {
  const text =
    "# Answer\n\n| Key | Value |\n| --- | --- |\n| a | **b** |\n\n1. outer\n   - nested\n\n[ref][id]\n\n[id]: https://example.com";
  const html = render({ text });
  assert(html.includes("<table>") && html.includes("<strong>b</strong>"), html);
  assert(html.includes("<ul>\n<li>nested</li>"), html);
  assert(html.includes('href="https://example.com"'), html);
  assert(html.includes('class="assistant-reply"'), html);
});

Deno.test("assistant replies retain their full escaped content without collapse", () => {
  const html = render({ text: "<script>" + "x".repeat(300) });
  const summary = html.split("</summary>")[0];
  assert(!summary.includes("<script>"), summary);
  assert(summary.length < 500, summary);
  assert(html.includes("x".repeat(300)), html);
});

Deno.test("timeline retains model rounds, summaries and tools in arrival order", () => {
  const html = render({
    text: "Final answer",
    timeline: [
      { kind: "reasoning", messageId: "m1" },
      { kind: "text", messageId: "m1", text: "Checking files" },
      { kind: "tool", index: 0 },
      { kind: "reasoning", messageId: "m2" },
      { kind: "text", messageId: "m2", text: "Final answer" },
    ],
    reasoning: [{ messageId: "m1", text: "First summary" }, {
      messageId: "m2",
      text: "Second summary",
    }],
    tools: [{ tool: "list_directory", status: "ok" }],
  });
  const positions = [
    "First summary",
    "Checking files",
    "list_directory",
    "Second summary",
    "Final answer",
  ].map((text) => html.indexOf(text));
  assert(positions.every((pos, i) => pos >= 0 && (i === 0 || pos > positions[i - 1])), html);
  assertEquals(html.match(/class="timeline-node /g)?.length, 3);
});

Deno.test("tool nodes expose structured original arguments, progress, results and edit diffs", () => {
  const html = render({
    tools: [{
      tool: "edit_file",
      status: "error",
      durationMs: 900,
      argsSummary: "a.ts",
      arguments: { path: "a.ts", text: "<new>" },
      progress: "Checking file",
      result: { status: "error", code: "denied" },
      diff: { unified: "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new\n", additions: 1, deletions: 1 },
    }],
  });
  assert(
    html.includes('aria-label="失败"') && html.includes('class="node-title">edit_file') &&
      html.includes("900ms"),
    html,
  );
  assert(html.includes("参数") && html.includes("结果") && html.includes("Checking file"), html);
  assert(html.includes("&lt;new&gt;") && html.includes("denied"), html);
  assert(html.includes('class="diffline add"') && html.includes('class="diffline del"'), html);
});

Deno.test("diff and tool-detail limits remain explicit in expanded nodes", () => {
  const html = render({
    tools: [{
      tool: "edit_file",
      status: "ok",
      detailsTruncated: true,
      diff: {
        unified: Array.from({ length: 60 }, (_, i) => `+line ${i}`).join("\n"),
        additions: 60,
        deletions: 0,
      },
    }],
  });
  assert(html.includes("diff preview truncated") && !html.includes("+line 59"), html);
  assert(html.includes("20,000"), html);
});

Deno.test("turn status has one collapsed block with model, usage, failure and scoped retry", () => {
  const html = render({
    nodeId: "header",
    text: "[Retry](command:maieutics.retryTurn?%5B%22a%22%2C0%5D)",
    error: { code: "run_cancelled", message: "Cancelled <safe>" },
    model: { profileId: "p", provider: "prov", model: "m" },
    usage: { inputTokens: 900, outputTokens: 1234 },
    truncated: true,
  });
  assertEquals(html.match(/<details /g)?.length, 1);
  assert(
    html.includes("已中断") && html.includes("prov/m") && html.includes("900↑ 1.2k↓ tokens"),
    html,
  );
  assert(html.includes('data-retry="1"') && html.includes("Cancelled &lt;safe&gt;"), html);
  assert(html.includes("输出已截断"), html);
});

Deno.test("summary and subagent content stay escaped and separate from the answer", () => {
  const html = render({
    text: "Parent",
    reasoning: [{ messageId: "m", text: "<script>bad</script>" }],
    subagents: [{
      status: "ok",
      text: "# Child",
      tools: [],
      reasoning: [{ messageId: "c", text: "Child summary" }],
    }],
  });
  assert(!html.includes("<script>"), html);
  assert(html.includes("&lt;script&gt;") && html.includes("Child summary"), html);
  assert(html.includes("🤖 子代理 · ✅") && html.includes("<h1>Child</h1>"), html);
});

Deno.test("native Markdown guard blocks executable HTML and links without changing other notebook Markdown", () => {
  const html = markdownHtml(
    '<img src=x onerror="alert(1)">\n\n<script>alert(2)</script>\n\n[bad](javascript:alert%281%29)\n\n[run](command:evil)',
  );
  assert(!html.includes("<img") && !html.includes("<script>"), html);
  assert(!html.includes('href="javascript:') && !html.includes('href="command:'), html);
  assert(markdown.render("<b>ordinary notebook HTML</b>").includes("<b>"));
});

Deno.test("a fragment renders only its own timeline node", () => {
  const html = render({
    nodeId: "entry:2",
    timeline: [{ kind: "tool", index: 0 }],
    tools: [{ tool: "read_file", status: "running" }],
  });
  assertEquals(html.match(/<details /g)?.length, 1);
  assert(
    html.includes('data-node-id="entry:2"') && html.includes('aria-label="运行中"') &&
      html.includes('class="node-title">read_file'),
    html,
  );
  assertEquals(render({}), "<em>(empty turn)</em>");
});
