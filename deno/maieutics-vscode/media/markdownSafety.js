import { protectMarkdownHtml } from "./turnRenderer.js";

/** Extend the default renderer before any Agent Markdown is rendered. */
export async function activate(context) {
  const markdown = await context.getRenderer("vscode.markdown-it-renderer");
  markdown?.extendMarkdownIt(protectMarkdownHtml);
}
