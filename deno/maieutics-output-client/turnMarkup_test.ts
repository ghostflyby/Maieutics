import { assert, assertEquals } from "@std/assert";
import MarkdownIt from "markdown-it";
import { renderTurn } from "./mod.ts";

const markdown = new MarkdownIt({ html: false });
const render = (turn: unknown) => renderTurn(turn, (text: string) => markdown.render(text));

Deno.test("activity nodes collapse while assistant replies remain expanded", () => {
  const html = render({
    text: "Answer",
    tools: [{ tool: "read_file", status: "ok" }],
    reasoning: [{ messageId: "m", text: "Review evidence" }],
  });
  assertEquals(html.match(/<details /g)?.length, 2);
  assert(!/<details\b[^>]*\bopen\b/.test(html), html);
  assert(
    html.includes("Review evidence") && html.includes("read_file") && html.includes("Answer"),
    html,
  );
});

Deno.test("assistant replies retain their full escaped content without collapse", () => {
  const html = render({ text: "<script>" + "x".repeat(300) });
  assert(!html.includes("<script>"), html);
  assert(html.includes("&lt;script&gt;"), html);
  assert(!html.includes("<details"), html);
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
    html.includes('class="status-icon error"') && html.includes('class="node-title">edit_file') &&
      html.includes("900ms"),
    html,
  );
  assert(
    (html.match(/<pre class="json">/g)?.length === 2) && html.includes("Checking file"),
    html,
  );
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
  assert(html.includes("+line 0") && !html.includes("+line 59"), html);
  assert(html.includes('<div class="note">'), html);
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
    html.includes("run_cancelled") && html.includes("prov/m") && html.includes("900↑ 1.2k↓"),
    html,
  );
  assert(html.includes('data-retry="1"') && html.includes("Cancelled &lt;safe&gt;"), html);
  assertEquals(html.match(/<div class="note">/g)?.length, 2);
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
  assert(html.includes('class="timeline-node subagent"') && html.includes("<h1>Child</h1>"), html);
});

Deno.test("a fragment renders only its own timeline node", () => {
  const html = render({
    nodeId: "entry:2",
    timeline: [{ kind: "tool", index: 0 }],
    tools: [{ tool: "read_file", status: "running" }],
  });
  assertEquals(html.match(/<details /g)?.length, 1);
  assert(
    html.includes('data-node-id="entry:2"') && html.includes('class="status-icon running"') &&
      html.includes('class="node-title">read_file'),
    html,
  );
});
