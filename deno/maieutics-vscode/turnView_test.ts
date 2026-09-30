/// <reference lib="deno.window" />

import { assert, assertEquals } from "@std/assert";
import {
  fencedDiffLines,
  MaximumSubagents,
  subagentSnapshotLines,
  turnOutputItems,
  type TurnOutputItemSpec,
  TurnOutputMime,
  TurnView,
} from "./turnView.ts";

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

Deno.test("structured edit results render diff fences and travel into the snapshot", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "tool.started", runId: "run-1", callId: "c1", tool: "write_text" });
  view.apply({
    type: "tool.finished",
    runId: "run-1",
    callId: "c1",
    result: {
      status: "ok",
      value: {
        uri: "workspace://local/a.txt",
        operation: "updated",
        diff: {
          unified: "--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n-old\n+new\n",
          additions: 1,
          deletions: 1,
          truncated: false,
        },
      },
    },
  });

  const lines = view.toolLines();
  // The bullet, a blank separator, and the seven fence lines.
  assertEquals(lines.length, 9);
  assertEquals(lines[0].startsWith("- ✅ `write_text` · `+1 −1` · "), true);
  assertEquals(lines[1], "");
  assertEquals(lines[2], "```diff");
  assertEquals(lines[3], "--- a/a.txt");
  assertEquals(lines[4], "+++ b/a.txt");
  assertEquals(lines[5], "@@ -1,2 +1,2 @@");
  assertEquals(lines[6], "-old");
  assertEquals(lines[7], "+new");
  assertEquals(lines[8], "```");

  const final = view.finalOutput();
  assertEquals(final.tools[0].diff, {
    unified: "--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n-old\n+new\n",
    additions: 1,
    deletions: 1,
    truncated: false,
  });
});

Deno.test("failed results and other tools carry no diff", () => {
  const view = new TurnView("run-1");
  view.apply({ type: "tool.started", runId: "run-1", callId: "c1", tool: "edit_text" });
  view.apply({
    type: "tool.finished",
    runId: "run-1",
    callId: "c1",
    result: {
      status: "ok",
      value: { uri: "workspace://local/a.txt", operation: "created" },
    },
  });
  view.apply({ type: "tool.started", runId: "run-1", callId: "c2", tool: "read_text" });
  view.apply({
    type: "tool.finished",
    runId: "run-1",
    callId: "c2",
    result: { status: "ok", value: { text: "irrelevant" } },
  });
  view.apply({ type: "tool.started", runId: "run-1", callId: "c3", tool: "edit_text" });
  view.apply({
    type: "tool.finished",
    runId: "run-1",
    callId: "c3",
    result: { status: "tool_error", code: "workspace_edit_target_not_found", message: "boom" },
  });

  assertEquals(view.toolLines().filter((line) => line.includes("+")).length, 0);
  assertEquals(view.finalOutput().tools.every((tool) => tool.diff === undefined), true);
});

Deno.test("diff fences cap the preview and honor the server truncation flag", () => {
  const long = Array.from({ length: 60 }, (_, index) => `+line ${index}`).join("\n");
  const capped = fencedDiffLines({
    unified: `${long}\n`,
    additions: 60,
    deletions: 0,
    truncated: false,
  });
  assertEquals(capped[0], "```diff");
  assertEquals(capped[capped.length - 1], "```");
  assertEquals(capped.some((line) => line.startsWith("… diff preview truncated")), true);
  // 40 body lines + the note, inside the fence.
  assertEquals(capped.length, 1 + 40 + 1 + 1);

  const flagged = fencedDiffLines({
    unified: "+one\n",
    additions: 1,
    deletions: 0,
    truncated: true,
  });
  assertEquals(flagged.some((line) => line.startsWith("… diff preview truncated")), true);
});

Deno.test("structured output composition: exactly the single turn+json item", () => {
  // The shared composition of the live paint (controller structuredOutput)
  // and the snapshot restore (serializer renderSnapshotOutputs): the timeline
  // renderer is the default view, and a text/markdown sibling would always
  // win VS Code's mime display order — so there must be exactly one item,
  // and it must be the turn+json item the serializer round-trips and the
  // usage badge scans.
  const snapshot = {
    runId: "run-1",
    input: "question",
    text: "answer",
    tools: [{ tool: "workspace_list", status: "ok" as const }],
    truncated: false,
  };
  const items: readonly TurnOutputItemSpec[] = turnOutputItems(snapshot);
  assertEquals(items.length, 1);
  const [item] = items;
  assertEquals(item.mime, TurnOutputMime);
  assert(item.mime !== "text/markdown");
  assertEquals(item.encoding, "json");
  // The payload is embedded verbatim, so save round-trips the full snapshot.
  assertEquals(item.value, snapshot);

  // The restore path feeds the persisted OutputSnapshot shape (all fields
  // optional); the composition accepts it unchanged.
  const persisted = turnOutputItems({ truncated: true, tools: [{ tool: "x", status: "error" }] });
  assertEquals(persisted.length, 1);
  assertEquals(persisted[0].mime, TurnOutputMime);
});

