// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";
// @deno-types="@types/markdown-it-container"
import container from "markdown-it-container";
import type { CodeTab } from "./codeFences.ts";

/** Native radio groups work with shared SSR HTML/CSS, without document scripts or hydration. */
export function installCodeGroups(md: MarkdownIt): void {
  container(md as unknown as Parameters<typeof container>[0], "code-group", {
    render(tokens, index) {
      const token = tokens[index];
      const count = token.meta?.codeGroupSize as number | undefined;
      if (token.nesting === -1) return count === undefined ? "</div>\n" : "</fieldset>\n";
      return count === undefined
        ? '<div class="vp-code-group-fallback">\n'
        : `<fieldset class="vp-code-group" style="--code-tabs:${count}"><legend>Code examples</legend>\n`;
    },
  });
  md.core.ruler.after("block", "maieutics_code_groups", (state) => {
    let ordinal = 0;
    for (let index = 0; index < state.tokens.length; index++) {
      const open = state.tokens[index];
      if (open.type !== "container_code-group_open") continue;
      let end = index + 1;
      while (
        end < state.tokens.length &&
        !(state.tokens[end].type === "container_code-group_close" &&
          state.tokens[end].level === open.level)
      ) end++;
      const members = state.tokens.slice(index + 1, end);
      // Preserve all content for empty, nested, or mixed groups instead of hiding non-code nodes.
      if (
        end === state.tokens.length || members.length === 0 ||
        !members.every((member) => member.type === "fence")
      ) continue;
      const group = `${state.env.documentId}-code-group-${ordinal++}`;
      open.meta = { ...open.meta, codeGroupSize: members.length };
      const close = state.tokens[end];
      close.meta = { ...close.meta, codeGroupSize: members.length };
      for (const [index, member] of members.entries()) {
        const codeTab: CodeTab = { group, index };
        member.meta = { ...member.meta, codeTab };
      }
    }
  });
}
