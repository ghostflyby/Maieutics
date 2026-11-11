import {
  createOutputRenderer,
  renderSubagents as subagentsHtml,
  renderTurn as render,
} from "./outputClient.js";

function escapeHtml(value) {
  return String(value).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

/** Scope the HTML guard to our synthetic output items, leaving ordinary notebook
 * Markdown and renderer extensions untouched. Executable links stay inert. */
function protectMarkdownHtml(markdownIt) {
  const originalParse = markdownIt.parse;
  if (!originalParse.maieuticsGuard) {
    const parse = (source, env) => {
      if (
        env?.outputItem?.maieuticsSafeMarkdown !== true &&
        env?.outputItem?.metadata?.maieuticsSafeMarkdown !== true
      ) {
        return originalParse.call(markdownIt, source, env);
      }
      // Match the kernel's passive html:false parser. Escaping an html_block afterwards
      // is too late: it has already swallowed Markdown following a partial Vue tag.
      const options = markdownIt.options;
      try {
        markdownIt.options = { ...options, html: false };
        return originalParse.call(markdownIt, source, env);
      } finally {
        markdownIt.options = options;
      }
    };
    parse.maieuticsGuard = true;
    markdownIt.parse = parse;
  }
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

/** VS Code notebook adapter; browser rendering lives in the standalone ESM client. */
export async function activate(context) {
  const markdown = await context.getRenderer("vscode.markdown-it-renderer");
  if (markdown === undefined) throw new Error("VS Code Markdown renderer is unavailable.");
  markdown.extendMarkdownIt(protectMarkdownHtml);
  const client = createOutputRenderer({
    sendMessage: (message) => context.postMessage?.(message),
    renderMarkdown: (text, element, options) =>
      markdown.renderOutputItem(
        markdownOutputItem(
          { id: options.outputId, metadata: options.metadata },
          text,
          options.index,
        ),
        element,
        options.signal,
      ),
    onAction: (href) => {
      try {
        if (!href?.startsWith("command:maieutics.retryTurn?")) return;
        const args = JSON.parse(decodeURIComponent(href.split("?")[1]));
        if (!Array.isArray(args) || typeof args[0] !== "string" || !Number.isInteger(args[1])) {
          return;
        }
        context.postMessage?.({
          source: "maieutics-turn",
          type: "retry",
          notebookUri: args[0],
          cellIndex: args[1],
        });
      } catch { /* Malformed notebook retry links are inert. */ }
    },
  });
  context.onDidReceiveMessage?.((message) => client.receive(message));
  return {
    renderOutputItem(output, element, signal) {
      const kind = output.mime === "application/vnd.maieutics.live-assistant+json"
        ? "assistant"
        : output.mime === "application/vnd.maieutics.vue+json"
        ? "vue"
        : output.mime === "application/vnd.maieutics.template+json"
        ? "template"
        : "turn";
      return client.render(
        { id: output.id, kind, value: output.json(), metadata: output.metadata },
        element,
        signal,
      );
    },
    disposeOutputItem(id) {
      client.disposeOutput(id);
    },
  };
}

export { markdownOutputItem, protectMarkdownHtml, render, subagentsHtml };
