// Maieutics turn timeline renderer for `application/vnd.maieutics.turn+json`.
// Dependency-free DOM/HTML only: notebook renderer scripts run in the
// renderer process with no bundler, so this file must stay plain JavaScript.
//
// The structured timeline is the DEFAULT view: a structured turn output
// carries only the turn+json item, because a sibling text/markdown item
// would always win VS Code's mime display order. The markdown view — the
// legacy fallback rendering — stays reachable through the in-output toggle.
// Answers and diffs render through a small escape-first markdown pass: the
// output text is untrusted model content, so nothing reaches the DOM
// unescaped.

"use strict";

const STYLE = `
.maieutics-turn { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px); color: var(--vscode-foreground); }
.maieutics-turn ul.tools { list-style: none; margin: 0 0 8px; padding: 0; }
.maieutics-turn ul.tools li { padding: 1px 0; }
.maieutics-turn .ok::before { content: "✅ "; }
.maieutics-turn .error::before { content: "❌ "; }
.maieutics-turn .text { margin-top: 4px; line-height: 1.5; }
.maieutics-turn .note { opacity: 0.8; margin-top: 8px; }
.maieutics-turn .detail { opacity: 0.75; }
.maieutics-turn .meta { opacity: 0.8; margin-top: 8px; }
.maieutics-turn .actions { margin-top: 8px; }
.maieutics-turn .actions a { color: var(--vscode-textLink-foreground); }
.maieutics-turn .viewtoggle { display: flex; justify-content: flex-end; gap: 8px; margin: 0 0 6px; }
.maieutics-turn .viewtoggle button {
  font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px);
  color: var(--vscode-descriptionForeground); background: transparent; border: none;
  padding: 0 2px; cursor: pointer;
}
.maieutics-turn .viewtoggle button[aria-pressed="true"] { color: var(--vscode-foreground); text-decoration: underline; }
.maieutics-turn .md { line-height: 1.5; }
.maieutics-turn .md h1, .maieutics-turn .md h2, .maieutics-turn .md h3,
.maieutics-turn .md h4, .maieutics-turn .md h5, .maieutics-turn .md h6 { margin: 10px 0 4px; }
.maieutics-turn .md p { margin: 4px 0; }
.maieutics-turn .md ul, .maieutics-turn .md ol { margin: 4px 0; padding-left: 22px; }
.maieutics-turn .md code {
  font-family: var(--vscode-editor-font-family, monospace);
  font-size: var(--vscode-editor-font-size, 12px);
  background: var(--vscode-textCodeBlock-background);
}
.maieutics-turn .md a { color: var(--vscode-textLink-foreground); }
.maieutics-turn pre.code, .maieutics-turn pre.diff {
  font-family: var(--vscode-editor-font-family, monospace);
  font-size: var(--vscode-editor-font-size, 12px);
  background: var(--vscode-textCodeBlock-background);
  padding: 6px 8px; margin: 4px 0 8px; overflow-x: auto;
}
.maieutics-turn .diffline { white-space: pre; }
.maieutics-turn .add { color: var(--vscode-gitDecoration.addedResourceForeground, #81b88b); }
.maieutics-turn .del { color: var(--vscode-gitDecoration.deletedResourceForeground, #c74e39); }
.maieutics-turn .hunk { opacity: 0.7; }
.maieutics-turn .subagents { margin: 6px 0; }
.maieutics-turn .subagent {
  margin: 4px 0 10px; padding: 2px 0 2px 10px;
  border-left: 2px solid var(--vscode-panel-border, rgba(128, 128, 128, 0.35));
}
.maieutics-turn .subagent-head { opacity: 0.85; margin-bottom: 2px; }
`;

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function formatTokens(value) {
  if (typeof value !== "number") return "";
  if (value < 1000) return String(value);
  if (value < 1000000) return `${(value / 1000).toFixed(1)}k`;
  return `${(value / 1000000).toFixed(1)}M`;
}

function formatDuration(ms) {
  if (typeof ms !== "number") return "";
  if (ms < 1000) return `${ms}ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${Math.round(seconds - minutes * 60)}s`;
}

// ---- edit diffs (same fence shape as turnView.ts fencedDiffLines) ----

