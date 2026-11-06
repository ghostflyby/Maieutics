// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";
import {
  evaluateTemplate,
  interpolationEnd,
  MaximumExpressionCharacters,
  MaximumExpressions,
  type TemplateContext,
  TemplateInputError,
  type TemplateResult,
} from "./template.ts";

/** Per-snapshot state shared with inline container titles, never retained by the parser. */
export interface TemplateEnvironment {
  readonly context: TemplateContext;
  readonly expressions: TemplateResult[];
}

/** Inline parsing precedes Markdown syntax; inserted values are text and never reparsed as markup. */
export function installInterpolation(md: MarkdownIt): void {
  md.inline.ruler.before("text", "maieutics_interpolation", (state, silent) => {
    const template = state.env.template as TemplateEnvironment | undefined;
    if (!template || state.src.slice(state.pos, state.pos + 2) !== "{{") return false;
    const rest = state.src.slice(state.pos + 2, state.posMax);
    const closing = interpolationEnd(rest);
    const end = closing === undefined ? state.posMax : state.pos + 2 + closing + 2;
    if (!silent) {
      if (template.expressions.length >= MaximumExpressions) {
        throw new TemplateInputError();
      }
      const result: TemplateResult = closing === undefined
        ? {
          state: rest.length > MaximumExpressionCharacters || template.context.sourceSealed
            ? "failed"
            : "incomplete",
          text: state.src.slice(state.pos, end),
          code: rest.length > MaximumExpressionCharacters
            ? "expression_too_large"
            : "incomplete_expression",
          dependencies: [],
        }
        : evaluateTemplate(rest.slice(0, closing), template.context);
      const token = state.push("maieutics_value", "", 0);
      token.content = result.text;
      token.meta = { templateResult: result, templateIndex: template.expressions.length };
      template.expressions.push(result);
    }
    state.pos = end;
    return true;
  });
  // Heading/title plugins deliberately consume text tokens. Restore after text_join, before attrs
  // and anchor core rules, so headings and TOCs read the same captured interpolation result.
  md.core.ruler.after("text_join", "maieutics_interpolation_titles", (state) => {
    for (const token of state.tokens) {
      for (const child of token.children ?? []) {
        if (child.type === "maieutics_value") child.type = "text";
      }
    }
  });
  const text = md.renderer.rules.text;
  md.renderer.rules.text = (tokens, index, options, env, renderer) => {
    const token = tokens[index];
    const result = token.meta?.templateResult as TemplateResult | undefined;
    if (!result) {
      return text
        ? text(tokens, index, options, env, renderer)
        : md.utils.escapeHtml(token.content);
    }
    return `<span class="template-value" data-template-state="${result.state}" data-template-index="${token.meta.templateIndex}"${
      result.state === "pending" ? ' aria-busy="true"' : ""
    }>${md.utils.escapeHtml(token.content)}</span>`;
  };
}
