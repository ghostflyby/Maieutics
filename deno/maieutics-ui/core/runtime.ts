import { contentKey, TextCache } from "./cache.ts";
import { changeSize, ConversationProjection } from "./projection.ts";
import { OwnedTasks } from "./tasks.ts";
import {
  compareRevision,
  type ConversationChange,
  type ConversationEntry,
  ConversationError,
  type ConversationLimits,
  type ConversationPage,
  type ConversationProjectionView,
  type ConversationSelection,
  type ConversationSource,
  defaultLimits,
  type PageLoadProgress,
  textBytes,
  type TextContent,
} from "./types.ts";

type Listener = () => void;
interface PendingPage {
  readonly changes: ConversationChange[];
  bytes: number;
  overflow: boolean;
}

/** One session connection's framework-neutral state, bounded caches and owned requests. */
export class ConversationRuntime {
  readonly #projection: ConversationProjection;
  readonly limits: ConversationLimits;
  readonly #source: ConversationSource;
  readonly #cache: TextCache;
  readonly #tasks: OwnedTasks;
  readonly #listeners = new Set<Listener>();
  readonly #entryListeners = new Map<string, Set<Listener>>();
  readonly #views = new Map<string, ConversationViewState>();
  readonly #pages = new Set<PendingPage>();
  readonly #pageWaiters = new Set<Listener>();
  #baselineRevision: string | undefined;
  #anchor = 0;
  #disposal: Promise<void> | undefined;
  constructor(
    readonly sessionId: string,
    source: ConversationSource,
    limits: Partial<ConversationLimits> = {},
  ) {
    this.limits = Object.freeze({ ...defaultLimits, ...limits });
    for (const value of Object.values(this.limits)) {
      if (!Number.isSafeInteger(value) || value <= 0) throw new ConversationError("invalid_limits");
    }
    if (this.limits.pageSize > Math.min(200, this.limits.entries)) {
      throw new ConversationError("invalid_limits");
    }
    this.#source = source;
    this.#projection = new ConversationProjection(sessionId, this.limits);
    this.#cache = new TextCache(this.limits.contentBytes);
    this.#tasks = new OwnedTasks(this.limits.concurrentRequests, this.limits.queuedRequests);
  }
  get signal(): AbortSignal {
    return this.#tasks.signal;
  }
  get projection(): ConversationProjectionView {
    return this.#projection;
  }
  get statistics(): {
    entries: number;
    ranges: number;
    contentBytes: number;
    activeRequests: number;
    queuedRequests: number;
    waitingPages: number;
    views: number;
  } {
    return {
      entries: this.projection.size,
      ranges: this.projection.rows.filter((row) => row.type === "range").length,
      contentBytes: this.#cache.bytes,
      activeRequests: this.#tasks.active,
      queuedRequests: this.#tasks.queued,
      waitingPages: this.#pageWaiters.size,
      views: this.#views.size,
    };
  }
  subscribe(listener: Listener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }
  subscribeEntry(id: string, listener: Listener): () => void {
    const listeners = this.#entryListeners.get(id) ?? new Set<Listener>();
    listeners.add(listener);
    this.#entryListeners.set(id, listeners);
    return () => {
      listeners.delete(listener);
      if (!listeners.size) this.#entryListeners.delete(id);
    };
  }
  #notify(id?: string): void {
    for (const listener of id ? this.#entryListeners.get(id) ?? [] : this.#listeners) listener();
  }
  #pins(): Set<number> {
    return new Set([...this.#views.values()].flatMap((view) => [...view.pins]));
  }
  #contentPins(): Set<string> {
    const keys = new Set<string>();
    for (const ordinal of this.#pins()) {
      const entry = this.projection.get(ordinal);
      if (entry) keys.add(contentKey(entry.id, entry.contentRevision));
    }
    return keys;
  }
  #remember(key: string, text: string, presentation?: TextContent["presentation"]): void {
    try {
      this.#cache.put(key, text, this.#contentPins(), presentation);
    } catch (error) {
      // Admission is optional; mounted readers still receive exact text when pins fill the cache.
      if (!(error instanceof ConversationError && error.code === "content_capacity")) throw error;
    }
  }
  async #awaitPage(
    page: ConversationPage,
    pending: PendingPage,
    signal: AbortSignal,
    onProgress?: (progress: PageLoadProgress) => void,
  ): Promise<void> {
    if (compareRevision(page.revision, this.projection.revision) <= 0) return;
    await new Promise<void>((resolve, reject) => {
      let settled = false;
      const finish = (error?: unknown) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        this.#pageWaiters.delete(check);
        signal.removeEventListener("abort", check);
        if (error !== undefined) reject(error);
        else resolve();
      };
      const check = () => {
        if (signal.aborted) finish(signal.reason);
        else if (pending.overflow) finish(new ConversationError("snapshot_gap"));
        else if (compareRevision(page.revision, this.projection.revision) <= 0) finish();
      };
      this.#pageWaiters.add(check);
      signal.addEventListener("abort", check, { once: true });
      const timer = setTimeout(
        () => finish(new ConversationError("stream_wait_timeout")),
        this.limits.pageWaitMilliseconds,
      );
      try {
        onProgress?.({ phase: "waiting-for-stream", revision: page.revision });
        check();
      } catch (error) {
        finish(error);
      }
    });
  }
  loadPage(
    offset?: number,
    caller?: AbortSignal,
    onProgress?: (progress: PageLoadProgress) => void,
  ): Promise<void> {
    if (offset !== undefined && (!Number.isSafeInteger(offset) || offset < 0)) {
      return Promise.reject(new ConversationError("invalid_ordinal"));
    }
    return this.#tasks.run(async (signal, commit) => {
      for (let attempt = 0; attempt < 2; attempt++) {
        const pending: PendingPage = { changes: [], bytes: 0, overflow: false };
        const minimumRevision = this.projection.revision;
        this.#pages.add(pending);
        try {
          onProgress?.({ phase: "reading" });
          signal.throwIfAborted();
          const page = await this.#source.readPage({ offset, limit: this.limits.pageSize }, signal);
          signal.throwIfAborted();
          if (pending.overflow) throw new ConversationError("snapshot_gap");
          if (compareRevision(page.revision, minimumRevision) < 0) {
            throw new ConversationError("snapshot_gap");
          }
          this.#projection.validatePage(page);
          // No await before the first publication: concurrent initial pages must observe its baseline.
          if (this.projection.epoch !== undefined) {
            await this.#awaitPage(page, pending, signal, onProgress);
          }
          signal.throwIfAborted();
          if (pending.overflow) throw new ConversationError("snapshot_gap");
          if (
            this.#baselineRevision !== undefined &&
            compareRevision(page.revision, this.#baselineRevision) < 0
          ) {
            throw new ConversationError("snapshot_gap");
          }
          const anchor = offset ?? Math.max(0, page.total - this.limits.pageSize);
          commit(() => {
            this.#projection.merge(page, pending.changes, this.#pins(), anchor);
            this.#baselineRevision ??= page.revision;
            this.#anchor = anchor;
          });
          this.#notify();
          return;
        } catch (error) {
          if (
            signal.aborted || attempt > 0 ||
            !(error instanceof ConversationError && error.code === "snapshot_gap")
          ) throw error;
        } finally {
          this.#pages.delete(pending);
        }
      }
    }, caller);
  }
  apply(change: ConversationChange): boolean {
    this.signal.throwIfAborted();
    const oldRows = this.projection.rows;
    const before = new Map(change.operations.map((op) => {
      const id = op.type === "upsert" ? op.entry.id : op.id;
      return [id, this.projection.find(id)];
    }));
    if (!this.#projection.apply(change, this.#pins(), this.#anchor)) return false;
    for (const pending of this.#pages) {
      if (pending.overflow) continue;
      pending.bytes += changeSize(change);
      if (pending.bytes > this.limits.reconciliationBytes) {
        pending.overflow = true;
        pending.changes.length = 0;
      } else pending.changes.push(change);
    }
    for (const waiter of this.#pageWaiters) waiter();
    for (const operation of change.operations) {
      const id = operation.type === "upsert" ? operation.entry.id : operation.id;
      if (operation.type === "append") {
        const cached = this.#cache.content(contentKey(id, operation.baseRevision));
        if (
          cached !== undefined &&
          cached.text.length + operation.text.length <= this.limits.contentCharacters
        ) {
          this.#remember(
            contentKey(id, operation.revision),
            cached.text + operation.text,
            cached.presentation,
          );
        }
      }
      this.#notify(id);
    }
    const rows = this.projection.rows;
    if (
      rows.length !== oldRows.length || rows.some((row, i) => row !== oldRows[i]) ||
      change.operations.some((op) => op.type === "upsert" && !before.get(op.entry.id))
    ) this.#notify();
    return true;
  }
  cachedText(id: string, revision: string): string | undefined {
    return this.#cache.get(contentKey(id, revision));
  }
  /** Deliver exact text even when pinned cache entries leave no retention capacity. */
  readText(id: string, revision: string, caller?: AbortSignal): Promise<string> {
    return this.readContent(id, revision, caller).then((content) => content.text);
  }
  /** Resolve text and its presentation together under the same exact revision and cache budget. */
  readContent(id: string, revision: string, caller?: AbortSignal): Promise<TextContent> {
    this.signal.throwIfAborted();
    caller?.throwIfAborted();
    const key = contentKey(id, revision);
    const cached = this.#cache.content(key);
    if (cached !== undefined) return Promise.resolve({ entryId: id, revision, ...cached });
    return this.#tasks.run(async (signal) => {
      const content = await this.#source.readContent(id, revision, signal);
      signal.throwIfAborted();
      if (content.entryId !== id || content.revision !== revision) {
        throw new ConversationError("content_revision_gap");
      }
      if (
        content.text.length > this.limits.contentCharacters ||
        (content.presentation &&
          JSON.stringify(content.presentation).length > this.limits.contentCharacters)
      ) {
        throw new ConversationError("content_limit");
      }
      this.#remember(key, content.text, content.presentation);
      return content;
    }, caller);
  }
  attachView(id: string): ConversationViewState {
    this.signal.throwIfAborted();
    if (!id || this.#views.has(id)) throw new ConversationError("view_identity_conflict");
    const view = new ConversationViewState(id, this, () => this.#views.delete(id));
    this.#views.set(id, view);
    return view;
  }
  /** Confirm only the accepted draft revision; preserve edits made while submission was pending. */
  confirmSubmission(viewId: string, revision: number): void {
    this.signal.throwIfAborted();
    this.#views.get(viewId)?.confirmSubmission(revision);
  }
  /** Copy reads exact historical source, without loading its entire index or DOM. */
  async copySelection(
    selection: ConversationSelection,
    expanded: ReadonlySet<string>,
    caller?: AbortSignal,
  ): Promise<string> {
    this.signal.throwIfAborted();
    if (selection.epoch !== this.projection.epoch) throw new ConversationError("epoch_changed");
    compareRevision(selection.projectionRevision, "0");
    let start = selection.anchor, end = selection.focus;
    if (!Number.isSafeInteger(start.ordinal) || !Number.isSafeInteger(end.ordinal)) {
      throw new ConversationError("invalid_selection");
    }
    if (
      start.ordinal > end.ordinal || (start.ordinal === end.ordinal && start.offset > end.offset)
    ) [start, end] = [end, start];
    if (
      start.ordinal < 0 || end.ordinal >= this.projection.total ||
      !Number.isSafeInteger(start.offset) || !Number.isSafeInteger(end.offset) ||
      start.offset < 0 || end.offset < 0
    ) throw new ConversationError("invalid_selection");
    const pieces: string[] = [];
    let bytes = 0, next = start.ordinal;
    while (next <= end.ordinal) {
      // Only source I/O holds a pool slot; the text reads below acquire their own requests.
      const page = await this.#tasks.run(
        (signal) =>
          this.#source.readPage({
            offset: next,
            limit: Math.min(this.limits.pageSize, end.ordinal - next + 1),
            revision: selection.projectionRevision,
          }, signal),
        caller,
      );
      if (
        page.epoch !== selection.epoch || page.revision !== selection.projectionRevision ||
        page.sessionId !== this.sessionId
      ) throw new ConversationError("selection_unavailable");
      let progressed = false;
      for (const entry of page.entries) {
        if (entry.ordinal !== next || entry.ordinal > end.ordinal || entry.kind === "unavailable") {
          throw new ConversationError("selection_unavailable");
        }
        const first = entry.ordinal === start.ordinal, last = entry.ordinal === end.ordinal;
        if (
          (first && (entry.id !== start.id || entry.contentRevision !== start.revision)) ||
          (last && (entry.id !== end.id || entry.contentRevision !== end.revision))
        ) throw new ConversationError("selection_unavailable");
        const collapsed = isActivity(entry) && !expanded.has(entry.id);
        const text = collapsed
          ? entry.preview
          : await this.readText(entry.id, entry.contentRevision, caller);
        if ((first && start.offset > text.length) || (last && end.offset > text.length)) {
          throw new ConversationError("invalid_selection");
        }
        const selected = text.slice(first ? start.offset : 0, last ? end.offset : undefined);
        bytes += textBytes(selected) + (pieces.length ? 4 : 0);
        if (bytes > this.limits.selectionBytes) throw new ConversationError("selection_capacity");
        pieces.push(selected);
        next++;
        progressed = true;
      }
      if (!progressed) throw new ConversationError("selection_unavailable");
    }
    return pieces.join("\n\n");
  }
  dispose(): Promise<void> {
    if (!this.#disposal) {
      for (const view of [...this.#views.values()]) view.dispose();
      this.#disposal = this.#tasks.dispose().then(() => {
        this.#cache.clear();
        this.#projection.clear();
        this.#listeners.clear();
        this.#entryListeners.clear();
      });
    }
    return this.#disposal;
  }
}

