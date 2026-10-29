// Maieutics turn timeline renderer for `application/vnd.maieutics.turn+json`.
// Dependency-free DOM/HTML only: this entry is hand-written (no bundler), but
// it is an ES module — VS Code loads renderer entrypoints with a native
// dynamic import(), so exports must be real export statements.
//
// Each node is one collapsed output. Expanded Markdown delegates to VS Code's
// native renderer, with model HTML escaped even in trusted workspaces.

"use strict";

const STYLE = `
.maieutics-turn { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px); color: var(--vscode-foreground); }
.maieutics-turn ul.tools { list-style: none; margin: 0 0 8px; padding: 0; }
.maieutics-turn ul.tools li { padding: 1px 0; }
.maieutics-turn .ok::before { content: "✅ "; }
.maieutics-turn .error::before { content: "❌ "; }
.maieutics-turn .running::before { content: "⏳ "; }
.maieutics-turn ol.timeline { list-style: none; margin: 0 0 8px 5px; padding: 0 0 0 16px; border-left: 2px solid var(--vscode-panel-border, #8885); }
.maieutics-turn .timeline-entry { position: relative; margin: 0 0 12px; }
.maieutics-turn .timeline-entry::before { content: ""; position: absolute; left: -21px; top: 8px; width: 8px; height: 8px; border-radius: 50%; background: var(--vscode-descriptionForeground); }
.maieutics-turn .markdown-unavailable { white-space: pre-wrap; }
.maieutics-turn details.timeline-node { margin: 0 0 0 5px; padding: 3px 0 3px 15px; border-left: 2px solid var(--vscode-panel-border, #8885); }
.maieutics-turn details.timeline-node > summary { cursor: pointer; display: flex; align-items: baseline; gap: 10px; list-style: none; line-height: 1.6; }
.maieutics-turn details.timeline-node > summary::-webkit-details-marker { display: none; }
.maieutics-turn details.timeline-node > summary::before { content: "▸"; color: var(--vscode-descriptionForeground); margin-left: -22px; }
.maieutics-turn details.timeline-node[open] > summary::before { content: "▾"; }
.maieutics-turn .node-title { flex-shrink: 0; }
.maieutics-turn .preview { color: var(--vscode-descriptionForeground); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.maieutics-turn details.timeline-node[open] > summary > .preview { display: none; }
.maieutics-turn .node-body { margin: 8px 0; line-height: 1.5; }
.maieutics-turn pre.json, .maieutics-turn pre.original { white-space: pre-wrap; overflow-wrap: anywhere; }
.maieutics-turn .detail-label { opacity: 0.75; margin-top: 8px; }
.maieutics-turn .text { margin-top: 4px; line-height: 1.5; }
.maieutics-turn .note { opacity: 0.8; margin-top: 8px; }
.maieutics-turn .detail { opacity: 0.75; }
.maieutics-turn .meta { opacity: 0.8; margin-top: 8px; }
.maieutics-turn .actions { margin-top: 8px; }
.maieutics-turn .actions a { color: var(--vscode-textLink-foreground); }
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
.maieutics-turn .reasoning { margin: 6px 0; color: var(--vscode-descriptionForeground); }
.maieutics-turn .reasoning summary { cursor: pointer; }
.maieutics-turn .reasoning pre { white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; }
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

/** Safe fallback for hosts without a Markdown renderer (never a partial parser). */
function plainMarkdown(text) {
  return `<pre class="markdown-unavailable">${escapeHtml(text)}</pre>`;
}

/** Scope the HTML guard to our synthetic output items, leaving ordinary notebook
 * Markdown and renderer extensions untouched. Executable links stay inert. */
function protectMarkdownHtml(markdownIt) {
  for (const name of ["html_inline", "html_block"]) {
    const original = markdownIt.renderer.rules[name];
    if (original?.maieuticsGuard) continue;
    const guarded = (tokens, index, options, env, renderer) =>
      (env?.outputItem?.maieuticsSafeMarkdown === true ||
          env?.outputItem?.metadata?.maieuticsSafeMarkdown === true)
        ? escapeHtml(tokens[index].content)
        : original(tokens, index, options, env, renderer);
    guarded.maieuticsGuard = true;
    markdownIt.renderer.rules[name] = guarded;
  }
  const originalLink = markdownIt.renderer.rules.link_open;
  if (originalLink?.maieuticsGuard) return;
  const guardedLink = (tokens, index, options, env, renderer) => {
    const token = tokens[index];
    if (
      (env?.outputItem?.maieuticsSafeMarkdown === true ||
        env?.outputItem?.metadata?.maieuticsSafeMarkdown === true) &&
      /^(command|javascript|vbscript|data):/i.test(token.attrGet("href") ?? "")
    ) {
      token.attrSet("href", "#");
    }
    return originalLink
      ? originalLink(tokens, index, options, env, renderer)
      : renderer.renderToken(tokens, index, options);
  };
  guardedLink.maieuticsGuard = true;
  markdownIt.renderer.rules.link_open = guardedLink;
}

function markdownOutputItem(output, text, index) {
  return {
    id: `${output.id}:markdown:${index}`,
    mime: "text/markdown",
    metadata: output.metadata ?? {},
    maieuticsSafeMarkdown: true,
    text: () => text,
    data: () => new TextEncoder().encode(text),
    blob: () => new Blob([text], { type: "text/markdown" }),
    json: () => JSON.parse(text),
  };
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

function retryHtml(text) {
  const retry = commandLinks(text).find((link) =>
    link.href.startsWith("command:maieutics.retryTurn?")
  );
  return retry === undefined
    ? ""
    : `<div class="actions"><a href="${escapeHtml(retry.href)}" data-retry="1">${
      escapeHtml(retry.label)
    }</a></div>`;
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
  }${
    tool.arguments !== undefined
      ? `<div class="detail-label">参数</div><pre class="json">${
        escapeHtml(JSON.stringify(tool.arguments, null, 2))
      }</pre>`
      : ""
  }${
    tool.progress
      ? `<div class="detail-label">进度</div><pre class="original">${
        escapeHtml(tool.progress)
      }</pre>`
      : ""
  }${
    tool.result !== undefined
      ? `<div class="detail-label">结果</div><pre class="json">${
        escapeHtml(JSON.stringify(tool.result, null, 2))
      }</pre>`
      : ""
  }${diff !== null ? diffHtml(diff) : ""}${
    tool.detailsTruncated
      ? '<div class="note">工具详情超过显示上限，仅保留前 20,000 个字符。</div>'
      : ""
  }</li>`;
}

