import type { EventFrame } from "./protocol.ts";

/** Display order only; reasoning entries reference explicitly permitted summaries. */
export type TimelineEntry =
  | { kind: "text"; messageId: string; text: string }
  | { kind: "reasoning"; messageId: string }
  | { kind: "tool"; index: number }
  | { kind: "subagent"; index: number };

export const MaximumTimelineEntries = 2048;
export const MaximumTimelineCharacters = 200_000;
export const MaximumDisplayTools = 256;

/** Retains message boundaries rather than overwriting earlier model/tool rounds. */
export class TurnTimeline {
  private readonly entries: TimelineEntry[] = [];
  private pending: number | undefined;
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
    let index = id === undefined
      ? this.pending ?? -1
      : this.entries.findIndex((entry) => entry.kind === "text" && entry.messageId === id);
    if (index < 0 && this.pending !== undefined) {
      const pending = this.entries[this.pending];
      if (pending?.kind === "text" && pending.messageId === "") index = this.pending;
    }
    if (index < 0) {
      if (!this.add({ kind: "text", messageId: id ?? "", text: "" })) return;
      index = this.entries.length - 1;
    }
    const entry = this.entries[index];
    if (entry?.kind !== "text") return;
    this.characters -= entry.text.length;
    const text = authoritative ?? entry.text + (frame.text ?? "");
    const retained = text.slice(0, Math.max(0, MaximumTimelineCharacters - this.characters));
    this.entries[index] = { kind: "text", messageId: id ?? entry.messageId, text: retained };
    this.characters += retained.length;
    this.truncated ||= retained.length < text.length;
    this.pending = authoritative === undefined ? index : undefined;
  }

  /** Entries are replaced on update, so unchanged entries keep render-cache identity. */
  renderEntries(): TimelineEntry[] {
    return this.entries.slice();
  }

  snapshot(): TimelineEntry[] {
    return this.entries.map((entry) => ({ ...entry }));
  }
}
