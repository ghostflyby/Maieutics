/**
 * The notebook controller: one ordinary cell is one submitted Agent turn
 * (invariant 2). Turn cells of a batch are enqueued on the server-owned
 * per-session turn queue (`POST /v1/agent/sessions/{sid}/queue`) and execute
 * there in order; the client only projects. It maps item ids onto cells,
 * shows the queued markers, starts each cell's execution when its item starts
 * running (the `queue.updated` frame announces the runId), and folds the
 * run's frames exactly like an inline run — the run's own frames flow on the
 * same session events socket. Command cells (`%`-prefixed) and blank cells
 * are never queued (the server rejects command text) and execute inline
 * between the queued runs, so a mixed batch keeps its document order.
 *
 * The per-notebook orchestration chain serializes the client-side steps only
 * (pin decisions, fork modals, inline command cells, and the enqueue POSTs
 * themselves), so overlapping batches stay deterministic (Run All then Run
 * Above run their orchestration in submission order). Turn execution itself
 * serializes in the server queue and is awaited through queue state, not
 * client chaining — two notebooks pinned to one session share the same server
 * queue and see each other's positions.
 *
 * The queue projection (the itemId ↔ cell map) is client state: it is lost on
 * an extension host reload, after which queued items keep running server-side
 * without markers or run following until the notebook's cells are run again.
 * There is deliberately no text-matching reconciliation — the map rebuilds
 * from the server's snapshots only for items enqueued by this client.
 *
 * History gate (docs/notebook-agent-semantics-design.md): cells bound to
 * committed turns are not re-submitted blindly. Run Below/Run All filter to
 * pending cells; Run Above finds only committed history and explains instead
 * of running; an explicit run on one committed cell forks at that point
 * (regenerate, or edit-and-continue when the text drifted) so history stays
 * immutable while the mainstream agent UX — continue from here — still works.
 *
 * Streaming: text deltas fold into a TurnView and repaint the cell's markdown
 * output at most every PaintIntervalMs (mirroring the kernel adapter's flush
 * cadence). Tool activity renders as status lines above the answer; REPL
 * presentation frames attach to their display id in a later renderer pass.
 * Subagent child runs (ADR 0030) share the parent run's session stream under
 * the child's own runId and fold into one indented 子代理 timeline segment of
 * the parent cell — no child ever gets a cell or execution of its own.
 */

import * as vscode from "vscode";
import type { FrontendClient, SubmitAnswer } from "./client.ts";
import type { EventFrame, QueueState, SessionInfo } from "./protocol.ts";
import { FrontendError } from "./protocol.ts";
import {
  cellHistoryState,
  type CellLike,
  isRunAboveSelection,
  partitionByHistory,
  readTurnBinding,
  withoutTurnBinding,
  withTurnBinding,
} from "./cellHistory.ts";
import {
  planBatchSteps,
  QueueProjection,
  QueueWatcher,
  type TrackedQueueItem,
} from "./queueProjection.ts";
import {
  bundleItems,
  drainPendingObjectItems,
  NotebookType,
  outputItemFromSpec,
  readStoredSessionId,
  StoredSessionMetadataKey,
} from "./serializer.ts";
import {
  type ReplDisplayEntry,
  type SubagentSnapshotView,
  type ToolSnapshotView,
  turnOutputItems,
  TurnView,
} from "./turnView.ts";
import { resolveSessionPin } from "./sessionPin.ts";
import { SessionActivity } from "./runActivity.ts";
import { coerceField, planElicitation } from "./elicitation.ts";
import {
  buildRgArgs,
  bundledRgPath,
  codeActionsToWire,
  collectDirty,
  collectProblems,
  type DebugSessionState,
  defaultTreeSearchBounds,
  dirtyBufferAnswer,
  extractIdeRequest,
  fsEntriesAnswer,
  fsRootsAnswer,
  ideCommandArgs,
  IdeFolderRegistry,
  ideMethodCommands,
  type IdeRequestPayload,
  isCoveredLocalTarget,
  isIdeMethodSupported,
  isValidIdeFsSegment,
  type LaunchConfiguration,
  localFsEntriesAnswer,
  localFsError,
  normalizedHoverAnswer,
  parseLaunchConfigurations,
  parseRgMatchEvent,
  passesSearchFilters,
  searchTree,
  type SearchTreeFs,
  sha256Hex,
  textEditsToWire,
  type TrackedDebugSession,
  transitionDebugSession,
  workspaceEditToWire,
} from "./ide.ts";
import { type ChildProcess, spawn } from "node:child_process";
import { childRunsOfParent, resolveFrameOwner } from "./runRouting.ts";
import { encodeSkillSubmission } from "./skillInteraction.ts";

const PaintIntervalMs = 60;
/** Bridges the protocol onto one notebook's outputs. Notebook execution
 * connects lazily, but activation-time session sync may connect earlier. */
export interface NotebookBridge {
  client(): Promise<FrontendClient>;
  /** Current server session for the notebook's connection. */
  session(): Promise<SessionInfo>;
  /** Fetches a binary object by content address (object bypass dereference). */
  fetchObject(sha256: string): Promise<Uint8Array>;
}

/** Adapts a VSCode cell onto the structural view the history model reads,
 * keeping the original cell reachable for execution and metadata edits. */
export interface TaggedCell extends CellLike {
  cell: vscode.NotebookCell;
}

export function taggedCell(cell: vscode.NotebookCell): TaggedCell {
  return { cell, metadata: cell.metadata, text: cell.document.getText() };
}

export class MaieuticsNotebookController implements vscode.Disposable {
  private readonly controller: vscode.NotebookController;
  /** Per-notebook orchestration chain: orders the client-side steps of
   * overlapping batches. Turn execution serializes in the server queue. */
  private readonly orchestrations = new Map<string, Promise<void>>();
  private readonly streams = new Map<string, NotebookStream>();
  private readonly warnedPins = new Set<string>();
  /** Run ids of each notebook's in-flight turn — inline or started from the
   * queue — for the interrupt handler. */
  private readonly activeRuns = new Map<string, string>();
  /** Notebooks closed with work queued or running: their remaining cells must
   * not submit and their streams must not be silently recreated. Cleared when
   * the document is submitted again (reopened). */
  private readonly closedNotebooks = new Set<string>();

  /** Session run-activity (busy/idle) shared with the extension surface. */
  readonly activity = new SessionActivity();

  constructor(
    private readonly bridge: NotebookBridge,
    private readonly output: vscode.OutputChannel,
    /** The itemId ↔ cell projection of the server-owned queue, for the
     * "queued" cell status markers and run following. */
    private readonly projection: QueueProjection<vscode.NotebookCell>,
  ) {
    this.controller = vscode.notebooks.createNotebookController(
      "maieutics",
      NotebookType,
      "Maieutics",
    );
    this.controller.description = "Maieutics agent kernel";
    this.controller.supportsExecutionOrder = true;
    this.controller.executeHandler = (cells, document) => this.executeAsync(cells, document);
    // Presence of the interrupt handler is what adds the stop button. The
    // kernel "Restart" verb routes through the same handler, which matches its
    // only agent meaning: abort the in-flight run. Once this is set, the cell
    // cancellation token no longer fires for UI stops, so cancellation goes
    // through here alone (cooperative cancel, invariant 9).
    this.controller.interruptHandler = (notebook) => this.interruptAsync(notebook);
  }

  dispose(): void {
    this.resetConnections();
    this.controller.dispose();
  }

  /** Drops live event streams after a server restart; the next execution in
   * each notebook reconnects against the new process. */
  resetConnections(): void {
    for (const stream of this.streams.values()) stream.dispose();
    this.streams.clear();
  }

  /** Cancels the notebook's in-flight runs and removes its queued items from
   * the server queue when its document closes (best effort: attach-mode
   * servers keep running, so the runs must be told and the items deleted).
   * The stream map is keyed by session id, so the notebook's pinned session —
   * the same pin ensureSessionAsync executes against — resolves the stream. */
  handleNotebookClosed(document: vscode.NotebookDocument): void {
    const queueKey = document.uri.toString();
    // Remaining cells of a closed notebook never submit.
    this.closedNotebooks.add(queueKey);
    this.orchestrations.delete(queueKey);
    // The items this notebook still has queued are removed server-side; the
    // list is captured before the markers clear.
    const items = this.projection.entriesWhere((cell) => cell.notebook.uri.toString() === queueKey);
    this.projection.clearCells((cell) => cell.notebook.uri.toString() === queueKey);
    if (items.length > 0) void this.deleteQueueItemsAsync(items);

    const sessionId = readStoredSessionId(document.metadata);
    if (sessionId === undefined) return;
    // Another open notebook pinned to the same session still owns the stream.
    const shared = vscode.workspace.notebookDocuments.some((other) =>
      other !== document && other.notebookType === NotebookType &&
      readStoredSessionId(other.metadata) === sessionId
    );
    if (shared) return;

    const stream = this.streams.get(sessionId);
    if (stream === undefined) return;
    stream.cancelAll();
    stream.dispose();
    this.streams.delete(sessionId);
  }

  /** Deletes the given queue items server-side (notebook closed): best effort
   * and sequential; an item that already started running is left to the
   * cancel path, and an unreachable server needs no retry. */
  private async deleteQueueItemsAsync(
    items: readonly { sessionId: string; itemId: string }[],
  ): Promise<void> {
    let client: FrontendClient;
    try {
      client = await this.bridge.client();
    } catch {
      return; // The server is gone; nothing to delete.
    }
    for (const item of items) {
      await client.dequeueItem(item.sessionId, item.itemId).catch(() => {
        // Best effort: a running or unknown item needs no retry.
      });
    }
  }

  /** Runs cells through the server queue (turn cells) and the inline path
   * (commands that submit follow-up turns). */
  runAsync(cells: vscode.NotebookCell[], document: vscode.NotebookDocument): Promise<void> {
    return this.executeAsync(cells, document);
  }

