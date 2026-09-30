/** MCP elicitation form planning (ADR 0029).
 *
 * An `input.request` frame may carry `schema` — the server's primitive form
 * schema — and `serverId`. This module turns that schema into a field plan a
 * UI can drive sequentially (VS Code has no native form, so each field is one
 * input box / yes-no pick), and coerces the collected strings back to typed
 * values for the `{value: {…}}` accept answer. Pure: no vscode imports, so
 * the planning and coercion are unit-testable. */

export interface ElicitField {
  readonly name: string;
  /** Prompt title: the property title, falling back to the field name. */
  readonly title: string;
  readonly description?: string;
  readonly type: "string" | "number" | "integer" | "boolean";
  readonly required: boolean;
  readonly defaultValue?: string;
  readonly enumValues?: readonly string[];
}

export interface ElicitPlan {
  readonly serverId?: string;
  readonly prompt: string;
  readonly fields: readonly ElicitField[];
}

/** Builds the field plan from a JSON-schema-shaped object; returns null when
 * the schema is not a usable form (missing, not an object, or without any
 * primitive property) — callers fall back to the plain input box. */
export function planElicitation(
  prompt: string,
  schema: unknown,
  serverId?: string,
): ElicitPlan | null {
  if (typeof schema !== "object" || schema === null) return null;
  const record = schema as Record<string, unknown>;
  const properties = record.properties;
  if (typeof properties !== "object" || properties === null) return null;

  const required = new Set(
    Array.isArray(record.required)
      ? record.required.filter((name): name is string => typeof name === "string")
      : [],
  );

  const fields: ElicitField[] = [];
  for (const [name, raw] of Object.entries(properties as Record<string, unknown>)) {
    if (typeof raw !== "object" || raw === null) continue;
    const property = raw as Record<string, unknown>;
    const type = typeof property.type === "string" ? property.type : "string";
    if (type !== "string" && type !== "number" && type !== "integer" && type !== "boolean") {
      continue;
    }

    const enumValues = Array.isArray(property.enum)
      ? property.enum.filter((value): value is string => typeof value === "string")
      : undefined;
    fields.push({
      name,
      title: typeof property.title === "string" && property.title.length > 0
        ? property.title
        : name,
      description: typeof property.description === "string" && property.description.length > 0
        ? property.description
        : undefined,
      type,
      required: required.has(name),
      defaultValue: property.default === undefined || property.default === null
        ? undefined
        : String(property.default),
      enumValues: enumValues !== undefined && enumValues.length > 0 ? enumValues : undefined,
    });
  }

  if (fields.length === 0) return null;
  return { prompt, serverId, fields };
}

export type CoerceResult =
  | { readonly ok: true; readonly value: string | number | boolean }
  | { readonly ok: false; readonly error: string };

/** Coerces one collected raw string into the field's declared type. Empty
 * input on an optional field yields undefined (the caller omits the key). */
export function coerceField(
  field: ElicitField,
  raw: string | undefined,
): CoerceResult | { readonly ok: true; readonly value: undefined } {
  const text = raw ?? "";
  if (text === "") {
    if (field.required && field.defaultValue === undefined) {
      return { ok: false, error: `'${field.title}' is required.` };
    }
    return { ok: true, value: field.defaultValue };
  }

  switch (field.type) {
    case "string":
      return { ok: true, value: text };
    case "number":
    case "integer": {
      const parsed = Number(text);
      if (!Number.isFinite(parsed)) {
        return { ok: false, error: `'${field.title}' must be a number.` };
      }
      if (field.type === "integer" && !Number.isInteger(parsed)) {
        return { ok: false, error: `'${field.title}' must be a whole number.` };
      }
      return { ok: true, value: parsed };
    }
    case "boolean":
      return { ok: true, value: /^(true|yes|y|1)$/i.test(text) };
  }
}
