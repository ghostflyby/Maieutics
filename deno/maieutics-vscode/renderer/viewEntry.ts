/**
 * Entry for the native view renderer script
 * (`application/vnd.maieutics.view+json`). Bundled by
 * `deno task build:media` into `media/viewRenderer.js`.
 *
 * The announcement value is `{modelId, viewFamily, version, state}`; the
 * embedded `state` seeds the view before (or instead of) live comm state —
 * the snapshot-restore posture.
 */

import { FORM_CSS, formFamily } from "./families/formFamily.tsx";
import { registerViewFamily } from "./registry.ts";
import { createRendererScript } from "./script.ts";

registerViewFamily("maieutics/form", formFamily);

export function activate() {
  return createRendererScript({
    source: "maieutics-ui",
    css: FORM_CSS,
    resolve(value) {
      const record = value as Record<string, unknown> | null;
      if (typeof record !== "object" || record === null) return undefined;
      const modelId = typeof record.modelId === "string" ? record.modelId : "";
      const viewFamily = typeof record.viewFamily === "string" ? record.viewFamily : "";
      if (modelId.length === 0 || viewFamily.length === 0) return undefined;
      const initialState = isRecord(record.state) ? record.state : undefined;
      const esmSource = typeof record.esmSource === "string" ? record.esmSource : undefined;
      const cssSource = typeof record.cssSource === "string" ? record.cssSource : undefined;
      const bundled = esmSource === undefined ? undefined : { esmSource, ...(cssSource === undefined ? {} : { cssSource }) };
      return { family: viewFamily, modelId, initialState, bundled };
    },
  });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
