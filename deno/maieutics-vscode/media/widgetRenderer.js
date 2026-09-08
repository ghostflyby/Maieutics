// Maieutics widget renderer for `application/vnd.jupyter.widget-view+json`.
// Renders the SDK's classic controls (sliders, text, toggle, button, box)
// from the model state the extension host relays over the comms channel
// (ADR 0024). Dependency-free DOM only: renderer scripts have no bundler.
//
// Wire with the host (renderer messaging, sender "maieutics-widget"):
//   renderer -> host: { source, type: "mount",  modelId }
//   renderer -> host: { source, type: "update", modelId, state }
//   host -> renderer: { source, type: "state",  modelId, state }
//
// Models arrive as comm_open {state, buffer_paths}; updates merge by key.
// Unknown model types render as read-only state JSON — still live.

"use strict";

const STYLE = `
.maieutics-widget { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px); color: var(--vscode-foreground); }
.maieutics-widget .row { display: flex; align-items: center; gap: 8px; margin: 4px 0; }
.maieutics-widget label { min-width: 90px; }
.maieutics-widget .value { opacity: 0.8; min-width: 48px; }
.maieutics-widget .box { border: 1px solid var(--vscode-panel-border); border-radius: 3px; padding: 6px; margin: 4px 0; }
.maieutics-widget input[type="text"] { flex: 1; }
.maieutics-widget .state { opacity: 0.75; white-space: pre-wrap; }
`;

/** The models this renderer knows how to draw as native controls; everything
 * else falls back to the live state JSON. */
function kindOf(state) {
  const name = String(state._model_name ?? "");
  if (name.includes("IntSlider")) return "int-slider";
  if (name.includes("FloatSlider")) return "float-slider";
  if (name.includes("TextInput")) return "text";
  if (name.includes("ToggleButton")) return "toggle";
  if (name.includes("Button")) return "button";
  if (name.includes("Box") || name.includes("VBox") || name.includes("HBox")) return "box";
  return "unknown";
}

function el(tag, className) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  return node;
}

/** Renders one model into `container`. Children referenced by IPY_MODEL_<id>
 * render recursively from `models` once those states are known. */
function renderModel(container, modelId, state, models, api) {
  const kind = kindOf(state);
  const row = el("div", "row");
  const sendUpdate = (patch) =>
    api.postMessage({ source: "maieutics-widget", type: "update", modelId, state: patch });

  if (kind === "int-slider" || kind === "float-slider") {
    const label = el("label");
    label.textContent = String(state.description ?? modelId);
    const input = document.createElement("input");
    input.type = "range";
    input.min = String(state.min ?? 0);
    input.max = String(state.max ?? 100);
    input.step = String(state.step ?? (kind === "int-slider" ? 1 : 0.1));
    input.value = String(state.value ?? 0);
    const readout = el("span", "value");
    readout.textContent = String(state.value ?? 0);
    input.addEventListener("input", () => {
      const value = kind === "int-slider"
        ? Number.parseInt(input.value, 10)
        : Number.parseFloat(input.value);
      readout.textContent = String(value);
      sendUpdate({ value });
    });
    row.append(label, input, readout);
    container.append(row);
    return;
  }

  if (kind === "text") {
    const label = el("label");
    label.textContent = String(state.description ?? "text");
    const input = document.createElement("input");
    input.type = "text";
    input.value = String(state.value ?? "");
    input.addEventListener("change", () => sendUpdate({ value: input.value }));
    row.append(label, input);
    container.append(row);
    return;
  }

  if (kind === "toggle") {
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = state.value === true;
    const label = el("label");
    label.textContent = String(state.description ?? modelId);
    const wrap = el("span");
    wrap.append(input, label);
    input.addEventListener("change", () => sendUpdate({ value: input.checked }));
    row.append(wrap);
    container.append(row);
    return;
  }

  if (kind === "button") {
    const button = document.createElement("button");
    button.textContent = String(state.description ?? state.tooltip ?? modelId);
    button.addEventListener("click", () => sendUpdate({ clicks: Number(state.clicks ?? 0) + 1 }));
    row.append(button);
    container.append(row);
    return;
  }

  if (kind === "box") {
    const box = el("div", "box");
    const children = Array.isArray(state.children) ? state.children : [];
    for (const child of children) {
      const match = typeof child === "string" ? /^IPY_MODEL_(.+)$/.exec(child) : null;
      const childId = match?.[1];
      const childState = childId === undefined ? undefined : models.get(childId);
      if (childState === undefined) {
        const placeholder = el("div", "state");
        placeholder.textContent = "(waiting for child model)";
        box.append(placeholder);
        continue;
      }
      renderModel(box, childId, childState, models, api);
    }
    container.append(box);
    return;
  }

  const stateView = el("div", "state");
  stateView.textContent = JSON.stringify(state, null, 2);
  container.append(stateView);
}

exports.activate = function activate() {
  let styled = false;
  // Model states are shared across outputs of the same renderer webview: a
  // comm_update for a nested model must reach every view rendering it.
  const models = new Map();
  const views = []; // { modelId, container }
  window.addEventListener("message", (event) => {
    const message = event.data;
    if (
      typeof message !== "object" || message === null ||
      message.source !== "maieutics-widget" || message.type !== "state"
    ) {
      return;
    }

    models.set(message.modelId, message.state ?? {});
    for (const view of views) {
      if (view.modelId !== message.modelId) continue;
      const state = models.get(message.modelId);
      view.container.replaceChildren();
      renderModel(view.container, view.modelId, state ?? {}, models, api);
    }
  });

  const api = typeof acquireVsCodeApi === "function" ? acquireVsCodeApi() : null;

  return {
    renderOutputItem(output, element) {
      if (!styled) {
        const style = document.createElement("style");
        style.textContent = STYLE;
        document.head.append(style);
        styled = true;
      }

      const view = output.item.json();
      const modelId = typeof view.model_id === "string" ? view.model_id : "";
      const container = el("div", "maieutics-widget");
      if (api === null) {
        container.textContent = "The widget renderer requires the VS Code messaging API.";
        element.append(container);
        return;
      }

      const state = models.get(modelId);
      if (state === undefined) {
        const pending = el("div", "state");
        pending.textContent = "Connecting to the widget model…";
        container.append(pending);
      } else {
        renderModel(container, modelId, state, models, api);
      }

      element.append(container);
      views.push({ modelId, container });
      api.postMessage({ source: "maieutics-widget", type: "mount", modelId });
    },
  };
};
