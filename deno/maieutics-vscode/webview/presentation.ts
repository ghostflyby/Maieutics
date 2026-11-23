/// <reference lib="dom" />
import type { ConversationBlockSurface, ConversationEntry, ConversationLocalizer, TextContent } from "../../maieutics-ui/mod.ts";
// @deno-types="../../maieutics-output-client/types.d.ts"
import { createActorFormRenderer, createOutputRenderer } from "../media/outputClient.js";

const templateMime = "application/vnd.maieutics.template+json";
const vueMime = "application/vnd.maieutics.vue+json";
const formMime = "application/vnd.maieutics.form+json";

/** Reuse the exact browser renderer shipped for Notebook output, with one surface per virtual row. */
export function createConversationSurfaces(
  post: (message: unknown) => void,
  stylesheetUrl: string,
  localize: ConversationLocalizer,
) {
  const renderer = createOutputRenderer({ sendMessage: post, stylesheetUrl, localize });
  const forms = createActorFormRenderer({ postMessage: post, localize });
  return {
    mountBlock(element: HTMLElement): ConversationBlockSurface {
      let id: string | undefined;
      let family: "output" | "form" | undefined;
      let closed = false;
      const clear = () => {
        if (id) {
          renderer.disposeOutput(id);
          forms.disposeOutput(id);
        }
        element.replaceChildren();
      };
      return {
        async render(entry: ConversationEntry, content: TextContent, signal: AbortSignal) {
          if (closed) return;
          signal.throwIfAborted();
          const presentation = content.presentation;
          let kind: "turn" | "assistant" | "vue" | "template" = presentation?.version === 1 && presentation.kind === "assistant" ? "assistant" : "turn";
          let value: unknown = { text: content.text, streaming: entry.state === "open" };
          let nextFamily: "output" | "form" = "output";
          if (presentation?.version === 1 && presentation.kind === "tool") {
            value = { nodeId: entry.id, tools: [presentation.value] };
          } else if (presentation?.version === 1 && presentation.kind === "mime") {
            const bundle = record(presentation.value);
            if (record(bundle[formMime]).version === 1) {
              nextFamily = "form";
              value = bundle[formMime];
            } else if (record(bundle[vueMime]).version === 1) {
              kind = "vue";
              value = bundle[vueMime];
            } else if (record(bundle[templateMime]).version === 1) {
              kind = "template";
              value = bundle[templateMime];
            } else {
              // HTML remains inert source; only the authenticated compiler produces executable DOM.
              const text = bundle["text/markdown"] ?? bundle["text/plain"];
              value = { text: typeof text === "string" ? text : "```json\n" + JSON.stringify(bundle["application/json"] ?? bundle, null, 2) + "\n```" };
            }
          }
          if (id !== entry.id || family !== nextFamily) clear();
          id = entry.id;
          family = nextFamily;
          if (family === "form") forms.render(id, value, element);
          else await renderer.render({ id, kind, value }, element, signal);
        },
        dispose() {
          if (closed) return;
          closed = true;
          clear();
        },
      };
    },
    async receive(message: unknown): Promise<void> {
      forms.receive(message);
      await renderer.receive(message);
    },
    async dispose(): Promise<void> {
      renderer.dispose();
      await forms.dispose();
    },
  };
}
function record(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}
