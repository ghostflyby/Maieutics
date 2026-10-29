import type { OutputSnapshot } from "./notebookFormat.ts";
import type { TranscriptTurn } from "./protocol.ts";
import { TurnView } from "./turnView.ts";

/** Public history retains message/tool order, but has no display-only reasoning summaries. */
export function transcriptOutput(turn: TranscriptTurn): OutputSnapshot {
  const view = new TurnView(turn.runId);
  for (const [index, message] of turn.messages.entries()) {
    if (message.role === "assistant") {
      view.apply({
        type: "message.completed",
        runId: turn.runId,
        messageId: `stored:${index}`,
        agentMessage: message,
      });
      for (const part of message.parts) {
        if (part.kind !== "tool_call" || part.callId === undefined) continue;
        view.apply({
          type: "tool.started",
          runId: turn.runId,
          callId: part.callId,
          tool: part.name,
          arguments: part.value,
        });
      }
    } else if (message.role === "tool") {
      for (const part of message.parts) {
        if (part.kind !== "tool_result" || part.callId === undefined) continue;
        view.apply({
          type: "tool.finished",
          runId: turn.runId,
          callId: part.callId,
          result: part.value,
        });
      }
    }
  }
  const snapshot = view.finalOutput();
  // Receipt-time durations would describe this synchronous projection, not the original run.
  for (const tool of snapshot.tools) delete tool.durationMs;
  return { ...snapshot, truncated: turn.truncated };
}
