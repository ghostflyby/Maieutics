// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";
import { TeX } from "mathjax-full/js/input/tex.js";
import { SVG } from "mathjax-full/js/output/svg.js";
import { liteAdaptor } from "mathjax-full/js/adaptors/liteAdaptor.js";
import { HTMLDocument } from "mathjax-full/js/handlers/html/HTMLDocument.js";
import "mathjax-full/js/input/tex/base/BaseConfiguration.js";
import "mathjax-full/js/input/tex/ams/AmsConfiguration.js";
import "mathjax-full/js/input/tex/newcommand/NewcommandConfiguration.js";
import { reportDiagnostic } from "./diagnostics.ts";
import { installMathSyntax } from "./vendor/markdown-it-mathjax3/syntax.ts";

/** Offline SVG math; every formula owns fresh macro and document state. */
export function installMath(md: MarkdownIt): void {
  installMathSyntax(md);
  md.core.ruler.after("inline", "maieutics_math_locations", (state) => {
    for (const token of state.tokens) {
      for (const child of token.children ?? []) {
        if (child.type === "math_inline") child.map = token.map;
      }
    }
  });
  for (const type of ["math_inline", "math_block"]) {
    md.renderer.rules[type] = (tokens, index, _options, env) => {
      const token = tokens[index];
      const block = type === "math_block";
      const tag = block ? "div" : "span";
      const wrap = (html: string) =>
        `<${tag}${env.vueMarkdown ? " v-pre" : ""} class="math-${
          block ? "block" : "inline"
        }">${html}</${tag}>`;
      const fallback = () =>
        wrap(
          md.utils.escapeHtml(
            `${block ? "$$" : "$"}${token.content}${
              block && !token.meta?.closed ? "" : block ? "$$" : "$"
            }`,
          ),
        );
      if (block && !token.meta?.closed) return fallback();
      env.mathCount = (env.mathCount ?? 0) + 1;
      if (env.mathCount > 128 || token.content.length > 4096) {
        reportDiagnostic(env, {
          code: "math_limit",
          line: (env.sourceLineOffset ?? 0) + (token.map?.[0] ?? 0) + 1,
          column: 1,
        });
        return fallback();
      }
      try {
        const adaptor = liteAdaptor();
        // No global handler registry, shared macros, autoload, URLs, HTML or resource packages.
        const document = new HTMLDocument(adaptor.parse("", "text/html"), adaptor, {
          InputJax: new TeX({
            packages: ["base", "ams", "newcommand"],
            maxBuffer: 4096,
            maxMacros: 1000,
          }),
          OutputJax: new SVG({ fontCache: "none" }),
        });
        const html = adaptor.outerHTML(document.convert(token.content, { display: block }));
        if (html.length > 200_000 || html.includes("data-mjx-error=")) {
          throw new Error("invalid_math");
        }
        return wrap(html);
      } catch {
        reportDiagnostic(env, {
          code: "math_invalid",
          line: (env.sourceLineOffset ?? 0) + (token.map?.[0] ?? 0) + 1,
          column: 1,
        });
        return fallback();
      }
    };
  }
}