/** Diff body lines rendered per tool; the server already bounds the text,
 * this only caps the visible preview. */
const MaximumDiffPreviewLines = 40;

function diffBodyLines(diff) {
  const unified = typeof diff.unified === "string" ? diff.unified : "";
  const body = unified.length > 0 && unified.endsWith("\n")
    ? unified.slice(0, -1).split("\n")
    : unified.split("\n");
  const lines = body.slice(0, MaximumDiffPreviewLines);
  if (body.length > MaximumDiffPreviewLines || diff.truncated === true) {
    lines.push("… diff preview truncated");
  }
  return lines;
}

function diffLineClass(line) {
  if (line.startsWith("+++") || line.startsWith("---") || line.startsWith("@@")) return "hunk";
  if (line.startsWith("+")) return "add";
  if (line.startsWith("-")) return "del";
  return "";
}

/** A snapshot diff as a colorized <pre>; the unified text is raw here and is
 * escaped line by line. */
function diffHtml(diff) {
  const rows = diffBodyLines(diff)
    .map((line) => `<div class="diffline ${diffLineClass(line)}">${escapeHtml(line)}</div>`)
    .join("");
  return `<pre class="diff">${rows}</pre>`;
}

// ---- light markdown (escape-first: model output is untrusted) ----

/** Inline transforms over already-escaped text; code spans are tokenized
 * first so no other transform can match inside them. Only http(s) and
 * command: links become anchors — any other target stays plain text. */
function inlineHtml(escaped) {
  return escaped
    .split(/(`[^`]*`)/)
    .map((part) => {
      if (part.length >= 2 && part.startsWith("`") && part.endsWith("`")) {
        return `<code>${part.slice(1, -1)}</code>`;
      }
      return part
        .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
        .replace(/(^|[^*])\*([^*\s][^*]*)\*/g, "$1<em>$2</em>")
        .replace(
          /\[([^\]]+)\]\(([^()\s]*(?:\([^()]*\)[^()\s]*)*)\)/g,
          (match, label, href) =>
            /^(https?:|command:)/i.test(href) ? `<a href="${href}">${label}</a>` : match,
        );
    })
    .join("");
}

/** A fenced block as HTML; the raw fence lines are escaped here. */
function fenceHtml(fence) {
  if (fence.lang === "diff") {
    const rows = fence.lines
      .map((line) => `<div class="diffline ${diffLineClass(line)}">${escapeHtml(line)}</div>`)
      .join("");
    return `<pre class="diff">${rows}</pre>`;
  }
  return `<pre class="code"><code>${fence.lines.map(escapeHtml).join("\n")}</code></pre>`;
}

/** A small markdown subset: headings, fenced code (```diff colorized),
 * blockquote notes, lists, hr, paragraphs, and the inline transforms above.
 * Everything renders from escaped text, so unknown constructs degrade to
 * plain text instead of injecting HTML. */
function markdownToHtml(markdown) {
  const lines = String(markdown).split("\n");
  const blocks = [];
  let paragraph = [];
  let list = null;
  let quote = [];
  let fence = null;

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      blocks.push(
        `<p>${paragraph.map((line) => inlineHtml(escapeHtml(line))).join("<br>")}</p>`,
      );
      paragraph = [];
    }
  };
  const flushList = () => {
    if (list !== null) {
      blocks.push(`</${list}>`);
      list = null;
    }
  };
  const flushQuote = () => {
    if (quote.length > 0) {
      blocks.push(
        `<div class="note">${quote.map((line) => inlineHtml(escapeHtml(line))).join("<br>")}</div>`,
      );
      quote = [];
    }
  };
  const flushAll = () => {
    flushParagraph();
    flushList();
    flushQuote();
  };

  for (const line of lines) {
    if (fence !== null) {
      if (/^```/.test(line)) {
        blocks.push(fenceHtml(fence));
        fence = null;
      } else {
        fence.lines.push(line);
      }
      continue;
    }
    const open = line.match(/^```(.*)$/);
    if (open !== null) {
      flushAll();
      fence = { lang: open[1].trim(), lines: [] };
      continue;
    }
    if (line.trim().length === 0) {
      flushAll();
      continue;
    }
    const heading = line.match(/^(#{1,6})\s+(.*)$/);
    if (heading !== null) {
      flushAll();
      const level = heading[1].length;
      blocks.push(`<h${level}>${inlineHtml(escapeHtml(heading[2]))}</h${level}>`);
      continue;
    }
    const bullet = line.match(/^[-*+]\s+(.*)$/);
    const ordered = line.match(/^\d+[.)]\s+(.*)$/);
    if (bullet !== null || ordered !== null) {
      flushParagraph();
      flushQuote();
      const kind = bullet !== null ? "ul" : "ol";
      const content = bullet !== null ? bullet[1] : ordered[1];
      if (list !== kind) {
        flushList();
        blocks.push(`<${kind}>`);
        list = kind;
      }
      blocks.push(`<li>${inlineHtml(escapeHtml(content))}</li>`);
      continue;
    }
    const quoteLine = line.match(/^>\s?(.*)$/);
    if (quoteLine !== null) {
      flushParagraph();
      flushList();
      quote.push(quoteLine[1]);
      continue;
    }
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(line.trim())) {
      flushAll();
      blocks.push("<hr>");
      continue;
    }
    flushList();
    flushQuote();
    paragraph.push(line);
  }
  if (fence !== null) blocks.push(fenceHtml(fence)); // an unterminated fence still renders
  flushAll();
  return blocks.join("\n");
}

