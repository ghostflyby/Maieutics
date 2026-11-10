// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";

/** Fixed codes and one-based source coordinates; no compiler messages or source snippets. */
export interface RenderDiagnostic {
  readonly code: "unclosed_code_fence" | "component_compile_failed" | "math_invalid" | "math_limit";
  readonly line: number;
  readonly column: number;
}

/** Parser-derived locations avoid treating fenced examples as executable components. */
export function installDiagnostics(md: MarkdownIt): void {
  md.core.ruler.after("block", "maieutics_diagnostics", (state) => {
    const diagnostics: RenderDiagnostic[] = state.env.diagnostics ??= [];
    const lines = state.src.split("\n");
    const original = state.env.originalSource;
    const offset = typeof original === "string" && original.endsWith(state.src)
      ? original.slice(0, original.length - state.src.length).split("\n").length - 1
      : 0;
    state.env.sourceLineOffset = offset;
    for (const token of state.tokens) {
      if (token.type !== "fence" || !token.map || diagnostics.length >= 32) continue;
      const last = lines[token.map[1] - 1] ?? "";
      const character = token.markup[0];
      const closing = new RegExp(
        `^[ \\t>]*${character === "`" ? "`" : "~"}{${token.markup.length},}[ \\t\\r]*$`,
      );
      if (token.map[1] - token.map[0] > 1 && closing.test(last)) continue;
      const opening = lines[token.map[0]] ?? "";
      diagnostics.push({
        code: "unclosed_code_fence",
        line: offset + token.map[0] + 1,
        column: Math.max(0, opening.indexOf(token.markup)) + 1,
      });
    }
  });
}

/** Append only bounded, fixed diagnostics to one request's environment. */
export function reportDiagnostic(env: Record<string, unknown>, diagnostic: RenderDiagnostic): void {
  const diagnostics = (env.diagnostics ??= []) as RenderDiagnostic[];
  if (diagnostics.length < 32) diagnostics.push(diagnostic);
}
