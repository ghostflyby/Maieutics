/// <reference lib="dom" />
// @deno-types="npm:@types/markdown-it@14.1.2"
import MarkdownIt from "markdown-it";
import { createRenderer } from "./renderer.js";

export { renderSubagents, renderTurn } from "./renderer.js";
export { mountVueIsland, vueUpdatesComplete } from "./vueIslands.ts";
export { createFormRenderer } from "./forms.ts";
export { createActorFormRenderer } from "./actorForms.ts";
export type { FormMessage, FormRenderer } from "./forms.ts";
export { createPluginHttpClient, useFetch } from "./pluginFetch.ts";
export type { PluginFetch, PluginHttpClient, PluginHttpOptions } from "./types.d.ts";

import type { MarkdownContext, OutputRenderer, OutputRendererOptions } from "./types.d.ts";
export type {
  ActorFormRenderer,
  ActorFormRendererOptions,
  MarkdownContext,
  Output,
  OutputLocalizer,
  OutputMessage,
  OutputRenderer,
  OutputRendererOptions,
  VueIslandDisposer,
} from "./types.d.ts";

const markdown = new MarkdownIt({ html: false });
const validateLink = markdown.validateLink;
markdown.validateLink = (href) => !/^command:/i.test(href) && validateLink(href);

function renderMarkdown(
  text: string,
  element: HTMLElement,
  context: MarkdownContext,
  stylesheetUrl?: string,
): void {
  context.signal.throwIfAborted();
  const root = element.shadowRoot ?? element.attachShadow({ mode: "open" });
  root.innerHTML = markdown.render(text);
  if (stylesheetUrl) {
    const stylesheet = document.createElement("link");
    stylesheet.rel = "stylesheet";
    stylesheet.href = stylesheetUrl;
    root.append(stylesheet);
  }
}

/** Create a standalone browser renderer; importing this module does not mount DOM or open I/O. */
export function createOutputRenderer(options: OutputRendererOptions): OutputRenderer {
  const lifetime = new AbortController();
  const renderer = createRenderer({
    sendMessage: options.sendMessage,
    renderMarkdown: options.renderMarkdown ??
      ((text: string, element: HTMLElement, context: MarkdownContext) =>
        renderMarkdown(text, element, context, options.stylesheetUrl)),
    stylesheetUrl: options.stylesheetUrl,
    localize: options.localize,
    onAction: options.onAction,
  });
  return {
    async render(output, element, signal) {
      lifetime.signal.throwIfAborted();
      if (!["turn", "assistant", "vue", "template"].includes(output.kind)) {
        throw new Error("invalid_output_kind");
      }
      await renderer.renderOutput(
        output,
        element,
        signal ? AbortSignal.any([signal, lifetime.signal]) : lifetime.signal,
      );
    },
    async receive(message) {
      if (!lifetime.signal.aborted) await renderer.receive(message);
    },
    disposeOutput(id) {
      renderer.disposeOutput(id);
    },
    dispose() {
      if (lifetime.signal.aborted) return;
      lifetime.abort();
      renderer.disposeOutput();
    },
  };
}
