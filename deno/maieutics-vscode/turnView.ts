/**
 * Pure per-run view state machine: folds event frames into a rendered turn.
 * No VSCode or network imports, so the folding behavior is unit-testable and
 * the notebook controller only bridges the result into cell outputs.
 *
 * Text deltas are folded eagerly; the controller decides when to repaint its
 * output (throttling is a rendering concern, not a protocol one — the server
 * forwards every Agent event unsampled).
 */

import type { EventFrame, ModelIdentity, UsageSummary } from "./protocol.ts";

/** Renders compactly: JSON collapsed to one line and truncated for the tool
 * activity line. */
function summarize(value: unknown, limit = 48): string | undefined {
  if (value === undefined || value === null) return undefined;
  let text: string;
  try {
    text = typeof value === "string" ? value : JSON.stringify(value);
  } catch {
    return undefined;
  }
  // Backticks and control characters are stripped: the preview is embedded
  // in a markdown code span, and tool arguments are untrusted model output.
  // deno-lint-ignore no-control-regex -- stripping control characters is the point.
  const oneLine = text.replace(/[\u0000-\u001f\u007f`]+/g, " ").trim();
  if (oneLine.length === 0 || oneLine === "{}") return undefined;
  return oneLine.length <= limit ? oneLine : `${oneLine.slice(0, limit)}…`;
}

export interface ToolEntry {
  callId: string;
  tool: string;
  status: "running" | "ok" | "error";
  /** Compact argument preview captured from tool.started. */
  args?: string;
  /** Latest progress text captured from tool.progress (last wins, so the
   * line stays bounded however chatty the tool is). */
  progress?: string;
  /** Wall-clock duration measured between the start and finish frames
   * (receipt time, so replayed streams under-report). */
  startedAt?: number;
  durationMs?: number;
}

export type TerminalState =
  | { kind: "completed"; truncated: boolean }
  | { kind: "failed"; code: string; message: string }
  | { kind: "missing" };

/** One REPL rich display routed into the run, keyed by display id. */
export interface ReplDisplayEntry {
  displayId: string;
  /** Mime bundle: mime -> payload (string or structured value). */
  data: Record<string, unknown>;
}

/** Identifies one cell output segment (a stable notebook output). */
export type SegmentId = `repl:${string}` | `tools:${string}` | `answer:${string}`;

/** Which output segments a frame touched. */
export type SegmentChange = {
  created: SegmentId[];
  updated: SegmentId[];
};

export class TurnView {
  readonly runId: string;
  private text = "";
  private readonly tools = new Map<string, ToolEntry>();
  private readonly toolOrder: string[] = [];
  private readonly replDisplays = new Map<string, ReplDisplayEntry>();
  private anonymousDisplays = 0;
  private truncated = false;
  private terminal: TerminalState | null = null;
  private usage: UsageSummary | undefined;
  private model: ModelIdentity | undefined;
  private readonly dirty = new Set<SegmentId>();

  constructor(runId: string) {
    this.runId = runId;
  }

  get isTerminal(): boolean {
    return this.terminal !== null;
  }

  get terminalState(): TerminalState | null {
    return this.terminal;
  }

  /** REPL rich displays in first-appearance order. */
  replList(): ReplDisplayEntry[] {
    return [...this.replDisplays.values()];
  }

  /** The stable output-segment id of a REPL display entry (must match the
   * dirty keys reported by {@link takeDirty}). */
  replSegmentId(entry: ReplDisplayEntry): SegmentId {
    const displayId = entry.displayId || `anon:${this.anonIndexOf(entry)}`;
    return `repl:${displayId}`;
  }

  private anonIndexOf(entry: ReplDisplayEntry): number {
    let anon = 0;
    for (const candidate of this.replDisplays.values()) {
      if (candidate === entry) return anon;
      if (candidate.displayId === "") anon++;
    }

    return anon;
  }

  /** Drains the set of output segments touched since the last call. The answer
   * segment is reported dirty while text streamed since the last drain. */
  takeDirty(): Set<SegmentId> {
    if (this.terminal !== null) this.dirty.add(`answer:${this.runId}`);
    const drained = new Set(this.dirty);
    this.dirty.clear();
    return drained;
  }

  /** Applies one frame. Frames of other runs, unknown types, and anything
   * after a terminal frame are ignored — terminal frames may repeat across
   * reconnects and must stay idempotent. REPL presentation frames carry no
   * runId (the session routes them to its single in-flight run). */
  apply(frame: EventFrame): boolean {
    if (this.terminal !== null) return false;
    if (frame.runId !== undefined && frame.runId !== this.runId) return false;
    switch (frame.type) {
      case "repl.display":
      case "repl.updateDisplay": {
        if (typeof frame.data !== "object" || frame.data === null) return false;
        const displayId = typeof frame.displayId === "string" ? frame.displayId : "";
        const key = displayId || `anon:${this.anonymousDisplays++}`;
        this.replDisplays.set(key, { displayId, data: frame.data });
        const segment: SegmentId = `repl:${key}`;
        this.dirty.add(segment);
        return true;
      }
      case "repl.clear": {
        this.replDisplays.clear();
        return true;
      }
      case "repl.error": {
        if (typeof frame.data !== "object" || frame.data === null) return false;
        const key = `anon:${this.anonymousDisplays++}`;
        this.replDisplays.set(key, { displayId: "", data: frame.data });
        this.dirty.add(`repl:${key}`);
        return true;
      }
      default:
        break;
    }
    if (frame.runId !== this.runId && frame.type !== "run.missing") return false;
    switch (frame.type) {
      case "text.delta": {
        if (typeof frame.text !== "string" || frame.text.length === 0) return false;
        this.text += frame.text;
        this.dirty.add(`answer:${this.runId}`);
        return true;
      }
      case "tool.started": {
        if (typeof frame.callId === "string") {
          this.tools.set(frame.callId, {
            callId: frame.callId,
            tool: typeof frame.tool === "string" ? frame.tool : "unknown",
            status: "running",
            args: summarize(frame.arguments),
            startedAt: Date.now(),
          });
          this.toolOrder.push(frame.callId);
          this.dirty.add(`tools:${this.runId}`);
        }
        return true;
      }
      case "tool.finished": {
        if (typeof frame.callId === "string") {
          const entry = this.tools.get(frame.callId);
          const failed = isFailureResult(frame.result);
          if (entry) {
            entry.status = failed ? "error" : "ok";
            if (entry.startedAt !== undefined) entry.durationMs = Date.now() - entry.startedAt;
          }
          this.dirty.add(`tools:${this.runId}`);
        }
        return true;
      }
      case "tool.progress": {
        // Progress rides the matching call's line; a call that never started
        // has no line to update and is ignored.
        if (typeof frame.callId === "string") {
          const entry = this.tools.get(frame.callId);
          const text = frame.content?.text;
          if (entry && typeof text === "string" && text.length > 0) {
            entry.progress = summarize(text);
            this.dirty.add(`tools:${this.runId}`);
          }
        }
        return true;
      }
      case "message.completed": {
        // The completed message is authoritative over the eagerly folded
        // deltas: replace the accumulated text with its text parts. A frame
        // without a message leaves the streamed text alone.
        const parts = frame.agentMessage?.parts;
        if (Array.isArray(parts)) {
          this.text = parts
            .filter((part) => part.kind === "text")
            .map((part) => part.text ?? "")
            .join("");
          this.dirty.add(`answer:${this.runId}`);
        }
        return true;
      }
      case "turn.truncated": {
        this.truncated = true;
        return true;
      }
      case "run.completed": {
        this.terminal = {
          kind: "completed",
          truncated: frame.truncated === true || this.truncated,
        };
        this.usage = readUsage(frame);
        this.model = readModel(frame);
        return true;
      }
      case "run.failed": {
        this.terminal = {
          kind: "failed",
          code: typeof frame.code === "string" ? frame.code : "agent_error",
          message: typeof frame.message === "string" ? frame.message : "The agent turn failed.",
        };
        return true;
      }
      case "run.missing": {
        this.terminal = { kind: "missing" };
        return true;
      }
      default:
        return false;
    }
  }

  /** The final assistant markdown, rendered for the cell output. */
  markdown(): string {
    return this.text;
  }

  /** Markdown lines summarizing tool activity, in call order, with argument
   * previews, the latest progress text, and durations (receipt-time measured). */
  toolLines(): string[] {
    return this.toolOrder
      .map((callId) => this.tools.get(callId))
      .filter((entry) => entry !== undefined)
      .map((entry) => {
        const detail = [
          entry.args === undefined ? undefined : `\`${entry.args}\``,
          entry.progress === undefined ? undefined : `\`${entry.progress}\``,
          entry.durationMs === undefined ? undefined : formatDuration(entry.durationMs),
        ].filter((part) => part !== undefined).join(" · ");
        const mark = entry.status === "running" ? "⏳" : entry.status === "error" ? "❌" : "✅";
        return `- ${mark} \`${entry.tool}\`${detail === "" ? "" : ` · ${detail}`}`;
      });
  }

  /** Whether anything user-visible streamed (answer text or REPL displays). */
  get hasStreamedContent(): boolean {
    return this.text.length > 0 || this.replDisplays.size > 0;
  }

  /** The provider-reported usage carried by the terminal frame, when known. */
  get runUsage(): UsageSummary | undefined {
    return this.usage;
  }

  /** The model identity carried by the terminal frame, when known. */
  get runModel(): ModelIdentity | undefined {
    return this.model;
  }

  /** Renders the complete cell output for a finished run. `input` is the
   * submitted cell text; together with `runId` it forms the turn binding that
   * travels inside the structured snapshot (and round-trips through save). */
  finalOutput(input?: string): {
    runId: string;
    input?: string;
    text: string;
    tools: ToolSnapshotView[];
    truncated: boolean;
    repl: ReplDisplayEntry[];
    usage?: UsageSummary;
    model?: ModelIdentity;
    error?: { code: string; message: string };
  } {
    return {
      runId: this.runId,
      ...(input === undefined ? {} : { input }),
      text: this.text,
      repl: this.replList(),
      tools: this.toolOrder
        .map((callId) => this.tools.get(callId))
        .filter((entry) => entry !== undefined)
        .map((entry) => ({
          tool: entry.tool,
          status: entry.status === "error" ? "error" as const : "ok" as const,
          ...(entry.args === undefined ? {} : { argsSummary: entry.args }),
          ...(entry.durationMs === undefined ? {} : { durationMs: entry.durationMs }),
        })),
      truncated: this.truncated || this.terminal?.kind === "completed" && this.terminal.truncated,
      ...(this.usage === undefined ? {} : { usage: this.usage }),
      ...(this.model === undefined ? {} : { model: this.model }),
      error: this.terminal?.kind === "failed"
        ? { code: this.terminal.code, message: this.terminal.message }
        : undefined,
    };
  }
}

