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
import { componentPlugin } from "@mdit-vue/plugin-component";
import { sfcPlugin } from "@mdit-vue/plugin-sfc";
import {
  type CompiledVueComponent,
  compileVueComponent,
  VueComponentError,
} from "./vueComponent.ts";
import { renderVueComponent } from "./vueSsr.ts";
import { renderToString } from "vue/server-renderer";
import { installCodeFences } from "./codeFences.ts";
import { installCodeGroups } from "./codeGroups.ts";
import { installContentPlugins } from "./contentPlugins.ts";
import { installInterpolation, type TemplateEnvironment } from "./interpolation.ts";
import { captureTemplate, type TemplateContext, type TemplateResult } from "./template.ts";

/** A complete derived presentation, independent of the frontend transport. */
export interface RenderedOutput {
  readonly documentId: string;
  readonly html: string;
  readonly headings: readonly RenderedHeading[];
  readonly expressions?: readonly TemplateResult[];
  readonly vue?: CompiledVueComponent;
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
  renderTemplate(
    documentId: string,
    source: string,
    context: TemplateContext,
    signal?: AbortSignal,
  ): Promise<RenderedOutput>;
  renderVue(
    documentId: string,
    source: string,
    format: "markdown" | "sfc",
    signal?: AbortSignal,
  ): Promise<RenderedOutput>;
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
 * Creates offline rendering primitives from VitePress's underlying ecosystem.
 * Raw HTML remains text; ordinary Markdown leaves expressions literal. The explicit template method
 * interprets only data expressions. Explicit live Vue output compiles complete components and runs
 * SSR in owned permissionless workers. File includes and external imports are unavailable.
 * Each renderer owns its parser and highlighter and must be disposed by its caller.
 */
export async function createOutputRenderer(): Promise<OutputRenderer> {
  const highlighter = await createHighlighterCore({
    themes: [githubLight, githubDark],
    langs: [typescript, javascript, json],
    engine: createJavaScriptRegexEngine(),
  });
  try {
    const markdown = (html: boolean) => {
      const md = new MarkdownIt({
        html,
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
      installInterpolation(md);
      installContentPlugins(md);

      if (html) {
        md.use(componentPlugin).use(sfcPlugin);
        const fence = md.renderer.rules.fence;
        md.renderer.rules.fence = (...args) => `<div v-pre>${fence?.(...args) ?? ""}</div>`;
        const inline = md.renderer.rules.code_inline;
        md.renderer.rules.code_inline = (tokens, index, options, env, renderer) =>
          `<span v-pre>${inline?.(tokens, index, options, env, renderer) ?? ""}</span>`;
      }
      return md;
    };
    const md = markdown(false);
    const vueMd = markdown(true);

    let disposed = false;
    const componentLifetime = new AbortController();
    const render = async (
      documentId: string,
      source: string,
      signal?: AbortSignal,
      template?: TemplateEnvironment,
    ): Promise<RenderedOutput> => {
      signal?.throwIfAborted();
      if (disposed) throw new OutputRenderingError("disposed");
      if (!/^[a-z][a-z\d_-]{0,63}$/i.test(documentId)) {
        throw new OutputRenderingError("invalid_document_id");
      }
      if (source.length > MaximumSourceCharacters) {
        throw new OutputRenderingError("source_too_large");
      }
      // All parser state is per call. A later link definition reparses earlier references.
      const env: {
        documentId: string;
        headers?: RenderedHeading[];
        template?: TemplateEnvironment;
      } = { documentId, template };
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
      return {
        documentId,
        html,
        headings: scopeHeadings(env.headers ?? []),
        ...(template ? { expressions: template.expressions } : {}),
      };
    };
    return {
      render,
      async renderTemplate(documentId, source, context, signal) {
        return await render(documentId, source, signal, {
          context: captureTemplate(context),
          expressions: [],
        });
      },
      async renderVue(documentId, source, format, signal) {
        signal?.throwIfAborted();
        if (disposed) throw new OutputRenderingError("disposed");
        if (
          !/^[a-z][a-z\d_-]{0,63}$/i.test(documentId) || source.length > MaximumSourceCharacters
        ) throw new VueComponentError("invalid_component");
        let sfc = source;
        if (format === "markdown") {
          const env: {
            documentId: string;
            sfcBlocks?: { scripts: { content: string }[]; styles: { content: string }[] };
          } = { documentId };
          const body = vueMd.render(source, env);
          if (body.length > MaximumHtmlCharacters) {
            throw new OutputRenderingError("output_too_large");
          }
          sfc = `<template><div class="maieutics-output vp-doc">${body}</div></template>\n${
            env.sfcBlocks?.scripts.map((block) => block.content).join("\n") ?? ""
          }\n${env.sfcBlocks?.styles.map((block) => block.content).join("\n") ?? ""}`;
        }
        const component = compileVueComponent(documentId, sfc);
        const active = signal
          ? AbortSignal.any([signal, componentLifetime.signal])
          : componentLifetime.signal;
        const html = await renderVueComponent(component, active);
        signal?.throwIfAborted();
        if (disposed) throw new OutputRenderingError("disposed");
        return { documentId, html, headings: [], vue: component };
      },
      [Symbol.dispose]() {
        if (disposed) return;
        disposed = true;
        componentLifetime.abort();
        highlighter.dispose();
      },
    };
  } catch (error) {
    highlighter.dispose();
    throw error;
  }
}
