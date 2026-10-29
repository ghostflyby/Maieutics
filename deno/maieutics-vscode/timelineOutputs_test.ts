import { assert, assertEquals } from "@std/assert";
import {
  emptyNotebook,
  mergeOutputSnapshots,
  parseNotebook,
  serializeNotebook,
} from "./notebookFormat.ts";
import { TimelineOutputCache, timelineOutputSnapshots } from "./timelineOutputs.ts";
import { TurnView } from "./turnView.ts";

Deno.test("streaming headers do not serialize answer or rich display payloads", () => {
  const snapshot = {
    text: "Answer",
    repl: [{ displayId: "d", data: { "text/plain": "rich value" } }],
    timeline: [{ kind: "text" as const, messageId: "m", text: "Answer" }],
  };
  const nodes = timelineOutputSnapshots(snapshot, true);
  assertEquals(nodes[0].text, undefined);
  assertEquals(nodes[0].repl, undefined);
  assertEquals(mergeOutputSnapshots(nodes)?.text, "Answer");
});

Deno.test("text paints reuse headers, tools, reasoning and child nodes without traversing old tool payloads", () => {
  const view = new TurnView("r");
  view.apply({ type: "reasoning.delta", runId: "r", messageId: "m", text: "Summary" });
  view.apply({ type: "tool.started", runId: "r", callId: "c", tool: "read_file" });
  view.apply({
    type: "tool.finished",
    runId: "r",
    callId: "c",
    result: { status: "ok", value: "x".repeat(19000) },
  });
  view.applySubagent({ type: "text.delta", runId: "child", messageId: "c", text: "Child report" });
  view.apply({ type: "text.delta", runId: "r", messageId: "m", text: "First" });
  const cache = new TimelineOutputCache();
  const snapshot = view.renderOutput("Question");
  const before = cache.update(snapshot, true);
  let reads = 0;
  const result = snapshot.tools[0].result;
  Object.defineProperty(snapshot.tools[0], "result", {
    get: () => {
      reads++;
      return result;
    },
    configurable: true,
  });
  view.apply({ type: "text.delta", runId: "r", messageId: "m", text: " second" });
  const after = cache.update(view.renderOutput("Question"), true);
  const changed = after.filter((node, index) => node !== before[index]);
  assertEquals(changed.map((node) => node.nodeId), ["entry:3"]);
  for (const node of changed) JSON.stringify(node);
  assertEquals(
    reads,
    0,
    "A text paint must neither clone nor stringify previously retained tool details",
  );
  view.apply({
    type: "tool.progress",
    runId: "r",
    callId: "c",
    content: { kind: "text", text: "Changed" },
  });
  const third = cache.update(view.renderOutput("Question"), true);
  assertEquals(third.filter((node, index) => node !== after[index]).map((node) => node.nodeId), [
    "entry:1",
  ]);
});

Deno.test("joining text nodes preserves an explicit empty final message and error retry text", () => {
  const timeline = [{ kind: "text" as const, messageId: "m", text: "Earlier message" }];
  assertEquals(mergeOutputSnapshots(timelineOutputSnapshots({ text: "", timeline }))?.text, "");
  const error = { code: "cancelled", message: "Cancelled" };
  assertEquals(
    mergeOutputSnapshots(timelineOutputSnapshots({ text: "Retry action", error, timeline }))?.text,
    "Retry action",
  );
});

Deno.test("each timeline node is a self-contained output with stable identity and no duplicated whole-turn arrays", () => {
  const view = new TurnView("r");
  view.apply({ type: "reasoning.delta", runId: "r", messageId: "m", text: "Summary" });
  view.apply({ type: "text.delta", runId: "r", messageId: "m", text: "Partial" });
  view.apply({
    type: "tool.started",
    runId: "r",
    callId: "c",
    tool: "read_file",
    arguments: { path: "a.ts" },
  });
  const before = timelineOutputSnapshots(view.finalOutput("Question"), true);
  assertEquals(before.map((node) => node.nodeId), ["header", "entry:0", "entry:1", "entry:2"]);
  assertEquals(before[0].timeline, undefined);
  assertEquals(before[0].tools, undefined);
  assertEquals(before[1].reasoning?.length, 1);
  assertEquals(before[2].timeline?.length, 1);
  assertEquals(before[3].tools?.length, 1);
  view.apply({ type: "text.delta", runId: "r", messageId: "m", text: " answer" });
  view.apply({
    type: "tool.finished",
    runId: "r",
    callId: "c",
    result: { status: "ok", value: "contents" },
  });
  const after = timelineOutputSnapshots(view.finalOutput("Question"), true);
  assertEquals(after.map((node) => node.nodeId), before.map((node) => node.nodeId));
  assertEquals(after[1], before[1]);
  assertEquals(after[3].tools?.[0].result, { status: "ok", value: "contents" });
});

