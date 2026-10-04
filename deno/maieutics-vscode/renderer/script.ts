/**
 * Shared plumbing for Maieutics notebook renderer scripts (custom-UI
 * framework): the per-webview model-state store, the host-message listener,
 * view tracking, and Preact mounting. A renderer entry registers its view
 * families and binds this to the notebook renderer API.
 *
 * DOM-dependent by design (it runs inside the notebook's renderer iframe);
 * the pure pieces (registry, family components) are unit-tested instead.
 *
 * Wire with the host (renderer messaging, per renderer script):
 *   renderer -> host: { source, type: "mount",  modelId }
 *   renderer -> host: { source, type: "update", modelId, state }
 *   host -> renderer: { source, type: "state",  modelId, state }
 */

import { h, render } from "preact";
import { FALLBACK_FAMILY, viewFamily, type ViewProps } from "./registry.ts";

export interface RendererScriptOptions {
  /** Envelope source this renderer speaks; host->renderer state frames carry it. */
  readonly source: string;
  /** Stylesheet injected once into the renderer document. */
  readonly css: string;
  /** Extract the family + model id from an output item's JSON value;
   * returning undefined skips the output (malformed announcement). */
  readonly resolve: (
    value: unknown,
  ) => { family: string; modelId: string; initialState?: Record<string, unknown> } | undefined;
}

/** The notebook renderer API surface this script implements. */
export interface RendererScriptHost {
  renderOutputItem(
    output: { id?: string; item: { json(): unknown } },
    element: HTMLElement,
  ): void;
  disposeOutputItem(outputId: string): void;
}

interface ViewEntry {
  readonly family: string;
  readonly modelId: string;
  /** Embedded announcement state — the pre-live / snapshot view. */
  readonly initialState: Record<string, unknown> | undefined;
  readonly container: HTMLElement;
  deps: Set<string>;
}

type VsCodeApi = { postMessage(message: unknown): void };

export function createRendererScript(options: RendererScriptOptions): RendererScriptHost {
  let styled = false;
  // Model states are shared across outputs of the same renderer webview: a
  // state frame for a nested model must reach every view rendering it.
  const models = new Map<string, Record<string, unknown>>();
  const views = new Map<string, ViewEntry>();

  const acquire = (globalThis as { acquireVsCodeApi?: () => VsCodeApi })
    .acquireVsCodeApi;
  const api: VsCodeApi | null = typeof acquire === "function" ? acquire() : null;
  const post = (message: unknown): void => {
    api?.postMessage(message);
  };

  globalThis.addEventListener("message", (event: MessageEvent) => {
    const message = event.data;
    if (
      typeof message !== "object" || message === null ||
      (message as Record<string, unknown>).source !== options.source ||
      (message as Record<string, unknown>).type !== "state"
    ) {
      return;
    }
    const modelId = (message as Record<string, unknown>).modelId;
    if (typeof modelId !== "string" || modelId.length === 0) return;
    const state = (message as Record<string, unknown>).state;
    models.set(modelId, isRecord(state) ? state : {});
    // A view owns a dependency set (its model plus nested children):
    // kernel-driven child updates must re-render the parent view.
    for (const view of views.values()) {
      if (!view.deps.has(modelId)) continue;
      paint(view);
    }
  });

  function paint(view: ViewEntry): void {
    const module = viewFamily(view.family) ?? viewFamily(FALLBACK_FAMILY);
    if (module === undefined) return;
    // Live comm state wins; the announcement's embedded state renders before
    // (or instead of) it — the snapshot-restore posture (ADR 0038 §5).
    const live = models.get(view.modelId);
    const props: ViewProps = {
      modelId: view.modelId,
      state: live ?? view.initialState ?? {},
      models,
      post,
      hasState: live !== undefined,
    };
    render(h(module.component, props), view.container);
    // Defensive union: the root is always a dep regardless of what a family
    // returns, so a family that forgets it cannot orphan its own view.
    view.deps = new Set([
      view.modelId,
      ...(module.collectDeps?.(view.modelId, models) ?? []),
    ]);
  }

  return {
    renderOutputItem(output, element) {
      if (!styled) {
        const style = document.createElement("style");
        style.textContent = options.css;
        document.head.append(style);
        styled = true;
      }

      const resolved = options.resolve(output.item.json());
      if (resolved === undefined) return;
      const container = document.createElement("div");
      element.append(container);
      const key = output.id ?? resolved.modelId;
      const view: ViewEntry = {
        family: resolved.family,
        modelId: resolved.modelId,
        initialState: resolved.initialState,
        container,
        deps: new Set([resolved.modelId]),
      };
      views.set(key, view);

      if (api === null) {
        render(
          h(
            "div",
            { class: "maieutics-widget" },
            "The widget renderer requires the VS Code messaging API.",
          ),
          container,
        );
        return;
      }
      paint(view);
      post({ source: options.source, type: "mount", modelId: resolved.modelId });
    },

    disposeOutputItem(outputId) {
      const view = views.get(outputId);
      if (view === undefined) return;
      views.delete(outputId);
      render(null, view.container);
    },
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
