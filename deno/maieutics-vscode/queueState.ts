/**
 * In-memory registry of cells waiting behind the per-notebook execution
 * queue. Pure state — no VSCode imports — so transitions and position queries
 * are unit-testable; the controller mutates it and the cell status bar
 * provider renders from it.
 *
 * Deliberately not cell metadata: queue membership is ephemeral UI state, and
 * writing it would dirty the notebook on every enqueue/dequeue and fire
 * document-change storms while runs stream. Identity is the cell object
 * reference (stable while the document is open), so entries never need
 * index-based cleanup when edits shift cell positions.
 *
 * Entries are tracked per batch (one executeHandler invocation). A batch's
 * cells leave the tracking two ways with the same visible outcome: the queue
 * loop removes a cell when it starts executing, and dequeue/clear remove it
 * early — removed cells are marked excluded so the loop skips them when its
 * turn comes, instead of submitting behind the user's back.
 */

/** What a batch needs from its owning queue (satisfied by {@link CellQueue}). */
interface QueueHost {
  /** Drops the notebook key once no batches remain (key hygiene). */
  dropEmpty(key: string): void;
  /** Fires the status bar refresh signal. */
  notify(): void;
}

/** Handle for one executeHandler invocation's queued cells. */
export interface QueuedBatch<T> {
  /** True once the cell was removed from this batch by dequeue/clear — the
   * consuming loop must skip it instead of executing it. */
  isExcluded(cell: T): boolean;

  /** Excludes and drops this batch's still-tracked cells (its turn ended, or
   * the notebook closed). Only this batch's entries are affected. */
  disposeRemaining(): void;
}

/** Per-notebook FIFO of queued cells, ordered by enqueue time across batches. */
export class CellQueue<T> {
  private readonly notebooks = new Map<string, Batch<T>[]>();
  private listener: (() => void) | undefined;

  /** Registers the single change listener (the status bar refresh signal). */
  onDidChange(listener: () => void): void {
    this.listener = listener;
  }

  /** Tracks a batch's cells at the end of the notebook's queue. Empty batches
   * are legal no-ops. Fires the change listener when cells were added. */
  enqueue(key: string, cells: readonly T[]): QueuedBatch<T> {
    const batches = this.notebooks.get(key) ?? [];
    this.notebooks.set(key, batches);
    if (cells.length === 0) return new Batch([], this, key);
    const batch = new Batch([...cells], this, key);
    batches.push(batch);
    this.notify();
    return batch;
  }

  /** Removes the first tracked occurrence of the cell across the notebook's
   * batches (the earliest queued copy): its marker clears, positions of later
   * cells shift up, and the owning batch will skip it. True when removed. */
  remove(key: string, cell: T): boolean {
    const batches = this.notebooks.get(key);
    if (batches === undefined) return false;
    for (const batch of batches) {
      if (batch.remove(cell)) {
        this.notify();
        return true;
      }
    }
    return false;
  }

  /** Removes every tracked cell of the notebook (all batches become excluded
   * and dropped). Returns how many cells were removed. */
  clear(key: string): number {
    const batches = this.notebooks.get(key);
    if (batches === undefined || batches.length === 0) return 0;
    const removed = batches.reduce((sum, batch) => sum + batch.dispose(), 0);
    this.notebooks.delete(key);
    if (removed > 0) this.notify();
    return removed;
  }

  /** 1-based queue position of the cell's earliest tracked occurrence, or
   * undefined when it is not queued. */
  position(key: string, cell: T): number | undefined {
    const batches = this.notebooks.get(key);
    if (batches === undefined) return undefined;
    let seen = 0;
    for (const batch of batches) {
      const at = batch.indexOf(cell);
      if (at !== undefined) return seen + at + 1;
      seen += batch.count();
    }
    return undefined;
  }

  /** Number of cells still tracked for the notebook. */
  size(key: string): number {
    return this.notebooks.get(key)?.reduce((sum, batch) => sum + batch.count(), 0) ?? 0;
  }

  /** Internal seam for {@link QueueHost}: batches drop their key when emptied. */
  dropEmpty(key: string): void {
    const batches = this.notebooks.get(key);
    if (batches !== undefined && batches.length === 0) this.notebooks.delete(key);
  }

  /** Internal seam for {@link QueueHost}: fires the refresh signal. */
  notify(): void {
    this.listener?.();
  }
}

class Batch<T> implements QueuedBatch<T> {
  private readonly remaining: T[];
  private readonly excluded = new Set<T>();

  constructor(
    remaining: T[],
    private readonly host: QueueHost,
    private readonly key: string,
  ) {
    this.remaining = remaining;
  }

  isExcluded(cell: T): boolean {
    return this.excluded.has(cell);
  }

  /** Flattened-array index of the cell within this batch, or undefined. */
  indexOf(cell: T): number | undefined {
    const at = this.remaining.indexOf(cell);
    return at < 0 ? undefined : at;
  }

  count(): number {
    return this.remaining.length;
  }

  /** Removes the cell from this batch's tracking; true when it was tracked. */
  remove(cell: T): boolean {
    const at = this.remaining.indexOf(cell);
    if (at < 0) return false;
    this.remaining.splice(at, 1);
    this.excluded.add(cell);
    this.host.dropEmpty(this.key);
    return true;
  }

  dispose(): number {
    const removed = this.remaining.length;
    for (const cell of this.remaining) this.excluded.add(cell);
    this.remaining.length = 0;
    return removed;
  }

  disposeRemaining(): void {
    const removed = this.dispose();
    this.host.dropEmpty(this.key);
    if (removed > 0) this.host.notify();
  }
}
