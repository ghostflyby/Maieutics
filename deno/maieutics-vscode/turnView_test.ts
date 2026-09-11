/// <reference lib="deno.window" />

import { assert, assertEquals } from "@std/assert";
import { TurnView } from "./turnView.ts";

Deno.test("text deltas fold in order", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "run.started", runId: "run-1" });
  view.apply({ type: "text.delta", runId: "run-1", sequence: 1, text: "hel" });
  view.apply({ type: "text.delta", runId: "run-1", sequence: 2, text: "lo" });
  assertEquals(view.markdown(), "hello");
  assertEquals(view.isTerminal, false);
});

Deno.test("tool lifecycle renders statuses in call order", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "tool.started", runId: "run-1", callId: "c1", tool: "workspace_list" });
  view.apply({ type: "tool.started", runId: "run-1", callId: "c2", tool: "repl_execute" });
  assertEquals(view.toolLines(), ["- ⏳ `workspace_list`", "- ⏳ `repl_execute`"]);
  view.apply({
    type: "tool.finished",
    runId: "run-1",
    callId: "c1",
    result: { status: "ok", value: [] },
  });
  view.apply({
    type: "tool.finished",
    runId: "run-1",
    callId: "c2",
    result: { status: "tool_error", message: "boom" },
  });
  // Finished entries carry a receipt-time duration segment.
  const [first, second] = view.toolLines();
  assertEquals(first.startsWith("- ✅ `workspace_list` · "), true);
  assertEquals(second.startsWith("- ❌ `repl_execute` · "), true);
  const final = view.finalOutput();
  assertEquals(
    final.tools.map((tool) => ({
      tool: tool.tool,
      status: tool.status,
      timed: typeof tool.durationMs === "number",
    })),
    [
      { tool: "workspace_list", status: "ok", timed: true },
      { tool: "repl_execute", status: "error", timed: true },
    ],
  );
});

Deno.test("completed terminal carries truncation", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "turn.truncated", runId: "run-1" });
  view.apply({ type: "run.completed", runId: "run-1", truncated: false });
  assertEquals(view.terminalState, { kind: "completed", truncated: true });
  assertEquals(view.finalOutput().truncated, true);
});

Deno.test("failed terminal carries code and message", () => {
  const view = new TurnView("run-1");
  view.apply({
    type: "run.failed",
    runId: "run-1",
    code: "agent_provider_error",
    message: "provider down",
  });
  assertEquals(view.terminalState, {
    kind: "failed",
    code: "agent_provider_error",
    message: "provider down",
  });
});

Deno.test("frames of other runs are ignored", () => {
  const view = new TurnView("run-1");
  assertEquals(view.apply({ type: "text.delta", runId: "run-2", sequence: 1, text: "no" }), false);
  assertEquals(view.markdown(), "");
});

Deno.test("frames after the terminal are ignored (idempotent terminals)", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "run.completed", runId: "run-1", truncated: false });
  view.apply({ type: "run.completed", runId: "run-1", truncated: true });
  assertEquals(view.terminalState, { kind: "completed", truncated: false });
});

Deno.test("run.missing terminates the view", () => {
  const view = new TurnView("run-9");
  view.apply({ type: "run.missing", runId: "run-9" });
  assertEquals(view.terminalState, { kind: "missing" });
});

Deno.test("repl displays fold by display id and update in place", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "repl.display", displayId: "d1", data: { "text/html": "<b>t</b>" } });
  view.apply({ type: "repl.display", data: { "text/plain": "untracked" } });
  view.apply({
    type: "repl.updateDisplay",
    displayId: "d1",
    data: { "text/markdown": "**updated**" },
  });

  const list = view.replList();
  assertEquals(list.length, 2);
  assertEquals(list[0].displayId, "d1");
  assertEquals(list[0].data, { "text/markdown": "**updated**" });
  assertEquals(list[1].data, { "text/plain": "untracked" });
});

Deno.test("repl clear empties the display list and errors append displays", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "repl.display", displayId: "d1", data: { "text/plain": "x" } });
  view.apply({ type: "repl.clear" });
  assertEquals(view.replList().length, 0);

  view.apply({ type: "repl.error", data: { "text/plain": "Boom: broken" } });
  const final = view.finalOutput();
  assertEquals(final.repl.length, 1);
  assertEquals(final.repl[0].data, { "text/plain": "Boom: broken" });
});