  private async executeAsync(
    cells: vscode.NotebookCell[],
    document: vscode.NotebookDocument,
  ): Promise<void> {
    const queueKey = document.uri.toString();
    // A fresh submission (the document was reopened) revives the notebook;
    // batches that were already queued when it closed stay gated below.
    this.closedNotebooks.delete(queueKey);
    // The per-notebook orchestration chain (see the module header): turn
    // execution serializes in the server queue, but the client-side steps
    // around it must not interleave when two batches overlap.
    const previous = this.orchestrations.get(queueKey) ?? Promise.resolve();
    const run = previous.then(async () => {
      if (this.closedNotebooks.has(queueKey)) return;
      const sessionId = await this.ensureSessionAsync(document);
      // The history gate: committed cells never re-submit silently.
      const { pending, committed } = partitionByHistory(cells.map(taggedCell));

      if (
        committed.length > 0 &&
        isRunAboveSelection(committed.map((entry) => entry.cell.index))
      ) {
        this.warnOnce(
          `${document.uri.toString()}:run-above`,
          "Maieutics: Run Above targets committed history, so there is nothing to run. " +
            "New cells continue below the last answered cell; run a committed cell to branch from it.",
        );
        return;
      }

      let targets = pending;
      let activeSession = sessionId;
      if (committed.length > 0) {
        // An explicit run on committed history forks at the first committed
        // cell (regenerate, or edit-and-continue when the text drifted); any
        // further committed cells in the same request stay on the old branch.
        if (pending.length > 0) {
          this.warnOnce(
            `${document.uri.toString()}:mixed-run`,
            "Maieutics: skipped committed cell(s) — history is immutable. " +
              "Run a committed cell on its own to branch from it.",
          );
        } else {
          if (committed.length > 1) {
            this.warnOnce(
              `${document.uri.toString()}:fork-siblings`,
              "Maieutics: only the first committed cell branches — the others stay on the old branch.",
            );
          }
          const forked = await this.forkAtCellAsync(committed[0].cell, document, activeSession);
          if (forked === undefined) return;
          activeSession = forked;
          targets = [committed[0]];
        }
      }

      // Walk the batch in document order: maximal runs of consecutive turn
      // cells enqueue as ONE POST and await their server-side completion by
      // watching queue state; command cells and blanks execute inline between
      // them. A close mid-batch stops the walk (no stream is recreated).
      for (const step of planBatchSteps(targets, (entry) => entry.text)) {
        if (this.closedNotebooks.has(queueKey)) return;
        if (step.kind === "inline") {
          await this.executeCellAsync(step.cell.cell, activeSession);
        } else {
          await this.enqueueTurnRunAsync(step.cells, activeSession, queueKey);
        }
      }
    });
    this.orchestrations.set(queueKey, run.then(() => {}, () => {}));
    await run;
  }

  /**
   * Enqueues one maximal run of consecutive turn cells as a single POST and
   * waits — by watching the server queue state — until every item leaves the
   * queue (finished, dequeued, or cleared). While items are queued their
   * cells show only the queued marker; when an item starts running, the
   * queue.updated frame drives applyQueueState → followRun. A failed enqueue
   * or a stream lost while waiting surfaces typed failures on the cells (they
   * stay pending for a retry) instead of hanging the batch.
   */
  private async enqueueTurnRunAsync(
    cells: TaggedCell[],
    sessionId: string,
    queueKey: string,
  ): Promise<void> {
    const client = await this.bridge.client();
    const stream = await this.ensureStreamAsync(sessionId);
    const texts = cells.map((entry) => entry.text);
    try {
      // Captured before the POST so the drain wait can ignore snapshots that
      // predate the enqueue (the enqueue frame itself always postdates it).
      const version = stream.queueSnapshotVersion;
      const items = await client.enqueueTurns(sessionId, texts);
      if (items.length !== cells.length) {
        throw new FrontendError(
          "protocol_error",
          202,
          "The queue answer does not match the enqueued items.",
        );
      }
      this.projection.track(
        sessionId,
        items.map((item, index) => ({
          id: item.id,
          position: item.position,
          cell: cells[index].cell,
          text: texts[index],
        })),
      );

      const drained = await stream.awaitQueueDrainedAsync(version, items.map((item) => item.id));
      if (!drained && !this.closedNotebooks.has(queueKey)) {
        const failure = new FrontendError(
          "events_disconnected",
          0,
          "The Maieutics server connection was reset.",
        );
        for (const entry of cells) this.paintUnstartedFailure(entry.cell, failure);
      }
    } catch (error) {
      for (const entry of cells) this.paintUnstartedFailure(entry.cell, error);
    }
  }

  /** Paints a typed failure on a cell whose turn never started (a failed
   * enqueue, or a stream lost while queued): the cell ends unsuccessfully and
   * stays pending for a retry. */
  private paintUnstartedFailure(cell: vscode.NotebookCell, error: unknown): void {
    const execution = this.controller.createNotebookCellExecution(cell);
    execution.start(Date.now());
    execution.clearOutput();
    execution.replaceOutput([errorOutput(error)]);
    execution.end(false, Date.now());
  }

  /** Applies one server queue snapshot (a queue.updated frame or a reconnect
   * GET): the item that started running takes its cell out of the marker
   * projection and the cell's execution begins, driven by the run frames that
   * follow on the same events socket; then the waiting items' markers
   * refresh. */
  private applyQueueState(sessionId: string, snapshot: QueueState): void {
    if (snapshot.sessionId !== sessionId) return;
    const running = snapshot.running;
    if (running !== null) {
      const tracked = this.projection.take(sessionId, running.itemId);
      if (tracked !== undefined) this.followRun(sessionId, running.runId, tracked);
    }
    this.projection.apply(sessionId, snapshot);
  }

  /** Attaches the cell of a queue item that started running: starts its
   * execution and folds the run's frames into it. The RunExecution registers
   * synchronously (before the first await) so frames already queued behind
   * the announcing queue.updated frame still route to the cell. */
  private followRun(
    sessionId: string,
    runId: string,
    tracked: TrackedQueueItem<vscode.NotebookCell>,
  ): void {
    const stream = this.streams.get(sessionId);
    if (stream === undefined) return; // The stream died; its waits already failed.
    const execution = this.controller.createNotebookCellExecution(tracked.cell);
    execution.start(Date.now());
    execution.clearOutput();

    // Track the in-flight run for the interrupt handler (same contract as the
    // inline path: the stop button cancels it).
    const runKey = tracked.cell.notebook.uri.toString();
    this.activeRuns.set(runKey, runId);
    const settled = stream.awaitRunAsync(runId, execution, tracked.text);
    void (async () => {
      try {
        const committed = await settled;
        // Only a successfully completed run becomes part of history: failed
        // and cancelled turns roll back server-side, so their cells stay
        // pending and can be retried.
        if (committed) {
          await this.writeTurnBindingForCell(tracked.cell, runId, tracked.text);
        }
      } catch (error) {
        // A failed binding write leaves the cell pending (re-runnable); the
        // run's output already settled.
        this.output.appendLine(`run follow failed: ${error}`);
      } finally {
        if (this.activeRuns.get(runKey) === runId) this.activeRuns.delete(runKey);
      }
    })();
  }

  /** Removes the cell's queued item from the server queue: the marker clears
   * via the next queue.updated. An item that already started running is
   * reported (cancel the run instead); the in-flight cell is not affected. */
  async dequeueQueuedCell(
    cell: vscode.NotebookCell,
  ): Promise<"removed" | "not-queued" | "running" | "failed"> {
    const entries = this.projection.entriesWhere((tracked) => tracked === cell);
    if (entries.length === 0) return "not-queued";
    const entry = entries[0];
    let client: FrontendClient;
    try {
      client = await this.bridge.client();
      await client.dequeueItem(entry.sessionId, entry.itemId);
    } catch (error) {
      if (error instanceof FrontendError && error.code === "item_running") return "running";
      if (error instanceof FrontendError && error.code === "not_found") {
        // Already gone server-side: the local entry goes too.
        this.projection.drop(entry.sessionId, entry.itemId);
        return "removed";
      }
      this.output.appendLine(`dequeue failed: ${error}`);
      return "failed";
    }
    this.projection.drop(entry.sessionId, entry.itemId);
    return "removed";
  }

  /** Removes every queued item of the notebook from the server queue; returns
   * how many were removed. The running item is not affected (use the
   * interrupt for that). Items that cannot be removed keep their markers; the
   * queue state self-heals on the next snapshot. */
  async clearQueuedCells(document: vscode.NotebookDocument): Promise<number> {
    const queueKey = document.uri.toString();
    const items = this.projection.entriesWhere((cell) => cell.notebook.uri.toString() === queueKey);
    if (items.length === 0) return 0;

    // A notebook pins one session, so the common shape is one clear-all call;
    // a multi-session projection (theoretical today) falls back to per-item
    // removals. The running item is not affected either way (the server's
    // clear endpoint leaves it; use the interrupt for that).
    const sessionIds = [...new Set(items.map((item) => item.sessionId))];
    try {
      const client = await this.bridge.client();
      if (sessionIds.length === 1) {
        await client.clearQueue(sessionIds[0]);
      } else {
        for (const item of items) await client.dequeueItem(item.sessionId, item.itemId);
      }
    } catch (error) {
      this.output.appendLine(`clear queue failed: ${error}`);
    }

    let removed = 0;
    for (const item of items) {
      this.projection.drop(item.sessionId, item.itemId);
      removed++;
    }
    return removed;
  }

  /** Forks the pinned session at a committed cell and re-pins the notebook to
   * the new head. Unedited cells regenerate; edited cells continue from the
   * edit (mainstream edit-rewind). Returns the fork's session id, or
   * <code>undefined</code> when the user declined or the fork failed. */
  private async forkAtCellAsync(
    cell: vscode.NotebookCell,
    document: vscode.NotebookDocument,
    sessionId: string,
  ): Promise<string | undefined> {
    const binding = readTurnBinding(taggedCell(cell));
    if (binding === undefined) return undefined;

    const configuration = vscode.workspace.getConfiguration();
    let profileId: string | undefined;
    if (configuration.get<boolean>("maieutics.confirmFork", true)) {
      const stale = cellHistoryState(taggedCell(cell)) === "stale";
      const verb = stale ? "continue from your edit" : "re-run this cell";
      const answer = await vscode.window.showWarningMessage(
        `Maieutics: run this cell? The session forks here and the run ${verb} as the new branch's first turn. ` +
          "The original branch is kept.",
        { modal: true },
        "Fork and run",
        "Fork with another model\u2026",
      );
      if (answer === undefined) return undefined;
      if (answer === "Fork with another model\u2026") {
        const client = await this.bridge.client();
        const profiles = await client.modelProfiles().catch(() => []);
        const picked = await vscode.window.showQuickPick(
          profiles.map((profile) => ({
            label: `${profile.selected ? "$(check) " : ""}${profile.id}`,
            description: `${profile.provider} / ${profile.model}`,
            id: profile.id,
          })),
          { placeHolder: "Model profile for the new branch" },
        );
        if (picked === undefined) return undefined;
        profileId = picked.id;
      }
    }

    let fork: { id: string; title?: string };
    try {
      fork = await (await this.bridge.client()).forkSession(
        sessionId,
        profileId === undefined ? { runId: binding.runId } : { runId: binding.runId, profileId },
      );
    } catch (error) {
      void vscode.window.showErrorMessage(
        `Maieutics: the fork failed (${error instanceof Error ? error.message : String(error)}).`,
      );
      return undefined;
    }

    await this.writeSessionId(document, fork.id);

    // The fork's history ends at this cell: committed cells below it belong to
    // the old branch and leave the view (they stay on the server under the old
    // branch's title). Pending cells below are view-only composer drafts the
    // server never saw — they stay, moving below the new frontier.
    const cells = document.getCells();
    const deletable = new Set<number>();
    for (let index = cell.index + 1; index < cells.length; index++) {
      if (readTurnBinding(taggedCell(cells[index])) !== undefined) deletable.add(index);
    }

    if (deletable.size > 0) {
      // Contiguous ranges, deleted bottom-up inside one edit so the earlier
      // ranges stay valid as each delete applies.
      const runs: Array<[number, number]> = [];
      for (const index of [...deletable].sort((a, b) => a - b)) {
        const last = runs.at(-1);
        if (last !== undefined && last[1] === index) last[1] = index + 1;
        else runs.push([index, index + 1]);
      }

      const edit = new vscode.WorkspaceEdit();
      // Labeled edits (WorkspaceEditMetadata.label) do not exist in the 1.99
      // stable API; the edit stays plain and the toast carries the story.
      edit.set(
        document.uri,
        runs.reverse().map(([start, end]) =>
          vscode.NotebookEdit.deleteCells(new vscode.NotebookRange(start, end))
        ),
      );
      await vscode.workspace.applyEdit(edit);
    }

    // This cell is about to create a fresh turn on the new head: drop the old
    // branch's binding so a failed re-run leaves the cell pending instead of
    // pointing at a run that belongs to the source session.
    const unbind = new vscode.WorkspaceEdit();
    unbind.set(document.uri, [
      vscode.NotebookEdit.updateCellMetadata(cell.index, withoutTurnBinding(cell.metadata)),
    ]);
    await vscode.workspace.applyEdit(unbind);

    void vscode.window.showInformationMessage(
      `Maieutics: original branch kept as "${fork.title ?? fork.id.slice(0, 12)}" — ` +
        "switch in the Sessions tree. Pending drafts below the fork were kept.",
    );
    return fork.id;
  }

