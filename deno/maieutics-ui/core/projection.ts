import {
  compareRevision,
  type ConversationChange,
  type ConversationEntry,
  ConversationError,
  type ConversationLimits,
  type ConversationPage,
  type ConversationRow,
} from "./types.ts";

interface Slot {
  readonly entry: ConversationEntry;
  readonly watermark: string;
}

function natural(value: number): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new ConversationError("invalid_ordinal");
}
function entryCopy(entry: ConversationEntry, total: number): ConversationEntry {
  natural(entry.ordinal);
  if (
    !entry.id || entry.id.length > 256 || entry.ordinal >= total || !entry.interactionId ||
    entry.preview.length > 1024
  ) {
    throw new ConversationError("invalid_entry");
  }
  compareRevision(entry.contentRevision, "0");
  return Object.freeze({ ...entry });
}

/** Sparse immutable projection with bounded metadata; rows aggregate unloaded history. */
export class ConversationProjection {
  #epoch: string | undefined;
  #revision = "0";
  #total = 0;
  #slots = new Map<number, Slot>();
  #rows: readonly ConversationRow[] = Object.freeze([]);
  constructor(readonly sessionId: string, readonly limits: ConversationLimits) {}
  get epoch(): string | undefined {
    return this.#epoch;
  }
  get revision(): string {
    return this.#revision;
  }
  get total(): number {
    return this.#total;
  }
  get size(): number {
    return this.#slots.size;
  }
  get rows(): readonly ConversationRow[] {
    return this.#rows;
  }
  get(ordinal: number): ConversationEntry | undefined {
    return this.#slots.get(ordinal)?.entry;
  }
  find(id: string): ConversationEntry | undefined {
    for (const { entry } of this.#slots.values()) if (entry.id === id) return entry;
  }
  validatePage(page: ConversationPage): void {
    natural(page.total);
    compareRevision(page.revision, "0");
    if (
      page.sessionId !== this.sessionId || !page.epoch || page.entries.length > this.limits.pageSize
    ) throw new ConversationError("invalid_page");
    if (this.#epoch !== undefined && page.epoch !== this.#epoch) {
      throw new ConversationError("epoch_changed");
    }
  }
  merge(
    page: ConversationPage,
    changes: readonly ConversationChange[],
    pins: ReadonlySet<number>,
    anchor: number,
  ): void {
    this.validatePage(page);
    const initial = this.#epoch === undefined;
    if (
      this.#epoch !== undefined &&
      (compareRevision(page.revision, this.#revision) > 0 || page.total > this.#total)
    ) throw new ConversationError("projection_gap");
    const total = this.#epoch === undefined ? page.total : this.#total;
    const slots = new Map(this.#slots);
    const ordinals = new Set<number>();
    const ids = new Set<string>();
    for (const raw of page.entries) {
      const entry = entryCopy(raw, page.total);
      if (ordinals.has(entry.ordinal) || ids.has(entry.id)) {
        throw new ConversationError("invalid_page");
      }
      ordinals.add(entry.ordinal);
      ids.add(entry.id);
      const previous = slots.get(entry.ordinal);
      if (!previous || compareRevision(previous.watermark, page.revision) <= 0) {
        slots.set(entry.ordinal, { entry, watermark: page.revision });
      }
    }
    for (const change of changes) {
      if (change.epoch !== page.epoch) throw new ConversationError("epoch_changed");
      if (compareRevision(change.revision, page.revision) > 0) this.#operate(slots, change, false);
    }
    if (!ordinals.size && anchor < total) throw new ConversationError("page_unavailable");
    this.#commit(slots, pins, anchor, total, page.epoch, ordinals);
    this.#epoch = page.epoch;
    if (initial) this.#revision = page.revision;
  }
  apply(change: ConversationChange, pins: ReadonlySet<number>, anchor: number): boolean {
    natural(change.total);
    if (change.epoch !== this.#epoch) throw new ConversationError("epoch_changed");
    if (compareRevision(change.revision, this.#revision) <= 0) return false;
    if (change.baseRevision !== this.#revision || change.total < this.#total) {
      throw new ConversationError("projection_gap");
    }
    const slots = new Map(this.#slots);
    this.#operate(slots, change, this.#total === 0 || slots.has(this.#total - 1));
    this.#commit(slots, pins, anchor, change.total, change.epoch);
    this.#revision = change.revision;
    return true;
  }
  #operate(slots: Map<number, Slot>, change: ConversationChange, admitTail: boolean): void {
    const touched = new Set<string>();
    for (const operation of change.operations) {
      const id = operation.type === "upsert" ? operation.entry.id : operation.id;
      if (touched.has(id)) throw new ConversationError("duplicate_operation");
      touched.add(id);
      if (operation.type === "upsert") {
        const entry = entryCopy(operation.entry, change.total);
        const previous = slots.get(entry.ordinal);
        if (!previous && !admitTail) continue;
        if (previous && compareRevision(previous.watermark, change.revision) >= 0) continue;
        if (previous && previous.entry.id !== entry.id) {
          throw new ConversationError("identity_changed");
        }
        if (
          previous && compareRevision(entry.contentRevision, previous.entry.contentRevision) < 0
        ) throw new ConversationError("content_revision_gap");
        slots.set(entry.ordinal, { entry, watermark: change.revision });
      } else if (operation.type === "remove") {
        natural(operation.ordinal);
        const previous = slots.get(operation.ordinal);
        if (!previous || compareRevision(previous.watermark, change.revision) >= 0) continue;
        if (previous.entry.id !== operation.id) throw new ConversationError("identity_changed");
        slots.set(operation.ordinal, {
          entry: Object.freeze({
            ...previous.entry,
            kind: "unavailable",
            preview: "内容已移除",
            contentRevision: change.revision,
            state: "sealed",
          }),
          watermark: change.revision,
        });
      } else if (operation.type === "append") {
        if (compareRevision(operation.revision, operation.baseRevision) <= 0) {
          throw new ConversationError("content_revision_gap");
        }
        const slot = [...slots.values()].find((slot) => slot.entry.id === operation.id);
        if (!slot || compareRevision(slot.watermark, change.revision) >= 0) continue;
        if (slot.entry.contentRevision !== operation.baseRevision || slot.entry.state !== "open") {
          throw new ConversationError("content_revision_gap");
        }
        slots.set(slot.entry.ordinal, {
          entry: Object.freeze({ ...slot.entry, contentRevision: operation.revision }),
          watermark: change.revision,
        });
      } else {
        throw new ConversationError("unsupported_operation");
      }
    }
  }
  #commit(
    slots: Map<number, Slot>,
    pins: ReadonlySet<number>,
    anchor: number,
    total: number,
    epoch: string,
    requested?: ReadonlySet<number>,
  ): void {
    const identities = new Set<string>();
    for (const { entry } of slots.values()) {
      if (identities.has(entry.id)) throw new ConversationError("identity_changed");
      identities.add(entry.id);
    }
    const candidates = [...slots.keys()].filter((ordinal) => !pins.has(ordinal)).sort((a, b) =>
      Math.abs(b - anchor) - Math.abs(a - anchor)
    );
    const rowList = (): ConversationRow[] => {
      const rows: ConversationRow[] = [];
      let next = 0;
      for (const ordinal of [...slots.keys()].sort((a, b) => a - b)) {
        if (ordinal > next) {
          rows.push({
            type: "range",
            key: `range:${epoch}:${next}:${ordinal}`,
            from: next,
            count: ordinal - next,
          });
        }
        const slot = slots.get(ordinal);
        if (slot) rows.push({ type: "entry", key: slot.entry.id, ordinal });
        next = ordinal + 1;
      }
      if (next < total) {
        rows.push({
          type: "range",
          key: `range:${epoch}:${next}:${total}`,
          from: next,
          count: total - next,
        });
      }
      return rows;
    };
    while (slots.size > this.limits.entries) {
      const candidate = candidates.shift();
      if (candidate === undefined) throw new ConversationError("index_capacity");
      slots.delete(candidate);
    }
    let rows = rowList();
    while (rows.filter((row) => row.type === "range").length > this.limits.ranges) {
      const candidate = candidates.shift();
      if (candidate === undefined) throw new ConversationError("range_capacity");
      slots.delete(candidate);
      rows = rowList();
    }
    if (requested?.size && ![...requested].some((ordinal) => slots.has(ordinal))) {
      throw new ConversationError("index_capacity");
    }
    const old = new Map(this.#rows.map((row) => [row.key, row]));
    this.#rows = Object.freeze(rows.map((row) => old.get(row.key) ?? Object.freeze(row)));
    this.#slots = slots;
    this.#total = total;
  }
  /** Restore the same empty baseline as a newly constructed projection. */
  clear(): void {
    this.#epoch = undefined;
    this.#revision = "0";
    this.#total = 0;
    this.#slots.clear();
    this.#rows = Object.freeze([]);
  }
}

/** Size charged to bounded pending snapshot reconciliation. */
export function changeSize(change: ConversationChange): number {
  return JSON.stringify(change).length * 2;
}
