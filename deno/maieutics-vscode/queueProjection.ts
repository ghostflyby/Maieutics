/**
 * Client-side projection of the server-owned per-session turn queue (the
 * `/v1/agent/sessions/{sid}/queue` surface). The server is authoritative: it
 * owns the queue, runs its items in order, and reports full state on every
 * mutation as a `queue.updated` event frame (idempotent replacement, no
 * sequence number; a missed frame self-heals on the next mutation or on the
 * reconnect snapshot). The extension holds only the itemId ↔ cell pairing
 * maintained here — markers and run following are projections, never the
 * queue itself.
 *
 * Two halves:
 *
 * - {@link QueueWatcher} consumes one session's event frames, applies queue
 *   snapshots, reconciles with `GET /queue` on every (re)connect, and resolves
 *   the per-item drain waits the batch orchestration uses in place of the old
 *   client-side promise chain.
 * - {@link QueueProjection} maps item ids onto notebook cells for the queued
 *   markers (1-based positions from the snapshot order) and hands the running
 *   item's cell to the controller when its run starts.
 *
 * No VSCode imports: cells stay generic, so transitions are unit-testable.
 * The map is client state and is lost on an extension host reload — queued
 * items keep running server-side, their cells simply show no markers or run
 * following until they are run again (see the controller header; there is
 * deliberately no text-matching reconciliation).
 */

import type { FrontendClient } from "./client.ts";
import type { EventFrame, QueueItemState, QueueState } from "./protocol.ts";

/** Parses a wire queue snapshot defensively: unknown fields are tolerated,
 * anything that breaks the pinned shape (session id, running pair, item ids,
 * items array, capacity) yields undefined so a malformed frame cannot poison
 * the projection. */
export function readQueueState(value: unknown): QueueState | undefined {
  if (typeof value !== "object" || value === null) return undefined;
  const record = value as Record<string, unknown>;
  if (typeof record.sessionId !== "string" || typeof record.capacity !== "number") return undefined;

  let running: QueueState["running"] = null;
  if (typeof record.running === "object" && record.running !== null) {
    const pair = record.running as Record<string, unknown>;
    if (typeof pair.itemId !== "string" || typeof pair.runId !== "string") return undefined;
    running = { itemId: pair.itemId, runId: pair.runId };
  } else if (record.running !== null && record.running !== undefined) {
    return undefined;
  }

  if (!Array.isArray(record.items)) return undefined;
  const items: QueueItemState[] = [];
  for (const entry of record.items) {
    if (typeof entry !== "object" || entry === null) return undefined;
    const item = entry as Record<string, unknown>;
    if (typeof item.id !== "string" || item.id.length === 0) return undefined;
    items.push({
      id: item.id,
      ...(typeof item.text === "string" ? { text: item.text } : {}),
      ...(typeof item.enqueuedAt === "string" ? { enqueuedAt: item.enqueuedAt } : {}),
    });
  }
  return { sessionId: record.sessionId, running, items, capacity: record.capacity };
}

/** The command tokens the executable executes as `%`-commands (the cell
 * text's first whitespace-delimited token, case-insensitive — mirrors
 * `MaieuticsCommandLanguage.IsCommandCell`). Their cells are never enqueued:
 * the server rejects command text on the queue. */
const CommandTokens = new Set([
  "%mcp",
  "%model",
  "%session",
  "%status",
  "%workspace",
  "%maieutics",
]);

/** True when the server would execute the text as a `%`-command instead of an
 * agent turn (command cells stay client-orchestrated inline). */
export function isCommandCellText(text: string): boolean {
  const trimmed = text.trimStart();
  if (trimmed.length === 0) return false;
  const end = trimmed.search(/\s/);
  const firstToken = end === -1 ? trimmed : trimmed.slice(0, end);
  return CommandTokens.has(firstToken.toLowerCase());
}

/** One planned step of a batch walk: a maximal run of consecutive turn cells
 * (enqueued as ONE POST; the items run server-side in order) or a single cell
 * that must execute inline (command text, or a blank no-op turn). */
export type BatchStep<T> = { kind: "queue"; cells: T[] } | { kind: "inline"; cell: T };

/** Splits ordered batch targets into queue runs and inline cells, preserving
 * order: an interleaved [turn, command, turn] batch becomes queue [turn],
 * inline command, queue [turn]. A pure-pending batch is a single queue run. */
