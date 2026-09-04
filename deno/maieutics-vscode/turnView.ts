/**
 * Pure per-run view state machine: folds event frames into a rendered turn.
 * No VSCode or network imports, so the folding behavior is unit-testable and
 * the notebook controller only bridges the result into cell outputs.
 *
 * Text deltas are folded eagerly; the controller decides when to repaint its
 * output (throttling is a rendering concern, not a protocol one — the server
 * forwards every Agent event unsampled).
 */

import type { EventFrame } from "./protocol.ts";

export interface ToolEntry {
  callId: string;
  tool: string;
  status: "running" | "ok" | "error";
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

export class TurnView {
  readonly runId: string;
  private text = "";
  private readonly tools = new Map<string, ToolEntry>();
  private readonly toolOrder: string[] = [];
  private readonly replDisplays = new Map<string, ReplDisplayEntry>();
  private anonymousDisplays = 0;
  private truncated = false;
  private terminal: TerminalState | null = null;

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
        return true;
      }
      default:
        break;
    }
    if (frame.runId !== this.runId && frame.type !== "run.missing") return false;
    switch (frame.type) {
      case "text.delta": {
        if (typeof frame.text === "string") this.text += frame.text;
        return true;
      }
      case "tool.started": {
        if (typeof frame.callId === "string") {
          this.tools.set(frame.callId, {
            callId: frame.callId,
            tool: typeof frame.tool === "string" ? frame.tool : "unknown",
            status: "running",
          });
          this.toolOrder.push(frame.callId);
        }
        return true;
      }
      case "tool.finished": {
        if (typeof frame.callId === "string") {
          const entry = this.tools.get(frame.callId);
          const failed = isFailureResult(frame.result);
          if (entry) entry.status = failed ? "error" : "ok";
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

  /** Markdown lines summarizing tool activity, in call order. */
  toolLines(): string[] {
    return this.toolOrder
      .map((callId) => this.tools.get(callId))
      .filter((entry) => entry !== undefined)
      .map((entry) =>
        entry.status === "running"
          ? `- ⏳ \`${entry.tool}\``
          : entry.status === "error"
          ? `- ❌ \`${entry.tool}\``
          : `- ✅ \`${entry.tool}\``
      );
  }

  /** Renders the complete cell output for a finished run. */
  finalOutput(): {
    text: string;
    tools: ToolSnapshotView[];
    truncated: boolean;
    repl: ReplDisplayEntry[];
    error?: { code: string; message: string };
  } {
    return {
      text: this.text,
      repl: this.replList(),
      tools: this.toolOrder
        .map((callId) => this.tools.get(callId))
        .filter((entry) => entry !== undefined)
        .map((entry) => ({
          tool: entry.tool,
          status: entry.status === "error" ? "error" as const : "ok" as const,
        })),
      truncated: this.truncated || this.terminal?.kind === "completed" && this.terminal.truncated,
      error: this.terminal?.kind === "failed"
        ? { code: this.terminal.code, message: this.terminal.message }
        : undefined,
    };
  }
}

export interface ToolSnapshotView {
  tool: string;
  status: "ok" | "error";
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