  /** Cancels the notebook's in-flight run (stop button / kernel restart). */
  private async interruptAsync(notebook: vscode.NotebookDocument): Promise<void> {
    const runId = this.activeRuns.get(notebook.uri.toString());
    if (runId === undefined) return;

    try {
      await (await this.bridge.client()).cancelRun(runId);
    } catch (error) {
      this.output.appendLine(`interrupt failed: ${error}`);
    }
  }

  private async executeCellAsync(cell: vscode.NotebookCell, sessionId: string): Promise<void> {
    // The boundary model encodes the text: tracked skill selections ride as
    // markers, untracked marker-shaped text rides as a mention (ADR 0039
    // selection design). A cell without a model (never opened for edit since
    // extension activation) passes through byte-for-byte.
    const text = encodeSkillSubmission(cell.document.uri.toString(), cell.document.getText());
    const execution = this.controller.createNotebookCellExecution(cell);
    // The failure path only starts the execution when it has not started yet:
    // a second start() can leave the matching end() ignored (spinner forever).
    let started = false;
    try {
      const stream = await this.ensureStreamAsync(sessionId);
      execution.start(Date.now());
      started = true;
      execution.clearOutput();

      // An empty cell mirrors the kernel contract: a successful no-op turn.
      if (text.trim().length === 0) {
        execution.end(true, Date.now());
        return;
      }

      const answer: SubmitAnswer = await (await this.bridge.client())
        .submitTurn(sessionId, text);
      if (answer.kind === "command") {
        // Session-switching commands change the active session; re-pin the
        // notebook so the next batch does not resume the previous one back.
        if (answer.sessionId !== undefined && answer.sessionId !== sessionId) {
          await this.writeSessionIdForCell(cell, answer.sessionId);
        }

        execution.replaceOutput([commandOutput(answer.markdown)]);
        execution.end(true, Date.now());
        return;
      }

      // Track the in-flight run for the interrupt handler; the stop button
      // cancels it instead of relying on the cell token (which the presence
      // of the interrupt handler disables for UI stops).
      const runKey = cell.notebook.uri.toString();
      this.activeRuns.set(runKey, answer.runId);
      try {
        const committed = await stream.awaitRunAsync(answer.runId, execution, text);
        // Only a successfully completed run becomes part of history: failed
        // and cancelled turns roll back server-side, so their cells stay
        // pending and can be retried.
        if (committed) {
          await this.writeTurnBindingForCell(cell, answer.runId, text);
        }
      } finally {
        this.activeRuns.delete(runKey);
      }
    } catch (error) {
      if (!started) {
        execution.start(Date.now());
        execution.clearOutput();
        execution.replaceOutput([errorOutput(error)]);
      } else {
        // A failure after streaming began (or even after the run painted its
        // answer) keeps what the cell already shows: append the failure, never
        // replace the streamed partial (paintFailure semantics).
        execution.appendOutput([errorOutput(error)]);
      }
      execution.end(false, Date.now());
    }
  }

  /** Persists the turn binding of a committed cell: metadata is the binding's
   * home (it survives clear-output), written via a metadata-only edit. */
  private async writeTurnBindingForCell(
    cell: vscode.NotebookCell,
    runId: string,
    input: string,
  ): Promise<void> {
    const edit = new vscode.WorkspaceEdit();
    edit.set(cell.notebook.uri, [
      vscode.NotebookEdit.updateCellMetadata(
        cell.index,
        withTurnBinding(cell.metadata, { runId, input }),
      ),
    ]);
    await vscode.workspace.applyEdit(edit);
  }

  /** Shows an informational toast once per key (document-scoped warnings). */
  private warnOnce(key: string, message: string): void {
    if (this.warnedPins.has(key)) return;
    this.warnedPins.add(key);
    void vscode.window.showWarningMessage(message);
  }

  /** Re-attaches the notebook to the server session stored in its metadata before the
   * batch runs, so two open notebooks alternate deterministically instead of racing for
   * the active session. The decision (and any failure warning) surfaces once per
   * document and session. */
  private async ensureSessionAsync(document: vscode.NotebookDocument): Promise<string> {
    const stored = readStoredSessionId(document.metadata);
    const decision = await resolveSessionPin(stored, await this.bridge.client());

    if (decision.warning !== undefined) {
      const warnKey = `${document.uri.toString()}:${decision.session.id}`;
      if (!this.warnedPins.has(warnKey)) {
        this.warnedPins.add(warnKey);
        void vscode.window.showWarningMessage(`Maieutics: ${decision.warning}`);
      }
    }

    if (decision.pinId !== undefined && decision.pinId !== stored) {
      await this.writeSessionId(document, decision.pinId);
    } else if (decision.kind === "resume") {
      await this.writeSessionId(document, decision.session.id);
    }

    return decision.session.id;
  }

  /** Persists the pinned session id for the notebook owning a cell. */
  private async writeSessionIdForCell(cell: vscode.NotebookCell, sessionId: string): Promise<void> {
    await this.writeSessionId(cell.notebook, sessionId);
  }

  /** Persists the pinned session id into the notebook's metadata via a workspace edit
   * (the file records the session it last ran against once saved). */
  private async writeSessionId(
    document: vscode.NotebookDocument,
    sessionId: string,
  ): Promise<void> {
    const edit = new vscode.WorkspaceEdit();
    edit.set(document.uri, [vscode.NotebookEdit.updateNotebookMetadata({
      ...document.metadata,
      [StoredSessionMetadataKey]: sessionId,
    })]);
    await vscode.workspace.applyEdit(edit);
  }

  private async ensureStreamAsync(sessionId: string): Promise<NotebookStream> {
    const existing = this.streams.get(sessionId);
    if (existing) return existing;

    const stream = new NotebookStream(
      await this.bridge.client(),
      sessionId,
      (runId) => this.output.appendLine(`run ${runId} finished`),
      (message) => this.output.appendLine(message),
      (snapshot) => this.applyQueueState(sessionId, snapshot),
      this.activity,
    );
    stream.start();
    this.streams.set(sessionId, stream);
    return stream;
  }
}

/** Consumes the session event stream and routes frames to in-flight runs. */
class NotebookStream {
  private readonly runs = new Map<string, RunExecution>();
  /** Subagent child run ids (ADR 0030) folded into their parent run's cell:
   * child frames ride the parent's session stream under the child's own
   * runId, which this map never holds in `runs`. */
  private readonly childRuns = new Map<string, string>();
  private readonly queue: QueueWatcher;
  private controller: AbortController | null = null;

  constructor(
    private readonly client: FrontendClient,
    private readonly sessionId: string,
    private readonly onRunFinished: (runId: string) => void,
    private readonly log: (message: string) => void,
    /** Applied queue snapshots (frames and reconnect GETs), for the marker
     * projection and run following. */
    onQueue: (snapshot: QueueState) => void,
    /** Session run-activity registry: `run.status` busy/idle frames mark it so
     * the status bar, tree badges, and background notifications can follow
     * runs the active editor is not showing. */
    private readonly activity: SessionActivity,
  ) {
    this.queue = new QueueWatcher(client, sessionId, onQueue, log);
  }

  dispose(): void {
    // Settle in-flight runs BEFORE aborting: a settled run ends its cell
    // execution and resolves the notebook's execution queue, so nothing is
    // left spinning behind a connection that no longer exists. Queue drain
    // waits resolve as failed too, so a batch never hangs on a dead socket.
    for (const run of this.runs.values()) {
      run.fail("events_disconnected", "The Maieutics server connection was reset.");
    }
    this.runs.clear();
    this.childRuns.clear();
    this.queue.failWaits();
    this.controller?.abort();
    this.controller = null;
  }

  start(): void {
    this.controller = new AbortController();
    void this.pumpAsync(this.controller.signal);
  }

  /** Cancels every in-flight run (notebook closing / window unloading).
   * Only the top-level run ids are cancelled: the server cascades a parent
   * cancellation to every child it spawned (ADR 0030 decision 10), and the
   * child-level cancel endpoint stays available without per-child UI. */
  cancelAll(): void {
    for (const runId of [...this.runs.keys()]) {
      void this.client.cancelRun(runId).catch((error) =>
        this.log(`cancel on close failed: ${error}`)
      );
      this.runs.get(runId)?.fail(
        "notebook_closed",
        "The notebook was closed while the run was in flight.",
      );
    }

    this.runs.clear();
    this.childRuns.clear();
  }

