/**
 * The notebook controller: one ordinary cell is one submitted Agent turn
 * (invariant 2). Per notebook, cells execute strictly in submission order and
 * one at a time — the session's single-run gate is the only serialization
 * point, so a second concurrent turn surfaces as the typed busy error instead
 * of a protocol queue.
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
/** Bridges the protocol onto one notebook's outputs. Connections start lazily
 * on the first execution, never at extension activation. */
export interface NotebookBridge {
  client(): Promise<FrontendClient>;
  /** Current server session for the notebook's connection. */
  session(): Promise<SessionInfo>;
  /** Fetches a binary object by content address (object bypass dereference). */
  fetchObject(sha256: string): Promise<Uint8Array>;
}

export class MaieuticsNotebookController implements vscode.Disposable {
  private readonly controller: vscode.NotebookController;
  private readonly queues = new Map<string, Promise<void>>();
  private readonly streams = new Map<string, NotebookStream>();
  private readonly warnedPins = new Set<string>();

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
  }

  dispose(): void {
    for (const stream of this.streams.values()) stream.dispose();
    this.streams.clear();
    this.controller.dispose();
  }

  /** Cancels the notebook's in-flight runs when its document closes (best
   * effort: attach-mode servers keep running, so the runs must be told). */
  handleNotebookClosed(document: vscode.NotebookDocument): void {
    const stream = this.streams.get(document.uri.toString());
    if (stream === undefined) return;

    stream.cancelAll();
    this.streams.delete(document.uri.toString());
    this.queues.delete(document.uri.toString());
  }

  private async executeAsync(
    cells: vscode.NotebookCell[],
    document: vscode.NotebookDocument,
  ): Promise<void> {
    const queueKey = document.uri.toString();
    const previous = this.queues.get(queueKey) ?? Promise.resolve();
    const run = previous.then(async () => {
      const sessionId = await this.ensureSessionAsync(document);
      await Promise.all(cells.map((cell) => this.executeCellAsync(cell, sessionId)));
    });
    this.queues.set(queueKey, run.then(() => {}, () => {}));
    await run;
  }

  private async executeCellAsync(cell: vscode.NotebookCell, sessionId: string): Promise<void> {
    const text = cell.document.getText();
    const execution = this.controller.createNotebookCellExecution(cell);
    try {
      const stream = await this.ensureStreamAsync(sessionId);
      execution.start(Date.now());
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

      await stream.awaitRunAsync(answer.runId, execution);
    } catch (error) {
      execution.start(Date.now());
      execution.clearOutput();
      execution.replaceOutput([errorOutput(error)]);
      execution.end(false, Date.now());
    }
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

  /** Registers an execution and resolves when its run reaches a terminal frame. */
  awaitRunAsync(
    runId: string,
    execution: vscode.NotebookCellExecution,
  ): Promise<void> {
    return new Promise<void>((resolve) => {
      const run = new RunExecution(runId, execution, this.client, resolve);
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
        if (frame.type === "run.completed" || frame.type === "run.failed") {
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
    private readonly client: FrontendClient,
    private readonly resolve: () => void,
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
      this.settle();
      return;
    }

    this.paint();
  }

  fail(code: string, message: string): void {
    if (this.settled) return;
    this.execution.replaceOutput([
      errorOutput(new FrontendError(code, 0, message)),
    ]);
    this.execution.end(false, Date.now());
    this.settle();
  }

  private settle(): void {
    this.settled = true;
    this.resolve();
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
      this.ensureSegment(key, output);
      if (hadObjectRefs) {
        void fillReplObjectItemsAsync(this.execution, output).catch(() => {});
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
   * items in place. Passing no items removes the segment (empty tools). */
  private ensureSegment(key: string, output: vscode.NotebookCellOutput | undefined): void {
    const existing = this.segments.get(key);
    if (output === undefined) {
      if (existing) {
        this.execution.replaceOutputItems([], existing);
      }

      return;
    }

    if (existing) {
      this.execution.replaceOutputItems(output.items, existing);
      return;
    }

    this.segments.set(key, output);
    this.execution.appendOutput([output]);
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
   * appended to the answer output, and final tool statuses. */
  private paintFinal(): void {
    const terminal = this.view.terminalState;
    const final = this.view.finalOutput();

    if (terminal?.kind === "failed") {
      this.execution.replaceOutput([
        errorOutput(new FrontendError(terminal.code, 0, terminal.message)),
      ]);
      this.execution.end(false, Date.now());
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

/** Awaits the async binary fills and replaces the output's items in place with
 * the complete item list. */
async function fillReplObjectItemsAsync(
  execution: vscode.NotebookCellExecution,
  output: vscode.NotebookCellOutput,
): Promise<void> {
  const pending = await drainPendingObjectItems();
  if (pending.length === 0) return;

  const complete = [...pending, ...output.items];
  execution.replaceOutputItems(complete, output);
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
