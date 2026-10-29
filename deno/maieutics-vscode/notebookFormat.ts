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

import type { ReasoningSummaryView, SubagentSnapshotView } from "./turnView.ts";
import type { ModelIdentity, UsageSummary } from "./protocol.ts";
import { mergeTimelineOutputSnapshots } from "./timelineOutputs.ts";
import {
  MaximumTimelineCharacters,
  MaximumTimelineEntries,
  type TimelineEntry,
} from "./turnTimeline.ts";

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
  /** Live node identity; consumed when joining outputs, never persisted as a node wrapper. */
  nodeId?: string;
  streaming?: boolean;
  runId?: string;
  input?: string;
  model?: ModelIdentity;
  usage?: UsageSummary;
  /** Final assistant markdown, when the turn produced text. */
  text?: string;
  /** First-appearance order of model messages, summaries, tools and child runs. */
  timeline?: TimelineEntry[];
  timelineTruncated?: boolean;
  reasoning?: ReasoningSummaryView[];
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
  subagentsOverflow?: boolean;
}

export interface ReplDisplaySnapshot {
  displayId: string;
  /** Mime bundle: mime -> payload (string or structured value). */
  data: Record<string, unknown>;
}

export interface ToolSnapshot {
  tool: string;
  status: "running" | "ok" | "error";
  argsSummary?: string;
  durationMs?: number;
  arguments?: unknown;
  result?: unknown;
  progress?: string;
  detailsTruncated?: boolean;
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
    ? value.tools.filter(isRecord).map(parseTool)
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
    ...(typeof value.runId === "string" ? { runId: value.runId } : {}),
    ...(typeof value.input === "string" ? { input: value.input } : {}),
    ...(isRecord(value.model) && typeof value.model.profileId === "string" &&
        typeof value.model.provider === "string" && typeof value.model.model === "string"
      ? {
        model: {
          profileId: value.model.profileId,
          provider: value.model.provider,
          model: value.model.model,
        },
      }
      : {}),
    ...(isRecord(value.usage) && typeof value.usage.inputTokens === "number" &&
        typeof value.usage.outputTokens === "number"
      ? {
        usage: {
          inputTokens: value.usage.inputTokens,
          outputTokens: value.usage.outputTokens,
          ...(typeof value.usage.totalTokens === "number"
            ? { totalTokens: value.usage.totalTokens }
            : {}),
        },
      }
      : {}),
    text: typeof value.text === "string" ? value.text : undefined,
    ...(Array.isArray(value.timeline) ? { timeline: parseTimeline(value.timeline) } : {}),
    ...(value.timelineTruncated === true ? { timelineTruncated: true } : {}),
    ...(Array.isArray(value.reasoning) ? { reasoning: parseReasoning(value.reasoning) } : {}),
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
    ...(value.subagentsOverflow === true ? { subagentsOverflow: true } : {}),
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
    ...(Array.isArray(value.reasoning) ? { reasoning: parseReasoning(value.reasoning) } : {}),
    text: typeof value.text === "string" ? value.text : "",
    tools: Array.isArray(value.tools) ? value.tools.filter(isRecord).map(parseTool) : [],
    ...(value.truncated === true ? { truncated: true } : {}),
    ...(typeof value.code === "string" ? { code: value.code } : {}),
  };
}

function parseTool(tool: Record<string, unknown>): ToolSnapshot {
  const diff = tool.diff;
  return {
    tool: typeof tool.tool === "string" ? tool.tool : "unknown",
    status: tool.status === "error" ? "error" : tool.status === "running" ? "running" : "ok",
    ...(typeof tool.argsSummary === "string" ? { argsSummary: tool.argsSummary } : {}),
    ...(typeof tool.durationMs === "number" ? { durationMs: tool.durationMs } : {}),
    ...(tool.arguments === undefined ? {} : { arguments: tool.arguments }),
    ...(tool.result === undefined ? {} : { result: tool.result }),
    ...(typeof tool.progress === "string" ? { progress: tool.progress } : {}),
    ...(tool.detailsTruncated === true ? { detailsTruncated: true } : {}),
    ...(isRecord(diff) && typeof diff.unified === "string" &&
        typeof diff.additions === "number" && typeof diff.deletions === "number"
      ? {
        diff: {
          unified: diff.unified,
          additions: diff.additions,
          deletions: diff.deletions,
          truncated: diff.truncated === true,
        },
      }
      : {}),
  };
}

/** Additive v1 field: old snapshots retain the category-based fallback. */
function parseTimeline(value: unknown[]): TimelineEntry[] {
  const entries: TimelineEntry[] = [];
  let room = MaximumTimelineCharacters;
  for (const entry of value.slice(0, MaximumTimelineEntries)) {
    if (!isRecord(entry)) continue;
    if (entry.kind === "text" && typeof entry.text === "string") {
      const text = entry.text.slice(0, room);
      room -= text.length;
      entries.push({
        kind: "text",
        messageId: typeof entry.messageId === "string" ? entry.messageId : "",
        text,
      });
    } else if (entry.kind === "reasoning" && typeof entry.messageId === "string") {
      entries.push({ kind: "reasoning", messageId: entry.messageId });
    } else if (
      (entry.kind === "tool" || entry.kind === "subagent") &&
      typeof entry.index === "number" && Number.isSafeInteger(entry.index) && entry.index >= 0
    ) {
      entries.push({ kind: entry.kind, index: entry.index });
    }
  }
  return entries;
}

const SubagentStatuses: Set<string> = new Set(["running", "ok", "failed", "cancelled"]);

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/** Optional additive snapshot member: old files load and malformed summaries are ignored. */
function parseReasoning(value: unknown): ReasoningSummaryView[] | undefined {
  if (!Array.isArray(value)) return undefined;
  let room = 20_000;
  return value.filter(isRecord)
    .filter((entry) => typeof entry.messageId === "string" && typeof entry.text === "string")
    .slice(0, 64)
    .map((entry) => {
      const text = (entry.text as string).slice(0, room);
      room -= text.length;
      return { messageId: entry.messageId as string, text };
    }).filter((entry) => entry.text.length > 0);
}

/** Combines independent live display segments when saving a running cell. A final turn
 * already carries text; partial summary segments use the streamed answer as a fallback. */
export function mergeOutputSnapshots(
  snapshots: readonly OutputSnapshot[],
  streamedText?: string,
): OutputSnapshot | undefined {
  if (snapshots.length === 0) return undefined;
  const merged = snapshots.some((snapshot) => typeof snapshot.nodeId === "string")
    ? mergeTimelineOutputSnapshots(snapshots)
    : snapshots.reduce<OutputSnapshot>(
      (result, snapshot) => ({ ...result, ...snapshot }),
      {},
    );
  if (merged.text === undefined && streamedText !== undefined) merged.text = streamedText;
  return merged;
}