  /** Registers an execution and resolves to whether the run committed (a
   * successful terminal frame) when it reaches a terminal frame. Failed,
   * cancelled, and missing runs resolve to false so their cells stay pending.
   * Also serves the queue path: a queued item's run is attached here when its
   * queue.updated announcement arrives. */
  awaitRunAsync(
    runId: string,
    execution: vscode.NotebookCellExecution,
    input: string,
  ): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      const run = new RunExecution(runId, execution, input, this.client, resolve);
      this.runs.set(runId, run);
      run.begin();
      execution.token.onCancellationRequested(() => {
        void this.client.cancelRun(runId).catch((error) => this.log(`cancel failed: ${error}`));
      });
    });
  }

  /** The queue watcher's snapshot version: captured before an enqueue so the
   * drain wait can ignore snapshots that predate it. */
  get queueSnapshotVersion(): number {
    return this.queue.snapshotVersion;
  }

  /** Resolves once a queue snapshot applied after `version` shows every item
   * gone; false when the stream fails first. */
  awaitQueueDrainedAsync(version: number, itemIds: readonly string[]): Promise<boolean> {
    return this.queue.awaitDrainedAsync(version, itemIds);
  }

  private async pumpAsync(signal: AbortSignal): Promise<void> {
    try {
      for await (const frame of this.client.events(this.sessionId, { signal })) {
        // Queue frames and the connect hello belong to the queue watcher; the
        // hello blocks until the reconnect snapshot applied, so an item that
        // started while the socket was away attaches before its run's
        // replayed frames route.
        if (await this.queue.handleFrame(frame)) continue;
        // Run-activity frames drive the busy/idle surfaces; the idle frame
        // trails the terminal frame (invariant 7), and the terminal frames
        // mark idle themselves so a socket lost between the two cannot leave
        // a session stuck busy forever.
        if (frame.type === "run.status") {
          if (frame.state === "busy") this.activity.markBusy(this.sessionId);
          else this.activity.markIdle(this.sessionId);
          continue;
        }

        this.route(frame);
        // Any terminal view outcome retires the run: a settled run must not
        // stay here catching run-less frames (repl displays, input requests).
        // Only a TOP-LEVEL terminal frame retires and flips idle — a child's
        // terminal frame carries the child's runId, which `runs` never held,
        // and the session busy/idle stays bound to the top-level run: a
        // child finishing must not extinguish the busy badge while its
        // parent is still running (ADR 0030 decision 6).
        const runId = frame.runId ?? "";
        if (
          (frame.type === "run.completed" || frame.type === "run.failed" ||
            frame.type === "run.missing") && this.runs.delete(runId)
        ) {
          // The retiring run's children go with it, so a late frame of an
          // old child can never be adopted by the NEXT run.
          for (const child of childRunsOfParent(this.childRuns, runId)) {
            this.childRuns.delete(child);
          }
          this.activity.markIdle(this.sessionId);
        }
      }
    } catch (error) {
      if (!signal.aborted) {
        this.log(`event stream ended: ${error}`);
        // Fail every in-flight run so cells never hang on a dead socket.
        for (const run of this.runs.values()) run.fail("events_disconnected", String(error));
        this.runs.clear();
      }
    } finally {
      // Whether the stream ended by error, abort, or server close, no batch
      // may keep waiting on its queue state.
      this.queue.failWaits();
    }
  }

  private route(frame: EventFrame): void {
    const owner = resolveFrameOwner(frame.runId, this.runs, this.childRuns);
    switch (owner.kind) {
      case "session": {
        // REPL presentation frames carry no runId; the session gate keeps at
        // most one run in flight, so they belong to it.
        for (const run of this.runs.values()) {
          run.apply(frame);
          return;
        }
        return;
      }
      case "owned":
        this.runs.get(owner.runId)?.apply(frame);
        return;
      case "child":
        this.runs.get(owner.parentRunId)?.applyChild(frame);
        return;
      case "adopt":
        // First-seen id with exactly one in-flight run: its subagent child.
        // The wire carries no parentRunId, so attribution leans on arrival
        // order under the single-run gate (see runRouting.ts).
        this.childRuns.set(frame.runId ?? "", owner.parentRunId);
        this.runs.get(owner.parentRunId)?.applyChild(frame);
        return;
      case "drop":
        return;
    }
  }
}

/** Executes one catalog IDE method against the VS Code language services
 * (ADR 0042 stage A). Params arrive kernel-translated — physical `file://` URIs,
 * LSP 0-based positions — and results return verbatim except hover contents,
 * which normalize to MarkupContent, and Problems, which filter to the
 * workspace. Only the fixed table dispatches; arbitrary command names never
 * reach executeCommand. */
const ideFolders = new IdeFolderRegistry();

/** Maps the editor's FileSystemError family onto the typed fs codes; the
 * physical target URI is stripped before any of this crosses to the model.
 * The mapping itself is the pure stage-F superset both fs families share. */
function ideFsError(error: unknown): Error {
  return localFsError(error);
}

/** Executes the maieutics/fs methods against vscode.workspace.fs (ADR 0042
 * decision 11): the root list answers the folder registry; folder enumeration
 * and reads resolve the id, validate every relative segment (defense in
 * depth — Uri.joinPath normalizes dot segments), and serve through
 * workspace.fs so every FileSystemProvider scheme works identically. */
async function executeIdeFs(
  method: string,
  params: Record<string, unknown>,
  registry: IdeFolderRegistry,
): Promise<unknown> {
  const segments = Array.isArray(params.segments)
    ? (params.segments as unknown[]).filter((s): s is string => typeof s === "string")
    : [];
  for (const segment of segments) {
    if (!isValidIdeFsSegment(segment)) {
      throw Object.assign(new Error("The path segment is not a valid workspace-path segment."), {
        code: "ide_invalid_arguments",
      });
    }
  }

  const folderId = typeof params.folderId === "string" ? params.folderId : undefined;
  if (method === "maieutics/fs/list" && folderId === undefined) {
    const roots = (vscode.workspace.workspaceFolders ?? []).map((folder) => ({
      id: registry.register(folder.uri.toString()),
      uri: folder.uri.toString(),
    }));
    return fsRootsAnswer(roots);
  }

  if (folderId === undefined) {
    throw Object.assign(
      new Error("The uri must address a file inside a folder."),
      { code: "ide_invalid_arguments" },
    );
  }

  const folderUri = registry.resolve(folderId);
  if (folderUri === undefined) {
    throw Object.assign(
      new Error("The folder id is not registered on this activation; re-list the roots."),
      { code: "fs_no_folder" },
    );
  }

  const base = vscode.Uri.parse(folderUri, true);
  const target = segments.length === 0 ? base : vscode.Uri.joinPath(base, ...segments);

  if (method === "maieutics/fs/search") {
    return await executeIdeFsSearch(target, params);
  }

  if (method === "maieutics/fs/list") {
    let listed: [string, vscode.FileType][];
    try {
      listed = await vscode.workspace.fs.readDirectory(target);
    } catch (error) {
      throw ideFsError(error);
    }
    return fsEntriesAnswer(
      listed.map(([name, fileType]) => ({
        name,
        isDirectory: fileType === vscode.FileType.Directory,
      })),
      typeof params.cursor === "string" ? params.cursor : undefined,
    );
  }

  // Editor semantics (decision 12 applied to the virtual family): a dirty open
  // document answers its buffer — unsaved edits on a remote or extension-scheme
  // file are visible the same way the local family serves them. Clean or closed
  // files answer the provider bytes below; untitled:/virtual schemes can never
  // match the joined folder URI, which keeps them excluded.
  const virtualUri = `idefs:///${folderId}${segments.map((segment) => `/${segment}`).join("")}`;
  const buffer = dirtyBufferAnswer(
    vscode.workspace.textDocuments.map((document) => ({
      uriString: document.uri.toString(),
      isDirty: document.isDirty,
      text: document.getText(),
    })),
    target.toString(),
  );
  if (buffer !== undefined) {
    if (new TextEncoder().encode(buffer).byteLength > 1024 * 1024) {
      throw Object.assign(
        new Error("The read is bounded at 1 MiB of text; split the file or window the source."),
        { code: "fs_too_large" },
      );
    }
    return { uri: virtualUri, content: buffer };
  }

  let bytes: Uint8Array;
  try {
    bytes = await vscode.workspace.fs.readFile(target);
  } catch (error) {
    throw ideFsError(error);
  }
  if (bytes.byteLength > 1024 * 1024) {
    throw Object.assign(
      new Error("The read is bounded at 1 MiB of text; split the file or window the source."),
      { code: "fs_too_large" },
    );
  }

  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    throw Object.assign(new Error("The file is not valid UTF-8 text."), { code: "fs_not_text" });
  }

  return {
    uri: virtualUri,
    content: text,
  };
}

/** The bridge's read of one file's current state (ADR 0042 decision 12):
 * a dirty open document answers its buffer; otherwise the file's bytes,
 * fatal-decoded. Returns undefined only for a missing file, so the write
 * precondition's `fs_conflict` and the exclusive create's `fs_exists` stay
 * distinguishable. */
async function readLocalState(
  target: vscode.Uri,
): Promise<string | undefined> {
  const open = vscode.workspace.textDocuments.find(
    (document) => document.uri.toString() === target.toString(),
  );
  if (open !== undefined && open.isDirty) return open.getText();
  let bytes: Uint8Array;
  try {
    bytes = await vscode.workspace.fs.readFile(target);
  } catch (error) {
    if ((error as { code?: unknown }).code === "FileNotFound") return undefined;
    throw localFsError(error);
  }
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    // A binary target has no text state to hash or answer; callers that read
    // for the model already failed typed in their own handler.
    return undefined;
  }
}

/** Adapts vscode.workspace.fs onto the pure scan's fs surface: paths are
 * canonical Uri strings, so the scan stays scheme-agnostic and the pure half
 * never imports vscode. */
function workspaceSearchTreeFs(): SearchTreeFs {
  return {
    list: async (path) =>
      await vscode.workspace.fs.readDirectory(vscode.Uri.parse(path, true)).then((entries) =>
        entries.map(([name, fileType]) => [name, fileType as number])
      ),
    read: async (path) => await vscode.workspace.fs.readFile(vscode.Uri.parse(path, true)),
    join: (base, segments) =>
      vscode.Uri.joinPath(vscode.Uri.parse(base, true), ...segments).toString(),
  };
}

/** The search answer shape both executors return. */
interface IdeFsSearchAnswer {
  matches: Array<{ path: string; line: number; column: number; preview: string }>;
  truncated: boolean;
  scannedBytes: number;
}

/** The filter knobs parsed from any search request. */
interface IdeFsSearchFilter {
  include: string[] | undefined;
  exclude: string[] | undefined;
  respectIgnore: boolean;
}

function parseSearchFilter(params: Record<string, unknown>): IdeFsSearchFilter {
  const read = (name: string): string[] | undefined => {
    if (!Array.isArray(params[name])) return undefined;
    const values = (params[name] as unknown[]).filter((v): v is string => typeof v === "string")
      .filter((v) => v.length > 0 && v.length <= 128 && !v.includes("\u0000"));
    return values.length === 0 ? undefined : values.slice(0, 16);
  };
  return {
    include: read("include"),
    exclude: read("exclude"),
    respectIgnore: params.respectIgnore !== false,
  };
}

/** Ripgrep resolution: the install's bundled rg first (it always exists on
 * desktop builds and ships with the remote server), then the PATH. Cached;
 * null means the grep and provider fallbacks take over. */
let rgResolution: Promise<string | null> | undefined;

function probeRgPath(candidate: string): Promise<string | null> {
  return new Promise((resolve) => {
    let child: ChildProcess;
    try {
      child = spawn(candidate, ["--version"], { stdio: ["ignore", "pipe", "ignore"] });
    } catch {
      resolve(null);
      return;
    }
    child.on("error", () => resolve(null));
    child.on("close", (code) => resolve(code === 0 ? candidate : null));
  });
}

async function resolveRg(): Promise<string | null> {
  rgResolution ??= (async () => {
    const bundled = await probeRgPath(bundledRgPath(vscode.env.appRoot));
    if (bundled !== null) return bundled;
    return await probeRgPath("rg");
  })();
  return await rgResolution;
}

