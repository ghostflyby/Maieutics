// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";
import type Token from "markdown-it/lib/token.mjs";
import anchor from "markdown-it-anchor";
// @deno-types="@types/markdown-it-attrs"
import attrs from "markdown-it-attrs";
import { headersPlugin } from "@mdit-vue/plugin-headers";
import { tocPlugin } from "@mdit-vue/plugin-toc";
import { slugify } from "@mdit-vue/shared";
import { containerPlugin } from "./vendor/vitepress/containers.ts";
import { gitHubAlertsPlugin } from "./vendor/vitepress/githubAlerts.ts";

/** Reuses upstream syntax while keeping model content passive and document-local. */
export function installContentPlugins(md: MarkdownIt): void {
  // This CJS declaration also omits the runtime options parameter.
  const installAttrs = attrs as unknown as (
    md: MarkdownIt,
    options: { allowedAttributes: string[] },
  ) => void;
  installAttrs(md, { allowedAttributes: ["id"] });
  md.use(anchor, { slugify });
  md.use(headersPlugin, { level: [1, 2, 3, 4, 5, 6], slugify });
  md.use(tocPlugin, { slugify });
  md.use(containerPlugin);
  md.use(gitHubAlertsPlugin);

  md.core.ruler.before("anchor", "maieutics_heading_ids", (state) => {
    const used = new Set<string>();
    const nextSuffix = new Map<string, number>();
    for (const token of state.tokens) {
      const tab = token.meta?.codeTab;
      if (tab) used.add(`${tab.group.slice(state.env.documentId.length + 1)}-tab-${tab.index}`);
    }
    const prepare = (tokens: Token[]) => {
      for (let index = 0; index < tokens.length; index++) {
        const token = tokens[index];
        if (token.type === "heading_open") {
          const requested = token.attrGet("id");
          const title = (tokens[index + 1]?.children ?? [])
            .filter((child) => child.type === "text" || child.type === "code_inline")
            .map((child) => child.content).join("");
          const base = requested !== null && /^[\p{L}_][\p{L}\p{N}_.:-]{0,127}$/u.test(requested)
            ? requested
            : slugify(title);
          let id = base;
          let suffix = nextSuffix.get(base) ?? 1;
          while (used.has(id)) id = `${base}-${suffix++}`;
          nextSuffix.set(base, suffix);
          used.add(id);
          // Anchor normally throws on duplicate explicit IDs. In streamed model text, keep
          // every heading renderable and deterministic, including automatic/explicit collisions.
          token.attrSet("id", id);
        } else if (token.attrs) {
          // attrs is syntax machinery only; content cannot claim IDs on arbitrary controls.
          token.attrs = token.attrs.filter(([name]) => name !== "id");
        }
        if (token.children) prepare(token.children);
      }
    };
    prepare(state.tokens);
  });
  md.core.ruler.after("anchor", "maieutics_heading_scope", (state) => {
    const prefix = `${state.env.documentId}-`;
    const scope = (tokens: Token[]) => {
      for (const token of tokens) {
        if (token.type === "heading_open") token.attrSet("id", prefix + token.attrGet("id"));
        const href = token.attrGet("href");
        if (href?.startsWith("#")) token.attrSet("href", `#${prefix}${href.slice(1)}`);
        if (token.children) scope(token.children);
      }
    };
    scope(state.tokens);
  });
  md.renderer.rules.github_alert_open = (tokens, index) => {
    const { title, type } = tokens[index].meta;
    // Upstream titles are literal strings, but its renderer interpolates them as HTML.
    return `<div class="custom-block ${type} github-alert"><p class="custom-block-title">${
      md.utils.escapeHtml(title)
    }</p>\n`;
  };
}
