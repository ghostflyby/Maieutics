/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import {
  isSettled,
  shortTaskId,
  taskDetailText,
  taskPicks,
  TasksEmptyHint,
  taskStatusLabel,
} from "./taskPicks.ts";
import type { TaskListEntry } from "./protocol.ts";

const agentSessionId = "a".repeat(32);

/** A working child run the way the plane serves it (camelCase snapshot). */
function agentTask(overrides: Partial<TaskListEntry> = {}): TaskListEntry {
  return {
    uri: `task://agent/${agentSessionId}/0123456789abcdef0123456789abcdef`,
    kind: "agent",
    status: "working",
    agent: {
      agentSessionId,
      runId: "0123456789abcdef0123456789abcdef",
    },
    ...overrides,
  };
}

/** A timed-out one-shot terminal command that already failed. */
function terminalTask(overrides: Partial<TaskListEntry> = {}): TaskListEntry {
  return {
    uri: `task://terminal/${agentSessionId}/ts-9e58c0d2`,
    kind: "terminal",
    status: "fail",
    terminal: {
      agentSessionId,
      sessionId: "ts-9e58c0d2",
      state: "completed",
      exitCode: 2,
    },
    ...overrides,
  };
}

Deno.test("settled statuses are exactly the terminal vocabulary; unknown stays cancellable", () => {
  assert(!isSettled("working"));
  assert(isSettled("complete"));
  assert(isSettled("fail"));
  assert(isSettled("cancel"));
  // A list entry without status (catalog identity) must offer cancel: the
  // endpoint answers a terminal task unchanged, so this is always safe.
  assert(!isSettled(undefined));
  // An unknown future status is not assumed settled.
  assert(!isSettled("deferred"));
});

Deno.test("short ids come from the authority detail and fall back to the URI", () => {
  assertEquals(shortTaskId(agentTask()), "89abcdef");
  assertEquals(shortTaskId(terminalTask()), "9e58c0d2");
  // A catalog-identity entry carries no detail: the URI's last segment stands
  // in, whatever authority minted it.
  assertEquals(shortTaskId({ uri: "task://future/session-x/task-77", kind: "future" }), "task-77");
});

Deno.test("status labels name the exit code of settled one-shots", () => {
  assertEquals(taskStatusLabel(agentTask()), "working");
  assertEquals(taskStatusLabel(terminalTask()), "fail · exit 2");
  // No exit code yet (still settling): the status alone.
  assertEquals(
    taskStatusLabel(terminalTask({
      status: "cancel",
      terminal: {
        agentSessionId,
        sessionId: "ts-9e58c0d2",
        state: "closing",
      },
    })),
    "cancel",
  );
  // Catalog identity without status: unknown, never a guess.
  assertEquals(taskStatusLabel({ uri: "task://agent/x/y", kind: "agent" }), "unknown");
});

Deno.test("working tasks pick as cancel, settled tasks pick as detail", () => {
  const picks = taskPicks([agentTask(), terminalTask()]);
  assertEquals(picks.length, 2);
  assertEquals(picks[0].action, { kind: "cancel", task: agentTask() });
  assertEquals(picks[1].action, { kind: "detail", task: terminalTask() });
});

Deno.test("picks label by kind and short id, describe by status, tolerate unknown kinds", () => {
  const [pick] = taskPicks([agentTask()]);
  assertEquals(pick.label, "$(git-branch) agent 89abcdef");
  assertEquals(pick.description, "working");

  const [terminal] = taskPicks([terminalTask()]);
  assertEquals(terminal.label, "$(terminal) terminal 9e58c0d2");
  assertEquals(terminal.description, "fail · exit 2");

  // An authority this client has never heard of still lists and cancels.
  const [future] = taskPicks([{
    uri: "task://future/session-x/task-77",
    kind: "future",
    description: "A running future task.",
  }]);
  assertEquals(future.label, "$(circle-outline) future task-77");
  assertEquals(future.description, "unknown");
  assertEquals(future.action, {
    kind: "cancel",
    task: {
      uri: "task://future/session-x/task-77",
      kind: "future",
      description: "A running future task.",
    },
  });
});

Deno.test("pick detail previews the report's first line with the truncation marker", () => {
  const reported = agentTask({
    agent: {
      agentSessionId,
      runId: "0123456789abcdef0123456789abcdef",
      report: "first line\nsecond line",
      reportTruncated: false,
    },
  });
  assertEquals(taskPicks([reported])[0].detail, "first line");

  const truncated = agentTask({
    status: "complete",
    agent: {
      agentSessionId,
      runId: "0123456789abcdef0123456789abcdef",
      report: "x".repeat(200),
      reportTruncated: true,
    },
  });
  const detail = taskPicks([truncated])[0].detail;
  assert(detail !== undefined);
  assert(detail.startsWith("x".repeat(120)));
  assert(detail.endsWith("(truncated)"));

  // No report: the one-shot's terminal state, else the server's description.
  assertEquals(taskPicks([terminalTask()])[0].detail, "terminal session ts-9e58c0d2 · completed");
  assertEquals(
    taskPicks([{
      uri: "task://future/s/t",
      kind: "future",
      description: "A running future task.",
    }])[0]
      .detail,
    "A running future task.",
  );
});

Deno.test("detail text carries identity, state, report preview, and usage — never more", () => {
  const text = taskDetailText(agentTask({
    status: "complete",
    description: "A settled subagent run of this process.",
    agent: {
      agentSessionId,
      runId: "0123456789abcdef0123456789abcdef",
      report: "The answer.",
      reportTruncated: true,
      usage: { inputTokens: 120, outputTokens: 34 },
    },
  }));
  assertEquals(
    text,
    [
      `agent task 89abcdef — complete`,
      `task://agent/${agentSessionId}/0123456789abcdef0123456789abcdef`,
      "A settled subagent run of this process.",
      "",
      "The answer.",
      "(report preview truncated)",
      "usage: 120 in / 34 out",
    ].join("\n"),
  );

  const terminal = taskDetailText(terminalTask());
  assert(terminal.includes("terminal session ts-9e58c0d2 is completed (exit 2)"));
  // A terminal task has no agent block: no usage line leaks in.
  assert(!terminal.includes("usage:"));
});

Deno.test("the empty-state hint explains why a fresh install lists nothing", () => {
  assert(TasksEmptyHint.includes("off by default"));
  assert(TasksEmptyHint.includes("one-shot"));
});
