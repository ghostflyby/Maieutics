import { ConversationError, textBytes, type TextContent } from "./types.ts";

/** An exact-revision text cache with atomic admission and explicit active-view pins. */
export class TextCache {
  readonly #values = new Map<
    string,
    { text: string; presentation?: TextContent["presentation"]; bytes: number }
  >();
  #bytes = 0;
  constructor(readonly capacity: number) {}
  get bytes(): number {
    return this.#bytes;
  }
  get size(): number {
    return this.#values.size;
  }
  get(key: string): string | undefined {
    return this.content(key)?.text;
  }
  content(key: string): { text: string; presentation?: TextContent["presentation"] } | undefined {
    const value = this.#values.get(key);
    if (!value) return;
    this.#values.delete(key);
    this.#values.set(key, value);
    return { text: value.text, presentation: value.presentation };
  }
  put(
    key: string,
    text: string,
    pinned: ReadonlySet<string>,
    presentation?: TextContent["presentation"],
  ): void {
    const bytes = textBytes(text) + (presentation ? textBytes(JSON.stringify(presentation)) : 0);
    let required = this.#bytes - (this.#values.get(key)?.bytes ?? 0) + bytes;
    const evictions: string[] = [];
    for (const [candidate, value] of this.#values) {
      if (required <= this.capacity) break;
      if (candidate === key || pinned.has(candidate)) continue;
      evictions.push(candidate);
      required -= value.bytes;
    }
    if (required > this.capacity) throw new ConversationError("content_capacity");
    for (const candidate of [...evictions, key]) this.delete(candidate);
    this.#values.set(key, { text, presentation, bytes });
    this.#bytes += bytes;
  }
  delete(key: string): void {
    const value = this.#values.get(key);
    if (value) {
      this.#bytes -= value.bytes;
      this.#values.delete(key);
    }
  }
  clear(): void {
    this.#values.clear();
    this.#bytes = 0;
  }
}

/** Keys cannot collide when an entry ID contains punctuation. */
export function contentKey(id: string, revision: string): string {
  return JSON.stringify([id, revision]);
}
