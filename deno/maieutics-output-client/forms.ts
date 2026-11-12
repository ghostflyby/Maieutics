/// <reference lib="dom" />
import {
  createApp,
  defineComponent,
  h,
  nextTick,
  reactive,
  shallowRef,
  watch,
} from "@vue/runtime-dom";
import type { App, ShallowRef } from "@vue/runtime-dom";

import type { FormMessage, FormRenderer } from "./types.d.ts";
export type { FormMessage, FormRenderer } from "./types.d.ts";
interface Field {
  name: string;
  label: string;
  type: string;
  choices: { value: string; label: string }[];
  required: boolean;
  placeholder?: string;
}
interface FormState {
  title?: string;
  fields: Field[];
  values: Record<string, unknown>;
  submitLabel: string;
  cancelLabel: string;
}
interface Model {
  state: FormState;
  live: boolean;
}
interface View {
  modelId: string;
  snapshot: boolean;
  slot: ShallowRef<Model>;
  app: App;
  element: HTMLElement;
}

/** Tolerantly decode bounded plain form data, never component code. */
export function readFormState(value: unknown): FormState {
  const state = isRecord(value) ? value : {};
  if (Array.isArray(state.fields) && state.fields.length > 128) {
    throw new Error("form_fields_limit");
  }
  const names = new Set<string>();
  const fields: Field[] = [];
  for (const item of (Array.isArray(state.fields) ? state.fields : [])) {
    if (!isRecord(item) || typeof item.name !== "string" || !item.name || names.has(item.name)) {
      continue;
    }
    names.add(item.name);
    if (Array.isArray(item.choices) && item.choices.length > 128) {
      throw new Error("form_options_limit");
    }
    const choices = (Array.isArray(item.choices) ? item.choices : []).flatMap((choice) =>
      typeof choice === "string"
        ? [{ value: choice, label: choice }]
        : isRecord(choice) && typeof choice.value === "string"
        ? [{
          value: choice.value,
          label: typeof choice.label === "string" ? choice.label : choice.value,
        }]
        : []
    );
    fields.push({
      name: item.name,
      label: typeof item.label === "string" ? item.label : item.name,
      type: typeof item.type === "string" ? item.type : "text",
      choices,
      required: item.required === true,
      placeholder: typeof item.placeholder === "string" ? item.placeholder : undefined,
    });
  }
  return {
    title: typeof state.title === "string" ? state.title : undefined,
    fields,
    values: isRecord(state.values) ? state.values : {},
    submitLabel: typeof state.submitLabel === "string" ? state.submitLabel : "Submit",
    cancelLabel: typeof state.cancelLabel === "string" ? state.cancelLabel : "Cancel",
  };
}

const Form = defineComponent({
  props: ["model", "send"],
  setup(props) {
    const draft = reactive<Record<string, unknown>>(Object.create(null));
    const model = props.model as ShallowRef<Model>;
    watch(() => model.value.state, (state, previous) => {
      const names = new Set(state.fields.map((field) => field.name));
      for (const name of Object.keys(draft)) if (!names.has(name)) delete draft[name];
      for (const field of state.fields) {
        if (
          !(field.name in draft) ||
          !Object.is(state.values[field.name], previous?.values[field.name])
        ) {
          draft[field.name] = state.values[field.name] ?? (field.type === "boolean" ? false : "");
        }
      }
    }, { immediate: true, flush: "sync" });
    const control = (field: Field) => {
      const common = { name: field.name, required: field.required, "aria-label": field.label };
      const onInput = (event: Event) => {
        const target = event.target as HTMLInputElement;
        draft[field.name] = field.type === "boolean"
          ? target.checked
          : field.type === "number" && target.value !== "" && Number.isFinite(target.valueAsNumber)
          ? target.valueAsNumber
          : target.value;
      };
      if (field.type === "choice") {
        return h(
          "select",
          { ...common, value: draft[field.name], onChange: onInput },
          field.choices.map((choice) =>
            h("option", { key: choice.value, value: choice.value }, choice.label)
          ),
        );
      }
      return h("input", {
        ...common,
        placeholder: field.placeholder,
        type: field.type === "boolean"
          ? "checkbox"
          : ["password", "number"].includes(field.type)
          ? field.type
          : "text",
        ...(field.type === "number" ? { step: "any" } : {}),
        ...(field.type === "boolean"
          ? { checked: draft[field.name] === true }
          : { value: draft[field.name] }),
        onInput,
      });
    };
    const send = (name: "submit" | "cancel") => {
      if (!model.value.live) return;
      props.send(name, name === "submit" ? { values: { ...draft } } : undefined);
    };
    return () => {
      const { state, live } = model.value;
      return h("form", {
        class: "maieutics-form",
        onSubmit: (event: Event) => {
          event.preventDefault();
          send("submit");
        },
      }, [
        state.title === undefined ? null : h("div", { class: "title" }, state.title),
        live ? null : h("div", { class: "stale" }, "Snapshot — this form is not connected"),
        h("fieldset", { disabled: !live }, [
          ...state.fields.map((field) =>
            h("label", { key: field.name, class: "row" }, [
              h("span", field.label),
              control(field),
            ])
          ),
          h("div", { class: "actions" }, [
            h("button", { type: "submit", class: "submit" }, state.submitLabel),
            h(
              "button",
              { type: "button", class: "cancel", onClick: () => send("cancel") },
              state.cancelLabel,
            ),
          ]),
        ]),
      ]);
    };
  },
});

