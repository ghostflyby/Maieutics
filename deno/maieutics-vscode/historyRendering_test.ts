/// <reference lib="deno.ns" />
import { assertEquals } from "@std/assert";
import { historicalAssistantOutput } from "./historyRendering.ts";
import type { OutputSnapshot } from "./notebookFormat.ts";
import type { TranscriptTurn } from "./protocol.ts";

const source = "<script setup>const count = 0</script>\n\n# Counter\n{{ count }}";
const binding = { runId: "run", input: "Question" };
const turn: TranscriptTurn = {
  runId: "run",
  truncated: false,
  messages: [
    { role: "user", parts: [{ kind: "text", text: "Question" }] },
    { role: "assistant", parts: [{ kind: "text", text: source }] },
  ],
};
const node: OutputSnapshot = {
  nodeId: "answer",
  timeline: [{ kind: "text", messageId: "old-message", text: source }],
};

Deno.test("historical assistant recovery verifies exact run, input, role and complete source", () => {
  assertEquals(historicalAssistantOutput(node, binding, turn), node);
  assertEquals(historicalAssistantOutput(node, { ...binding, runId: "other" }, turn), undefined);
  assertEquals(historicalAssistantOutput(node, { ...binding, input: "Edited" }, turn), undefined);
  assertEquals(historicalAssistantOutput(node, binding, { ...turn, truncated: true }), undefined);
  assertEquals(
    historicalAssistantOutput(
      { ...node, timeline: [{ kind: "text", messageId: "old-message", text: source + ";" }] },
      binding,
      turn,
    ),
    undefined,
  );
  assertEquals(
    historicalAssistantOutput(node, binding, {
      ...turn,
      messages: [turn.messages[0], { role: "tool", parts: [{ kind: "text", text: source }] }],
    }),
    undefined,
  );
});

Deno.test("recovery joins canonical message parts and cannot promote extra file-authored content", () => {
  const multipart = {
    ...turn,
    messages: [turn.messages[0], {
      role: "assistant",
      parts: [{ kind: "text" as const, text: source.slice(0, 20) }, {
        kind: "text" as const,
        text: source.slice(20),
      }],
    }],
  };
  assertEquals(historicalAssistantOutput(node, binding, multipart), node);
  assertEquals(
    historicalAssistantOutput({ ...node, text: "unverified", subagents: [] }, binding, turn),
    node,
  );
  for (
    const fields of [{ streaming: true }, { truncated: true }, {
      error: { code: "failed", message: "" },
    }]
  ) {
    assertEquals(historicalAssistantOutput({ ...node, ...fields }, binding, turn), undefined);
  }
  assertEquals(historicalAssistantOutput({ ...node, timeline: [] }, binding, turn), undefined);
  assertEquals(
    historicalAssistantOutput(
      { ...node, timeline: [...(node.timeline ?? []), { kind: "tool", index: 0 }] },
      binding,
      turn,
    ),
    undefined,
  );
});
