/**
 * Cell history state model (docs/notebook-agent-semantics-design.md): a code
 * cell is `committed` when it carries the binding of the turn it created and
 * its text still matches the submitted input, `stale` when the text drifted,
 * and `pending` when it never created a turn. Bindings live in cell metadata,
 * so they survive clear-output and save/reopen and the commit frontier never
 * depends on outputs being present.
 *
 * Pure and structural — no VSCode imports — so the derivation and the run
 * partitioning behind the executeHandler gate are unit-testable.
 */

/** The cell metadata key holding the turn binding of a committed cell. */
export const TurnBindingMetadataKey = "maieuticsTurn";

/** Structural cell view the controller adapts VSCode cells onto. */
export interface CellLike {
  metadata: { [key: string]: unknown };
  text: string;
}

export interface TurnBinding {
  runId: string;
  input: string;
}

export type CellHistoryState = "committed" | "stale" | "pending";

/** Reads the turn binding from cell metadata; anything malformed or written
 * by an older build degrades to "no binding" (the cell is pending). */
export function readTurnBinding(cell: CellLike): TurnBinding | undefined {
  const value = cell.metadata[TurnBindingMetadataKey];
  if (typeof value !== "object" || value === null) return undefined;
  const record = value as Record<string, unknown>;
  if (typeof record.runId !== "string" || record.runId.length === 0) return undefined;
  if (typeof record.input !== "string") return undefined;
  return { runId: record.runId, input: record.input };
}

/** Derives the history state of one cell from its binding and current text. */
export function cellHistoryState(cell: CellLike): CellHistoryState {
  const binding = readTurnBinding(cell);
  if (binding === undefined) return "pending";
  return binding.input === cell.text ? "committed" : "stale";
}

/** The metadata object to assign when a committed turn lands on a cell. */
export function withTurnBinding(
  metadata: { [key: string]: unknown },
  binding: TurnBinding,
): { [key: string]: unknown } {
  return { ...metadata, [TurnBindingMetadataKey]: binding };
}

/** The metadata object to assign when a cell is about to create a fresh turn
 * (fork re-runs), dropping the previous branch's binding. */
export function withoutTurnBinding(
  metadata: { [key: string]: unknown },
): { [key: string]: unknown } {
  const next = { ...metadata };
  delete next[TurnBindingMetadataKey];
  return next;
}

/** Index of the last committed cell (the commit frontier), or -1 when every
 * cell is pending. Cells after the frontier are the composer region. */
export function frontierIndex(cells: CellLike[]): number {
  for (let index = cells.length - 1; index >= 0; index--) {
    if (cellHistoryState(cells[index]) !== "pending") return index;
  }
  return -1;
}

export interface RunPartition<T> {
  /** Cells that would submit fresh turns, in document order. */
  pending: T[];
  /** Cells bound to committed turns, in document order. */
  committed: T[];
}

/** Partitions requested cells by history state, preserving order. */
export function partitionByHistory<T extends CellLike>(cells: T[]): RunPartition<T> {
  const pending: T[] = [];
  const committed: T[] = [];
  for (const cell of cells) {
    (cellHistoryState(cell) === "pending" ? pending : committed).push(cell);
  }
  return { pending, committed };
}

/** Whether the committed cells occupy exactly the document's leading
 * positions (0..n-1) — the shape "Run Above" produces: everything above the
 * active cell is committed history, so there is nothing to run. A
 * single-cell selection is excluded: an explicit run on one committed cell
 * must stay forkable. Takes document positions (`NotebookCell.index`),
 * compared numerically: VSCode hands out fresh cell wrappers per `getCells()`
 * call, so object identity is not stable across calls. */
export function isRunAboveSelection(committedIndices: number[]): boolean {
  if (committedIndices.length < 2) return false;
  for (let index = 0; index < committedIndices.length; index++) {
    if (committedIndices[index] !== index) return false;
  }
  return true;
}