/** Runs ripgrep with the kernel's match bound: stdout parses incrementally and
 * the child is killed once the ceiling is reached — the cancellation and the
 * bound are the same mechanism. Returns null when rg could not run at all. */
function rgSearch(
  rgPath: string,
  root: vscode.Uri,
  query: string,
  regex: boolean,
  caseSensitive: boolean,
  filter: IdeFsSearchFilter,
  maximumMatches: number,
): Promise<IdeFsSearchAnswer | null> {
  return new Promise((resolve) => {
    let child: ChildProcess;
    try {
      child = spawn(
        rgPath,
        buildRgArgs(query, regex, caseSensitive, filter),
        { cwd: root.fsPath, stdio: ["ignore", "pipe", "pipe"] },
      );
    } catch {
      resolve(null);
      return;
    }

    const matches: IdeFsSearchAnswer["matches"] = [];
    let truncated = false;
    let settled = false;
    const finish = (result: IdeFsSearchAnswer | null): void => {
      if (settled) return;
      settled = true;
      if (result === null) child.kill();
      resolve(result);
    };

    // The wall clock is the ReDoS/runaway backstop; a partial answer beats a
    // hang, and the truncation flag makes the cut visible to the model.
    const budget = setTimeout(() => {
      truncated = true;
      child.kill();
      finish({ matches, truncated, scannedBytes: 0 });
    }, 20_000);

    let buffer = "";
    child.stdout?.on("data", (chunk: Buffer) => {
      buffer += chunk.toString("utf-8");
      let newline = buffer.indexOf("\n");
      while (newline >= 0) {
        const eventLine = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        const match = parseRgMatchEvent(eventLine);
        if (match !== null && passesSearchFilters(match.path, filter)) matches.push(match);
        if (matches.length >= maximumMatches) {
          clearTimeout(budget);
          child.kill();
          finish({ matches, truncated: true, scannedBytes: 0 });
          return;
        }
        newline = buffer.indexOf("\n");
      }
    });
    child.on("error", () => {
      clearTimeout(budget);
      finish(null);
    });
    child.on("close", (code) => {
      if (settled) return;
      clearTimeout(budget);
      if (code === 0 || code === 1) {
        finish({ matches, truncated, scannedBytes: 0 });
        return;
      }

      // Exit 2 with parsed matches still answers them (truncated); a total
      // failure — the kernel pre-validates patterns, so this is environmental —
      // falls back to the slower executors.
      finish(matches.length > 0 ? { matches, truncated: true, scannedBytes: 0 } : null);
    });
  });
}

/** The universal grep fallback: line numbers and previews, every column a 1
 * (grep has no per-match column), `--null` separating the path so paths with
 * colons still parse. Degraded but present on every machine. */
function grepSearch(
  root: vscode.Uri,
  query: string,
  caseSensitive: boolean,
  filter: IdeFsSearchFilter,
  maximumMatches: number,
): Promise<IdeFsSearchAnswer | null> {
  return new Promise((resolve) => {
    const args = [
      "-r",
      "-n",
      "-I",
      "--binary-files=without-match",
      "--null",
      ...(caseSensitive ? [] : ["-i"]),
      "-F",
      "-e",
      query,
      ".",
    ];
    let child: ChildProcess;
    try {
      child = spawn("grep", args, { cwd: root.fsPath, stdio: ["ignore", "pipe", "pipe"] });
    } catch {
      resolve(null);
      return;
    }

    const matches: IdeFsSearchAnswer["matches"] = [];
    let truncated = false;
    let settled = false;
    const finish = (result: IdeFsSearchAnswer | null): void => {
      if (settled) return;
      settled = true;
      if (result === null) child.kill();
      resolve(result);
    };
    const budget = setTimeout(() => {
      truncated = true;
      child.kill();
      finish({ matches, truncated, scannedBytes: 0 });
    }, 20_000);

    let buffer = "";
    child.stdout?.on("data", (chunk: Buffer) => {
      buffer += chunk.toString("utf-8");
      let newline = buffer.indexOf("\n");
      while (newline >= 0) {
        const record = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        const nul = record.indexOf("\u0000");
        const colon = nul >= 0 ? record.indexOf(":", nul) : record.indexOf(":");
        if (nul < 0 || colon < 0) {
          newline = buffer.indexOf("\n");
          continue;
        }
        const path = record.slice(0, nul).replace(/^\.\//, "").replace(/\\/g, "/");
        const line = Number(record.slice(nul + 1, colon));
        const text = record.slice(colon + 1);
        if (Number.isFinite(line) && passesSearchFilters(path, filter)) {
          matches.push({ path, line, column: 1, preview: text.slice(0, 512) });
        }
        if (matches.length >= maximumMatches) {
          clearTimeout(budget);
          child.kill();
          finish({ matches, truncated: true, scannedBytes: 0 });
          return;
        }
        newline = buffer.indexOf("\n");
      }
    });
    child.on("error", () => {
      clearTimeout(budget);
      finish(null);
    });
    child.on("close", () => {
      if (settled) return;
      clearTimeout(budget);
      finish({ matches, truncated, scannedBytes: 0 });
    });
  });
}

/** Runs the search for both fs families: ripgrep for local-disk roots (the
 * install's bundled rg — fast, ignore-aware), grep as the universal fallback,
 * and the bounded provider scan for every other scheme and as the last
 * resort. `maximumMatches` is the kernel's own 1-200 bound. */
async function executeIdeFsSearch(
  root: vscode.Uri,
  params: Record<string, unknown>,
): Promise<unknown> {
  const query = typeof params.query === "string" ? params.query : undefined;
  if (query === undefined || query.length === 0) {
    throw Object.assign(new Error("The search requires a non-empty query."), {
      code: "ide_invalid_arguments",
    });
  }
  const regex = params.regex === true;
  const caseSensitive = params.caseSensitive !== false;
  const maximumMatches =
    typeof params.maximumMatches === "number" && Number.isFinite(params.maximumMatches)
      ? Math.max(1, Math.min(200, Math.floor(params.maximumMatches)))
      : 200;
  const filter = parseSearchFilter(params);

  if (root.scheme === "file") {
    const rgPath = await resolveRg();
    if (rgPath !== null) {
      const fast = await rgSearch(
        rgPath,
        root,
        query,
        regex,
        caseSensitive,
        filter,
        maximumMatches,
      );
      if (fast !== null) return fast;
    }
    const grep = await grepSearch(root, query, caseSensitive, filter, maximumMatches);
    if (grep !== null) return grep;
  }

  const result = await searchTree(
    workspaceSearchTreeFs(),
    root.toString(),
    defaultTreeSearchBounds(query, regex, caseSensitive, maximumMatches),
  );
  return {
    matches: result.matches.filter((match) => passesSearchFilters(match.path, filter)),
    truncated: result.truncated,
    scannedBytes: result.scannedBytes,
  };
}

/** Executes the maieutics/fs/local methods — the workspace repository file
 * system served through the IDE (ADR 0042 decision 12). Params carry
 * kernel-translated physical `file://` URIs (the stage-A translation
 * authority; no containment re-check here — the kernel owns the workspace
 * domain). Reads answer the IDE's text state (dirty buffers included);
 * writes precondition on the hash of the state a read would answer, so a
 * concurrent IDE-side change fails `fs_conflict` instead of silently
 * winning, and land through workspace.fs so watchers update open editors. */
async function executeIdeFsLocal(
  method: string,
  params: Record<string, unknown>,
): Promise<unknown> {
  const rawUri = typeof params.uri === "string" ? params.uri : undefined;
  if (rawUri === undefined || !rawUri.startsWith("file:")) {
    throw Object.assign(
      new Error("The local fs family takes a kernel-translated file:// uri."),
      { code: "ide_invalid_arguments" },
    );
  }
  const target = vscode.Uri.parse(rawUri, true);
  // The coverage ruling (ADR 0042 decision 12): this window serves only what its
  // workspace folders cover — defense in depth behind the kernel's cached prefix
  // check, and the authority when folders changed since the kernel fetched them.
  if (
    !isCoveredLocalTarget(
      target.fsPath,
      (vscode.workspace.workspaceFolders ?? []).map((folder) => folder.uri.fsPath),
    )
  ) {
    throw Object.assign(new Error("the path is outside this frontend's workspace folders"), {
      code: "fs_not_covered",
    });
  }

  if (method === "maieutics/fs/local/search") {
    return await executeIdeFsSearch(target, params);
  }

  if (method === "maieutics/fs/local/read") {
    // Buffer-authoritative: the dirty buffer is the point of the bridge. A
    // clean or closed file answers a fatal-decoded byte read, which keeps
    // binary files typed `fs_not_text` instead of surfacing replacement mush.
    const open = vscode.workspace.textDocuments.find(
      (document) => document.uri.toString() === target.toString(),
    );
    if (open !== undefined && open.isDirty) return { content: open.getText() };
    let bytes: Uint8Array;
    try {
      bytes = await vscode.workspace.fs.readFile(target);
    } catch (error) {
      throw localFsError(error);
    }
    if (bytes.byteLength > 8 * 1024 * 1024) {
      throw Object.assign(
        new Error("The read is bounded at 8 MiB of text."),
        { code: "fs_too_large" },
      );
    }
    try {
      return { content: new TextDecoder("utf-8", { fatal: true }).decode(bytes) };
    } catch {
      throw Object.assign(new Error("The file is not valid UTF-8 text."), { code: "fs_not_text" });
    }
  }

  if (method === "maieutics/fs/local/list") {
    const maximum =
      typeof params.maximumEntries === "number" && Number.isFinite(params.maximumEntries)
        ? Math.max(0, Math.floor(params.maximumEntries))
        : 10_000;
    let listed: [string, vscode.FileType][];
    try {
      listed = await vscode.workspace.fs.readDirectory(target);
    } catch (error) {
      throw localFsError(error);
    }
    return localFsEntriesAnswer(
      listed.map(([name, fileType]) => [name, fileType as number]),
      maximum,
    );
  }

  if (method === "maieutics/fs/local/write") {
    const content = typeof params.content === "string" ? params.content : undefined;
    if (content === undefined) {
      throw Object.assign(new Error("The write requires string content."), {
        code: "ide_invalid_arguments",
      });
    }
    const expectedHash = typeof params.expectedHash === "string" ? params.expectedHash : undefined;
    const createExclusive = params.createExclusive === true;
    const current = await readLocalState(target);
    if (expectedHash !== undefined) {
      if (current === undefined || (await sha256Hex(current)) !== expectedHash) {
        throw Object.assign(
          new Error("The file changed since the kernel read it; re-read and retry."),
          { code: "fs_conflict" },
        );
      }
    } else if (createExclusive && current !== undefined) {
      throw Object.assign(new Error("The workspace path already exists."), { code: "fs_exists" });
    }
    await vscode.workspace.fs.writeFile(target, new TextEncoder().encode(content));
    return {};
  }

  if (method === "maieutics/fs/local/delete") {
    try {
      await vscode.workspace.fs.delete(target, { recursive: false });
    } catch (error) {
      throw localFsError(error);
    }
    return {};
  }

  throw Object.assign(new Error(`The method '${method}' is not supported.`), {
    code: "ide_method_unsupported",
  });
}

// ---------- debug bridge (decision 13) ----------

/** The tracked debug sessions (decision 13): state transitions land here from
 * the debug events, and the kernel's state/wait tool reads or waits on them.
 * Sessions are visible in the debug UI by design — the user ruled the UI
 * impact acceptable for the bridge. */
const debugSessions = new Map<string, TrackedDebugSession>();
const debugWatchers = new Map<string, Array<(session: TrackedDebugSession) => void>>();
const debugObjects = new Map<string, vscode.DebugSession>();

function applyDebugTransition(
  sessionId: string,
  event: Parameters<typeof transitionDebugSession>[1],
): TrackedDebugSession {
  const next = transitionDebugSession(debugSessions.get(sessionId), event);
  debugSessions.set(sessionId, next);
  const watchers = debugWatchers.get(sessionId);
  if (watchers !== undefined && watchers.length > 0) {
    debugWatchers.set(
      sessionId,
      watchers.filter((watcher) => {
        watcher(next);
        return false;
      }),
    );
  }
  return next;
}

/** The debug event wiring runs once at module load: the listeners live for the
 * extension host's lifetime, matching the bridge's app-level role. */
vscode.debug.onDidStartDebugSession((session) => {
  debugObjects.set(session.id, session);
  applyDebugTransition(session.id, { kind: "start" });
});
vscode.debug.onDidReceiveDebugSessionCustomEvent((event) => {
  if (event.event !== "stopped") return;
  const body = (event.body ?? {}) as { reason?: unknown; threadId?: unknown };
  applyDebugTransition(event.session.id, {
    kind: "stopped",
    reason: typeof body.reason === "string" ? body.reason : undefined,
    threadId: typeof body.threadId === "number" ? body.threadId : undefined,
  });
});
vscode.debug.onDidTerminateDebugSession((session) => {
  applyDebugTransition(session.id, { kind: "terminated" });
});

/** Reads the first workspace folder's launch.json through workspace.fs.
 * A missing file is an empty list; anything unreadable is typed. */
async function readLaunchConfigurations(): Promise<LaunchConfiguration[]> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  if (folder === undefined) {
    throw Object.assign(new Error("No workspace folder is open."), {
      code: "ide_invalid_arguments",
    });
  }
  const uri = vscode.Uri.joinPath(folder.uri, ".vscode", "launch.json");
  let text: string;
  try {
    text = new TextDecoder().decode(await vscode.workspace.fs.readFile(uri));
  } catch (error) {
    if ((error as { code?: unknown }).code === "FileNotFound") return [];
    throw localFsError(error);
  }
  return parseLaunchConfigurations(text);
}