Deno.test("saving independent nodes rebases tool and child indexes and restores original order", () => {
  const snapshot = {
    text: "Answer",
    runId: "r",
    input: "Question",
    model: { profileId: "p", provider: "prov", model: "m" },
    usage: { inputTokens: 2, outputTokens: 3 },
    timeline: [
      { kind: "tool" as const, index: 0 },
      { kind: "subagent" as const, index: 0 },
      { kind: "reasoning" as const, messageId: "m" },
      { kind: "tool" as const, index: 1 },
      { kind: "subagent" as const, index: 1 },
      { kind: "text" as const, messageId: "m", text: "Answer" },
    ],
    tools: [{
      tool: "a",
      status: "ok" as const,
      arguments: { key: 1 },
      result: { status: "ok", value: 2 },
    }, { tool: "b", status: "running" as const }],
    reasoning: [{ messageId: "m", text: "Summary" }],
    subagents: [{ status: "ok" as const, text: "child a", tools: [] }, {
      status: "running" as const,
      text: "child b",
      tools: [],
    }],
  };
  const outputs = timelineOutputSnapshots(snapshot);
  const merged = mergeOutputSnapshots(outputs);
  assertEquals(merged, snapshot);
  const notebook = emptyNotebook();
  notebook.cells[0].output = merged;
  const saved = serializeNotebook(notebook);
  assert(!new TextDecoder().decode(saved).includes('"nodeId"'));
  const restored = parseNotebook(saved).cells[0].output;
  assertEquals(restored?.timeline, snapshot.timeline);
  assertEquals(restored?.tools, snapshot.tools);
  assertEquals(restored?.usage, snapshot.usage);
  assertEquals(
    timelineOutputSnapshots(restored ?? {}).map((node) => node.nodeId),
    outputs.map((node) => node.nodeId),
  );
});

Deno.test("legacy snapshots and missing final-answer entries get separate expandable outputs", () => {
  const nodes = timelineOutputSnapshots({
    text: "Full answer",
    tools: [{ tool: "read_file", status: "ok" }],
  });
  assertEquals(nodes.map((node) => node.nodeId), ["header", "entry:0", "answer"]);
  assertEquals(mergeOutputSnapshots(nodes)?.text, "Full answer");
  assertEquals(nodes[2].timeline?.[0], { kind: "text", messageId: "", text: "Full answer" });
});

Deno.test("streaming a new model round never creates a duplicated concatenated answer node", () => {
  const view = new TurnView("r");
  view.apply({ type: "text.delta", runId: "r", messageId: "m1", text: "Checking" });
  view.apply({
    type: "message.completed",
    runId: "r",
    messageId: "m1",
    agentMessage: { role: "assistant", parts: [{ kind: "text", text: "Checking" }] },
  });
  view.apply({ type: "text.delta", runId: "r", messageId: "m2", text: "Answer" });
  assertEquals(view.finalOutput().text, "Answer");
  const outputs = timelineOutputSnapshots(view.finalOutput(), true);
  assertEquals(outputs.map((node) => node.nodeId), ["header", "entry:0", "entry:1"]);
  assertEquals(mergeOutputSnapshots(outputs)?.text, "Answer");
});

Deno.test("expanded tool data is detached and bounded, including on cancellation", () => {
  const view = new TurnView("r");
  view.apply({
    type: "tool.started",
    runId: "r",
    callId: "c",
    tool: "read_file",
    arguments: { path: "a.ts" },
  });
  view.apply({
    type: "tool.finished",
    runId: "r",
    callId: "c",
    result: { status: "ok", value: "x".repeat(30_000) },
  });
  view.apply({ type: "run.failed", runId: "r", code: "run_cancelled", message: "Cancelled" });
  const snapshot = view.finalOutput();
  assertEquals(snapshot.tools[0].detailsTruncated, true);
  assertEquals(typeof snapshot.tools[0].result, "string");
  assertEquals((snapshot.tools[0].result as string).length, 20_000);
  const argumentsValue = snapshot.tools[0].arguments as { path: string };
  argumentsValue.path = "mutated";
  assertEquals(view.finalOutput().tools[0].arguments, { path: "a.ts" });
});
