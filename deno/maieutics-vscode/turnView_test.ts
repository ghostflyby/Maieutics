/// <reference lib="deno.window" />

import { assertEquals } from "@std/assert";
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
  assertEquals(view.toolLines(), ["- ✅ `workspace_list`", "- ❌ `repl_execute`"]);
  const final = view.finalOutput();
  assertEquals(final.tools, [
    { tool: "workspace_list", status: "ok" },
    { tool: "repl_execute", status: "error" },
  ]);
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