function debugStateAnswer(sessionId: string, session: TrackedDebugSession): unknown {
  return {
    sessionId,
    version: session.version,
    state: session.state,
    ...(session.reason === undefined ? {} : { reason: session.reason }),
    ...(session.threadId === undefined ? {} : { threadId: session.threadId }),
  };
}

/** Waits for the session's next state transition, bounded; the current state
 * answers immediately when it is not running (nothing to wait for). */
function waitForDebugState(sessionId: string, waitSeconds: number): Promise<TrackedDebugSession> {
  const current = debugSessions.get(sessionId);
  if (current === undefined) {
    throw Object.assign(new Error("debug_no_session: no such debug session is tracked."), {
      code: "debug_no_session",
    });
  }
  if (current.state !== "running" || waitSeconds <= 0) {
    return Promise.resolve(current);
  }
  return new Promise((resolve) => {
    const list = debugWatchers.get(sessionId) ?? [];
    const onTransition = (session: TrackedDebugSession): void => {
      clearTimeout(timer);
      resolve(session);
    };
    const timer = setTimeout(() => {
      debugWatchers.set(
        sessionId,
        (debugWatchers.get(sessionId) ?? []).filter((watcher) => watcher !== onTransition),
      );
      resolve(debugSessions.get(sessionId) ?? current);
    }, waitSeconds * 1000);
    list.push(onTransition);
    debugWatchers.set(sessionId, list);
  });
}

async function executeIdeDebug(
  method: string,
  params: Record<string, unknown>,
): Promise<unknown> {
  const fail = (code: string, message: string): Error =>
    Object.assign(new Error(message), { code });

  switch (method) {
    case "maieutics/debug/configurations": {
      const configs = await readLaunchConfigurations();
      return {
        configs: configs.map((config) => ({
          name: config.name,
          type: config.type,
          request: config.request,
        })),
      };
    }

    case "maieutics/debug/start": {
      const launchName = typeof params.launchName === "string" ? params.launchName : undefined;
      const inline = (params.configuration ?? undefined) as Record<string, unknown> | undefined;
      if ((launchName === undefined) === (inline === undefined)) {
        throw fail(
          "ide_invalid_arguments",
          "Provide exactly one of 'launchName' or 'configuration'.",
        );
      }
      let config: vscode.DebugConfiguration;
      if (launchName !== undefined) {
        const configs = await readLaunchConfigurations();
        const found = configs.find((entry) => entry.name === launchName);
        if (found === undefined) {
          throw fail(
            "debug_config_unknown",
            "No launch configuration named '" + launchName + "' (available: " +
              (configs.map((entry) => entry.name).join(", ") || "none") + ").",
          );
        }
        config = {
          type: found.type,
          request: found.request,
          name: found.name,
          ...found.configuration,
        };
      } else {
        if (
          typeof inline !== "object" || inline === null ||
          typeof inline.type !== "string" || typeof inline.request !== "string"
        ) {
          throw fail(
            "ide_invalid_arguments",
            "'configuration' must be a debug configuration object with string 'type' and 'request'.",
          );
        }
        config = { ...inline } as vscode.DebugConfiguration;
      }
      if (params.stopOnEntry === true) config.stopOnEntry = true;

      const folder = vscode.workspace.workspaceFolders?.[0];
      // The start event may carry the id of a session the user was already
      // debugging — only a session that is NEW since this call answers.
      const before = new Set(debugSessions.keys());
      const started = await vscode.debug.startDebugging(folder, config);
      if (!started) {
        throw fail("debug_start_failed", "The debug start was canceled or rejected.");
      }
      const fresh = [...debugSessions.keys()].find((id) => !before.has(id));
      const sessionId = fresh ??
        (await new Promise<string>((resolve, reject) => {
          const disposable = vscode.debug.onDidStartDebugSession((session) => {
            if (before.has(session.id)) return;
            clearTimeout(timer);
            disposable.dispose();
            resolve(session.id);
          });
          const timer = setTimeout(() => {
            disposable.dispose();
            reject(
              fail("debug_start_timeout", "The debug session did not start within 10 seconds."),
            );
          }, 10_000);
        }));
      return { sessionId, state: debugSessions.get(sessionId)?.state ?? "running" };
    }

    case "maieutics/debug/state": {
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      if (sessionId.length === 0) throw fail("ide_invalid_arguments", "sessionId is required.");
      const waitSeconds =
        typeof params.waitSeconds === "number" && Number.isFinite(params.waitSeconds)
          ? Math.max(0, Math.min(20, Math.floor(params.waitSeconds)))
          : 0;
      const session = await waitForDebugState(sessionId, waitSeconds);
      return debugStateAnswer(sessionId, session);
    }

    case "maieutics/debug/stack": {
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      const session = debugObjects.get(sessionId);
      if (session === undefined) throw fail("debug_no_session", "No such debug session is live.");
      const threadId = typeof params.threadId === "number" ? params.threadId : undefined;
      const stack = await session.customRequest("stackTrace", {
        ...(threadId === undefined ? {} : { threadId }),
      }) as { stackFrames?: Array<Record<string, unknown>> };
      return { stackFrames: stack.stackFrames ?? [] };
    }

    case "maieutics/debug/variables": {
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      const session = debugObjects.get(sessionId);
      if (session === undefined) throw fail("debug_no_session", "No such debug session is live.");
      const variablesReference = typeof params.variablesReference === "number"
        ? params.variablesReference
        : undefined;
      const frameId = typeof params.frameId === "number" ? params.frameId : undefined;
      if (variablesReference !== undefined) {
        const children = await session.customRequest("variables", { variablesReference }) as {
          variables?: Array<Record<string, unknown>>;
        };
        return { variables: children.variables ?? [] };
      }
      if (frameId === undefined) {
        throw fail(
          "ide_invalid_arguments",
          "Provide either 'frameId' (flattened scopes) or 'variablesReference' (direct children).",
        );
      }
      const scopes = await session.customRequest("scopes", { frameId }) as {
        scopes?: Array<{ name?: string; variablesReference?: number }>;
      };
      const named = await Promise.all((scopes.scopes ?? []).map(async (scope) => {
        if (typeof scope.variablesReference !== "number") {
          return { name: scope.name ?? "", variables: [] };
        }
        const children = await session.customRequest("variables", {
          variablesReference: scope.variablesReference,
        }) as { variables?: Array<Record<string, unknown>> };
        return { name: scope.name ?? "", variables: children.variables ?? [] };
      }));
      return { scopes: named };
    }

    case "maieutics/debug/evaluate": {
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      const session = debugObjects.get(sessionId);
      if (session === undefined) throw fail("debug_no_session", "No such debug session is live.");
      const expression = typeof params.expression === "string" ? params.expression : "";
      if (expression.length === 0) throw fail("ide_invalid_arguments", "expression is required.");
      const frameId = typeof params.frameId === "number" ? params.frameId : undefined;
      return await session.customRequest("evaluate", {
        expression,
        context: "repl",
        ...(frameId === undefined ? {} : { frameId }),
      });
    }

    case "maieutics/debug/breakpoints": {
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      const session = debugObjects.get(sessionId);
      if (session === undefined) throw fail("debug_no_session", "No such debug session is live.");
      const uriText = typeof params.uri === "string" ? params.uri : "";
      if (!uriText.startsWith("file:")) {
        throw fail("ide_invalid_arguments", "'uri' must be a file:// source path.");
      }
      const lines = Array.isArray(params.lines)
        ? (params.lines as unknown[]).filter((v): v is number => typeof v === "number" && v >= 1)
        : [];
      const result = await session.customRequest("setBreakpoints", {
        source: { path: vscode.Uri.parse(uriText, true) },
        breakpoints: lines.map((line) => ({ line })),
      }) as { breakpoints?: Array<Record<string, unknown>> };
      return { breakpoints: result.breakpoints ?? [] };
    }

    case "maieutics/debug/control": {
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      const action = typeof params.action === "string" ? params.action : "";
      const threadId = typeof params.threadId === "number" ? params.threadId : undefined;
      const session = debugObjects.get(sessionId);
      if (session === undefined) throw fail("debug_no_session", "No such debug session is live.");
      if (action === "terminate") {
        await vscode.debug.stopDebugging(session);
        applyDebugTransition(sessionId, { kind: "terminated" });
        return debugStateAnswer(sessionId, debugSessions.get(sessionId)!);
      }
      const thread = threadId ?? debugSessions.get(sessionId)?.threadId;
      switch (action) {
        case "continue":
          await session.customRequest("continue", { threadId: thread ?? 0 });
          break;
        case "stepOver":
          await session.customRequest("next", { threadId: thread ?? 0 });
          break;
        case "stepIn":
          await session.customRequest("stepIn", { threadId: thread ?? 0 });
          break;
        case "stepOut":
          await session.customRequest("stepOut", { threadId: thread ?? 0 });
          break;
        default:
          throw fail(
            "ide_invalid_arguments",
            "action must be one of: continue, stepOver, stepIn, stepOut, terminate.",
          );
      }
      applyDebugTransition(sessionId, { kind: "resumed" });
      return debugStateAnswer(sessionId, debugSessions.get(sessionId)!);
    }

    default:
      throw fail("ide_method_unsupported", "The method '" + method + "' is not supported.");
  }
}

