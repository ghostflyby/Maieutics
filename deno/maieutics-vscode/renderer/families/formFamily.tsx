/**
 * `maieutics/form` family renderer: title, typed fields, submit/cancel.
 * Field editing stays DOM-local between renders (mirroring widget controls);
 * submit reads the field elements and posts one `submit` event carrying the
 * values, cancel posts a `cancel` event. When no live state has arrived
 * (`!hasState`) the view renders the announcement's embedded state with a
 * stale marker — the snapshot-restore posture (ADR 0038 §5).
 */

import type { FunctionalComponent, VNode } from "preact";
import type { ViewFamilyModule, ViewProps } from "../registry.ts";

export const FORM_CSS = `
.maieutics-form { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size, 13px); color: var(--vscode-foreground); max-width: 520px; }
.maieutics-form .title { font-weight: 600; margin: 4px 0; }
.maieutics-form .stale { opacity: 0.6; font-style: italic; margin: 2px 0; }
.maieutics-form .row { display: flex; align-items: center; gap: 8px; margin: 4px 0; }
.maieutics-form label { min-width: 110px; }
.maieutics-form input[type="text"], .maieutics-form input[type="password"], .maieutics-form input[type="number"], .maieutics-form select { flex: 1; }
.maieutics-form .actions { display: flex; gap: 8px; margin-top: 8px; }
.maieutics-form .submit { background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: none; padding: 4px 12px; cursor: pointer; }
.maieutics-form .cancel { background: transparent; color: var(--vscode-foreground); border: 1px solid var(--vscode-panel-border); padding: 4px 12px; cursor: pointer; }
.maieutics-form .state { opacity: 0.75; white-space: pre-wrap; }
`;

export interface FormFieldView {
  readonly name: string;
  readonly label: string;
  readonly type: "text" | "number" | "boolean" | "choice" | "password" | string;
  readonly choices: { value: string; label: string }[];
  readonly required: boolean;
  readonly placeholder?: string;
}

export interface FormViewDef {
  readonly title?: string;
  readonly fields: FormFieldView[];
  readonly submitLabel: string;
  readonly cancelLabel: string;
  readonly values: Record<string, unknown>;
}

/** Tolerantly read the form state (kernel state is untrusted input). */
export function readFormDef(state: Record<string, unknown>): FormViewDef {
  const rawFields = Array.isArray(state.fields) ? state.fields : [];
  const fields: FormFieldView[] = [];
  for (const entry of rawFields) {
    if (typeof entry !== "object" || entry === null) continue;
    const field = entry as Record<string, unknown>;
    if (typeof field.name !== "string" || field.name.length === 0) continue;
    const rawChoices = Array.isArray(field.choices) ? field.choices : [];
    const choices = rawChoices.flatMap((choice): { value: string; label: string }[] => {
      if (typeof choice === "string") return [{ value: choice, label: choice }];
      if (typeof choice === "object" && choice !== null) {
        const pair = choice as Record<string, unknown>;
        const value = typeof pair.value === "string" ? pair.value : undefined;
        const label = typeof pair.label === "string" ? pair.label : value;
        return value === undefined ? [] : [{ value, label: label ?? value }];
      }
      return [];
    });
    fields.push({
      name: field.name,
      label: typeof field.label === "string" && field.label.length > 0 ? field.label : field.name,
      type: typeof field.type === "string" ? field.type : "text",
      choices,
      required: field.required === true,
      placeholder: typeof field.placeholder === "string" ? field.placeholder : undefined,
    });
  }
  const values =
    typeof state.values === "object" && state.values !== null && !Array.isArray(state.values)
      ? state.values as Record<string, unknown>
      : {};
  return {
    ...(typeof state.title === "string" ? { title: state.title } : {}),
    fields,
    submitLabel: typeof state.submitLabel === "string" ? state.submitLabel : "Submit",
    cancelLabel: typeof state.cancelLabel === "string" ? state.cancelLabel : "Cancel",
    values,
  };
}

/** Convert a raw element reading into the field's value shape. */
export function fieldValue(
  field: FormFieldView,
  raw: { text: string; checked: boolean },
): string | number | boolean {
  if (field.type === "boolean") return raw.checked;
  if (field.type === "number") {
    if (raw.text.length === 0) return "";
    const parsed = Number.parseFloat(raw.text);
    return Number.isNaN(parsed) ? raw.text : parsed;
  }
  return raw.text;
}

export const FormView: FunctionalComponent<ViewProps> = (props) => {
  const { modelId, state, post, hasState } = props;
  const def = readFormDef(state);
  const send = (name: string, payload?: unknown) =>
    post({
      source: "maieutics-ui",
      type: "event",
      modelId,
      name,
      ...(payload === undefined ? {} : { payload }),
    });

  // Field elements register themselves by name; submit reads their current
  // DOM state (editing stays local between kernel state renders).
  const inputs = new Map<string, HTMLElement>();
  const track = (name: string) => (el: HTMLElement | null) => {
    if (el === null) inputs.delete(name);
    else inputs.set(name, el);
  };
  const submit = () => {
    const values: Record<string, unknown> = {};
    for (const field of def.fields) {
      const el = inputs.get(field.name);
      if (el === undefined) continue;
      const raw = {
        text: "value" in el ? String((el as { value: unknown }).value ?? "") : "",
        checked: "checked" in el && (el as { checked: unknown }).checked === true,
      };
      values[field.name] = fieldValue(field, raw);
    }
    send("submit", { values });
  };

  return (
    <div class="maieutics-form">
      {def.title === undefined ? null : <div class="title">{def.title}</div>}
      {hasState ? null : <div class="stale">snapshot — the model is not live in this session</div>}
      <div>
        {def.fields.map((field) => (
          <div class="row" key={field.name}>
            <label>{field.label}</label>
            {fieldControl(field, def.values[field.name], track(field.name))}
          </div>
        ))}
        <div class="actions">
          <button type="button" class="submit" onClick={submit}>{def.submitLabel}</button>
          <button type="button" class="cancel" onClick={() => send("cancel")}>
            {def.cancelLabel}
          </button>
        </div>
      </div>
    </div>
  );
};

function fieldControl(
  field: FormFieldView,
  initial: unknown,
  ref: (el: HTMLElement | null) => void,
): VNode {
  const initialText = initial === undefined || initial === null ? "" : String(initial);
  if (field.type === "boolean") {
    return (
      <input
        type="checkbox"
        name={field.name}
        checked={initial === true}
        ref={ref}
      />
    );
  }
  if (field.type === "choice") {
    return (
      <select name={field.name} ref={ref}>
        {field.choices.map((choice) => (
          <option value={choice.value} selected={choice.value === initialText}>
            {choice.label}
          </option>
        ))}
      </select>
    );
  }
  const type = field.type === "password" ? "password" : field.type === "number" ? "number" : "text";
  return (
    <input
      type={type}
      name={field.name}
      value={initialText}
      placeholder={field.placeholder}
      ref={ref}
    />
  );
}

export const formFamily: ViewFamilyModule = { component: FormView };