export function planBatchSteps<T>(
  cells: readonly T[],
  textOf: (cell: T) => string,
): BatchStep<T>[] {
  const steps: BatchStep<T>[] = [];
  let run: T[] | undefined;
  const flush = () => {
    if (run !== undefined && run.length > 0) steps.push({ kind: "queue", cells: run });
    run = undefined;
  };
  for (const cell of cells) {
    const text = textOf(cell);
    if (text.trim().length === 0 || isCommandCellText(text)) {
      flush();
      steps.push({ kind: "inline", cell });
      continue;
    }
    (run ??= []).push(cell);
  }
  flush();
  return steps;
}

/** One tracked queue item: the cell it was enqueued from, the text that was
 * submitted (the run's turn-binding input), and the item's 1-based position
 * in the latest snapshot. */
export interface TrackedQueueItem<C> {
  cell: C;
  text: string;
  position: number;
}

/** The notebook-side view of the server queue: itemId → tracked cell, per
 * session. Entries exist only for items this client enqueued; items enqueued
 * by other clients occupy positions in the snapshots but have no entry. */
export class QueueProjection<C> {
  private readonly sessions = new Map<string, Map<string, TrackedQueueItem<C>>>();
  private listener: (() => void) | undefined;

  /** Registers the single change listener (the status bar refresh signal). */
  onDidChange(listener: () => void): void {
    this.listener = listener;
  }

  /** Seeds the entries for one enqueue answer: the answer's items (ids and
   * positions) map by index onto the enqueued cells. The next snapshot
   * refreshes positions and drops finished items. */
  track(
    sessionId: string,
    items: readonly { id: string; position: number; cell: C; text: string }[],
  ): void {
    if (items.length === 0) return;
    const entries = this.sessions.get(sessionId) ?? new Map<string, TrackedQueueItem<C>>();
    this.sessions.set(sessionId, entries);
    for (const item of items) {
      entries.set(item.id, { cell: item.cell, text: item.text, position: item.position });
    }
    this.notify();
  }

  /** Applies a full snapshot for the session: refreshes positions from the
   * snapshot order (1-based; items of other clients occupy positions too) and
   * drops entries whose items finished, were dequeued, or were cleared. */
  apply(sessionId: string, snapshot: QueueState): void {
    const entries = this.sessions.get(sessionId);
    if (entries === undefined || entries.size === 0) return;
    let changed = false;
    const positions = new Map<string, number>();
    for (let index = 0; index < snapshot.items.length; index++) {
      positions.set(snapshot.items[index].id, index + 1);
    }
    for (const [itemId, entry] of [...entries]) {
      const position = positions.get(itemId);
      if (position === undefined) {
        entries.delete(itemId);
        changed = true;
        continue;
      }
      if (entry.position !== position) {
        entry.position = position;
        changed = true;
      }
    }
    if (changed) this.notify();
  }

  /** Removes and returns the entry of the item that started running: its cell
   * leaves the marker projection (the execution spinner takes over) and the
   * controller follows the run announced by the snapshot. */
  take(sessionId: string, itemId: string): TrackedQueueItem<C> | undefined {
    const entries = this.sessions.get(sessionId);
    const entry = entries?.get(itemId);
    if (entries === undefined || entry === undefined) return undefined;
    entries.delete(itemId);
    if (entries.size === 0) this.sessions.delete(sessionId);
    this.notify();
    return entry;
  }

  /** Drops one entry without returning it (optimistic local removal after a
   * successful DELETE, or after a failed wait). */
  drop(sessionId: string, itemId: string): void {
    const entries = this.sessions.get(sessionId);
    if (entries === undefined || !entries.delete(itemId)) return;
    if (entries.size === 0) this.sessions.delete(sessionId);
    this.notify();
  }

  /** The 1-based position of the cell's queued item in the latest snapshot, or
   * undefined when the cell has nothing queued. */
  positionOf(cell: C): number | undefined {
    for (const entries of this.sessions.values()) {
      for (const entry of entries.values()) {
        if (entry.cell === cell) return entry.position;
      }
    }
    return undefined;
  }

  /** Collects the tracked items whose cell matches — the notebook-scoped
   * deletion lists (close, clear, dequeue). */
  entriesWhere(matches: (cell: C) => boolean): {
    sessionId: string;
    itemId: string;
    text: string;
  }[] {
    const found: { sessionId: string; itemId: string; text: string }[] = [];
    for (const [sessionId, entries] of this.sessions) {
      for (const [itemId, entry] of entries) {
        if (matches(entry.cell)) found.push({ sessionId, itemId, text: entry.text });
      }
    }
    return found;
  }