export async function executeIdeMethod(
  method: string,
  params: Record<string, unknown>,
): Promise<unknown> {
  if (method.startsWith("maieutics/debug/")) {
    return await executeIdeDebug(method, params);
  }
  if (method === "maieutics/problems") {
    const wanted = typeof params.uri === "string" ? vscode.Uri.parse(params.uri) : undefined;
    const roots = (vscode.workspace.workspaceFolders ?? []).map((folder) => folder.uri.fsPath);
    const entries = wanted === undefined
      ? vscode.languages.getDiagnostics() as [
        uri: { fsPath?: string; toString(): string },
        diagnostics: unknown,
      ][]
      : [[wanted, vscode.languages.getDiagnostics(wanted)] as [
        uri: { fsPath?: string; toString(): string },
        diagnostics: unknown,
      ]];
    return collectProblems(entries, wanted?.toString(), roots);
  }

  if (method === "maieutics/dirty") {
    const wanted = Array.isArray(params.uris)
      ? (params.uris as unknown[]).filter((u): u is string => typeof u === "string")
      : [];
    const documents = vscode.workspace.textDocuments.map((document) => ({
      fsPath: document.uri.fsPath,
      isDirty: document.isDirty,
    }));
    return { entries: collectDirty(documents, wanted, (uri) => vscode.Uri.parse(uri).fsPath) };
  }

  if (method === "maieutics/fs/list" || method === "maieutics/fs/read") {
    return await executeIdeFs(method, params, ideFolders);
  }

  if (method.startsWith("maieutics/fs/local/")) {
    return await executeIdeFsLocal(method, params);
  }

  const command = ideMethodCommands[method];
  const args = ideCommandArgs(method, params).map(materializeIdeArg);
  const result = await vscode.commands.executeCommand(command, ...args);
  if (method === "textDocument/hover") {
    // The provider command answers Hover[] (one entry per registered
    // provider); the wire answer is the documented single {contents, range}
    // shape with MarkupContent contents (ADR 0042 decision 4) — a raw SDK
    // hover never crosses the bridge. When no provider answered with a
    // payload yet, the raw result passes through so polling callers keep
    // seeing "not yet".
    const answer = normalizedHoverAnswer(result);
    if (answer !== undefined) return answer;
  }

  // Write group (stage C): compute-only — the answers flatten to plain data
  // and the kernel's gated write path does any applying.
  if (method === "textDocument/rename") {
    if (typeof result !== "object" || result === null || !("entries" in result)) {
      return { changes: {} };
    }
    return workspaceEditToWire(result as { entries(): readonly (readonly [unknown, unknown])[] });
  }
  if (method === "textDocument/codeAction") {
    return codeActionsToWire(Array.isArray(result) ? result : []);
  }
  if (method === "textDocument/formatting" && Array.isArray(result)) {
    return textEditsToWire(result);
  }

  return result;
}

/** Converts URI strings and URI-bearing hierarchy items into the vscode objects
 * the built-in provider commands accept; everything else passes through. */
function materializeIdeArg(value: unknown): unknown {
  if (typeof value === "string") {
    return value.startsWith("file:") ? vscode.Uri.parse(value) : value;
  }
  if (Array.isArray(value)) return value.map(materializeIdeArg);
  if (typeof value === "object" && value !== null) {
    const record = value as Record<string, unknown>;
    if (typeof record.uri === "string") {
      return { ...record, uri: vscode.Uri.parse(record.uri) };
    }
  }
  return value;
}

/** Folds frames for one run and paints the cell output with throttling. */
class RunExecution {
  private readonly view: TurnView;
  private lastPaint = 0;
  private paintTimer: ReturnType<typeof setTimeout> | null = null;
  private settled = false;
  /** Stable output handles keyed by segment id: created once, updated in place. */
  private readonly segments = new Map<string, vscode.NotebookCellOutput>();
  /** True once the first answer paint ran (the answer output object exists). */
  private answerCreated = false;

  constructor(
    runId: string,
    private readonly execution: vscode.NotebookCellExecution,
    private readonly input: string,
    private readonly client: FrontendClient,
    private readonly resolve: (committed: boolean) => void,
  ) {
    this.view = new TurnView(runId);
  }

  /** Routes one input request to a VS Code surface and posts the answer back.
   * A plain REPL prompt stays an input box (dismissing posts an empty value).
   * An MCP elicitation carrying a form schema (ADR 0029) becomes a choice —
   * fill the form field by field, or refuse with decline/cancel, which the
   * plain input box could never express. */
  private answerInputRequest(frame: EventFrame): void {
    const requestId = frame.requestId;
    if (requestId === undefined) return;

    const extras = frame as { schema?: unknown; serverId?: unknown };
    const plan = planElicitation(
      frame.prompt ?? "Input requested",
      extras.schema,
      typeof extras.serverId === "string" ? extras.serverId : undefined,
    );

    void (async () => {
      if (plan === null) {
        const value = await vscode.window.showInputBox({
          prompt: frame.prompt ?? "REPL input",
          password: frame.password === true,
          ignoreFocusOut: true,
        });
        await this.client.submitInput(requestId, value ?? "");
        return;
      }

      const origin = plan.serverId === undefined ? "" : ` (server ${plan.serverId})`;
      const choice = await vscode.window.showQuickPick(
        [
          { label: "$(edit) Fill in form", description: "Answer every field", action: "accept" },
          {
            label: "$(circle-slash) Decline",
            description: "Refuse this request",
            action: "decline",
          },
          { label: "$(close) Cancel", description: "Cancel the request", action: "cancel" },
        ] as const,
        {
          title: `${plan.prompt}${origin}`,
          placeHolder: "This MCP server asked for structured input",
          ignoreFocusOut: true,
        },
      );
      if (choice === undefined) {
        await this.client.submitInput(requestId, "", { action: "cancel" });
        return;
      }

      if (choice.action === "decline" || choice.action === "cancel") {
        await this.client.submitInput(requestId, "", { action: choice.action });
        return;
      }

      const values: Record<string, unknown> = {};
      for (const field of plan.fields) {
        const raw = await this.promptField(plan, field);
        if (raw === null) {
          // The user abandoned the form midway: cancel rather than submit a
          // partial object the server would treat as complete.
          await this.client.submitInput(requestId, "", { action: "cancel" });
          return;
        }

        const coerced = coerceField(field, raw);
        if (!coerced.ok) {
          vscode.window.showWarningMessage(
            `Maieutics: ${coerced.error} The request was cancelled.`,
          );
          await this.client.submitInput(requestId, "", { action: "cancel" });
          return;
        }

        if (coerced.value !== undefined) values[field.name] = coerced.value;
      }

      await this.client.submitInput(requestId, values, { action: "accept" });
    })().catch((error: unknown) => {
      this.execution.replaceOutput([
        errorOutput(new FrontendError("input_failed", 0, String(error))),
      ]);
    });
  }

  /** Prompts one form field; null when the user abandoned the form. Enum
   * fields and booleans are picks; everything else an input box. */
  private async promptField(
    plan: { prompt: string; serverId?: string },
    field: {
      name: string;
      title: string;
      description?: string;
      type: "string" | "number" | "integer" | "boolean";
      required: boolean;
      defaultValue?: string;
      enumValues?: readonly string[];
    },
  ): Promise<string | null> {
    const title = `${plan.prompt} — ${field.title}${field.required ? "" : " (optional)"}`;
    if (field.enumValues !== undefined) {
      const picked = await vscode.window.showQuickPick(
        field.enumValues.map((value) => ({
          label: value,
          description: value === field.defaultValue ? "default" : undefined,
        })),
        { title, placeHolder: field.description, ignoreFocusOut: true },
      );
      return picked?.label ?? null;
    }

    if (field.type === "boolean") {
      const picked = await vscode.window.showQuickPick(
        [
          { label: "$(check) Yes", value: "true" },
          { label: "$(circle-slash) No", value: "false" },
        ],
        { title, placeHolder: field.description, ignoreFocusOut: true },
      );
      return picked?.value ?? null;
    }

    const value = await vscode.window.showInputBox({
      title,
      prompt: field.description,
      value: field.defaultValue,
      ignoreFocusOut: true,
    });
    return value ?? null;
  }

  /** Paints the placeholder output so the cell shows progress before frames arrive. */
  begin(): void {
    this.paintAnswer();
  }

  apply(frame: EventFrame): void {
    if (this.settled) return;
    if (frame.type === "input.request") {
      void this.answerInputRequest(frame);
      return;
    }
    // An `ide.request` payload rides tool.progress (ADR 0042 stage A): answer it
    // and never paint the raw JSON progress item into the cell.
    if (frame.type === "tool.progress") {
      const request = extractIdeRequest(frame);
      if (request !== undefined) {
        void this.answerIdeRequest(request);
        return;
      }
    }
    if (!this.view.apply(frame)) return;
    if (this.view.isTerminal) {
      if (this.paintTimer !== null) {
        clearTimeout(this.paintTimer);
        this.paintTimer = null;
      }
      this.paintFinal();
      this.settle(this.view.terminalState?.kind === "completed");
      return;
    }

    this.paint();
  }

  /** Folds one subagent child frame into this run's cell timeline (ADR 0030).
   * Child frames only ever render inside the parent's subagent segment: they
   * never answer input requests, never create cells or executions, and a
   * child's terminal frame must not take the apply() terminal branch that
   * settles the PARENT run and ends its cell execution. */
  applyChild(frame: EventFrame): void {
    if (this.settled) return;
    // A child run may have been narrowed onto the ide tools; its requests
    // answer exactly like a parent's (and paint nothing).
    if (frame.type === "tool.progress") {
      const request = extractIdeRequest(frame);
      if (request !== undefined) {
        void this.answerIdeRequest(request);
        return;
      }
    }
    if (!this.view.applySubagent(frame)) return;
    this.paint();
  }

