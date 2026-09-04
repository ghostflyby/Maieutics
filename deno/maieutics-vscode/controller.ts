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
import { NotebookType } from "./serializer.ts";
import { type ToolSnapshotView, TurnOutputMime, TurnView } from "./turnView.ts";

const PaintIntervalMs = 60;
/** Bridges the protocol onto one notebook's outputs. Connections start lazily
 * on the first execution, never at extension activation. */
export interface NotebookBridge {
  client(): Promise<FrontendClient>;
  /** Current server session for the notebook's connection. */
  session(): Promise<SessionInfo>;
}

export class MaieuticsNotebookController implements vscode.Disposable {
  private readonly controller: vscode.NotebookController;
  private readonly queues = new Map<string, Promise<void>>();
  private readonly streams = new Map<string, NotebookStream>();

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

  private async executeAsync(
    cells: vscode.NotebookCell[],
    document: vscode.NotebookDocument,
  ): Promise<void> {
    const queueKey = document.uri.toString();
    const previous = this.queues.get(queueKey) ?? Promise.resolve();
    const run = previous.then(() => Promise.all(cells.map((cell) => this.executeCellAsync(cell))));
    this.queues.set(queueKey, run.then(() => {}, () => {}));
    await run;
  }

  private async executeCellAsync(cell: vscode.NotebookCell): Promise<void> {
    const text = cell.document.getText();
    const execution = this.controller.createNotebookCellExecution(cell);
    try {
      const session = await this.bridge.session();
      const stream = await this.ensureStreamAsync(session.id);
      execution.start(Date.now());
      execution.clearOutput();

      // An empty cell mirrors the kernel contract: a successful no-op turn.
      if (text.trim().length === 0) {
        execution.end(true, Date.now());
        return;
      }

      const answer: SubmitAnswer = await (await this.bridge.client())
        .submitTurn(session.id, text);
      if (answer.kind === "command") {
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

  /** Registers an execution and resolves when its run reaches a terminal frame. */
  awaitRunAsync(
    runId: string,
    execution: vscode.NotebookCellExecution,
  ): Promise<void> {
    return new Promise<void>((resolve) => {
      const run = new RunExecution(runId, execution, resolve);
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
    if (frame.runId === undefined) return;
    this.runs.get(frame.runId)?.apply(frame);
  }
}

/** Folds frames for one run and paints the cell output with throttling. */
class RunExecution {
  private readonly view: TurnView;
  private lastPaint = 0;
  private paintTimer: ReturnType<typeof setTimeout> | null = null;
  private settled = false;

  constructor(
    runId: string,
    private readonly execution: vscode.NotebookCellExecution,
    private readonly resolve: () => void,
  ) {
    this.view = new TurnView(runId);
  }

  /** Paints the placeholder output so the cell shows progress before frames arrive. */
  begin(): void {
    this.replaceStreamingOutput();
  }

  apply(frame: EventFrame): void {
    if (this.settled) return;
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

  /** Repaints the markdown output, throttled while the run streams. */
  private paint(): void {
    if (this.paintTimer !== null) return;
    const elapsed = Date.now() - this.lastPaint;
    if (elapsed >= PaintIntervalMs) {
      this.lastPaint = Date.now();
      this.replaceStreamingOutput();
      return;
    }

    this.paintTimer = setTimeout(() => {
      this.paintTimer = null;
      this.lastPaint = Date.now();
      this.replaceStreamingOutput();
    }, PaintIntervalMs - elapsed);
  }

  private replaceStreamingOutput(): void {
    const sections: string[] = [];
    const tools = this.view.toolLines();
    if (tools.length > 0) sections.push(tools.join("\n"));
    const text = this.view.markdown();
    if (text.length > 0) sections.push(text);
    if (sections.length === 0) sections.push("…");
    this.execution.replaceOutput([
      new vscode.NotebookCellOutput([
        vscode.NotebookCellOutputItem.text(sections.join("\n\n"), "text/markdown"),
      ]),
    ]);
  }

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

    this.execution.replaceOutput([finalOutput(final)]);
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
  error?: { code: string; message: string };
}): vscode.NotebookCellOutput {
  return structuredOutput(final);
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