Deno.test("subagent deltas fold in order into their child view", () => {
  const view = new TurnView("run-1");
  assert(view.applySubagent({ type: "run.started", runId: "child-1" }));
  assert(view.applySubagent({ type: "text.delta", runId: "child-1", sequence: 1, text: "hel" }));
  assert(view.applySubagent({ type: "text.delta", runId: "child-1", sequence: 2, text: "lo" }));
  // A second child folds alongside, in first-appearance order.
  assert(view.applySubagent({ type: "run.started", runId: "child-2" }));
  assert(view.applySubagent({ type: "text.delta", runId: "child-2", sequence: 1, text: "salut" }));

  assertEquals(view.markdown(), ""); // the parent text is untouched
  const [first, second] = view.finalOutput().subagents ?? [];
  assertEquals(first, { status: "running", text: "hello", tools: [] });
  assertEquals(second, { status: "running", text: "salut", tools: [] });
  assertEquals(view.isTerminal, false);
});

Deno.test("subagent tool lifecycle, progress, and authoritative message text fold", () => {
  const view = new TurnView("run-1");
  view.applySubagent({ type: "run.started", runId: "child-1" });
  view.applySubagent({
    type: "tool.started",
    runId: "child-1",
    callId: "c1",
    tool: "workspace_search",
    arguments: { pattern: "route(" },
  });
  view.applySubagent({
    type: "tool.progress",
    runId: "child-1",
    callId: "c1",
    content: { kind: "text", text: "scanning src/" },
  });
  view.applySubagent({
    type: "tool.finished",
    runId: "child-1",
    callId: "c1",
    result: { status: "ok", value: [] },
  });
  view.applySubagent({ type: "text.delta", runId: "child-1", sequence: 1, text: "draft" });
  // The completed message is authoritative over the streamed deltas.
  view.applySubagent({
    type: "message.completed",
    runId: "child-1",
    agentMessage: { role: "assistant", parts: [{ kind: "text", text: "final report" }] },
  });

  const [child] = view.finalOutput().subagents ?? [];
  assertEquals(child.status, "running");
  assertEquals(child.text, "final report");
  assertEquals(child.tools.length, 1);
  assertEquals(child.tools[0].tool, "workspace_search");
  assertEquals(child.tools[0].argsSummary, `{"pattern":"route("}`);
  assertEquals(child.tools[0].status, "ok");
});

Deno.test("child terminal frames settle the child, never the parent", () => {
  const view = new TurnView("run-1");
  view.applySubagent({ type: "run.started", runId: "child-1" });
  assert(view.applySubagent({ type: "run.completed", runId: "child-1", sequence: 9 }));
  assert(!view.isTerminal);
  assertEquals(view.terminalState, null);

  view.applySubagent({ type: "run.started", runId: "child-2" });
  assert(view.applySubagent({ type: "run.failed", runId: "child-2", code: "cancelled" }));
  view.applySubagent({ type: "run.started", runId: "child-3" });
  assert(view.applySubagent({ type: "run.failed", runId: "child-3", code: "task_failed" }));

  const subagents = view.finalOutput().subagents ?? [];
  assertEquals(subagents.map((child) => ({ status: child.status, code: child.code })), [
    { status: "ok", code: undefined },
    { status: "cancelled", code: "cancelled" },
    { status: "failed", code: "task_failed" },
  ]);
  // The parent stays runnable: apply() still owns its lifecycle.
  assert(view.apply({ type: "run.completed", runId: "run-1", truncated: false }));
  assertEquals(view.terminalState, { kind: "completed", truncated: false });
});

Deno.test("apply still rejects foreign runIds; the parent state is never polluted", () => {
  const view = new TurnView("run-1");
  assertEquals(view.apply({ type: "text.delta", runId: "child-1", sequence: 1, text: "x" }), false);
  assertEquals(view.apply({ type: "run.completed", runId: "child-1" }), false);
  assertEquals(view.markdown(), "");
  assertEquals(view.isTerminal, false);
  assertEquals(view.finalOutput().subagents, undefined);
  // And the child fold ignores run-less frames (repl/input are parent-only
  // or server-declined for children).
  assertEquals(view.applySubagent({ type: "text.delta", text: "x" }), false);
  assertEquals(view.applySubagent({ type: "repl.display", runId: "child-1", data: {} }), false);
  assertEquals((view.finalOutput().subagents ?? []).length, 0);
});