  /** Drops every entry whose cell matches (notebook closed): the markers
   * clear immediately; the caller deletes the items server-side. */
  clearCells(matches: (cell: C) => boolean): number {
    let removed = 0;
    for (const [sessionId, entries] of [...this.sessions]) {
      for (const [itemId, entry] of [...entries]) {
        if (!matches(entry.cell)) continue;
        entries.delete(itemId);
        removed++;
      }
      if (entries.size === 0) this.sessions.delete(sessionId);
    }
    if (removed > 0) this.notify();
    return removed;
  }

  private notify(): void {
    this.listener?.();
  }
}

/** Consumes one session's queue surface on its event stream: applies full
 * snapshots, reconciles with `GET /queue` on every (re)connect, and resolves
 * the drain waits batch orchestration awaits. Stream failures resolve pending
 * waits as false so a dead socket never hangs a batch. */
export class QueueWatcher {
  private version = 0;
  private snapshot: QueueState | undefined;
  private readonly waits: {
    version: number;
    ids: Set<string>;
    resolve: (drained: boolean) => void;
  }[] = [];

  constructor(
    private readonly client: FrontendClient,
    private readonly sessionId: string,
    private readonly onSnapshot: (snapshot: QueueState) => void,
    private readonly log: (message: string) => void,
  ) {}

  /** Feeds one event frame; true when the frame belonged to the queue surface
   * (`queue.updated` or the connect `hello`) and was consumed — the caller
   * skips its run routing for it. The hello blocks until the reconnect
   * snapshot applied, so an item that started while the socket was away
   * attaches to its cell before the run's replayed frames route. */
  async handleFrame(frame: EventFrame): Promise<boolean> {
    if (frame.type === "queue.updated") {
      const snapshot = readQueueState(frame.queue);
      if (snapshot !== undefined) this.apply(snapshot);
      return true;
    }
    if (frame.type === "hello") {
      await this.reconcileAsync();
      return true;
    }
    return false;
  }

  /** The current snapshot version: capture before enqueueing and pass it to
   * {@link awaitDrainedAsync} so the wait can ignore snapshots that predate
   * the enqueue. */
  get snapshotVersion(): number {
    return this.version;
  }

  /** The last applied snapshot (a `queue.updated` frame or a reconnect GET). */
  get lastSnapshot(): QueueState | undefined {
    return this.snapshot;
  }

  /** Resolves to true once a snapshot applied strictly after `version` shows
   * every item gone (finished, dequeued, or cleared). The enqueue frame itself
   * always postdates the captured version, so absence in any later snapshot is
   * a real drain — a pre-enqueue snapshot that predates the POST can never
   * resolve the wait. Resolves to false when the stream fails first. */
  awaitDrainedAsync(version: number, itemIds: readonly string[]): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      this.waits.push({ version, ids: new Set(itemIds), resolve });
      this.evaluateWaits();
    });
  }

  /** Resolves every pending wait with false (stream disposed or failed). */
  failWaits(): void {
    for (const wait of this.waits) wait.resolve(false);
    this.waits.length = 0;
  }

  private async reconcileAsync(): Promise<void> {
    try {
      this.apply(await this.client.getQueue(this.sessionId));
    } catch (error) {
      // An unreachable or pre-queue server: the next mutation frame or
      // reconnect snapshot heals the projection.
      this.log(`queue reconcile failed: ${error}`);
    }
  }

  private apply(snapshot: QueueState): void {
    this.version++;
    this.snapshot = snapshot;
    this.evaluateWaits();
    this.onSnapshot(snapshot);
  }

  private evaluateWaits(): void {
    for (let index = this.waits.length - 1; index >= 0; index--) {
      const wait = this.waits[index];
      // Only snapshots applied after the enqueue may resolve the wait: the
      // current snapshot at version <= wait.version predates the POST.
      if (this.version <= wait.version) continue;
      const drained = [...wait.ids].every((id) => !this.holdsItem(id));
      if (!drained) continue;
      this.waits.splice(index, 1);
      wait.resolve(true);
    }
  }

  private holdsItem(itemId: string): boolean {
    if (this.snapshot === undefined) return true; // Unknown state: keep waiting.
    if (this.snapshot.running?.itemId === itemId) return true;
    return this.snapshot.items.some((item) => item.id === itemId);
  }
}