const CSS =
  `:host{font-family:var(--vscode-font-family,system-ui);font-size:var(--vscode-font-size,13px);color:var(--vscode-foreground,inherit)}
.maieutics-form{max-width:520px}.title{font-weight:600;margin:4px 0}.stale{opacity:.65;font-style:italic}
fieldset{border:0;padding:0;margin:0}.row{display:flex;align-items:center;gap:8px;margin:4px 0}.row>span{min-width:110px}
.row input:not([type=checkbox]),select{flex:1;min-width:0}.actions{display:flex;gap:8px;margin-top:8px}
button{padding:4px 12px;cursor:pointer}.submit{background:var(--vscode-button-background,#3451b2);color:var(--vscode-button-foreground,white);border:0}
.cancel{background:transparent;color:inherit;border:1px solid var(--vscode-panel-border,#888)}fieldset:disabled button{cursor:default}`;

/** Vue form renderer shared by notebook and standalone hosts. The host owns transport subscriptions. */
export function createFormRenderer(
  options: { sendMessage(message: FormMessage): void },
): FormRenderer {
  const views = new Map<string, View>();
  let closed = false;
  const release = (id: string) => {
    const view = views.get(id);
    if (!view) return;
    views.delete(id);
    try {
      view.app.unmount();
    } finally {
      if (!view.snapshot) {
        options.sendMessage({
          source: "maieutics-form",
          type: "unmount",
          modelId: view.modelId,
          viewId: id,
        });
      }
    }
  };
  const disposeOutput = (id?: string) => {
    const errors: unknown[] = [];
    for (const key of id === undefined ? [...views.keys()] : [id]) {
      try {
        release(key);
      } catch (error) {
        errors.push(error);
      }
    }
    if (errors.length) throw new AggregateError(errors, "form_disposal_failed");
  };
  return {
    render(id, value, element) {
      if (closed) throw new Error("form_renderer_disposed");
      if (
        !isRecord(value) || value.version !== 1 || typeof value.modelId !== "string" ||
        !value.modelId
      ) {
        throw new Error("invalid_form");
      }
      const modelId = value.modelId;
      const snapshot = value.snapshot === true;
      const existing = views.get(id);
      if (
        existing && existing.element === element && existing.modelId === modelId &&
        existing.snapshot === snapshot
      ) {
        if (!existing.slot.value.live) {
          existing.slot.value = { state: readFormState(value.state), live: false };
        }
        return;
      }
      release(id);
      if (views.size >= 128) throw new Error("form_view_limit");
      const shared = snapshot
        ? undefined
        : [...views.values()].find((view) => !view.snapshot && view.modelId === modelId);
      const slot = shared?.slot ??
        shallowRef<Model>({ state: readFormState(value.state), live: false });
      const root = element.shadowRoot ?? element.attachShadow({ mode: "open" });
      root.replaceChildren();
      const style = document.createElement("style");
      style.textContent = CSS;
      const container = document.createElement("div");
      root.append(style, container);
      const app = createApp(Form, {
        model: slot,
        send(name: "submit" | "cancel", payload?: { values: Record<string, unknown> }) {
          if (!closed && views.has(id) && slot.value.live && !snapshot) {
            options.sendMessage({
              source: "maieutics-form",
              type: "event",
              modelId,
              viewId: id,
              name,
              ...(payload ? { payload } : {}),
            });
          }
        },
      });
      app.mount(container);
      views.set(id, { modelId, snapshot, slot, app, element });
      if (!snapshot) {
        options.sendMessage({ source: "maieutics-form", type: "mount", modelId, viewId: id });
      }
    },
    async receive(message) {
      if (
        closed || !isRecord(message) || message.source !== "maieutics-form" ||
        !["state", "closed"].includes(String(message.type))
      ) return;
      const slots = new Set(
        [...views.values()].filter((view) => !view.snapshot && view.modelId === message.modelId)
          .map((view) => view.slot),
      );
      for (const slot of slots) {
        slot.value = message.type === "closed"
          ? { ...slot.value, live: false }
          : { state: readFormState(message.state), live: true };
      }
      await nextTick();
    },
    disposeOutput,
    dispose() {
      if (closed) return;
      closed = true;
      disposeOutput();
    },
  };
}
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
