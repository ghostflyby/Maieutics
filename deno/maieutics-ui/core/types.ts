/** Display kinds understood by the initial passive conversation host. */
export type EntryKind =
  | "input"
  | "message"
  | "tool"
  | "reasoning-summary"
  | "display"
  | "child-run"
  | "diagnostic"
  | "unavailable";

/** An immutable catalog entry. Ordinals are navigation coordinates, never view identities. */
export interface ConversationEntry {
  readonly id: string;
  readonly ordinal: number;
  readonly interactionId: string;
  readonly kind: EntryKind;
  readonly contentRevision: string;
  readonly preview: string;
  readonly state: "open" | "sealed";
}

/** An internal source port snapshot, independent of any future HTTP wire schema. */
export interface ConversationPage {
  readonly sessionId: string;
  readonly epoch: string;
  readonly revision: string;
  readonly total: number;
  readonly entries: readonly ConversationEntry[];
}

/** Exact content reads must reject unavailable revisions rather than substituting newer text. */
export interface TextContent {
  readonly entryId: string;
  readonly revision: string;
  readonly text: string;
}

/** The shell owns this source; a runtime owns only its outstanding requests. */
export interface ConversationSource {
  readPage(
    query: { offset?: number; limit: number; revision?: string },
    signal: AbortSignal,
  ): Promise<ConversationPage>;
  readContent(id: string, revision: string, signal: AbortSignal): Promise<TextContent>;
}

/** Semantic projection updates, not raw Agent or transport events. */
export type ConversationOperation =
  | { readonly type: "upsert"; readonly entry: ConversationEntry }
  | {
    readonly type: "append";
    readonly id: string;
    readonly baseRevision: string;
    readonly revision: string;
    readonly text: string;
  }
  | { readonly type: "remove"; readonly id: string; readonly ordinal: number };

/** One atomic transition in the session display projection. */
export interface ConversationChange {
  readonly epoch: string;
  readonly baseRevision: string;
  readonly revision: string;
  readonly total: number;
  readonly operations: readonly ConversationOperation[];
}

/** Virtual rows contain only a bounded catalog window and aggregated unloaded ranges. */
export type ConversationRow =
  | { readonly type: "entry"; readonly key: string; readonly ordinal: number }
  | { readonly type: "range"; readonly key: string; readonly from: number; readonly count: number };

/** Readonly catalog access cannot bypass the runtime's ordered reducer or notifications. */
export interface ConversationProjectionView {
  readonly epoch: string | undefined;
  readonly revision: string;
  readonly total: number;
  readonly size: number;
  readonly rows: readonly ConversationRow[];
  get(ordinal: number): ConversationEntry | undefined;
  find(id: string): ConversationEntry | undefined;
}

/** Capacity choices are configurable without changing projection semantics. */
export interface ConversationLimits {
  readonly entries: number;
  readonly ranges: number;
  readonly pageSize: number;
  readonly contentBytes: number;
  readonly contentCharacters: number;
  readonly concurrentRequests: number;
  readonly queuedRequests: number;
  readonly reconciliationBytes: number;
  readonly selectionBytes: number;
}

/** Conservative defaults for the first, passive browser prototype. */
export const defaultLimits: ConversationLimits = Object.freeze({
  entries: 2000,
  ranges: 128,
  pageSize: 80,
  contentBytes: 32 * 1024 * 1024,
  contentCharacters: 200_000,
  concurrentRequests: 4,
  queuedRequests: 128,
  reconciliationBytes: 4 * 1024 * 1024,
  selectionBytes: 8 * 1024 * 1024,
});

/** Typed failures let hosts distinguish capacity, recovery, and unavailable content. */
export class ConversationError extends Error {
  constructor(readonly code: string) {
    super(code);
    this.name = "ConversationError";
  }
}

/** Compare bounded decimal revisions without losing integer precision. */
export function compareRevision(a: string, b: string): number {
  for (const value of [a, b]) {
    if (!/^(0|[1-9]\d{0,19})$/.test(value)) throw new ConversationError("invalid_revision");
  }
  return a.length - b.length || (a < b ? -1 : a > b ? 1 : 0);
}

/** Count retained string storage conservatively, including UTF-16 browser strings. */
export function textBytes(text: string): number {
  return Math.max(text.length * 2, new TextEncoder().encode(text).byteLength);
}

/** A stable point into a particular text representation and content revision. */
export interface TextPoint {
  readonly id: string;
  readonly ordinal: number;
  readonly revision: string;
  readonly offset: number;
}

/** A logical selection survives removal of intermediate DOM rows. */
export interface ConversationSelection {
  readonly epoch: string;
  readonly projectionRevision: string;
  readonly anchor: TextPoint;
  readonly focus: TextPoint;
}