/** Command affordances ([label](command:...)) inside a snapshot's markdown
 * text — the only actionable content of a failure body in the timeline. */
function commandLinks(text) {
  const links = [];
  const pattern = /\[([^\]]+)\]\((command:[^()\s]*(?:\([^()]*\)[^()\s]*)*)\)/g;
  for (let match = pattern.exec(text); match !== null; match = pattern.exec(text)) {
    links.push({ label: match[1], href: match[2] });
  }
  return links;
}

/** Tool entries as legacy markdown bullets with a capped ```diff fence under
 * each structured edit result (the removed markdown item's composition). */
function toolMarkdownLines(tools) {
  const lines = [];
  for (const tool of tools) {
    const detail = [
      typeof tool.argsSummary === "string" && tool.argsSummary.length > 0
        ? `\`${tool.argsSummary}\``
        : "",
      tool.diff && typeof tool.diff === "object"
        ? `\`+${tool.diff.additions} −${tool.diff.deletions}\``
        : "",
      typeof tool.durationMs === "number" ? formatDuration(tool.durationMs) : "",
    ].filter((part) => part !== "").join(" · ");
    const mark = tool.status === "error" ? "❌" : "✅";
    lines.push(`- ${mark} \`${tool.tool}\`${detail === "" ? "" : ` · ${detail}`}`);
    if (tool.diff && typeof tool.diff === "object") {
      lines.push("", "```diff", ...diffBodyLines(tool.diff), "```");
    }
  }
  return lines;
}

/** The subagent timeline as markdown: one quoted block per child, every line
 * carrying the `> ` prefix so the child's own markdown cannot escape the
 * indentation level (bare `>` keeps the quote continuous across blank
 * lines). Mirrors the live segment composition of turnView.ts. */
function subagentsMarkdown(subagents) {
  const blocks = [];
  const many = subagents.length > 1;
  const quoted = (line) => (line.length === 0 ? ">" : `> ${line}`);
  for (const [index, child] of subagents.entries()) {
    const lines = [`> 🤖 子代理${many ? ` ${index + 1}` : ""} · ${subagentMark(child)}`];
    for (const line of toolMarkdownLines(Array.isArray(child.tools) ? child.tools : [])) {
      lines.push(quoted(line));
    }
    if (typeof child.text === "string" && child.text.length > 0) {
      for (const line of child.text.split("\n")) lines.push(quoted(line));
    }
    if (child.truncated) lines.push(quoted("> ⚠️ 子代理输出被截断。"));
    while (lines.length > 0 && lines.at(-1) === ">") lines.pop();
    blocks.push(lines);
  }
  const out = [];
  for (const block of blocks) {
    if (out.length > 0) out.push("");
    out.push(...block);
  }
  return out.join("\n");
}

/** One tool row of a timeline (parent or subagent): status mark, name,
 * argument/duration detail, and the colorized diff body. All fields are
 * escaped — tool arguments are untrusted model output. */
