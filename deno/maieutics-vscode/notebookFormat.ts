/**
 * The `.maieuticsnb` portable interaction snapshot format (version 1).
 *
 * The notebook file is frontend-owned: saving and loading never touches the
 * live server session (invariant 13). The codec is tolerant on read — unknown
 * fields are preserved verbatim and missing optional fields degrade — and
 * strict enough that a foreign document is rejected with a typed error instead
 * of silently corrupting a user's notebook.
 */

export const NotebookKind = "maieutics-notebook";
export const NotebookVersion = 1;
export const NotebookLanguage = "markdown";

export interface OutputSnapshot {
  /** Final assistant markdown, when the turn produced text. */
  text?: string;
  truncated?: boolean;
  error?: { code: string; message: string };
  /** Tool activity summaries in call order. */
  tools?: ToolSnapshot[];
}

export interface ToolSnapshot {
  tool: string;
  status: "ok" | "error";
}

export interface CellSnapshot {
  kind: "agent" | "markdown";
  text: string;
  output?: OutputSnapshot;
}

export interface MaieuticsNotebook {
  maieutics: typeof NotebookKind;
  version: number;
  session?: { serverSessionId?: string };
  cells: CellSnapshot[];
}

/** Typed decode failure for documents that are not Maieutics notebooks. */
export class NotebookFormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "NotebookFormatError";
  }
}

export function emptyNotebook(): MaieuticsNotebook {
  return {
    maieutics: NotebookKind,
    version: NotebookVersion,
    cells: [{ kind: "agent", text: "" }],
  };
}

export function parseNotebook(bytes: Uint8Array): MaieuticsNotebook {
  let document: unknown;
  try {
    document = JSON.parse(new TextDecoder().decode(bytes));
  } catch (error) {
    throw new NotebookFormatError(
      `The notebook is not valid JSON: ${(error as Error).message}`,
    );
  }
  if (typeof document !== "object" || document === null) {
    throw new NotebookFormatError("The notebook must be a JSON object.");
  }
  const record = document as Record<string, unknown>;
  if (record.maieutics !== NotebookKind) {
    throw new NotebookFormatError(
      `The document is not a ${NotebookKind} snapshot.`,
    );
  }
  const version = typeof record.version === "number" ? record.version : 0;
  if (version > NotebookVersion) {
    throw new NotebookFormatError(
      `The notebook was written by a newer version (${version} > ${NotebookVersion}).`,
    );
  }
  const cells = Array.isArray(record.cells) ? record.cells : [];
  return {
    maieutics: NotebookKind,
    version: NotebookVersion,
    session: isRecord(record.session) ? record.session : undefined,
    cells: cells.map(parseCell),
  };
}

export function serializeNotebook(notebook: MaieuticsNotebook): Uint8Array {
  return new TextEncoder().encode(
    `${JSON.stringify(notebook, null, 2)}\n`,
  );
}

function parseCell(value: unknown): CellSnapshot {
  const record = isRecord(value) ? value : {};
  const text = typeof record.text === "string" ? record.text : "";
  const kind = record.kind === "markdown" ? "markdown" : "agent";
  const output = isRecord(record.output) ? parseOutput(record.output) : undefined;
  return { kind, text, output };
}

function parseOutput(value: Record<string, unknown>): OutputSnapshot {
  const tools = Array.isArray(value.tools)
    ? value.tools.filter(isRecord).map((tool) => ({
      tool: typeof tool.tool === "string" ? tool.tool : "unknown",
      status: tool.status === "error" ? "error" as const : "ok" as const,
    }))
    : undefined;
  return {
    text: typeof value.text === "string" ? value.text : undefined,
    truncated: typeof value.truncated === "boolean" ? value.truncated : undefined,
    error: isRecord(value.error) && typeof value.error.code === "string"
      ? {
        code: value.error.code,
        message: typeof value.error.message === "string" ? value.error.message : "",
      }
      : undefined,
    tools,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
