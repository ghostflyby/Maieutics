/**
 * The `maieutics/form` view family: declarative forms (fields, choices,
 * submit/cancel) — the first native family of the custom-UI framework.
 *
 * Producer API (REPL cells):
 *
 * ```ts
 * const { form } = maieutics.ui;
 * const answer = form(
 *   { title: "Approve?", fields: [{ name: "note", type: "text" }] },
 *   (values) => { /* submit *&#47; },
 * );
 * answer; // displays the live form via its announcement
 * ```
 *
 * Field editing stays renderer-local (DOM state between renders, mirroring
 * widget controls); submit/cancel arrive as one-shot uplink events carrying
 * the field values. The kernel-side `values` state key exists for producer
 * `sync()` round-trips, not for per-keystroke sync.
 */

import { NativeFamilyContract } from "./family.ts";
import type { UiModel, UiModelRuntime } from "./runtime.ts";

export const FORM_FAMILY = "maieutics/form";

export type FormFieldType = "text" | "number" | "boolean" | "choice" | "password";

export interface FormFieldDef {
  readonly name: string;
  readonly label?: string;
  readonly type: FormFieldType;
  /** For `choice` fields: accepted values (label = value when a plain string). */
  readonly choices?: readonly (string | { label: string; value: string })[];
  readonly required?: boolean;
  readonly placeholder?: string;
  /** Initial value; also seeds the frozen view a snapshot restore renders. */
  readonly default?: string | number | boolean;
}

export interface FormDef {
  readonly title?: string;
  readonly fields: readonly FormFieldDef[];
  readonly submitLabel?: string;
  readonly cancelLabel?: string;
}

export interface FormHandlers {
  /** Submit with the field values read from the form. */
  onSubmit?: (values: Record<string, unknown>) => void;
  /** Cancel button. */
  onCancel?: () => void;
}

export interface FormState extends Record<string, unknown> {
  title?: string;
  fields: readonly FormFieldDef[];
  submitLabel?: string;
  cancelLabel?: string;
  /** Producer-synced echo of the last submitted values. */
  values: Record<string, unknown>;
}

export class FormFamilyContract extends NativeFamilyContract {
  constructor() {
    super(FORM_FAMILY, "1.0");
  }

  override decodeIncoming(data: unknown) {
    const dispatch = super.decodeIncoming(data);
    if (dispatch?.event === undefined) return dispatch;
    const { name, payload } = dispatch.event;
    if (name === "submit") {
      const values =
        typeof payload === "object" && payload !== null &&
        "values" in payload &&
        typeof (payload as { values: unknown }).values === "object"
          ? (payload as { values: Record<string, unknown> }).values
          : {};
      return { event: { name, payload: values } };
    }
    return dispatch;
  }
}

/** Normalize a choices list into `{value, label}` pairs. */
export function normalizeChoices(
  choices: readonly (string | { label: string; value: string })[],
): { value: string; label: string }[] {
  return choices.map((choice) =>
    typeof choice === "string" ? { value: choice, label: choice } : choice
  );
}

/** The initial `values` state seeded from field defaults. */
export function initialValues(def: FormDef): Record<string, unknown> {
  const values: Record<string, unknown> = {};
  for (const field of def.fields) {
    values[field.name] = field.default ?? (field.type === "boolean" ? false : "");
  }
  return values;
}

/** Build the form family's state from a producer definition. */
export function formState(def: FormDef): FormState {
  const fields = def.fields.map((field) => ({
    ...field,
    choices: field.choices === undefined ? undefined : normalizeChoices(field.choices),
  }));
  return {
    ...(def.title === undefined ? {} : { title: def.title }),
    fields,
    ...(def.submitLabel === undefined ? {} : { submitLabel: def.submitLabel }),
    ...(def.cancelLabel === undefined ? {} : { cancelLabel: def.cancelLabel }),
    values: initialValues(def),
  };
}

/** Create a live form model bound to the runtime's transport. */
export function createForm(
  runtime: UiModelRuntime,
  def: FormDef,
  handlers: FormHandlers = {},
): UiModel<FormState> {
  return runtime.create<FormState>(FORM_FAMILY, formState(def), {
    onEvent: (name, payload) => {
      if (name === "submit") handlers.onSubmit?.(payload as Record<string, unknown>);
      if (name === "cancel") handlers.onCancel?.();
    },
  });
}
