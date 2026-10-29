import { assertEquals } from "@std/assert";
import { transcriptOutput } from "./transcriptView.ts";

Deno.test("history restoration retains all answer parts, model rounds and tool outcomes", () => {
  const output = transcriptOutput({
    runId: "stored",
    truncated: false,
    messages: [
      { role: "user", parts: [{ kind: "text", text: "Question" }] },
      {
        role: "assistant",
        parts: [
          { kind: "text", text: "Checking files" },
          { kind: "tool_call", callId: "c", name: "list_directory", value: { path: "." } },
        ],
      },
      { role: "tool", parts: [{ kind: "tool_result", callId: "c", value: { status: "error" } }] },
      {
        role: "assistant",
        parts: [
          { kind: "text", text: "Final " },
          { kind: "text", text: "answer" },
        ],
      },
    ],
  });
  assertEquals(output.text, "Final answer");
  assertEquals(output.timeline, [
    { kind: "text", messageId: "stored:1", text: "Checking files" },
    { kind: "tool", index: 0 },
    { kind: "text", messageId: "stored:3", text: "Final answer" },
  ]);
  assertEquals(output.tools, [{
    tool: "list_directory",
    status: "error",
    argsSummary: '{"path":"."}',
    arguments: { path: "." },
    result: { status: "error" },
  }]);
  assertEquals(output.reasoning, undefined);
});

Deno.test("interrupted history ending in a tool result still displays earlier assistant text", () => {
  const output = transcriptOutput({
    runId: "stored",
    truncated: true,
    messages: [
      { role: "user", parts: [{ kind: "text", text: "Question" }] },
      {
        role: "assistant",
        parts: [
          { kind: "text", text: "Partial answer" },
          { kind: "tool_call", callId: "c", name: "read_file" },
        ],
      },
      {
        role: "tool",
        parts: [{ kind: "tool_result", callId: "c", value: { status: "cancelled" } }],
      },
    ],
  });
  assertEquals(output.text, "Partial answer");
  assertEquals(output.truncated, true);
  assertEquals(output.timeline?.length, 2);
});