function toolLi(tool) {
  const diff = tool && typeof tool.diff === "object" && tool.diff !== null ? tool.diff : null;
  const detail = [
    typeof tool.argsSummary === "string" && tool.argsSummary.length > 0
      ? `<span class="detail"><code>${escapeHtml(tool.argsSummary)}</code></span>`
      : "",
    diff !== null
      ? `<span class="detail"><code>+${escapeHtml(String(diff.additions))} −${
        escapeHtml(String(diff.deletions))
      }</code></span>`
      : "",
    typeof tool.durationMs === "number"
      ? `<span class="detail">${escapeHtml(formatDuration(tool.durationMs))}</span>`
      : "",
  ].filter((part) => part !== "").join(" · ");
  return `<li class="${escapeHtml(tool.status)}">${escapeHtml(tool.tool)}${
    detail === "" ? "" : ` · ${detail}`
  }${diff !== null ? diffHtml(diff) : ""}</li>`;
}

function toolListHtml(tools) {
  return `<ul class="tools">${tools.map(toolLi).join("")}</ul>`;
}

/** Status marks of the subagent timeline (⏳ running, ✅ ok, ❌ failed,
 * 🚫 cancelled — the cancelled child publishes run.failed code=cancelled). */
const SubagentMarks = { running: "⏳", ok: "✅", failed: "❌", cancelled: "🚫" };

function subagentMark(child) {
  return SubagentMarks[child.status] ?? SubagentMarks.running;
}

/** The folded subagent child runs as indented sections inside the timeline:
 * a status heading, the child's tool rows, then its report text (rendered
 * through the same escape-first markdown pass as the answer). */
function subagentsHtml(subagents) {
  const many = subagents.length > 1;
  const sections = subagents.map((child, index) => {
    const body = [];
    if (Array.isArray(child.tools) && child.tools.length > 0) {
      body.push(toolListHtml(child.tools));
    }
    if (typeof child.text === "string" && child.text.length > 0) {
      body.push(`<div class="text">${markdownToHtml(child.text)}</div>`);
    }
    if (child.truncated) {
      body.push('<div class="note">⚠️ 子代理输出被截断。</div>');
    }
    const head = `🤖 子代理${many ? ` ${index + 1}` : ""} · ${subagentMark(child)}`;
    return `<div class="subagent"><div class="subagent-head">${escapeHtml(head)}</div>${
      body.join("\n")
    }</div>`;
  });
  return `<div class="subagents">${sections.join("\n")}</div>`;
}

/** The structured timeline (the default view): tool rows with diffs, the
 * subagent child timeline, the answer, truncation/error notes, and the
 * terminal model/token metadata. */
function render(turn) {
  const parts = [];
  if (Array.isArray(turn.tools) && turn.tools.length > 0) {
    parts.push(toolListHtml(turn.tools));
  }
  if (Array.isArray(turn.subagents) && turn.subagents.length > 0) {
    parts.push(subagentsHtml(turn.subagents));
  }
  if (typeof turn.text === "string" && turn.text.length > 0) {
    // A failed turn's text is the failure markdown and duplicates the
    // structured error note below; surface only its command affordances
    // (Retry) as actions and skip the rest.
    const links = turn.error ? commandLinks(turn.text) : [];
    parts.push(
      links.length > 0
        ? `<div class="actions">${
          links
            .map((link) => `<a href="${escapeHtml(link.href)}">${escapeHtml(link.label)}</a>`)
            .join(" ")
        }</div>`
        : `<div class="text">${markdownToHtml(turn.text)}</div>`,
    );
  }
  if (turn.truncated) {
    parts.push(
      '<div class="note">⚠️ The agent turn was truncated after exhausting its model iteration budget.</div>',
    );
  }
  if (turn.error) {
    parts.push(
      `<div class="note">❌ <code>${escapeHtml(turn.error.code)}</code> — ${
        escapeHtml(turn.error.message)
      }</div>`,
    );
  }
  // Terminal metadata: which model answered and what it cost in tokens.
  const meta = [];
  if (turn.model && typeof turn.model === "object") {
    meta.push(`<code>${escapeHtml(turn.model.provider)}/${escapeHtml(turn.model.model)}</code>`);
  }
  if (turn.usage && typeof turn.usage === "object") {
    const input = formatTokens(turn.usage.inputTokens);
    const output = formatTokens(turn.usage.outputTokens);
    if (input !== "" || output !== "") meta.push(`${input}\u2191 ${output}\u2193 tokens`);
  }
  if (meta.length > 0) parts.push(`<div class="meta">${meta.join(" \u00b7 ")}</div>`);
  return parts.length > 0 ? parts.join("\n") : "<em>(empty turn)</em>";
}