export interface ToolSnapshotView {
  tool: string;
  status: "ok" | "error";
  argsSummary?: string;
  durationMs?: number;
}

function readUsage(frame: EventFrame): UsageSummary | undefined {
  const usage = frame.usage;
  if (typeof usage !== "object" || usage === null) return undefined;
  const record = usage as unknown as Record<string, unknown>;
  if (typeof record.inputTokens !== "number" || typeof record.outputTokens !== "number") {
    return undefined;
  }
  return {
    inputTokens: record.inputTokens,
    outputTokens: record.outputTokens,
    ...(typeof record.totalTokens === "number" ? { totalTokens: record.totalTokens } : {}),
  };
}

function readModel(frame: EventFrame): ModelIdentity | undefined {
  const model = frame.model;
  if (typeof model !== "object" || model === null) return undefined;
  const record = model as unknown as Record<string, unknown>;
  if (
    typeof record.profileId !== "string" || typeof record.provider !== "string" ||
    typeof record.model !== "string"
  ) return undefined;
  return { profileId: record.profileId, provider: record.provider, model: record.model };
}

/** 900 → "0.9s", 61000 → "1m 1s". */
function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${Math.round(seconds - minutes * 60)}s`;
}

/** Output item mimes: markdown renders; the turn snapshot round-trips structure. */
export const AgentOutputMime = "text/markdown";
export const ToolOutputMime = "application/vnd.maieutics.tool+json";
export const TurnOutputMime = "application/vnd.maieutics.turn+json";

function isFailureResult(result: unknown): boolean {
  if (typeof result !== "object" || result === null) return false;
  const status = (result as Record<string, unknown>).status;
  return typeof status === "string" && status !== "ok" && status !== "cancelled";
}