/** Activities use a lazy collapsed summary until a view explicitly expands them. */
export function isActivity(entry: ConversationEntry): boolean {
  return entry.kind === "tool" || entry.kind === "reasoning-summary" || entry.kind === "child-run";
}

/** Local draft, selection, expansion and pins belong to one independent view. */
export class ConversationViewState {
  readonly #owner = new AbortController();
  readonly #listeners = new Set<Listener>();
  readonly #expanded = new Set<string>();
  #visible: readonly number[] = [];
  #draft = "";
  #draftRevision = 0;
  #selection: ConversationSelection | undefined;
  readonly #release: () => void;
  constructor(readonly id: string, readonly runtime: ConversationRuntime, release: () => void) {
    this.#release = release;
  }
  get signal(): AbortSignal {
    return this.#owner.signal;
  }
  get draft(): string {
    return this.#draft;
  }
  get selection(): ConversationSelection | undefined {
    return this.#selection;
  }
  get pins(): ReadonlySet<number> {
    const points = this.#selection
      ? [this.#selection.anchor.ordinal, this.#selection.focus.ordinal]
      : [];
    return new Set([...this.#visible, ...points]);
  }
  subscribe(listener: Listener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }
  #changed(): void {
    for (const listener of this.#listeners) listener();
  }
  setDraft(text: string): void {
    this.signal.throwIfAborted();
    if (text.length > 64_000) throw new ConversationError("draft_limit");
    this.#draft = text;
    this.#draftRevision++;
    this.#changed();
  }
  captureSubmission(): { viewId: string; text: string; revision: number } {
    this.signal.throwIfAborted();
    return Object.freeze({ viewId: this.id, text: this.#draft, revision: this.#draftRevision });
  }
  confirmSubmission(revision: number): void {
    if (this.#draftRevision === revision) this.setDraft("");
  }
  setVisible(ordinals: readonly number[]): void {
    this.signal.throwIfAborted();
    if (ordinals.length > 68) throw new ConversationError("pin_capacity");
    this.#visible = [...ordinals];
  }
  setSelection(selection: ConversationSelection | undefined): void {
    this.signal.throwIfAborted();
    this.#selection = selection;
    this.#changed();
  }
  isExpanded(id: string): boolean {
    return this.#expanded.has(id);
  }
  toggle(id: string): void {
    this.signal.throwIfAborted();
    if (this.#expanded.has(id)) this.#expanded.delete(id);
    else {
      if (this.#expanded.size >= 128) {
        const first = this.#expanded.values().next().value;
        if (first !== undefined) this.#expanded.delete(first);
      }
      this.#expanded.add(id);
    }
    this.#changed();
  }
  copySelection(): Promise<string> {
    if (!this.#selection) return Promise.reject(new ConversationError("selection_unavailable"));
    return this.runtime.copySelection(this.#selection, this.#expanded, this.signal);
  }
  dispose(): void {
    if (this.signal.aborted) return;
    this.#owner.abort(new DOMException("View closed", "AbortError"));
    this.#visible = [];
    this.#selection = undefined;
    this.#draft = "";
    this.#expanded.clear();
    this.#listeners.clear();
    this.#release();
  }
}
