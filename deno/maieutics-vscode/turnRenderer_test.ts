/// <reference lib="deno.window" />

import { assert, assertEquals } from "@std/assert";

// The renderer is an ES module (VS Code loads entrypoints with a native
// dynamic import), so the pure view functions import directly.
const {
  render,
  renderMarkdownView,
  viewToggleHtml,
  subagentsHtml,
} = await import("./media/turnRenderer.js");

Deno.test("timeline renders the structured edit diff and its stat", () => {
  const html = render({
    tools: [{
      tool: "workspace_edit",
      status: "ok",
      argsSummary: "src/a.ts",
      durationMs: 900,
      diff: {
        unified: "--- a/src/a.ts\n+++ b/src/a.ts\n@@ -1,1 +1,1 @@\n-old\n+new\n",
        additions: 1,
        deletions: 1,
        truncated: false,
      },
    }],
    text: "",
    truncated: false,
  });
  // The diff body is the default view's only diff presentation since the
  // markdown item is gone: stat on the tool row, colorized body below it.
  assert(html.includes("+1 −1"), html);
  assert(html.includes(`class="diffline hunk"`), html);
  assert(html.includes(`class="diffline del"`), html);
  assert(html.includes(`class="diffline add"`), html);
  assert(html.includes("-old"), html);
  assert(html.includes("+new"), html);
  assert(html.includes("900ms"), html);
});

Deno.test("timeline caps a long diff and notes the truncation", () => {
  const lines = Array.from({ length: 60 }, (_, index) => `+line ${index}`);
  const html = render({
    tools: [{
      tool: "workspace_edit",
      status: "ok",
      diff: { unified: `${lines.join("\n")}\n`, additions: 60, deletions: 0, truncated: false },
    }],
    text: "",
    truncated: false,
  });
  assert(html.includes("… diff preview truncated"), html);
  assert(!html.includes("+line 59"), html);
});

Deno.test("timeline renders usage, model, truncation, and the error note", () => {
  const html = render({
    tools: [],
    text: "",
    truncated: true,
    usage: { inputTokens: 900, outputTokens: 1234 },
    model: { profileId: "p", provider: "prov", model: "m-1" },
    error: { code: "agent_error", message: "boom <boom>" },
  });
  assert(html.includes("⚠️"), html);
  assert(html.includes("<code>prov/m-1</code>"), html);
  assert(html.includes("900\u2191 1.2k\u2193 tokens"), html);
  assert(html.includes("<code>agent_error</code>"), html);
  // The message is escaped.
  assert(html.includes("boom &lt;boom&gt;"), html);
});

Deno.test("timeline renders the answer markdown escaped, code spans intact", () => {
  const html = render({
    tools: [],
    text: "# Title\n\nUse `<b>bold</b>` and **strong**.\n\n- one\n- two\n",
    truncated: false,
  });
  assert(html.includes("<h1>Title</h1>"), html);
  assert(html.includes("<code>&lt;b&gt;bold&lt;/b&gt;</code>"), html);
  assert(html.includes("<strong>strong</strong>"), html);
  assert(html.includes("<ul>"), html);
  assert(html.includes("<li>one</li>"), html);
  assert(html.includes("<li>two</li>"), html);
});

Deno.test("timeline collapses a failed turn's text to its retry action", () => {
  const html = render({
    tools: [],
    text:
      "> ❌ `events_disconnected` — reset\n\n[\u21bb Retry turn](command:maieutics.retryTurn?%5B%22a%22%5D)",
    truncated: false,
    error: { code: "events_disconnected", message: "reset" },
  });
  // The raw failure markdown would duplicate the error note; only the
  // command affordance survives. A renderer cannot execute a command: link
  // without the messaging bridge, so the anchor carries data-retry and the
  // bridge posts the retry instead.
  assert(!html.includes("[\u21bb Retry turn]"), html);
  assert(html.includes('data-retry="1"'), html);
  assert(html.includes("<code>events_disconnected</code>"), html);
});

Deno.test("timeline keeps the full answer text when there is no error", () => {
  const text = "[link](command:maieutics.retryTurn?%5B1%5D) stays inline";
  const html = render({ tools: [], text, truncated: false });
  // Without a structured error the text is the answer, not failure markdown.
  assert(html.includes(`href="command:maieutics.retryTurn?%5B1%5D"`), html);
});

