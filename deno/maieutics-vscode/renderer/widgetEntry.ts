/**
 * Entry for the jupyter.widget renderer script
 * (`application/vnd.jupyter.widget-view+json`). Bundled by
 * `deno task build:media` into `media/widgetRenderer.js`; the built file is
 * the single-file renderer entrypoint VS Code loads.
 */

import { widgetFamily, WIDGET_CSS } from "./families/widgetFamily.tsx";
import { registerViewFamily } from "./registry.ts";
import { createRendererScript } from "./script.ts";

registerViewFamily("jupyter.widget", widgetFamily);

export function activate() {
  return createRendererScript({
    source: "maieutics-widget",
    css: WIDGET_CSS,
    resolve(value) {
      const record = value as Record<string, unknown> | null;
      const modelId =
        typeof record === "object" && record !== null && typeof record.model_id === "string"
          ? record.model_id
          : "";
      return modelId.length > 0 ? { family: "jupyter.widget", modelId } : undefined;
    },
  });
}