/** The legacy markdown view (what the removed text/markdown item showed):
 * tool bullets with diff fences, the subagent child timeline, the answer,
 * and the notes, through the same markdown pass so the fallback keeps its
 * old look. */
function renderMarkdownView(turn) {
  const sections = [];
  if (Array.isArray(turn.tools) && turn.tools.length > 0) {
    sections.push(toolMarkdownLines(turn.tools).join("\n"));
  }
  if (Array.isArray(turn.subagents) && turn.subagents.length > 0) {
    sections.push(subagentsMarkdown(turn.subagents));
  }
  if (typeof turn.text === "string" && turn.text.length > 0) sections.push(turn.text);
  if (turn.truncated) {
    sections.push("> ⚠️ The agent turn was truncated after exhausting its model iteration budget.");
  }
  if (turn.error) sections.push(`> ❌ \`${turn.error.code}\` — ${turn.error.message}`);
  const meta = [];
  if (turn.model && typeof turn.model === "object") {
    meta.push(`${turn.model.provider}/${turn.model.model}`);
  }
  if (turn.usage && typeof turn.usage === "object") {
    const input = formatTokens(turn.usage.inputTokens);
    const output = formatTokens(turn.usage.outputTokens);
    if (input !== "" || output !== "") meta.push(`${input}\u2191 ${output}\u2193 tokens`);
  }
  if (meta.length > 0) sections.push(meta.join(" \u00b7 "));
  return sections.length > 0
    ? `<div class="md">${markdownToHtml(sections.join("\n\n"))}</div>`
    : "<em>(empty turn)</em>";
}

/** The in-output view switcher: with no markdown sibling item, VS Code's
 * "change mimetype" picker has nothing to switch to, so the markdown
 * fallback lives here instead. */
function viewToggleHtml(mode) {
  const button = (id, label) =>
    `<button type="button" data-maieutics-view="${id}" aria-pressed="${
      mode === id
    }">${label}</button>`;
  return `<div class="viewtoggle" role="group" aria-label="Turn view">${
    button("timeline", "Timeline")
  }${button("markdown", "Markdown")}</div>`;
}

exports.activate = function activate() {
  return {
    renderOutputItem(output, element) {
      if (element.querySelector("style") === null) {
        const style = document.createElement("style");
        style.textContent = STYLE;
        element.appendChild(style);
      }

      const turn = output.json();
      const views = { timeline: render(turn), markdown: renderMarkdownView(turn) };
      // In-memory per render: the timeline is the default view. VS Code may
      // re-render the output (scroll, resize, updates), which resets it.
      let mode = "timeline";
      let content = null;
      const paint = () => {
        if (content !== null) content.remove();
        content = document.createElement("div");
        content.className = "maieutics-turn";
        content.innerHTML = viewToggleHtml(mode) +
          (mode === "markdown" ? views.markdown : views.timeline);
        content.addEventListener("click", (event) => {
          const target = event.target instanceof Element
            ? event.target.closest("[data-maieutics-view]")
            : null;
          const requested = target === null ? null : target.getAttribute("data-maieutics-view");
          if (requested === "timeline" || requested === "markdown") {
            mode = requested;
            paint();
          }
        });
        element.appendChild(content);
      };
      paint();
    },
    disposeOutputItem() {},
  };
};

// Test seam: the notebook renderer contract reads only `activate`; the pure
// view functions are exposed so the rendering stays unit-testable without a
// webview.
exports.render = render;
exports.renderMarkdownView = renderMarkdownView;
exports.viewToggleHtml = viewToggleHtml;
exports.subagentsHtml = subagentsHtml;