Deno.test("repl frames after the terminal are ignored", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "run.completed", runId: "run-1", truncated: false });
  assertEquals(
    view.apply({ type: "repl.display", displayId: "d1", data: { "text/plain": "late" } }),
    false,
  );
  assertEquals(view.replList().length, 0);
});

Deno.test("takeDirty reports only the touched segments since the last drain", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "run.started", runId: "run-1" });

  // Nothing dirty before content
  assertEquals(view.takeDirty().size, 0);

  view.apply({ type: "text.delta", runId: "run-1", sequence: 1, text: "a" });
  view.apply({ type: "tool.started", runId: "run-1", callId: "c1", tool: "echo" });
  const dirty = view.takeDirty();
  assertEquals([...dirty].some((key) => key === "tools:run-1"), true);
  // A text delta marks the answer segment dirty.
  assertEquals([...dirty].some((key) => key.startsWith("answer:")), true);

  // Drained: empty again
  assertEquals(view.takeDirty().size, 0);
});

Deno.test("repl updates mark the same segment id as their display id", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "repl.display", displayId: "d1", data: { "text/html": "a" } });
  const created = view.takeDirty();
  assertEquals([...created].includes("repl:d1"), true);

  view.apply({ type: "repl.updateDisplay", displayId: "d1", data: { "text/html": "b" } });
  const updated = view.takeDirty();
  assertEquals([...updated].includes("repl:d1"), true);

  // The segment id returned by replSegmentId matches the dirty key.
  const entry = view.replList().find((candidate) => candidate.displayId === "d1");
  assert(entry !== undefined);
  assertEquals(view.replSegmentId(entry), "repl:d1");
});

Deno.test("terminal drain always includes the answer segment", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "run.completed", runId: "run-1", truncated: false });
  const dirty = view.takeDirty();
  assertEquals([...dirty].includes("answer:run-1"), true);
});

Deno.test("final output carries the turn binding for committed cells", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "text.delta", runId: "run-1", sequence: 1, text: "answer" });
  view.apply({ type: "run.completed", runId: "run-1" });

  const final = view.finalOutput("the question");
  assertEquals(final.runId, "run-1");
  assertEquals(final.input, "the question");
  // Without the submitted input (older callers) the binding degrades.
  assertEquals(view.finalOutput().input, undefined);
  assertEquals(view.finalOutput().runId, "run-1");
});

Deno.test("terminal usage and model identity are captured", () => {
  const view = new TurnView("run-1");
  view.apply({
    type: "text.delta",
    runId: "run-1",
    sequence: 1,
    text: "answer",
  });
  view.apply({
    type: "run.completed",
    runId: "run-1",
    model: { profileId: "default", provider: "OpenAI", model: "gpt-test" },
    usage: { inputTokens: 11, outputTokens: 7, totalTokens: 18 },
  });

  const final = view.finalOutput("question");
  assertEquals(final.usage, { inputTokens: 11, outputTokens: 7, totalTokens: 18 });
  assertEquals(final.model, { profileId: "default", provider: "OpenAI", model: "gpt-test" });
  // Older servers omit both; the snapshot degrades instead of failing.
  assertEquals(new TurnView("r2").finalOutput().usage, undefined);
});

