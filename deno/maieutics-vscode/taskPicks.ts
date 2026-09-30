/**
 * Pure pick-model for the session task surface (ADR 0028): turns task list
 * entries into quick-pick rows and decides what executing a row does —
 * working tasks cancel by URI, settled tasks show their bounded detail.
 * No vscode or network imports, so the decisions are unit-testable. Unknown
 * task kinds are tolerated (ADR 0031): they render generically and cancel by
 * URI like any other authority.
 */

import type { TaskListEntry } from "./protocol.ts";

/** What executing one picked row does: the row's task rides along so the
 * caller can route the cancel (run cancel for agent children, the task
 * endpoint for everything else) without re-finding the entry. */
export type TaskPickAction =
  | { kind: "cancel"; task: TaskListEntry }
  | { kind: "detail"; task: TaskListEntry };

/** One quick-pick row (structurally a vscode.QuickPickItem plus the action). */
export interface TaskPick {
  label: string;
  description: string;
  detail?: string;
  action: TaskPickAction;
}

/** Why the task list is empty, so the surface explains itself: subagents are
 * off by default, and one-shot terminals only surface on timeout. */
export const TasksEmptyHint =
  "Subagent runs appear here while they execute (subagents are off by default); " +
  "timed-out one-shot terminal commands appear until closed.";

/** A task whose lifecycle already ended: cancelling is idempotent, so there
 * is nothing to do but look at it. An unknown or missing status is treated as
 * unsettled — the cancel endpoint answers a terminal task unchanged, so
 * offering cancel on a stale status is always safe. */
export function isSettled(status: string | undefined): boolean {
  return status === "complete" || status === "fail" || status === "cancel";
}

/** The short display id: the authority's tail id when its detail is present
 * (the agent child's run id, the terminal session id), else the URI's last
 * path segment. Eight hex/id characters are enough to tell tasks apart. */
export function shortTaskId(task: TaskListEntry): string {
  const candidate = task.agent?.runId ?? task.terminal?.sessionId ?? lastUriSegment(task.uri);
  return candidate.slice(-8);
}

/** The status word plus what sharpens it (a one-shot's exit code). Absent
 * statuses render as "unknown" instead of guessing. */
export function taskStatusLabel(task: TaskListEntry): string {
  const status = task.status ?? "unknown";
  const exitCode = task.terminal?.exitCode;
  return exitCode === undefined ? status : `${status} · exit ${exitCode}`;
}

/** The quick-pick rows, server order (newest children first is the server's
 * choice, not ours). */
export function taskPicks(tasks: readonly TaskListEntry[]): TaskPick[] {
  return tasks.map((task) => ({
    label: `${kindIcon(task.kind)} ${task.kind} ${shortTaskId(task)}`,
    description: taskStatusLabel(task),
    detail: pickDetail(task),
    action: isSettled(task.status) ? { kind: "detail", task } : { kind: "cancel", task },
  }));
}

/** The bounded detail text of one task: identity, status, and whatever the
 * plane carries — a one-shot's terminal state or the child's report preview
 * and usage. Never fetches anything and never carries a transcript. */
export function taskDetailText(task: TaskListEntry): string {
  const lines = [
    `${task.kind} task ${shortTaskId(task)} — ${taskStatusLabel(task)}`,
    task.uri,
  ];
  if (task.terminal !== undefined) {
    lines.push(
      `terminal session ${task.terminal.sessionId} is ${task.terminal.state}` +
        (task.terminal.exitCode === undefined ? "" : ` (exit ${task.terminal.exitCode})`),
    );
  }
  if (task.description !== undefined && task.description.length > 0) lines.push(task.description);

  const agent = task.agent;
  if (agent?.report !== undefined && agent.report.length > 0) {
    lines.push("", agent.report);
    if (agent.reportTruncated === true) lines.push("(report preview truncated)");
  }
  const usage = agent?.usage;
  if (usage !== undefined) {
    const parts: string[] = [];
    if (usage.inputTokens !== undefined) parts.push(`${usage.inputTokens} in`);
    if (usage.outputTokens !== undefined) parts.push(`${usage.outputTokens} out`);
    if (parts.length > 0) lines.push(`usage: ${parts.join(" / ")}`);
  }
  return lines.join("\n");
}

function kindIcon(kind: string): string {
  if (kind === "agent") return "$(git-branch)";
  if (kind === "terminal") return "$(terminal)";
  return "$(circle-outline)";
}

/** The one-line pick detail: the report's first line when there is one, else
 * the one-shot's terminal state, else the server's own description. */
function pickDetail(task: TaskListEntry): string | undefined {
  const report = task.agent?.report;
  if (report !== undefined && report.length > 0) {
    const firstLine = report.split("\n", 1)[0];
    const preview = firstLine.length > 120 ? `${firstLine.slice(0, 120)}…` : firstLine;
    return task.agent?.reportTruncated === true ? `${preview} (truncated)` : preview;
  }
  if (task.terminal !== undefined) {
    return `terminal session ${task.terminal.sessionId} · ${task.terminal.state}`;
  }
  return task.description;
}

function lastUriSegment(uri: string): string {
  const withoutQuery = uri.split("?", 1)[0];
  const segment = withoutQuery.split("/").at(-1) ?? withoutQuery;
  return segment.length > 0 ? segment : uri;
}