Deno.test("markdown view mirrors the legacy composition with diff fences", () => {
  const html = renderMarkdownView({
    tools: [{
      tool: "workspace_edit",
      status: "ok",
      argsSummary: "src/a.ts",
      diff: { unified: "+new\n", additions: 1, deletions: 0, truncated: false },
    }],
    text: "## Answer\n\nbody",
    truncated: true,
    error: { code: "c", message: "m" },
    usage: { inputTokens: 5, outputTokens: 7 },
    model: { profileId: "p", provider: "prov", model: "m-1" },
  });
  assert(html.includes(`class="md"`), html);
  // Legacy tool bullet with the diff stat, then the colorized fence.
  assert(
    html.includes(
      "<li>✅ <code>workspace_edit</code> · <code>src/a.ts</code> · <code>+1 −0</code></li>",
    ),
    html,
  );
  assert(html.includes(`class="diffline add"`), html);
  assert(html.includes("+new"), html);
  assert(html.includes("<h2>Answer</h2>"), html);
  // Notes and terminal metadata ride along.
  assert(html.includes("truncated after exhausting"), html);
  assert(html.includes("<code>c</code> — m"), html);
  assert(html.includes("5\u2191 7\u2193 tokens"), html);
});

Deno.test("view toggle offers both views with the active one pressed", () => {
  assertEquals(
    viewToggleHtml("timeline"),
    `<div class="viewtoggle" role="group" aria-label="Turn view">` +
      `<button type="button" data-maieutics-view="timeline" aria-pressed="true">Timeline</button>` +
      `<button type="button" data-maieutics-view="markdown" aria-pressed="false">Markdown</button></div>`,
  );
  const markdown = viewToggleHtml("markdown");
  assert(markdown.includes(`data-maieutics-view="markdown" aria-pressed="true"`), markdown);
  assert(markdown.includes(`data-maieutics-view="timeline" aria-pressed="false"`), markdown);
});

Deno.test("empty turn renders the placeholder in both views", () => {
  assertEquals(render({ tools: [], text: "", truncated: false }), "<em>(empty turn)</em>");
  assertEquals(renderMarkdownView({}), "<em>(empty turn)</em>");
});

Deno.test("timeline renders each subagent child as an indented section", () => {
  const html = render({
    tools: [{ tool: "agent_spawn", status: "ok" }],
    subagents: [
      {
        status: "ok",
        text: "child report body",
        tools: [{
          tool: "workspace_search",
          status: "ok",
          argsSummary: "route(",
          durationMs: 900,
        }],
      },
      { status: "cancelled", text: "", tools: [], code: "cancelled" },
    ],
    text: "answer",
    truncated: false,
  });
  // The child sections ride the default timeline: heading with the status
  // mark, reused tool row template, then the report text.
  assert(html.includes(`class="subagents"`), html);
  assert(html.includes(`class="subagent"`), html);
  assert(html.includes("🤖 子代理 1 · ✅"), html);
  assert(html.includes("🤖 子代理 2 · 🚫"), html);
  assert(html.includes(`<li class="ok">workspace_search`), html);
  assert(html.includes("<code>route(</code>"), html);
  assert(html.includes("900ms"), html);
  assert(html.includes("child report body"), html);
});

Deno.test("subagent child text is untrusted and renders escaped", () => {
  const html = subagentsHtml([
    {
      status: "failed",
      text: "<img src=x onerror=alert(1)> # not-a-heading **bold**",
      tools: [],
      code: "task_failed",
    },
  ]);
  // Nothing raw reaches the DOM: the markdown pass escapes before rendering.
  assert(!html.includes("<img"), html);
  assert(html.includes("&lt;img src=x onerror=alert(1)&gt;"), html);
  assert(html.includes("🤖 子代理 · ❌"), html);
  // Malformed entries degrade instead of throwing.
  assertEquals(
    subagentsHtml([{ status: "weird" }, { status: "ok", tools: "nope", text: 7 } as unknown]),
    `<div class="subagents"><div class="subagent"><div class="subagent-head">🤖 子代理 1 · ⏳</div></div>\n` +
      `<div class="subagent"><div class="subagent-head">🤖 子代理 2 · ✅</div></div></div>`,
  );
});

Deno.test("a turn without subagents renders unchanged (backward compatible)", () => {
  const without = render({ tools: [], text: "plain", truncated: false });
  assert(!without.includes("子代理"), without);
  assertEquals(render({}), "<em>(empty turn)</em>");
});

Deno.test("markdown fallback view quotes the subagent timeline", () => {
  const html = renderMarkdownView({
    tools: [],
    subagents: [
      {
        status: "ok",
        text: "line one\n\nline two",
        tools: [{ tool: "workspace_list", status: "ok" }],
      },
    ],
    text: "answer",
    truncated: false,
  });
  assert(html.includes(`class="md"`), html);
  // The quoted block renders as one note: heading, tool bullet, then the
  // child text — the child's blank line stays inside the quote, and none of
  // its structure can escape the block.
  assert(
    html.includes(
      `<div class="note">🤖 子代理 · ✅<br>- ✅ <code>workspace_list</code><br>` +
        `line one<br><br>line two</div>`,
    ),
    html,
  );
});
