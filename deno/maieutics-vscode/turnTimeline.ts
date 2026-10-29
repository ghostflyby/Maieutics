import type { EventFrame } from "./protocol.ts";

/** Display order only; reasoning entries reference explicitly permitted summaries. */
export type TimelineEntry =
  | { kind: "text"; messageId: string; text: string }
  | { kind: "reasoning"; messageId: string }
  | { kind: "tool"; index: number }
  | { kind: "subagent"; index: number };

export const MaximumTimelineEntries = 2048;
export const MaximumTimelineCharacters = 200_000;

/** Retains message boundaries rather than overwriting earlier model/tool rounds. */
export class TurnTimeline {
  private readonly entries: TimelineEntry[] = [];
  private pending: Extract<TimelineEntry, { kind: "text" }> | undefined;
  private characters = 0;
  truncated = false;

  add(entry: TimelineEntry): boolean {
    if (this.entries.length >= MaximumTimelineEntries) {
      this.truncated = true;
      return false;
    }
    this.entries.push(entry);
    return true;
  }

  text(frame: EventFrame, authoritative?: string): void {
    const id = frame.messageId;
    let entry = id === undefined
      ? this.pending
      : this.entries.find((entry) => entry.kind === "text" && entry.messageId === id);
    if (entry === undefined) {
      // Legacy streams can omit message ids on deltas, then supply one at completion.
      entry = this.pending?.messageId === "" ? this.pending : undefined;
    }
    if (entry === undefined) {
      entry = { kind: "text", messageId: id ?? "", text: "" };
      if (!this.add(entry)) return;
    }
    if (entry.kind !== "text") return;
    if (id !== undefined) entry.messageId = id;
    this.characters -= entry.text.length;
    const text = authoritative ?? entry.text + (frame.text ?? "");
    entry.text = text.slice(0, Math.max(0, MaximumTimelineCharacters - this.characters));
    this.characters += entry.text.length;
    this.truncated ||= entry.text.length < text.length;
    this.pending = authoritative === undefined ? entry : undefined;
  }

  snapshot(): TimelineEntry[] {
    return this.entries.map((entry) => ({ ...entry }));
  }
}
