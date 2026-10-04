/**
 * The `.maieuticsnb` portable interaction snapshot format (version 1).
 *
 * The notebook file is frontend-owned: saving and loading never touches the
 * live server session (invariant 13). The codec is tolerant on read — missing
 * optional fields degrade, and unknown fields are ignored (they do not
 * survive a round trip; forward-compatible data must become explicit fields,
 * as `CellSnapshot.turn` and `OutputSnapshot.subagents` did) — and strict
 * enough that a foreign document is rejected with a typed error instead of
 * silently corrupting a user's notebook.
 */

import type { SubagentSnapshotView } from "./turnView.ts";

export const NotebookKind = "maieutics-notebook";
export const NotebookVersion = 1;
export const NotebookLanguage = "markdown";

/** The per-cell turn binding: the run a committed cell created and the text
 * that was submitted, so stale detection needs no server round trip. */
export interface TurnBinding {
  runId: string;
  input: string;
}

export interface OutputSnapshot {
  /** Final assistant markdown, when the turn produced text. */
  text?: string;
  truncated?: boolean;
  error?: { code: string; message: string };
  /** Tool activity summaries in call order. */
  tools?: ToolSnapshot[];
  /** REPL rich displays in first-appearance order. */
  repl?: ReplDisplaySnapshot[];
  /** Folded subagent child reports (ADR 0030): display data only — the
   * child transcript itself is never persisted ("carry reports, never live
   * child state"). */
  subagents?: SubagentSnapshotView[];
}

export interface ReplDisplaySnapshot {
  displayId: string;
  /** Mime bundle: mime -> payload (string or structured value). */
  data: Record<string, unknown>;
}

export interface ToolSnapshot {
  tool: string;
  status: "ok" | "error";
  /** Bounded unified diff captured from a structured edit-tool result;
   * optional so older snapshots keep loading. */
  diff?: ToolDiffSnapshot;
}

/** The persisted edit diff of a tool snapshot (already capped when written). */
export interface ToolDiffSnapshot {
  unified: string;
  additions: number;
  deletions: number;
  truncated: boolean;
}

export interface CellSnapshot {
  kind: "agent" | "markdown";
  text: string;
  output?: OutputSnapshot;
  /** The turn binding of a committed cell; survives clear-output, so the
   * commit frontier never depends on outputs being present. */
  turn?: TurnBinding;
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

/** The display-mime member native view-family announcements ride. */
export const NativeViewMime = "application/vnd.maieutics.view+json";

/**
 * Strip bundled-family component sources (esmSource/cssSource) from a
 * snapshot's REPL displays before it is persisted: a `.maieuticsnb` file is
 * untrusted input (AGENTS.md), and bundled code must only ever reach the
 * renderer as live output of a running session (ADR 0038 stage 1b's trust
 * boundary). Reopened notebooks render the frozen announcement state via
 * the fallback view; a live re-display re-materializes the family.
 */
export function stripBundledDisplaySources(output: OutputSnapshot): OutputSnapshot {
  if (output.repl === undefined) return output;
  let changed = false;
  const repl = output.repl.map((display) => {
    const announcement = display.data[NativeViewMime];
    if (!isRecord(announcement)) return display;
    if (!("esmSource" in announcement) && !("cssSource" in announcement)) return display;
    changed = true;
    const cleaned = { ...announcement };
    delete cleaned.esmSource;
    delete cleaned.cssSource;
    return { ...display, data: { ...display.data, [NativeViewMime]: cleaned } };
  });
  return changed ? { ...output, repl } : output;
}

function parseCell(value: unknown): CellSnapshot {
  const record = isRecord(value) ? value : {};
  const text = typeof record.text === "string" ? record.text : "";
  const kind = record.kind === "markdown" ? "markdown" : "agent";
  const output = isRecord(record.output) ? parseOutput(record.output) : undefined;
  const turn = isRecord(record.turn) && typeof record.turn.runId === "string" &&
      typeof record.turn.input === "string"
    ? { runId: record.turn.runId, input: record.turn.input }
    : undefined;
  return { kind, text, output, turn };
}

function parseOutput(value: Record<string, unknown>): OutputSnapshot {
  const tools = Array.isArray(value.tools)
    ? value.tools.filter(isRecord).map((tool) => ({
      tool: typeof tool.tool === "string" ? tool.tool : "unknown",
      status: tool.status === "error" ? "error" as const : "ok" as const,
    }))
    : undefined;
  const repl = Array.isArray(value.repl)
    ? value.repl.filter(isRecord).map((display) => ({
      displayId: typeof display.displayId === "string" ? display.displayId : "",
      data: isRecord(display.data) ? display.data : {},
    }))
    : undefined;
  const subagents = Array.isArray(value.subagents)
    ? value.subagents.filter(isRecord).map(parseSubagent)
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
    repl,
    subagents,
  };
}

/** One folded subagent child report; malformed entries degrade field by
 * field (an unknown status reads as running, matching the live mark). */
function parseSubagent(value: Record<string, unknown>): SubagentSnapshotView {
  const status = SubagentStatuses.has(value.status as string)
    ? value.status as SubagentSnapshotView["status"]
    : "running";
  return {
    status,
    text: typeof value.text === "string" ? value.text : "",
    tools: Array.isArray(value.tools)
      ? value.tools.filter(isRecord).map((tool) => ({
        tool: typeof tool.tool === "string" ? tool.tool : "unknown",
        status: tool.status === "error" ? "error" as const : "ok" as const,
      }))
      : [],
    ...(value.truncated === true ? { truncated: true } : {}),
    ...(typeof value.code === "string" ? { code: value.code } : {}),
  };
}

const SubagentStatuses: Set<string> = new Set(["running", "ok", "failed", "cancelled"]);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