Deno.test("subagent folds report the shared subagents segment as dirty", () => {
  const view = new TurnView("run-1");
  view.applySubagent({ type: "run.started", runId: "child-1" });
  let dirty = view.takeDirty();
  assertEquals([...dirty], [`subagents:run-1`]);

  view.applySubagent({ type: "text.delta", runId: "child-1", sequence: 1, text: "hi" });
  dirty = view.takeDirty();
  assertEquals([...dirty], [`subagents:run-1`]);
  assertEquals(view.takeDirty().size, 0); // drained

  // Terminal child frames dirty the segment too (the status mark flips).
  view.applySubagent({ type: "run.completed", runId: "child-1" });
  assertEquals([...view.takeDirty()], [`subagents:run-1`]);
});

Deno.test("hasStreamedContent includes child activity", () => {
  const view = new TurnView("run-1");
  assertEquals(view.hasStreamedContent, false);
  view.applySubagent({ type: "run.started", runId: "child-1" });
  // A bare registration is not content yet.
  assertEquals(view.hasStreamedContent, false);
  view.applySubagent({ type: "tool.started", runId: "child-1", callId: "c1", tool: "t" });
  assertEquals(view.hasStreamedContent, true);
});

Deno.test("a failed parent run keeps the streamed child timeline", () => {
  const view = new TurnView("run-1");
  view.applySubagent({ type: "run.started", runId: "child-1" });
  view.applySubagent({ type: "text.delta", runId: "child-1", sequence: 1, text: "partial" });
  view.apply({ type: "run.failed", runId: "run-1", code: "agent_error", message: "down" });
  const final = view.finalOutput();
  assertEquals(final.error, { code: "agent_error", message: "down" });
  assertEquals(final.subagents?.[0].text, "partial");
  // The failure paint keeps streamed content: the segment lines still render.
  assert(view.subagentLines().some((line) => line.includes("partial")));
});

Deno.test("subagentLines quote-prefix every line so child markdown cannot escape", () => {
  const view = new TurnView("run-1");
  view.applySubagent({ type: "run.started", runId: "child-1" });
  view.applySubagent({
    type: "tool.started",
    runId: "child-1",
    callId: "c1",
    tool: "workspace_read",
    arguments: "src/a.ts",
  });
  view.applySubagent({
    type: "tool.finished",
    runId: "child-1",
    callId: "c1",
    result: { status: "ok", value: {} },
  });
  view.applySubagent({
    type: "text.delta",
    runId: "child-1",
    sequence: 1,
    text: "# Heading\n\n- list item\n",
  });

  const lines = view.subagentLines();
  assert(lines[0].startsWith("> 🤖 **子代理** · ⏳"), lines.join("\n"));
  for (const line of lines) {
    // Bare ">" is the quote-continuation marker for blank child lines.
    assert(line === "" || line === ">" || line.startsWith("> "), line);
  }
  // The tool finished ok before the text arrived.
  assert(lines.some((line) => line.startsWith("> - ✅ `workspace_read`")), lines.join("\n"));
  assert(lines.some((line) => line.startsWith("> # Heading")), lines.join("\n"));
});

Deno.test("subagent folds cap child count and text length", () => {
  const view = new TurnView("run-1");
  for (let index = 0; index < MaximumSubagents + 3; index++) {
    view.applySubagent({ type: "run.started", runId: `child-${index}` });
  }
  assertEquals((view.finalOutput().subagents ?? []).length, MaximumSubagents);
  // The overflow note renders once in the shared segment.
  assert(
    view.subagentLines().some((line) => line.includes("子代理过多")),
    view.subagentLines().join("\n"),
  );

  const capped = new TurnView("run-1");
  capped.applySubagent({ type: "run.started", runId: "child-1" });
  const chunk = "x".repeat(1500);
  for (let index = 0; index < 30; index++) {
    capped.applySubagent({ type: "text.delta", runId: "child-1", sequence: index, text: chunk });
  }
  const [child] = capped.finalOutput().subagents ?? [];
  // The stored fold capped at 20 000 chars; the snapshot appends the marker.
  assertEquals(child.text.length, 20_002);
  assert(child.text.endsWith("\n…"), "capped text carries the ellipsis marker");
});

Deno.test("subagentSnapshotLines composes blocks with blank-line separators", () => {
  const lines = subagentSnapshotLines([
    { status: "ok", text: "done\n", tools: [{ tool: "workspace_list", status: "ok" }] },
    { status: "failed", text: "", tools: [], code: "cancelled" },
  ]);
  assertEquals(lines, [
    "> 🤖 **子代理** · ✅",
    "> - ✅ `workspace_list`",
    "> done",
    "",
    "> 🤖 **子代理** · ❌ (`cancelled`)",
  ]);
});
