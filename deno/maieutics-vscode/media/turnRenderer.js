// Maieutics turn timeline renderer for `application/vnd.maieutics.turn+json`.
// Dependency-free DOM/HTML only: notebook renderer scripts run in the
// renderer process with no bundler, so this file must stay plain JavaScript.
//
// The cell output also carries a text/markdown item as the fallback view;
// this renderer offers the structured timeline (tools, truncation, errors)
// via the output's "change mimetype" picker.

"use strict";

const STYLE = `
.maieutics-turn { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px); color: var(--vscode-foreground); }
.maieutics-turn ul.tools { list-style: none; margin: 0 0 8px; padding: 0; }
.maieutics-turn ul.tools li { padding: 1px 0; }
.maieutics-turn .ok::before { content: "✅ "; }
.maieutics-turn .error::before { content: "❌ "; }
.maieutics-turn .text { white-space: pre-wrap; }
.maieutics-turn .note { opacity: 0.8; margin-top: 8px; }
.maieutics-turn .detail { opacity: 0.75; }
.maieutics-turn .meta { opacity: 0.8; margin-top: 8px; }
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

function render(turn) {
  const parts = [];
  if (Array.isArray(turn.tools) && turn.tools.length > 0) {
    const items = turn.tools
      .map((tool) => {
        const detail = [
          typeof tool.argsSummary === "string" && tool.argsSummary.length > 0
            ? `<span class="detail"><code>${escapeHtml(tool.argsSummary)}</code></span>`
            : "",
          typeof tool.durationMs === "number"
            ? `<span class="detail">${escapeHtml(formatDuration(tool.durationMs))}</span>`
            : "",
        ].filter((part) => part !== "").join(" · ");
        return `<li class="${escapeHtml(tool.status)}">${escapeHtml(tool.tool)}${
          detail === "" ? "" : ` · ${detail}`
        }</li>`;
      })
      .join("");
    parts.push(`<ul class="tools">${items}</ul>`);
  }
  if (typeof turn.text === "string" && turn.text.length > 0) {
    parts.push(`<div class="text">${escapeHtml(turn.text)}</div>`);
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

exports.activate = function activate() {
  let styled = false;
  return {
    renderOutputItem(output, element) {
      if (!styled) {
        const style = document.createElement("style");
        style.textContent = STYLE;
        element.appendChild(style);
        styled = true;
      }

      element.insertAdjacentHTML("beforeend", render(output.json()));
    },
    disposeOutputItem() {},
  };
};