  /** Answers one `ide.request` payload (ADR 0042 stage A): dispatches through
   * the fixed read catalog, posts `{value}` or a typed `{error}`, and absorbs
   * the 404 a replayed or already-settled request draws — replay noise is not
   * an error surface. */
  private async answerIdeRequest(request: IdeRequestPayload): Promise<void> {
    let answer: { value?: unknown; error?: { code: string; message: string } };
    try {
      if (!isIdeMethodSupported(request.method)) {
        answer = {
          error: {
            code: "ide_method_unsupported",
            message: `The method '${request.method}' is not in the IDE read catalog.`,
          },
        };
      } else {
        answer = { value: await executeIdeMethod(request.method, request.params) };
      }
    } catch (error) {
      answer = { error: { code: "ide_provider_error", message: String(error) } };
    }

    await this.client.submitIdeAnswer(request.requestId, answer).catch(() => {});
  }

  fail(code: string, message: string): void {
    if (this.settled) return;
    this.paintFailure(code, message);
    this.settle(false);
  }

  /** Renders a terminal failure, preserving what already streamed: mainstream
   * agents keep the partial answer and append the failure instead of wiping
   * the cell. Only a cell with nothing to show collapses to the error alone. */
  private paintFailure(code: string, message: string): void {
    if (this.paintTimer !== null) {
      clearTimeout(this.paintTimer);
      this.paintTimer = null;
    }

    const streamed = this.view.hasStreamedContent;
    const error = failureOutputWithRetry(new FrontendError(code, 0, message), this.execution.cell);
    if (streamed) {
      this.paintChanged();
      this.execution.appendOutput([error]);
    } else {
      this.execution.replaceOutput([error]);
    }

    this.execution.end(false, Date.now());
    notifyBackgroundSettled(this.execution.cell, "failed");
  }

  private settle(committed: boolean): void {
    this.settled = true;
    if (committed) notifyBackgroundSettled(this.execution.cell, "completed");
    this.resolve(committed);
  }

  /** Repaints changed segments, throttled while the run streams. */
  private paint(): void {
    if (this.paintTimer !== null) return;
    const elapsed = Date.now() - this.lastPaint;
    if (elapsed >= PaintIntervalMs) {
      this.lastPaint = Date.now();
      this.paintChanged();
      return;
    }

    this.paintTimer = setTimeout(() => {
      this.paintTimer = null;
      this.lastPaint = Date.now();
      this.paintChanged();
    }, PaintIntervalMs - elapsed);
  }

  /** Creates or updates only the segments the view marked dirty since the last
   * paint. Segment outputs are stable objects: a REPL display is created once
   * and its items replaced in place on updateDisplay; the tools timeline and
   * the answer markdown are the only other mutable segments. */
  private paintChanged(): void {
    const dirty = this.view.takeDirty();
    if (dirty.size === 0) return;

    for (const entry of this.view.replList()) {
      const key = this.view.replSegmentId(entry);
      if (!dirty.has(key)) continue;
      const output = replOutputSync(entry, this.client);
      const hadObjectRefs = Object.values(entry.data).some((value) =>
        typeof value === "object" && value !== null &&
        "$object" in (value as Record<string, unknown>)
      );
      // The async fill must target the ATTACHED segment output: a fresh
      // NotebookCellOutput that ensureSegment did not keep is an orphan, and
      // replacing its items paints nothing (the binary items would be lost).
      const attached = this.ensureSegment(key, output);
      if (hadObjectRefs && attached !== undefined) {
        void fillReplObjectItemsAsync(this.execution, attached, output.items).catch(() => {});
      }
    }

    if (dirty.has(`tools:${this.view.runId}`)) {
      const lines = this.view.toolLines();
      const markdown = lines.length > 0 ? lines.join("\n") : "";
      this.ensureSegment(
        `tools:${this.view.runId}`,
        markdown
          ? new vscode.NotebookCellOutput([
            vscode.NotebookCellOutputItem.text(markdown, "text/markdown"),
          ])
          : undefined,
      );
    }

    if (dirty.has(`subagents:${this.view.runId}`)) {
      const lines = this.view.subagentLines();
      const markdown = lines.length > 0 ? lines.join("\n") : "";
      this.ensureSegment(
        `subagents:${this.view.runId}`,
        markdown
          ? new vscode.NotebookCellOutput([
            vscode.NotebookCellOutputItem.text(markdown, "text/markdown"),
          ])
          : undefined,
      );
    }

    if (dirty.has(`answer:${this.view.runId}`)) this.paintAnswer();
  }

  /** Creates the segment output when absent (appended after the existing
   * outputs so segment order is REPL displays, tools, answer) or replaces its
   * items in place. Passing no items removes the segment (empty tools).
   * Returns the output that is attached to the cell after the call — the
   * stable segment when one already existed — or undefined when the segment
   * was removed. */
  private ensureSegment(
    key: string,
    output: vscode.NotebookCellOutput | undefined,
  ): vscode.NotebookCellOutput | undefined {
    const existing = this.segments.get(key);
    if (output === undefined) {
      if (existing) {
        this.execution.replaceOutputItems([], existing);
      }

      return undefined;
    }

    if (existing) {
      this.execution.replaceOutputItems(output.items, existing);
      return existing;
    }

    this.segments.set(key, output);
    this.execution.appendOutput([output]);
    return output;
  }

  /** Paints (or repaints) the answer markdown; creates its output once. */
  private paintAnswer(): void {
    const text = this.view.markdown();
    const markdown = text.length > 0 ? text : "…";
    const items = [vscode.NotebookCellOutputItem.text(markdown, "text/markdown")];
    if (!this.answerCreated) {
      this.answerCreated = true;
      const output = new vscode.NotebookCellOutput(items);
      this.segments.set(`answer:${this.view.runId}`, output);
      this.execution.appendOutput([output]);
      return;
    }

    const output = this.segments.get(`answer:${this.view.runId}`);
    if (output) this.execution.replaceOutputItems(items, output);
  }

  /** Freezes the segments: final tool statuses, and the streamed markdown
   * answer output REPLACED by the structured turn snapshot (carrying the
   * runId + input binding) — the timeline renderer takes the output over as
   * the default view. */
  private paintFinal(): void {
    const terminal = this.view.terminalState;
    const final = this.view.finalOutput(this.input);

    if (terminal?.kind === "failed") {
      this.paintFailure(terminal.code, terminal.message);
      return;
    }

    // The freeze REPLACES every streamed segment with the single structured
    // turn+json item: its default renderer view covers tools, subagents, and
    // the answer (with the overflow hint carried through), so keeping the
    // streamed markdown siblings would render those timelines twice. The
    // renderer provides the markdown fallback view for the frozen snapshot.
    this.paintAnswer();
    const answerOutput = this.segments.get(`answer:${this.view.runId}`);
    if (answerOutput) {
      this.execution.replaceOutputItems(
        turnOutputItems(final).map(outputItemFromSpec),
        answerOutput,
      );
    }

    for (const key of [`tools:${this.view.runId}`, `subagents:${this.view.runId}`]) {
      const stale = this.segments.get(key);
      if (stale !== undefined) {
        this.execution.replaceOutputItems([], stale);
        this.segments.delete(key);
      }
    }

    this.execution.end(true, Date.now());
  }
}

export function commandOutput(markdown: string): vscode.NotebookCellOutput {
  return structuredOutput({ text: markdown, tools: [], truncated: false });
}

export function finalOutput(final: {
  text: string;
  tools: ToolSnapshotView[];
  truncated: boolean;
  repl?: ReplDisplayEntry[];
  subagents?: SubagentSnapshotView[];
  error?: { code: string; message: string };
}): vscode.NotebookCellOutput {
  return structuredOutput(final);
}

/** Builds the synchronous items for one REPL display; object-reference items
 * fill asynchronously via drainPendingObjectItems. */
function replOutputSync(
  entry: ReplDisplayEntry,
  client: FrontendClient,
): vscode.NotebookCellOutput {
  const items = bundleItems(entry.data, (sha) => client.fetchObject(sha));
  return new vscode.NotebookCellOutput(items);
}

/** Awaits the async binary fills and replaces the attached segment's items in
 * place with the complete list: binary items first, then the synchronous
 * items the paint just produced. */
async function fillReplObjectItemsAsync(
  execution: vscode.NotebookCellExecution,
  output: vscode.NotebookCellOutput,
  items: readonly vscode.NotebookCellOutputItem[],
): Promise<void> {
  const pending = await drainPendingObjectItems();
  if (pending.length === 0) return;

  execution.replaceOutputItems([...pending, ...items], output);
}

/** A run failure's error output with an in-output Retry affordance: the
 * failed cell stayed pending, so re-running it re-submits the same turn. */
function failureOutputWithRetry(
  error: unknown,
  cell: vscode.NotebookCell,
): vscode.NotebookCellOutput {
  const code = error instanceof FrontendError ? error.code : "turn_failed";
  const message = error instanceof Error ? error.message : String(error);
  const args = encodeURIComponent(
    JSON.stringify([cell.document.uri.toString(), cell.index]),
  );
  const markdown =
    `> \u274c \`${code}\` \u2014 ${message}\n\n[\u21bb Retry turn](command:maieutics.retryTurn?${args})`;
  return structuredOutput({
    text: markdown,
    tools: [],
    truncated: false,
    error: { code, message },
  });
}

/** Notifies when a run settles in a notebook no editor is showing: background
 * runs used to be silent (the agent-ux gap list). */
function notifyBackgroundSettled(cell: vscode.NotebookCell, outcome: "completed" | "failed"): void {
  const visible = vscode.window.visibleNotebookEditors.some(
    (editor) => editor.notebook === cell.notebook,
  );
  if (visible) return;

  const label = cell.notebook.cellCount > 0
    ? cell.document.uri.path.split("/").pop() ?? "notebook"
    : "notebook";
  const detail = outcome === "completed"
    ? `Maieutics: turn finished in ${label}.`
    : `Maieutics: turn FAILED in ${label}.`;
  void vscode.window.showInformationMessage(detail, "Open").then((picked) => {
    if (picked === "Open") void vscode.window.showNotebookDocument(cell.notebook);
  });
}

function errorOutput(error: unknown): vscode.NotebookCellOutput {
  const code = error instanceof FrontendError ? error.code : "turn_failed";
  const message = error instanceof Error ? error.message : String(error);
  const markdown = `> ❌ \`${code}\` — ${message}`;
  return structuredOutput({
    text: markdown,
    tools: [],
    truncated: false,
    error: { code, message },
  });
}

/** A structured turn output carries exactly the single turn+json item
 * (turnOutputItems): the timeline renderer is the default view, and the
 * markdown fallback lives inside that renderer as a per-output view toggle. */
function structuredOutput(snapshot: {
  text: string;
  tools: ToolSnapshotView[];
  truncated: boolean;
  repl?: ReplDisplayEntry[];
  subagents?: SubagentSnapshotView[];
  error?: { code: string; message: string };
}): vscode.NotebookCellOutput {
  return new vscode.NotebookCellOutput(turnOutputItems(snapshot).map(outputItemFromSpec));
}