/** Summary text is always escaped plain text; it cannot inject HTML or command links. */
function reasoningHtml(summaries) {
  if (!Array.isArray(summaries)) return "";
  const entries = summaries.filter((entry) =>
    entry !== null && typeof entry === "object" && typeof entry.text === "string" &&
    entry.text.length > 0
  );
  const note = entries.reduce((count, entry) => count + entry.text.length, 0) >= 20_000
    ? '<div class="note">思考摘要仅显示前 20,000 个字符。</div>'
    : "";
  return entries.map((entry) => `<pre class="original">${escapeHtml(entry.text)}</pre>`).join(
    "\n",
  ) + note;
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
function subagentsHtml(subagents, overflow = false, markdownToHtml = plainMarkdown) {
  const many = subagents.length > 1;
  const sections = subagents.map((child, index) => {
    const body = [reasoningHtml(child.reasoning)];
    if (Array.isArray(child.tools) && child.tools.length > 0) {
      body.push(toolListHtml(child.tools));
    }
    if (typeof child.text === "string" && child.text.length > 0) {
      body.push(`<div class="text">${markdownToHtml(child.text)}</div>`);
    }
    if (child.truncated) {
      body.push('<div class="note">⚠️ 子代理输出被截断。</div>');
    }
    if (overflow) {
      body.push('<div class="note">⚠️ 子代理过多，仅显示前 16 个。</div>');
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
function renderExpanded(turn, markdownToHtml = plainMarkdown) {
  const parts = [];
  if (Array.isArray(turn.timeline) && turn.timeline.length > 0) {
    parts.push(timelineHtml(turn, markdownToHtml));
  } else {
    const reasoning = reasoningHtml(turn.reasoning);
    if (reasoning) parts.push(reasoning);
    if (Array.isArray(turn.tools) && turn.tools.length > 0) {
      parts.push(toolListHtml(turn.tools));
    }
    if (Array.isArray(turn.subagents) && turn.subagents.length > 0) {
      parts.push(subagentsHtml(turn.subagents, turn.subagentsOverflow === true, markdownToHtml));
    }
  }
  if (typeof turn.text === "string" && turn.text.length > 0) {
    // A failed turn's text duplicates the structured error note below in two
    // ways the reviewers proved with deno eval: the whole failure markdown
    // re-renders the message the error note already shows, and unlinked
    // failure text renders a second time in the default timeline view. Keep
    // only the Retry affordance (through the messaging bridge, which alone
    // can execute commands from a renderer) and drop the duplicated text.
    const retry = turn.error ? retryHtml(turn.text) : "";
    if (retry) {
      parts.push(retry);
    } else if (
      !turn.error &&
      !(Array.isArray(turn.timeline) &&
        turn.timeline.some((entry) => entry?.kind === "text" && entry.text === turn.text))
    ) {
      parts.push(`<div class="text">${markdownToHtml(turn.text)}</div>`);
    }
  }
  if (turn.truncated) {
    parts.push(
      '<div class="note">⚠️ 输出已截断。</div>',
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

/** Ordered display entries reference the folded summaries, tools and children, so
 * status updates replace one row without losing the earlier model rounds. */
function timelineHtml(turn, markdownToHtml) {
  const rows = [];
  for (const entry of turn.timeline) {
    if (entry === null || typeof entry !== "object") continue;
    let body = "";
    if (entry.kind === "text" && typeof entry.text === "string" && entry.text) {
      body = `<div class="text">${markdownToHtml(entry.text)}</div>`;
    } else if (entry.kind === "reasoning") {
      body = reasoningHtml(
        (Array.isArray(turn.reasoning) ? turn.reasoning : []).filter((summary) =>
          summary?.messageId === entry.messageId
        ),
      );
    } else if (entry.kind === "tool" && turn.tools?.[entry.index]) {
      body = toolListHtml([turn.tools[entry.index]]);
    } else if (entry.kind === "subagent" && turn.subagents?.[entry.index]) {
      body = subagentsHtml([turn.subagents[entry.index]], false, markdownToHtml);
    }
    if (body) rows.push(body);
  }
  if (turn.timelineTruncated) {
    rows.push('<li class="note">⚠️ 时间线预览已截断。</li>');
  }
  if (turn.subagentsOverflow) rows.push('<li class="note">⚠️ 子代理过多，仅显示前 16 个。</li>');
  return rows.join("\n");
}

function compact(value) {
  const text = String(value ?? "").replace(/\s+/g, " ").trim();
  return text.length > 110 ? `${text.slice(0, 110)}…` : text;
}

function collapsedNode(id, title, preview, body, kind = "text") {
  return `<details class="timeline-node ${escapeHtml(kind)}" data-node-id="${
    escapeHtml(id)
  }"><summary><span class="node-title">${escapeHtml(title)}</span><span class="preview">${
    escapeHtml(compact(preview))
  }</span></summary><div class="node-body">${body}</div></details>`;
}

/** The sole Agent rendering: compact nodes which reveal their original content on demand. */
function render(turn, markdownToHtml = plainMarkdown) {
  const parts = [];
  if (
    turn.nodeId === "header" ||
    (!turn.nodeId && (turn.runId || turn.error || turn.model || turn.usage || turn.truncated))
  ) {
    const title = turn.streaming
      ? "⏳ 运行中"
      : turn.error?.code === "run_cancelled"
      ? "🚫 已中断"
      : turn.error
      ? "❌ 失败"
      : turn.truncated
      ? "⚠️ 已截断"
      : "✅ 已完成";
    const meta = [];
    if (turn.model) meta.push(`${turn.model.provider}/${turn.model.model}`);
    if (turn.usage) {
      meta.push(
        `${formatTokens(turn.usage.inputTokens)}↑ ${formatTokens(turn.usage.outputTokens)}↓ tokens`,
      );
    }
    if (turn.timelineTruncated) meta.push("⚠️ 时间线预览已截断");
    if (turn.subagentsOverflow) meta.push("⚠️ 子代理过多，仅显示前 16 个");
    const body = (turn.input
      ? `<div class="detail-label">输入</div><pre class="original">${escapeHtml(turn.input)}</pre>`
      : "") +
      renderExpanded({
        ...turn,
        text: turn.error ? turn.text : "",
        timeline: [],
        tools: [],
        reasoning: [],
        subagents: [],
      }, markdownToHtml);
    parts.push(
      collapsedNode("header", title, turn.error?.message ?? meta.join(" · "), body, "status"),
    );
    if (turn.nodeId === "header") {
      return parts.join("\n");
    }
  }
  const entries = Array.isArray(turn.timeline) ? turn.timeline : [
    ...(Array.isArray(turn.reasoning) ? turn.reasoning : []).map((entry) => ({
      kind: "reasoning",
      messageId: entry.messageId,
    })),
    ...(Array.isArray(turn.tools) ? turn.tools : []).map((_, index) => ({ kind: "tool", index })),
    ...(Array.isArray(turn.subagents) ? turn.subagents : []).map((_, index) => ({
      kind: "subagent",
      index,
    })),
  ];
  for (const [index, entry] of entries.entries()) {
    if (!entry || typeof entry !== "object") continue;
    let title = "", preview = "";
    if (entry.kind === "text" && typeof entry.text === "string" && entry.text) {
      title = "助手回复";
      preview = entry.text;
    } else if (entry.kind === "reasoning" && Array.isArray(turn.reasoning)) {
      const summary = turn.reasoning.find((item) => item?.messageId === entry.messageId);
      if (summary?.text) {
        title = "思考摘要";
        preview = summary.text;
      }
    } else if (entry.kind === "tool" && turn.tools?.[entry.index]) {
      const tool = turn.tools[entry.index];
      title = `${
        tool.status === "running" ? "⏳" : tool.status === "error" ? "❌" : "✅"
      } ${tool.tool}`;
      preview = [
        tool.argsSummary,
        typeof tool.durationMs === "number" ? formatDuration(tool.durationMs) : "",
      ].filter(Boolean).join(" · ");
    } else if (entry.kind === "subagent" && turn.subagents?.[entry.index]) {
      const child = turn.subagents[entry.index];
      title = `🤖 子代理 · ${subagentMark(child)}`;
      preview = child.text ?? "";
    }
    if (title) {
      if (entry.kind === "text") {
        parts.push(`<div class="assistant-reply">${markdownToHtml(entry.text)}</div>`);
        continue;
      }
      parts.push(
        collapsedNode(
          turn.nodeId ?? `entry:${index}`,
          title,
          preview,
          renderExpanded({
            timeline: [entry],
            tools: turn.tools,
            reasoning: turn.reasoning,
            subagents: turn.subagents,
          }, markdownToHtml),
          entry.kind,
        ),
      );
    }
  }
  if (
    !turn.error && typeof turn.text === "string" && turn.text &&
    !entries.some((entry) => entry?.kind === "text" && entry.text === turn.text)
  ) {
    parts.push(`<div class="assistant-reply">${markdownToHtml(turn.text)}</div>`);
  }
  return parts.join("\n") || "<em>(empty turn)</em>";
}

// ES module: VS Code loads notebook renderer entrypoints with a native
// dynamic import(), so the entry must use real export statements (a classic
// script assigning to a bare `exports` global throws ReferenceError there —
// the webview provides no such global).
export async function activate(context) {
  const markdown = await context.getRenderer("vscode.markdown-it-renderer");
  if (markdown === undefined) throw new Error("VS Code Markdown renderer is unavailable.");
  markdown.extendMarkdownIt(protectMarkdownHtml);
  const states = new Map();
  return {
    renderOutputItem(output, element) {
      if (element.querySelector("style") === null) {
        const style = document.createElement("style");
        style.textContent = STYLE;
        element.appendChild(style);
      }

      const turn = output.json();
      const state = states.get(output.id) ?? { openNodes: new Set() };
      states.set(output.id, state);
      for (const detail of element.querySelectorAll("details.timeline-node")) {
        if (detail.open) state.openNodes.add(detail.dataset.nodeId);
        else state.openNodes.delete(detail.dataset.nodeId);
      }
      element.querySelectorAll(".maieutics-turn").forEach((node) => node.remove());
      const blocks = [];
      const markdownToHtml = (text) => {
        const index = blocks.push(text) - 1;
        return `<div data-markdown-index="${index}"></div>`;
      };
      const content = document.createElement("div");
      content.className = "maieutics-turn";
      content.innerHTML = render(turn, markdownToHtml);
      const hydrate = (root) => {
        for (const node of root.querySelectorAll("[data-markdown-index]")) {
          if (node.dataset.rendered) continue;
          let hidden = false;
          for (
            let detail = node.closest("details");
            detail !== null;
            detail = detail.parentElement?.closest("details") ?? null
          ) {
            if (!detail.open) {
              hidden = true;
              break;
            }
          }
          if (hidden) continue;
          const index = Number(node.getAttribute("data-markdown-index"));
          markdown.renderOutputItem(markdownOutputItem(output, blocks[index], index), node);
          node.dataset.rendered = "true";
        }
      };
      for (const detail of content.querySelectorAll("details.timeline-node")) {
        detail.open = state.openNodes.has(detail.dataset.nodeId);
        detail.addEventListener("toggle", () => {
          if (!detail.isConnected) return;
          if (detail.open) {
            state.openNodes.add(detail.dataset.nodeId);
            hydrate(detail);
          } else state.openNodes.delete(detail.dataset.nodeId);
        });
      }
      content.addEventListener("click", (event) => {
        const action = event.target instanceof Element
          ? event.target.closest("[data-retry]")
          : null;
        if (action === null) return;
        event.preventDefault();
        try {
          const args = JSON.parse(decodeURIComponent(action.getAttribute("href").split("?")[1]));
          if (!Array.isArray(args) || typeof args[0] !== "string" || !Number.isInteger(args[1])) {
            return;
          }
          context.postMessage({
            source: "maieutics-turn",
            type: "retry",
            notebookUri: args[0],
            cellIndex: args[1],
          });
        } catch { /* Malformed notebook retry links are inert. */ }
      });
      element.appendChild(content);
      hydrate(content);
    },
    disposeOutputItem(id) {
      if (id === undefined) states.clear();
      else states.delete(id);
    },
  };
}

// Test seam: the notebook renderer contract reads only `activate`; the pure
// view functions are exported so the rendering stays unit-testable without a
// webview.
export { markdownOutputItem, protectMarkdownHtml, render, subagentsHtml };
