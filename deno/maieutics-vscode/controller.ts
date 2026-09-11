/**
 * The notebook controller: one ordinary cell is one submitted Agent turn
 * (invariant 2). Per notebook, cells execute strictly in submission order and
 * one at a time — the session's single-run gate is the only serialization
 * point, so a second concurrent turn surfaces as the typed busy error instead
 * of a protocol queue.
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
 */

import * as vscode from "vscode";
import type { FrontendClient, SubmitAnswer } from "./client.ts";
import type { EventFrame, SessionInfo } from "./protocol.ts";
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
  bundleItems,
  drainPendingObjectItems,
  NotebookType,
  readStoredSessionId,
  StoredSessionMetadataKey,
} from "./serializer.ts";
import {
  type ReplDisplayEntry,
  type ToolSnapshotView,
  TurnOutputMime,
  TurnView,
} from "./turnView.ts";
import { resolveSessionPin } from "./sessionPin.ts";

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
  private readonly queues = new Map<string, Promise<void>>();
  private readonly streams = new Map<string, NotebookStream>();
  private readonly warnedPins = new Set<string>();
  /** Run ids of each notebook's in-flight turn, for the interrupt handler. */
  private readonly activeRuns = new Map<string, string>();
  /** Notebooks closed with work queued or running: their remaining queued
   * cells must not submit and their streams must not be silently recreated.
   * Cleared when the document is submitted again (reopened). */
  private readonly closedNotebooks = new Set<string>();

  constructor(
    private readonly bridge: NotebookBridge,
    private readonly output: vscode.OutputChannel,
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

  /** Cancels the notebook's in-flight runs when its document closes (best
   * effort: attach-mode servers keep running, so the runs must be told). The
   * stream map is keyed by session id, so the notebook's pinned session — the
   * same pin ensureSessionAsync executes against — resolves the stream. */
  handleNotebookClosed(document: vscode.NotebookDocument): void {
    const queueKey = document.uri.toString();
    // Remaining queued cells of a closed notebook never submit.
    this.closedNotebooks.add(queueKey);
    this.queues.delete(queueKey);

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

  /** Runs cells through the notebook's execution queue (commands that submit
   * follow-up turns join here, behind anything already in flight). */
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
    const previous = this.queues.get(queueKey) ?? Promise.resolve();
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

      // One cell at a time, in document order: the session's single-run gate
      // makes concurrent submissions typed busy errors, not a queue. A close
      // mid-batch stops the remaining cells (no stream is recreated).
      for (const target of targets) {
        if (this.closedNotebooks.has(queueKey)) return;
        await this.executeCellAsync(target.cell, activeSession);
      }
    });
    this.queues.set(queueKey, run.then(() => {}, () => {}));
    await run;
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
    const text = cell.document.getText();
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
    );
    stream.start();
    this.streams.set(sessionId, stream);
    return stream;
  }
}

/** Consumes the session event stream and routes frames to in-flight runs. */
class NotebookStream {
  private readonly runs = new Map<string, RunExecution>();
  private controller: AbortController | null = null;

  constructor(
    private readonly client: FrontendClient,
    private readonly sessionId: string,
    private readonly onRunFinished: (runId: string) => void,
    private readonly log: (message: string) => void,
  ) {}

  dispose(): void {
    // Settle in-flight runs BEFORE aborting: a settled run ends its cell
    // execution and resolves the notebook's execution queue, so nothing is
    // left spinning behind a connection that no longer exists.
    for (const run of this.runs.values()) {
      run.fail("events_disconnected", "The Maieutics server connection was reset.");
    }
    this.runs.clear();
    this.controller?.abort();
    this.controller = null;
  }

  start(): void {
    this.controller = new AbortController();
    void this.pumpAsync(this.controller.signal);
  }

