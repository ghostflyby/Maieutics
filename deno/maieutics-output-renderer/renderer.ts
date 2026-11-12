// @deno-types="@types/markdown-it"
import MarkdownIt from "markdown-it";
import { createHighlighterCore } from "shiki/core";
import { createJavaScriptRegexEngine } from "shiki/engine/javascript";
import typescript from "@shikijs/langs/typescript";
import javascript from "@shikijs/langs/javascript";
import json from "@shikijs/langs/json";
import python from "@shikijs/langs/python";
import bash from "@shikijs/langs/bash";
import csharp from "@shikijs/langs/csharp";
import cpp from "@shikijs/langs/cpp";
import c from "@shikijs/langs/c";
import java from "@shikijs/langs/java";
import go from "@shikijs/langs/go";
import rust from "@shikijs/langs/rust";
import sql from "@shikijs/langs/sql";
import yamlLanguage from "@shikijs/langs/yaml";
import toml from "@shikijs/langs/toml";
import html from "@shikijs/langs/html";
import css from "@shikijs/langs/css";
import markdown from "@shikijs/langs/markdown";
import diff from "@shikijs/langs/diff";
import dockerfile from "@shikijs/langs/dockerfile";
import xml from "@shikijs/langs/xml";
import powershell from "@shikijs/langs/powershell";
import ruby from "@shikijs/langs/ruby";
import swift from "@shikijs/langs/swift";
import kotlin from "@shikijs/langs/kotlin";
import githubLight from "@shikijs/themes/github-light";
import githubDark from "@shikijs/themes/github-dark";
import { createSSRApp, h } from "vue";
import yaml from "js-yaml";
import { frontmatterPlugin } from "@mdit-vue/plugin-frontmatter";
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
import { installDiagnostics, type RenderDiagnostic } from "./diagnostics.ts";
import { installMath } from "./math.ts";
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
  readonly renderedSource?: string;
  readonly diagnostics?: readonly RenderDiagnostic[];
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
  renderAssistant(
    documentId: string,
    source: string,
    signal?: AbortSignal,
    progressive?: { compileOnly: boolean },
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
    readonly code:
      | "invalid_document_id"
      | "source_too_large"
      | "output_too_large"
      | "disposed"
      | "invalid_frontmatter",
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
    langs: [
      typescript,
      javascript,
      json,
      python,
      bash,
      csharp,
      cpp,
      c,
      java,
      go,
      rust,
      sql,
      yamlLanguage,
      toml,
      html,
      css,
      markdown,
      diff,
      dockerfile,
      xml,
      powershell,
      ruby,
      swift,
      kotlin,
    ],
    engine: createJavaScriptRegexEngine(),
  });
  try {
    const markdown = (html: boolean) => {
      const md = new MarkdownIt({
        html,
        linkify: true,
      });
      // Reuse upstream framing with a non-executable, JSON-shaped YAML schema.
      const parseMetadata = (source: string, json = false) => {
        if (source.length > 16_000) throw new OutputRenderingError("invalid_frontmatter");
        try {
          const value =
            (json ? JSON.parse(source) : yaml.safeLoad(source, { schema: yaml.JSON_SCHEMA })) ?? {};
          if (typeof value !== "object" || value === null || Array.isArray(value)) {
            throw new Error();
          }
          return captureTemplate({
            version: 1,
            bindings: [{ name: "$frontmatter", state: "ready", value }],
          }).bindings[0].value;
        } catch {
          throw new OutputRenderingError("invalid_frontmatter");
        }
      };
      const rejectMetadata = () => {
        throw new OutputRenderingError("invalid_frontmatter");
      };
      const withoutMetadata = md.render.bind(md);
      md.use(frontmatterPlugin, {
        renderExcerpt: false,
        grayMatterOptions: {
          engines: {
            yaml: (source: string) => parseMetadata(source),
            yml: (source: string) => parseMetadata(source),
            json: (source: string) => parseMetadata(source, true),
            js: rejectMetadata,
            javascript: rejectMetadata,
            coffee: rejectMetadata,
            coffeescript: rejectMetadata,
          },
        },
      });
      const withMetadata = md.render.bind(md);
      md.render = (source, env = {}) => {
        env.originalSource = source;
        const opening = /^(?:\uFEFF)?---(?:[ \t]*([A-Za-z][\w-]*))?[ \t]*(?:\r?\n|$)/.exec(source);
        if (!opening) return withoutMetadata(source, env);
        if (opening[1] && !["yaml", "yml", "json"].includes(opening[1])) {
          throw new OutputRenderingError("invalid_frontmatter");
        }
        if (!/^(?:---|\.\.\.)[ \t]*\r?$/m.test(source.slice(opening[0].length))) {
          env.unsealedFrontmatter = true;
          return withoutMetadata(source, env);
        }
        return withMetadata(source, env);
      };
      installCodeFences(md, highlighter);
      installCodeGroups(md);
      installMath(md);
      installDiagnostics(md);
      const validateLink = md.validateLink;
      md.validateLink = (url) => {
        const scheme = /^([a-z][a-z\d+.-]*):/i.exec(url)?.[1].toLowerCase();
        return validateLink(url) && (scheme === undefined ||
          ["http", "https", "mailto", "workspace", "task", "skill", "mcp"].includes(scheme));
      };
      md.linkify.set({ fuzzyLink: false });
      {
        md.core.ruler.before("block", "maieutics-frontmatter", (state) => {
          state.env.vueMarkdown = html;
          const binding = {
            name: "$frontmatter",
            ...(state.env.unsealedFrontmatter
              ? { state: "pending" as const }
              : { state: "ready" as const, value: state.env.frontmatter ?? {} }),
          };
          if (!state.env.unsealedFrontmatter) {
            state.env.frontmatterTemplate = {
              context: { version: 1, bindings: [binding], sourceSealed: true },
              expressions: [],
            };
          }
          if (state.env.template) {
            const context = state.env.template.context;
            state.env.template.context = {
              ...context,
              bindings: [
                ...context.bindings.filter((item: { name: string }) =>
                  item.name !== "$frontmatter"
                ),
                binding,
              ],
            };
          }
        });
      }
      installInterpolation(md);
      installContentPlugins(md);

      if (html) {
        md.use(componentPlugin).use(sfcPlugin);
        md.core.ruler.after("block", "maieutics-script-source", (state) => {
          const script = state.tokens.find((token) =>
            token.type === "html_block" &&
            token.level === 0 && /^\s*<script(?:\s|>)/i.test(token.content)
          );
          state.env.authoredScript = script !== undefined;
          const original = state.env.originalSource ?? state.src;
          state.env.scriptLine =
            original.slice(0, original.length - state.src.length).split("\n").length +
            (script?.map?.[0] ?? 0);
        });
        const htmlBlock = md.renderer.rules.html_block;
        md.renderer.rules.html_block = (tokens, index, options, env, renderer) => {
          const token = tokens[index];
          if (token.level > 0 && /^\s*<(?:script|style)(?:\s|>)/i.test(token.content)) {
            return `<pre v-pre>${md.utils.escapeHtml(token.content)}</pre>`;
          }
          return htmlBlock?.(tokens, index, options, env, renderer) ?? "";
        };
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
        diagnostics?: RenderDiagnostic[];
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
        diagnostics: env.diagnostics,
        headings: scopeHeadings(env.headers ?? []),
        ...(template ? { expressions: template.expressions } : {}),
      };
    };
    const prepareVueMarkdown = (documentId: string, source: string) => {
      const env: {
        documentId: string;
        authoredScript?: boolean;
        scriptLine?: number;
        diagnostics?: RenderDiagnostic[];
        unsealedFrontmatter?: boolean;
        frontmatter?: Record<string, unknown>;
        sfcBlocks?: { scripts: { content: string }[]; styles: { content: string }[] };
      } = { documentId };
      const body = vueMd.render(source, env);
      if (body.length > MaximumHtmlCharacters) throw new OutputRenderingError("output_too_large");
      return {
        diagnostics: env.diagnostics,
        frontmatter: env.frontmatter ?? {},
        scripted: env.authoredScript === true,
        scriptLine: env.scriptLine ?? 1,
        unsealedFrontmatter: env.unsealedFrontmatter === true,
        source: `<template><div class="maieutics-output vp-doc">${body}</div></template>\n${
          env.sfcBlocks?.scripts.map((block) => block.content).join("\n") ?? ""
        }\n${env.sfcBlocks?.styles.map((block) => block.content).join("\n") ?? ""}`,
      };
    };
    const compileLocated = (
      documentId: string,
      source: string,
      frontmatter?: Record<string, unknown>,
      progressive = false,
      line = 1,
    ) => {
      try {
        return compileVueComponent(documentId, source, frontmatter, progressive);
      } catch (error) {
        if (error instanceof VueComponentError) {
          throw new VueComponentError(error.code, [{
            code: "component_compile_failed",
            line,
            column: 1,
          }]);
        }
        throw error;
      }
    };
    const renderVue = async (
      documentId: string,
      source: string,
      format: "markdown" | "sfc",
      signal?: AbortSignal,
      prepared?: string,
      frontmatter?: Record<string, unknown>,
      diagnostics?: RenderDiagnostic[],
      scriptLine?: number,
    ): Promise<RenderedOutput> => {
      signal?.throwIfAborted();
      if (disposed) throw new OutputRenderingError("disposed");
      if (!/^[a-z][a-z\d_-]{0,63}$/i.test(documentId) || source.length > MaximumSourceCharacters) {
        throw new VueComponentError("invalid_component");
      }
      const markdown = format === "markdown" && prepared === undefined
        ? prepareVueMarkdown(documentId, source)
        : undefined;
      const sfc = prepared ?? markdown?.source ?? source;
      const component = compileLocated(
        documentId,
        sfc,
        frontmatter ?? markdown?.frontmatter,
        false,
        scriptLine ?? markdown?.scriptLine ?? 1,
      );
      const active = signal
        ? AbortSignal.any([signal, componentLifetime.signal])
        : componentLifetime.signal;
      const html = await renderVueComponent(component, active);
      signal?.throwIfAborted();
      if (disposed) throw new OutputRenderingError("disposed");
      return {
        documentId,
        html,
        headings: [],
        vue: component,
        diagnostics: diagnostics ?? markdown?.diagnostics,
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
      renderVue,
      async renderAssistant(documentId, source, signal, progressive) {
        if (progressive) {
          signal?.throwIfAborted();
          if (disposed) throw new OutputRenderingError("disposed");
          if (
            !/^[a-z][a-z\d_-]{0,63}$/i.test(documentId) || source.length > MaximumSourceCharacters
          ) throw new VueComponentError("invalid_component");
          if (!/<script(?:\s|>)/i.test(source)) return await render(documentId, source, signal);
          let candidate = source;
          for (let attempt = 0; attempt < 16; attempt++) {
            let component: CompiledVueComponent | undefined;
            let diagnostics: RenderDiagnostic[] | undefined;
            try {
              const prepared = prepareVueMarkdown(documentId, candidate);
              diagnostics = prepared.diagnostics;
              if (!prepared.scripted) return await render(documentId, source, signal);
              if (prepared.unsealedFrontmatter) {
                throw new VueComponentError("invalid_component");
              }
              // One closed setup module owns all later template bindings.
              component = compileLocated(
                documentId,
                prepared.source,
                prepared.frontmatter,
                true,
                prepared.scriptLine,
              );
            } catch (error) {
              if (!(error instanceof VueComponentError)) throw error;
              const boundary = candidate.lastIndexOf("\n\n");
              if (boundary < 0) throw error;
              candidate = candidate.slice(0, boundary);
              continue;
            }
            const html = progressive.compileOnly ? "" : await renderVueComponent(
              component,
              signal
                ? AbortSignal.any([signal, componentLifetime.signal])
                : componentLifetime.signal,
            );
            return {
              documentId,
              html,
              headings: [],
              renderedSource: candidate,
              diagnostics,
              vue: progressive.compileOnly ? { ...component, code: "" } : component,
            };
          }
          throw new VueComponentError("invalid_component");
        }
        signal?.throwIfAborted();
        if (disposed) throw new OutputRenderingError("disposed");
        if (!/^[a-z][a-z\d_-]{0,63}$/i.test(documentId)) {
          throw new OutputRenderingError("invalid_document_id");
        }
        if (source.length > MaximumSourceCharacters) {
          throw new OutputRenderingError("source_too_large");
        }
        // The public SFC/component plugins see source after frontmatter removal. Their parser
        // distinguishes top-level scripts from fenced, escaped, inline and quoted examples.
        if (!/<script(?:\s|>)/i.test(source)) return await render(documentId, source, signal);
        const prepared = prepareVueMarkdown(documentId, source);
        if (prepared.unsealedFrontmatter) throw new OutputRenderingError("invalid_frontmatter");
        return prepared.scripted
          ? await renderVue(
            documentId,
            source,
            "markdown",
            signal,
            prepared.source,
            prepared.frontmatter,
            prepared.diagnostics,
            prepared.scriptLine,
          )
          : await render(documentId, source, signal);
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
