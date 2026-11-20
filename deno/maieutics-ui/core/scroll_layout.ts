import type { ConversationRow } from "./types.ts";

/** A bounded geometry snapshot keeps the native scrollbar's coordinate system fixed during input. */
export class FrozenScrollLayout {
  readonly #sizes = new Map<number, number>();
  readonly #ordinals: number[];
  readonly #deltas: number[] = [0];
  constructor(
    rows: readonly ConversationRow[],
    measured: ReadonlyMap<string, number>,
    readonly estimate: number,
    readonly extent: number,
  ) {
    for (const row of rows) {
      if (row.type === "entry") this.#sizes.set(row.ordinal, measured.get(row.key) ?? estimate);
    }
    this.#ordinals = [...this.#sizes.keys()].sort((a, b) => a - b);
    for (const ordinal of this.#ordinals) {
      this.#deltas.push(
        (this.#deltas.at(-1) ?? 0) + (this.#sizes.get(ordinal) ?? estimate) - estimate,
      );
    }
  }
  #prefix(ordinal: number): number {
    let low = 0, high = this.#ordinals.length;
    while (low < high) {
      const middle = low + Math.floor((high - low) / 2);
      if (this.#ordinals[middle] < ordinal) low = middle + 1;
      else high = middle;
    }
    return this.#deltas[low];
  }
  height(row: ConversationRow): number {
    return row.type === "entry"
      ? this.#sizes.get(row.ordinal) ?? this.estimate
      : row.count * this.estimate + this.#prefix(row.from + row.count) - this.#prefix(row.from);
  }
  /** Keep the logical position when a loaded page is split, merged, or evicted during the gesture. */
  locate(row: ConversationRow, offset: number): { ordinal: number; inside: number } {
    if (row.type === "entry") return { ordinal: row.ordinal, inside: Math.max(0, offset) };
    let low = row.from, high = row.from + row.count;
    while (low + 1 < high) {
      const middle = low + Math.floor((high - low) / 2);
      const before = (middle - row.from) * this.estimate + this.#prefix(middle) -
        this.#prefix(row.from);
      if (before <= offset) low = middle;
      else high = middle;
    }
    const before = (low - row.from) * this.estimate + this.#prefix(low) - this.#prefix(row.from);
    return { ordinal: low, inside: Math.max(0, offset - before) };
  }
}