  /** Cancels every in-flight run (notebook closing / window unloading). */
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
  }

  /** Registers an execution and resolves to whether the run committed (a
   * successful terminal frame) when it reaches a terminal frame. Failed,
   * cancelled, and missing runs resolve to false so their cells stay pending. */
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

  private async pumpAsync(signal: AbortSignal): Promise<void> {
    try {
      for await (const frame of this.client.events(this.sessionId, { signal })) {
        this.route(frame);
        // Any terminal view outcome retires the run: a settled run must not
        // stay here catching run-less frames (repl displays, input requests).
        if (
          frame.type === "run.completed" || frame.type === "run.failed" ||
          frame.type === "run.missing"
        ) {
          this.runs.delete(frame.runId ?? "");
        }
      }
    } catch (error) {
      if (!signal.aborted) {
        this.log(`event stream ended: ${error}`);
        // Fail every in-flight run so cells never hang on a dead socket.
        for (const run of this.runs.values()) run.fail("events_disconnected", String(error));
        this.runs.clear();
      }
    }
  }

  private route(frame: EventFrame): void {
    if (frame.runId === undefined) {
      // REPL presentation frames carry no runId; the session gate keeps at
      // most one run in flight, so they belong to it.
      for (const run of this.runs.values()) {
        run.apply(frame);
        return;
      }

      return;
    }

    this.runs.get(frame.runId)?.apply(frame);
  }
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

  /** Routes one input request to a VS Code input box and posts the answer back. */
  private answerInputRequest(frame: EventFrame): void {
    const requestId = frame.requestId;
    if (requestId === undefined) return;

    void (async () => {
      const value = await vscode.window.showInputBox({
        prompt: frame.prompt ?? "REPL input",
        password: frame.password === true,
        ignoreFocusOut: true,
      });
      await this.client.submitInput(requestId, value ?? "");
    })().catch((error: unknown) => {
      this.execution.replaceOutput([
        errorOutput(new FrontendError("input_failed", 0, String(error))),
      ]);
    });
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
    const error = errorOutput(new FrontendError(code, 0, message));
    if (streamed) {
      this.paintChanged();
      this.execution.appendOutput([error]);
    } else {
      this.execution.replaceOutput([error]);
    }

    this.execution.end(false, Date.now());
  }

  private settle(committed: boolean): void {
    this.settled = true;
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

  /** Freezes the segments: final answer text, the structured turn snapshot
   * (carrying the runId + input binding) appended to the answer output, and
   * final tool statuses. */
  private paintFinal(): void {
    const terminal = this.view.terminalState;
    const final = this.view.finalOutput(this.input);

    if (terminal?.kind === "failed") {
      this.paintFailure(terminal.code, terminal.message);
      return;
    }

    // Final tool statuses (an entry can flip after the last streamed paint).
    const toolsKey = `tools:${this.view.runId}`;
    if (final.tools.length > 0) {
      const markdown = final.tools
        .map((tool) => tool.status === "error" ? `- ❌ \`${tool.tool}\`` : `- ✅ \`${tool.tool}\``)
        .join("\n");
      const existing = this.segments.get(toolsKey);
      const items = [vscode.NotebookCellOutputItem.text(markdown, "text/markdown")];
      if (existing) this.execution.replaceOutputItems(items, existing);
      else this.ensureSegment(toolsKey, new vscode.NotebookCellOutput(items));
    }

    // Final answer text, then the structured snapshot appended to the same
    // output: appending items never re-renders the markdown item.
    this.paintAnswer();
    const answerOutput = this.segments.get(`answer:${this.view.runId}`);
    if (answerOutput) {
      this.execution.appendOutputItems(
        [vscode.NotebookCellOutputItem.json(final, TurnOutputMime)],
        answerOutput,
      );
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

function structuredOutput(snapshot: {
  text: string;
  tools: ToolSnapshotView[];
  truncated: boolean;
  repl?: ReplDisplayEntry[];
  error?: { code: string; message: string };
}): vscode.NotebookCellOutput {
  const markdown = renderLiveSnapshot(snapshot);
  return new vscode.NotebookCellOutput([
    vscode.NotebookCellOutputItem.text(markdown, "text/markdown"),
    vscode.NotebookCellOutputItem.json(snapshot, TurnOutputMime),
  ]);
}

function renderLiveSnapshot(snapshot: {
  text: string;
  tools: ToolSnapshotView[];
  truncated: boolean;
  error?: { code: string; message: string };
}): string {
  const sections: string[] = [];
  if (snapshot.tools.length > 0) {
    sections.push(
      snapshot.tools
        .map((tool) => tool.status === "error" ? `- ❌ \`${tool.tool}\`` : `- ✅ \`${tool.tool}\``)
        .join("\n"),
    );
  }
  if (snapshot.text.length > 0) sections.push(snapshot.text);
  if (snapshot.truncated) {
    sections.push(
      "> ⚠️ The agent turn was truncated after exhausting its model iteration budget.",
    );
  }
  if (snapshot.error) {
    sections.push(`> ❌ \`${snapshot.error.code}\` — ${snapshot.error.message}`);
  }
  return sections.join("\n\n");
}
