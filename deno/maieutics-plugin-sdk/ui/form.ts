/** Declarative form data shared by REPL and plugin producers. */
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