Deno.test("tool lines carry argument previews and durations", () => {
  const view = new TurnView("run-1");
  view.apply({
    type: "tool.started",
    runId: "run-1",
    callId: "c1",
    tool: "workspace_list",
    arguments: { root: "/repos/alpha", depth: 3 },
  });
  assertEquals(view.toolLines(), [
    "- ⏳ `workspace_list` · `{" +
    '"root":"/repos/alpha","depth":3}`',
  ]);

  view.apply({ type: "tool.finished", runId: "run-1", callId: "c1", result: { status: "ok" } });
  const line = view.toolLines()[0];
  assertEquals(line.startsWith("- ✅ `workspace_list` · `"), true);
  assertEquals(line.includes("` · "), true); // the duration segment follows

  // Malformed or absent arguments render as the bare tool name.
  const bare = new TurnView("run-2");
  bare.apply({ type: "tool.started", runId: "run-2", callId: "c2", tool: "repl_execute" });
  assertEquals(bare.toolLines(), ["- ⏳ `repl_execute`"]);

  // Arguments are untrusted model output: backticks cannot break the code
  // span the preview is embedded in.
  const hostile = new TurnView("run-3");
  hostile.apply({
    type: "tool.started",
    runId: "run-3",
    callId: "c3",
    tool: "terminal_run",
    arguments: { command: "echo `injected` \u0007" },
  });
  const hostileLine = hostile.toolLines()[0];
  assertEquals(hostileLine.includes("injected"), true);
  assertEquals(hostileLine.includes("`injected`"), false);
  // The preview stays one intact code span (open + close) beside the tool
  // name's own span.
  assertEquals(hostileLine.split("`").length, 5);
});

Deno.test("message.completed replaces the folded text with the authoritative parts", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "run.started", runId: "run-1" });
  view.apply({ type: "text.delta", runId: "run-1", sequence: 1, text: "drif" });
  view.apply({ type: "text.delta", runId: "run-1", sequence: 2, text: "t" });
  assertEquals(view.markdown(), "drift");

  view.apply({
    type: "message.completed",
    runId: "run-1",
    sequence: 3,
    messageId: "m1",
    agentMessage: {
      role: "assistant",
      parts: [
        { kind: "text", text: "The answer" },
        { kind: "tool_call", callId: "c1", name: "workspace_read" },
        { kind: "text", text: " is 42." },
      ],
    },
  });
  // Text parts fold in order; non-text parts contribute nothing.
  assertEquals(view.markdown(), "The answer is 42.");
  // The repaint is requested through the same dirty segment as the deltas.
  const dirty = view.takeDirty();
  assertEquals([...dirty].includes("answer:run-1"), true);
});

Deno.test("message.completed without a message leaves the streamed text alone", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "text.delta", runId: "run-1", sequence: 1, text: "partial" });
  view.apply({ type: "message.completed", runId: "run-1", sequence: 2, messageId: "m1" });
  assertEquals(view.markdown(), "partial");
});

Deno.test("tool.progress updates the matching call's line", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "tool.started", runId: "run-1", callId: "c1", tool: "repl_execute" });
  view.apply({
    type: "tool.progress",
    runId: "run-1",
    sequence: 2,
    callId: "c1",
    content: { kind: "text", text: "executing cell 1/3" },
  });
  const line = view.toolLines()[0];
  assertEquals(line.includes("executing cell 1/3"), true);
  const dirty = view.takeDirty();
  assertEquals([...dirty].includes("tools:run-1"), true);

  // Last progress wins, so chatty tools stay on one bounded line; the
  // preview is untrusted output and cannot break the code span.
  view.apply({
    type: "tool.progress",
    runId: "run-1",
    sequence: 3,
    callId: "c1",
    content: { kind: "text", text: "executing cell 2/3 `quoted`" },
  });
  const updated = view.toolLines()[0];
  assertEquals(updated.includes("cell 1/3"), false);
  assertEquals(updated.includes("cell 2/3"), true);
  assertEquals(updated.includes("`quoted`"), false);
});

Deno.test("tool.progress for unknown calls or non-text content is ignored", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "tool.started", runId: "run-1", callId: "c1", tool: "repl_execute" });
  view.takeDirty(); // drain the started mark

  // A progress frame for a call that never started has no line to update.
  view.apply({
    type: "tool.progress",
    runId: "run-1",
    sequence: 1,
    callId: "ghost",
    content: { kind: "text", text: "no such call" },
  });
  assertEquals(view.toolLines(), ["- ⏳ `repl_execute`"]);

  // Non-text progress content contributes nothing.
  view.apply({
    type: "tool.progress",
    runId: "run-1",
    sequence: 2,
    callId: "c1",
    content: { kind: "data", value: { step: 2 } },
  });
  assertEquals(view.toolLines(), ["- ⏳ `repl_execute`"]);
  assertEquals(view.takeDirty().size, 0);
});
