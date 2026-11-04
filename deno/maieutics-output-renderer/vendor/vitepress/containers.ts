// Extracted from VitePress v1.6.4; see upstream.json and LICENSE.
// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";
// @deno-types="@types/markdown-it-container"
import container from "markdown-it-container";
import type Token from "markdown-it/lib/token.mjs";

export const containerPlugin = (md: MarkdownIt, options?: ContainerOptions) => {
  for (
    const [name, label] of [
      ["tip", options?.tipLabel || "TIP"],
      ["info", options?.infoLabel || "INFO"],
      ["warning", options?.warningLabel || "WARNING"],
      ["danger", options?.dangerLabel || "DANGER"],
      ["details", options?.detailsLabel || "Details"],
    ]
  ) {
    // CJS container declarations and ESM Markdown-it declarations describe the same runtime.
    const [plugin, kind, config] = createContainer(name, label, md);
    plugin(md as unknown as Parameters<typeof container>[0], kind, config);
  }
};

function createContainer(
  klass: string,
  defaultTitle: string,
  md: MarkdownIt,
) {
  return [
    container,
    klass,
    {
      render(
        tokens: Token[],
        idx: number,
        _options: unknown,
        env: { documentId: string; references?: unknown },
      ) {
        const token = tokens[idx];
        const info = token.info.trim().slice(klass.length).trim();
        const attrs = md.renderer.renderAttrs(token);
        if (token.nesting === 1) {
          const title = md.renderInline(info || defaultTitle, {
            references: env.references,
            documentId: env.documentId,
          });
          if (klass === "details") {
            return `<details class="custom-block ${klass}"${attrs}><summary>${title}</summary>\n`;
          }
          return `<div class="custom-block ${klass}"${attrs}><p class="custom-block-title">${title}</p>\n`;
        } else return klass === "details" ? `</details>\n` : `</div>\n`;
      },
    },
  ] as const;
}

export interface ContainerOptions {
  infoLabel?: string;
  noteLabel?: string;
  tipLabel?: string;
  warningLabel?: string;
  dangerLabel?: string;
  detailsLabel?: string;
  importantLabel?: string;
  cautionLabel?: string;
}
