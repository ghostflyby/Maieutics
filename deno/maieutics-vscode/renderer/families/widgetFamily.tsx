/**
 * jupyter.widget family: the classic ipywidgets controls, ported one-to-one
 * from the hand-written media/widgetRenderer.js (custom-UI framework
 * stage 0). Behavior contract is parity with that script: the same control
 * dispatch (kindOf, including its quirks — TextModel intentionally falls to
 * the state-JSON fallback today), the same update envelopes
 * ({source:"maieutics-widget", type:"update", modelId, state}), unknown
 * models degrade to live state JSON. Components are pure functions of
 * (state, models, post); DOM events post updates, kernel state re-renders.
 */

import type { FunctionalComponent, JSX, VNode } from "preact";
import type { ViewFamilyModule, ViewProps } from "../registry.ts";

/** Styles from the hand-written renderer, verbatim. */
export const WIDGET_CSS = `
.maieutics-widget { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px); color: var(--vscode-foreground); }
.maieutics-widget .row { display: flex; align-items: center; gap: 8px; margin: 4px 0; }
.maieutics-widget label { min-width: 90px; }
.maieutics-widget .value { opacity: 0.8; min-width: 48px; }
.maieutics-widget .box { border: 1px solid var(--vscode-panel-border); border-radius: 3px; padding: 6px; margin: 4px 0; }
.maieutics-widget input[type="text"] { flex: 1; }
.maieutics-widget .state { opacity: 0.75; white-space: pre-wrap; }
`;

/** The model-kind dispatch, ported verbatim (parity is the contract). */
export function kindOf(state: Record<string, unknown>): string {
  const name = String(state._model_name ?? "");
  if (name.includes("IntSlider")) return "int-slider";
  if (name.includes("FloatSlider")) return "float-slider";
  if (name.includes("TextInput")) return "text";
  if (name.includes("ToggleButton")) return "toggle";
  if (name.includes("Button")) return "button";
  if (name.includes("Box")) return "box";
  return "unknown";
}

const IPY_MODEL_REF = /^IPY_MODEL_(.+)$/;

/** The model id an `IPY_MODEL_<id>` child reference points at, if any. */
export function ipyModelRef(child: unknown): string | undefined {
  return typeof child === "string" ? IPY_MODEL_REF.exec(child)?.[1] : undefined;
}

/** Records modelId plus every nested IPY_MODEL_ child id in deps. */
export function collectWidgetDeps(
  deps: Set<string>,
  modelId: string,
  models: ReadonlyMap<string, Record<string, unknown>>,
): void {
  const state = models.get(modelId);
  const children = state && Array.isArray(state.children) ? state.children : [];
  for (const child of children) {
    const id = ipyModelRef(child);
    if (id !== undefined && !deps.has(id)) {
      deps.add(id);
      collectWidgetDeps(deps, id, models);
    }
  }
}

export const WidgetView: FunctionalComponent<ViewProps> = (props) => {
  const { modelId, state, models, post, hasState } = props;
  if (!hasState) {
    return (
      <div class="maieutics-widget">
        <div class="state">Connecting to the widget model…</div>
      </div>
    );
  }
  return <div class="maieutics-widget">{control(props)}</div>;
}

function control(props: ViewProps): VNode {
  const { modelId, state, models, post } = props;
  const kind = kindOf(state);
  const sendUpdate = (patch: Record<string, unknown>) =>
    post({ source: "maieutics-widget", type: "update", modelId, state: patch });

  if (kind === "int-slider" || kind === "float-slider") {
    const isInt = kind === "int-slider";
    // The readout follows the slider between kernel echoes; the kernel does
    // not broadcast its own state back, so update it locally from the event.
    let readout: HTMLElement | null = null;
    const commit = (event: JSX.TargetedEvent<HTMLInputElement>) => {
      const value = isInt
        ? Number.parseInt(event.currentTarget.value, 10)
        : Number.parseFloat(event.currentTarget.value);
      if (readout !== null) readout.textContent = String(value);
      sendUpdate({ value });
    };
    return (
      <div class="row">
        <label>{String(state.description ?? modelId)}</label>
        <input
          type="range"
          min={String(state.min ?? 0)}
          max={String(state.max ?? 100)}
          step={String(state.step ?? (isInt ? 1 : 0.1))}
          value={String(state.value ?? 0)}
          onInput={commit}
        />
        <span class="value" ref={(el) => (readout = el)}>{String(state.value ?? 0)}</span>
      </div>
    );
  }

  if (kind === "text") {
    return (
      <div class="row">
        <label>{String(state.description ?? "text")}</label>
        <input
          type="text"
          value={String(state.value ?? "")}
          onChange={(event: JSX.TargetedEvent<HTMLInputElement>) =>
            sendUpdate({ value: event.currentTarget.value })}
        />
      </div>
    );
  }

  if (kind === "toggle") {
    return (
      <div class="row">
        <span>
          <input
            type="checkbox"
            checked={state.value === true}
            onChange={(event: JSX.TargetedEvent<HTMLInputElement>) =>
              sendUpdate({ value: event.currentTarget.checked })}
          />
          <label>{String(state.description ?? modelId)}</label>
        </span>
      </div>
    );
  }

  if (kind === "button") {
    return (
      <div class="row">
        <button
          onClick={() => sendUpdate({ clicks: Number(state.clicks ?? 0) + 1 })}
        >
          {String(state.description ?? state.tooltip ?? modelId)}
        </button>
      </div>
    );
  }

  if (kind === "box") {
    const children = Array.isArray(state.children) ? state.children : [];
    return (
      <div class="box">
        {children.map((child, index) => {
          const id = ipyModelRef(child);
          const childState = id === undefined ? undefined : models.get(id);
          if (id === undefined || childState === undefined) {
            return <div class="state" key={index}>(waiting for child model)</div>;
          }
          return (
            <WidgetView
              key={index}
              modelId={id}
              state={childState}
              models={models}
              post={post}
              hasState={true}
            />
          );
        })}
      </div>
    );
  }

  return <div class="state">{JSON.stringify(state, null, 2)}</div>;
}

export const widgetFamily: ViewFamilyModule = {
  component: WidgetView,
  collectDeps(modelId, models) {
    const deps = new Set<string>();
    collectWidgetDeps(deps, modelId, models);
    return deps;
  },
};
