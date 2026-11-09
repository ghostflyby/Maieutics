import type { TurnBinding } from "./cellHistory.ts";
import type { OutputSnapshot } from "./notebookFormat.ts";
import type { TranscriptTurn } from "./protocol.ts";

/** Reconstruct only an exact assistant text node verified against committed kernel history. */
export function historicalAssistantOutput(
  node: OutputSnapshot,
  binding: TurnBinding,
  turn: TranscriptTurn,
): OutputSnapshot | undefined {
  if (
    turn.runId !== binding.runId || turn.truncated !== false || node.streaming === true ||
    node.truncated === true || node.error !== undefined || node.timeline?.length !== 1
  ) return undefined;
  const user = turn.messages[0];
  if (
    user?.role !== "user" ||
    user.parts.find((part) => part.kind === "text")?.text !== binding.input
  ) return undefined;
  const entry = node.timeline[0];
  if (entry.kind !== "text" || !entry.text || entry.text.length > 200_000) return undefined;
  const verified = turn.messages.some((message) =>
    message.role === "assistant" &&
    message.parts.filter((part) => part.kind === "text").map((part) => part.text ?? "").join("") ===
      entry.text
  );
  // Discard other file-authored fields, even if one text fragment matches the transcript.
  return verified
    ? {
      nodeId: node.nodeId,
      timeline: [{ kind: "text", messageId: entry.messageId, text: entry.text }],
    }
    : undefined;
}
