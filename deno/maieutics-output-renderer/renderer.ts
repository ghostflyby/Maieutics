// @deno-types="@types/markdown-it"
import MarkdownIt from "markdown-it";
import { createHighlighterCore } from "shiki/core";
import { createJavaScriptRegexEngine } from "shiki/engine/javascript";
import typescript from "@shikijs/langs/typescript";
import javascript from "@shikijs/langs/javascript";
import json from "@shikijs/langs/json";
import githubLight from "@shikijs/themes/github-light";
import githubDark from "@shikijs/themes/github-dark";
import { createSSRApp, h } from "vue";
import { renderToString } from "vue/server-renderer";
import { installCodeFences } from "./codeFences.ts";
import { installCodeGroups } from "./codeGroups.ts";
import { installContentPlugins } from "./contentPlugins.ts";

/** A complete derived presentation, independent of the frontend transport. */
export interface RenderedOutput {
  readonly documentId: string;
  readonly html: string;
  readonly headings: readonly RenderedHeading[];
}

/** Heading metadata collected by the same plugin family as VitePress. */
export interface RenderedHeading {
  readonly level: number;
  readonly title: string;
  readonly slug: string;
  readonly children: readonly RenderedHeading[];
}

/** An explicitly owned highlighter and Markdown pipeline. */
export interface OutputRenderer extends Disposable {
  render(documentId: string, source: string, signal?: AbortSignal): Promise<RenderedOutput>;
}

/** Recoverable failure at the content rendering boundary. */
export class OutputRenderingError extends Error {
  constructor(
    readonly code: "invalid_document_id" | "source_too_large" | "output_too_large" | "disposed",
  ) {
    super(`Output rendering failed: ${code}.`);
    this.name = "OutputRenderingError";
  }
}

/** Source is measured in UTF-16 code units, matching the frontend text limits. */
export const MaximumSourceCharacters = 200_000;
/** Generated HTML is bounded independently of source and syntax highlighting expansion. */
export const MaximumHtmlCharacters = 4_000_000;

/**
 * Creates an offline, passive Markdown renderer from VitePress's underlying ecosystem.
 * Raw HTML and Vue expressions remain text; no document code, file includes, or imports execute.
 * Each renderer owns its parser and highlighter and must be disposed by its caller.
 */
export async function createOutputRenderer(): Promise<OutputRenderer> {
  const highlighter = await createHighlighterCore({
    themes: [githubLight, githubDark],
    langs: [typescript, javascript, json],
    engine: createJavaScriptRegexEngine(),
  });
  try {
    const md = new MarkdownIt({
      html: false,
      linkify: true,
    });
    installCodeFences(md, highlighter);
    installCodeGroups(md);
    const validateLink = md.validateLink;
    md.validateLink = (url) => {
      const scheme = /^([a-z][a-z\d+.-]*):/i.exec(url)?.[1].toLowerCase();
      return validateLink(url) && (scheme === undefined ||
        ["http", "https", "mailto", "workspace", "task", "skill", "mcp"].includes(scheme));
    };
    md.linkify.set({ fuzzyLink: false });
    installContentPlugins(md);

    let disposed = false;
    return {
      async render(documentId, source, signal) {
        signal?.throwIfAborted();
        if (disposed) throw new OutputRenderingError("disposed");
        if (!/^[a-z][a-z\d_-]{0,63}$/i.test(documentId)) {
          throw new OutputRenderingError("invalid_document_id");
        }
        if (source.length > MaximumSourceCharacters) {
          throw new OutputRenderingError("source_too_large");
        }
        // All parser state is per call. A later link definition reparses earlier references.
        const env: { documentId: string; headers?: RenderedHeading[] } = { documentId };
        const body = md.render(source, env);
        if (body.length > MaximumHtmlCharacters) throw new OutputRenderingError("output_too_large");
        const html = await renderToString(createSSRApp({
          render: () =>
            h("div", {
              class: "maieutics-output vp-doc",
              "data-document": documentId,
              innerHTML: body,
            }),
        }));
        signal?.throwIfAborted();
        if (disposed) throw new OutputRenderingError("disposed");
        if (html.length > MaximumHtmlCharacters) throw new OutputRenderingError("output_too_large");
        const scopeHeadings = (headings: readonly RenderedHeading[]): RenderedHeading[] =>
          headings.map((heading) => ({
            level: heading.level,
            title: heading.title,
            slug: heading.slug,
            children: scopeHeadings(heading.children),
          }));
        return { documentId, html, headings: scopeHeadings(env.headers ?? []) };
      },
      [Symbol.dispose]() {
        if (disposed) return;
        disposed = true;
        highlighter.dispose();
      },
    };
  } catch (error) {
    highlighter.dispose();
    throw error;
  }
}
