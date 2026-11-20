import {
  type ConversationChange,
  type ConversationEntry,
  ConversationError,
  type ConversationPage,
  type ConversationSource,
  type TextContent,
} from "../core/types.ts";

const kinds = [
  "input",
  "message",
  "tool",
  "reasoning-summary",
  "display",
  "message",
  "tool",
  "child-run",
  "diagnostic",
  "message",
] as const;
const summaries = [
  "请检查这份研究笔记，并整理值得继续探索的问题。",
  "我先梳理已有结论，再对照原始材料。",
  "读取工作区资料",
  "整理可展示的分析摘要",
  "研究结果 · 数据概览",
  "材料中的三个主题逐渐清晰。",
  "搜索相关章节",
  "子任务 · 核对来源",
  "来源核对完成",
  "可以从证据、假设和下一步实验三个方向继续。",
];

/** A lazy deterministic source: 100,000 entries do not become 100,000 client records. */
export class FixtureConversation implements ConversationSource {
  readonly epoch = "fixture-1";
  readonly #initial: number;
  readonly #listeners = new Set<(change: ConversationChange) => void>();
  readonly #latest = new Map<number, { entry: ConversationEntry; text: string }>();
  readonly #versions = new Map<string, TextContent>();
  readonly #history: {
    revision: string;
    total: number;
    updates: readonly { entry: ConversationEntry; text: string }[];
  }[] = [];
  #total: number;
  #revision = 0n;
  pageReads = 0;
  contentReads = 0;
  constructor(readonly sessionId = "sample", turns = 10_000) {
    if (!Number.isSafeInteger(turns) || turns < 1 || turns > 100_000) {
      throw new ConversationError("invalid_fixture_size");
    }
    this.#initial = turns * 10;
    this.#total = this.#initial;
  }
  get total(): number {
    return this.#total;
  }
  get revision(): string {
    return String(this.#revision);
  }
  #base(ordinal: number): { entry: ConversationEntry; text: string } {
    const slot = ordinal % 10, turn = Math.floor(ordinal / 10) + 1;
    const kind = kinds[slot];
    const preview = summaries[slot];
    const entry: ConversationEntry = Object.freeze({
      id: `entry-${ordinal}`,
      ordinal,
      interactionId: `turn-${turn}`,
      kind,
      contentRevision: "0",
      preview,
      state: "sealed",
    });
    const text = kind === "tool"
      ? `工作区检索已完成。\n\n找到 ${turn % 11 + 2} 个相关片段，原始内容与结构化结果保留在输出中。`
      : kind === "display"
      ? `主题             证据数\n界面与呈现       ${turn % 9 + 3}\n数据与状态       ${
        turn % 7 + 2
      }\n执行与协作       ${turn % 5 + 1}`
      : `${preview}${
        kind === "message"
          ? `\n\n这是第 ${
            turn.toLocaleString("en-US")
          } 轮记录。历史按需载入，阅读位置可以独立保留。\n\n${
            "保留问题的上下文，让新的证据修正已有判断。 ".repeat(turn % 4 + 1)
          }`
          : ""
      }`;
    return { entry, text };
  }
  #snapshot(
    revision?: string,
  ): {
    total: number;
    overrides: ReadonlyMap<number, { entry: ConversationEntry; text: string }>;
    revision: string;
  } {
    if (revision === undefined || revision === this.revision) {
      return { total: this.#total, overrides: this.#latest, revision: this.revision };
    }
    if (revision === "0") return { total: this.#initial, overrides: new Map(), revision };
    if (
      !this.#history.some((item) => item.revision === revision) ||
      this.#history[0]?.revision !== "1"
    ) throw new ConversationError("selection_unavailable");
    const overrides = new Map<number, { entry: ConversationEntry; text: string }>();
    let total = this.#initial;
    for (const item of this.#history) {
      if (BigInt(item.revision) > BigInt(revision)) break;
      for (const update of item.updates) overrides.set(update.entry.ordinal, update);
      total = item.total;
    }
    return { total, overrides, revision };
  }
  async readPage(
    query: { offset?: number; limit: number; revision?: string },
    signal: AbortSignal,
  ): Promise<ConversationPage> {
    signal.throwIfAborted();
    this.pageReads++;
    const snapshot = this.#snapshot(query.revision);
    const offset = Math.min(
      query.offset ?? Math.max(0, snapshot.total - query.limit),
      snapshot.total,
    );
    const entries: ConversationEntry[] = [];
    for (
      let ordinal = offset;
      ordinal < Math.min(snapshot.total, offset + query.limit);
      ordinal++
    ) {
      const value = snapshot.overrides.get(ordinal) ??
        (ordinal < this.#initial ? this.#base(ordinal) : undefined);
      if (!value) throw new ConversationError("selection_unavailable");
      entries.push(value.entry);
    }
    await Promise.resolve();
    signal.throwIfAborted();
    return Object.freeze({
      sessionId: this.sessionId,
      epoch: this.epoch,
      revision: snapshot.revision,
      total: snapshot.total,
      entries: Object.freeze(entries),
    });
  }
  async readContent(id: string, revision: string, signal: AbortSignal): Promise<TextContent> {
    signal.throwIfAborted();
    this.contentReads++;
    const retained = this.#versions.get(JSON.stringify([id, revision]));
    const ordinal = Number(id.replace(/^entry-/, ""));
    const base = revision === "0" && id === `entry-${ordinal}` && Number.isSafeInteger(ordinal) &&
        ordinal >= 0 && ordinal < this.#initial
      ? this.#base(ordinal)
      : undefined;
    await Promise.resolve();
    signal.throwIfAborted();
    if (retained) return retained;
    if (base) return { entryId: id, revision, text: base.text };
    throw new ConversationError("content_unavailable");
  }
  subscribe(listener: (change: ConversationChange) => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }
  #publish(
    updates: readonly { entry: ConversationEntry; text: string }[],
    operations: ConversationChange["operations"],
  ): void {
    const baseRevision = this.revision;
    this.#revision++;
    for (const update of updates) {
      this.#latest.set(update.entry.ordinal, update);
      this.#versions.set(JSON.stringify([update.entry.id, update.entry.contentRevision]), {
        entryId: update.entry.id,
        revision: update.entry.contentRevision,
        text: update.text,
      });
    }
    while (this.#versions.size > 128) {
      const key = this.#versions.keys().next().value;
      if (key !== undefined) this.#versions.delete(key);
    }
    this.#history.push({ revision: this.revision, total: this.#total, updates });
    if (this.#history.length > 128) this.#history.shift();
    const change: ConversationChange = Object.freeze({
      epoch: this.epoch,
      baseRevision,
      revision: this.revision,
      total: this.#total,
      operations,
    });
    for (const listener of this.#listeners) listener(change);
  }
  submit(text: string): string {
    if (!text.trim() || text.length > 64_000 || this.#latest.size >= 1000) {
      throw new ConversationError("fixture_submission_limit");
    }
    const ordinal = this.#total;
    const revision = String(this.#revision + 1n);
    const input: ConversationEntry = {
      id: `entry-${ordinal}`,
      ordinal,
      interactionId: `turn-${ordinal}`,
      kind: "input",
      contentRevision: revision,
      preview: text.slice(0, 256),
      state: "sealed",
    };
    const message: ConversationEntry = {
      id: `entry-${ordinal + 1}`,
      ordinal: ordinal + 1,
      interactionId: input.interactionId,
      kind: "message",
      contentRevision: revision,
      preview: "新的回复",
      state: "open",
    };
    this.#total += 2;
    this.#publish([{ entry: Object.freeze(input), text }, {
      entry: Object.freeze(message),
      text: "正在整理这条输入。",
    }], [{ type: "upsert", entry: input }, { type: "upsert", entry: message }]);
    return message.id;
  }
  append(id: string, text: string): void {
    const value = [...this.#latest.values()].find((item) => item.entry.id === id);
    if (!value || value.entry.state !== "open" || value.text.length + text.length > 200_000) {
      throw new ConversationError("fixture_output_limit");
    }
    const revision = String(this.#revision + 1n);
    const entry = Object.freeze({ ...value.entry, contentRevision: revision });
    this.#publish([{ entry, text: value.text + text }], [{
      type: "append",
      id,
      baseRevision: value.entry.contentRevision,
      revision,
      text,
    }]);
  }
  seal(id: string): void {
    const value = [...this.#latest.values()].find((item) => item.entry.id === id);
    if (!value) throw new ConversationError("content_unavailable");
    const entry = Object.freeze({ ...value.entry, state: "sealed" as const });
    this.#publish([{ entry, text: value.text }], [{ type: "upsert", entry }]);
  }
}
