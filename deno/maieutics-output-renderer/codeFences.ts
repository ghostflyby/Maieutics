// @deno-types="@types/markdown-it"
import type MarkdownIt from "markdown-it";
import type { HighlighterCore, ShikiTransformer } from "shiki/core";
import {
  transformerNotationDiff,
  transformerNotationErrorLevel,
  transformerNotationFocus,
  transformerNotationHighlight,
} from "@shikijs/transformers";

interface FenceInfo {
  language: string;
  title: string | undefined;
  numberStart: number | undefined;
  ranges: readonly (readonly [number, number])[];
}

/** Presentation-only fence metadata; never an attribute or expression evaluation surface. */
export function readFenceInfo(info: string): FenceInfo {
  const title = /\[([^\]\r\n]*)\]/.exec(info);
  const metadata = title === null ? info : info.replace(title[0], "");
  const language = /^\s*([\w+-]+)/.exec(metadata)?.[1].toLowerCase() ?? "text";
  const numbering = /:(no-)?line-numbers(?:=(\d+))?(?=\s|\{|$)/.exec(metadata);
  const requestedStart = Number(numbering?.[2] ?? 1);
  const numberStart = numbering === null || numbering[1] !== undefined
    ? undefined
    : Number.isSafeInteger(requestedStart) && requestedStart > 0 && requestedStart <= 1_000_000_000
    ? requestedStart
    : 1;
  const ranges: [number, number][] = [];
  for (const part of (/\{([\d,\s-]+)\}/.exec(metadata)?.[1] ?? "").split(",")) {
    const match = /^(\d+)(?:\s*-\s*(\d+))?$/.exec(part.trim());
    if (match === null) continue;
    const start = Number(match[1]);
    const end = Number(match[2] ?? match[1]);
    if (Number.isSafeInteger(start) && Number.isSafeInteger(end) && start > 0 && end >= start) {
      ranges.push([start, end]);
    }
  }
  ranges.sort((a, b) => a[0] - b[0]);
  const merged: [number, number][] = [];
  for (const range of ranges) {
    const previous = merged.at(-1);
    if (previous !== undefined && range[0] <= previous[1]) {
      previous[1] = Math.max(previous[1], range[1]);
    } else merged.push(range);
  }
  return { language, title: title?.[1], numberStart, ranges: merged };
}

/** Assigned only to direct fences inside a valid, document-scoped code group. */
export interface CodeTab {
  group: string;
  index: number;
}

function includesLine(ranges: FenceInfo["ranges"], line: number): boolean {
  let low = 0;
  let high = ranges.length - 1;
  while (low <= high) {
    const middle = (low + high) >>> 1;
    const [start, end] = ranges[middle];
    if (line < start) high = middle - 1;
    else if (line > end) low = middle + 1;
    else return true;
  }
  return false;
}

/** Installs VitePress-style passive code presentation with statically loaded Shiki grammars. */
export function installCodeFences(md: MarkdownIt, highlighter: HighlighterCore): void {
  md.renderer.rules.fence = (tokens, index) => {
    const token = tokens[index];
    const info = readFenceInfo(token.info);
    const language = highlighter.getLoadedLanguages().includes(info.language)
      ? info.language
      : "text";
    const lineMetadata: ShikiTransformer = {
      name: "maieutics:fence-metadata",
      line(node, number) {
        // Membership is logarithmic in the number of ranges; never expand author-supplied ranges.
        if (includesLine(info.ranges, number)) this.addClassToHast(node, "highlighted");
      },
      code(node) {
        if (info.numberStart === undefined) return;
        let number = info.numberStart;
        // Notation transformers remove comment-only lines before numbering visible code.
        for (const child of node.children) {
          if (child.type === "element" && child.tagName === "span") {
            child.properties["data-line"] = number++;
          }
        }
      },
    };
    const html = highlighter.codeToHtml(token.content.replace(/\n$/, ""), {
      lang: language,
      themes: { light: "github-light", dark: "github-dark" },
      defaultColor: false,
      transformers: [
        transformerNotationDiff({ matchAlgorithm: "v3" }),
        transformerNotationFocus({
          matchAlgorithm: "v3",
          classActiveLine: "has-focus",
          classActivePre: "has-focused-lines",
        }),
        transformerNotationHighlight({ matchAlgorithm: "v3" }),
        transformerNotationErrorLevel({ matchAlgorithm: "v3" }),
        lineMetadata,
      ],
    });
    const tab = token.meta?.codeTab as CodeTab | undefined;
    const caption = info.title === undefined || tab !== undefined
      ? ""
      : `<div class="code-title">${md.utils.escapeHtml(info.title)}</div>`;
    const block = `<div class="code-block language-${
      md.utils.escapeHtml(info.language)
    }">${caption}<button type="button" class="code-copy" data-code-copy="${
      md.utils.escapeHtml(token.content)
    }" aria-label="复制代码">复制</button>${html}</div>\n`;
    if (tab === undefined) return block;
    const id = `${tab.group}-tab-${tab.index}`;
    const label = md.utils.escapeHtml(info.title || info.language);
    return `<div class="code-tab"><input type="radio" name="${tab.group}" id="${id}" aria-controls="${id}-panel"${
      tab.index === 0 ? " checked" : ""
    }><label id="${id}-label" for="${id}">${label}</label><div class="code-panel" id="${id}-panel" aria-labelledby="${id}-label">${block}</div></div>\n`;
  };
}
