/**
 * `.maieuticsnb` serializer: bridges the frontend-owned snapshot format onto
 * the VSCode notebook model. Deserialization only reads the file; live session
 * state is never touched (invariant 13). Structured turn results ride along as
 * a custom output item so save round-trips without scraping markdown.
 */

import * as vscode from "vscode";
import {
  type CellSnapshot,
  emptyNotebook,
  type MaieuticsNotebook,
  NotebookKind,
  NotebookLanguage,
  type OutputSnapshot,
  parseNotebook,
  serializeNotebook as serializeNotebookBytes,
  type ToolSnapshot,
} from "./notebookFormat.ts";
import { TurnOutputMime } from "./turnView.ts";

export const NotebookType = "maieutics-notebook";

/** Notebook metadata key carrying the server session the file last ran against. */
export const StoredSessionMetadataKey = "maieuticsSessionId";

/** Reads the pinned server session id from notebook metadata. */
export function readStoredSessionId(
  metadata: { [key: string]: unknown } | undefined,
): string | undefined {
  const value = metadata?.[StoredSessionMetadataKey];
  return typeof value === "string" && value.length === 32 ? value : undefined;
}

export class MaieuticsNotebookSerializer implements vscode.NotebookSerializer {
  deserializeNotebook(
    content: Uint8Array,
    _token: vscode.CancellationToken,
  ): vscode.NotebookData {
    let notebook: MaieuticsNotebook;
    try {
      notebook = parseNotebook(new Uint8Array(content));
    } catch (error) {
      // A foreign or broken document opens as an empty notebook with the error
      // surfaced in a read-only Markdown cell, never as a corrupted file.
      const data = new vscode.NotebookData([
        new vscode.NotebookCellData(
          vscode.NotebookCellKind.Markup,
          `> ⚠️ ${error instanceof Error ? error.message : String(error)}`,
          NotebookLanguage,
        ),
      ]);
      return data;
    }

    const data = new vscode.NotebookData([]);
    data.metadata = notebook.session?.serverSessionId
      ? { [StoredSessionMetadataKey]: notebook.session.serverSessionId }
      : undefined;
    data.cells = notebook.cells.map((snapshot) => {
      const cell = new vscode.NotebookCellData(
        snapshot.kind === "markdown"
          ? vscode.NotebookCellKind.Markup
          : vscode.NotebookCellKind.Code,
        snapshot.text,
        NotebookLanguage,
      );
      if (snapshot.kind === "agent" && snapshot.output) {
        cell.outputs = renderSnapshotOutputs(snapshot.output);
      }
      return cell;
    });
    return data;
  }

  serializeNotebook(
    data: vscode.NotebookData,
    _token: vscode.CancellationToken,
  ): Uint8Array {
    const notebook = emptyNotebook();
    const storedSessionId = readStoredSessionId(data.metadata);
    if (storedSessionId !== undefined) {
      notebook.session = { serverSessionId: storedSessionId };
    }

    notebook.cells = data.cells.map((cell): CellSnapshot => {
      if (cell.kind === vscode.NotebookCellKind.Markup) {
        return { kind: "markdown", text: cell.value };
      }

      const structured = findTurnSnapshot(cell);
      return {
        kind: "agent",
        text: cell.value,
        output: structured ?? snapshotFromMarkdownOutputs(cell),
      };
    });
    return serializeNotebookBytes(notebook);
  }
}

/** The structured output the controller leaves on executed cells. */
function findTurnSnapshot(cell: vscode.NotebookCellData): OutputSnapshot | undefined {
  for (const output of cell.outputs ?? []) {
    for (const item of output.items) {
      if (item.mime !== TurnOutputMime) continue;
      try {
        const snapshot = JSON.parse(new TextDecoder().decode(item.data)) as OutputSnapshot;
        if (typeof snapshot === "object" && snapshot !== null) return snapshot;
      } catch {
        // Fall through to the markdown render.
      }
    }
  }
  return undefined;
}

/** Best-effort fallback when only the markdown render survived. */
function snapshotFromMarkdownOutputs(cell: vscode.NotebookCellData): OutputSnapshot | undefined {
  if ((cell.outputs ?? []).length === 0) return undefined;
  for (const output of cell.outputs ?? []) {
    for (const item of output.items) {
      if (item.mime !== "text/markdown") continue;
      return { text: new TextDecoder().decode(item.data) };
    }
  }
  return { text: "" };
}

export function renderSnapshotOutputs(output: OutputSnapshot): vscode.NotebookCellOutput[] {
  const outputs = (output.repl ?? []).map((display) =>
    new vscode.NotebookCellOutput(bundleItems(display.data))
  );
  const markdown = renderSnapshotMarkdown(output);
  const items = [vscode.NotebookCellOutputItem.text(markdown, "text/markdown")];
  // The structured item is what the serializer round-trips.
  items.push(vscode.NotebookCellOutputItem.json(output, TurnOutputMime));
  outputs.push(new vscode.NotebookCellOutput(items));
  return outputs;
}

/** Renderable mimes in a REPL display bundle; anything else is skipped. */
const DisplayMimes = new Set([
  "text/markdown",
  "text/html",
  "text/plain",
  "application/json",
]);

/** Maps a mime bundle onto notebook output items in bundle order. */
export function bundleItems(data: Record<string, unknown>): vscode.NotebookCellOutputItem[] {
  const items: vscode.NotebookCellOutputItem[] = [];
  for (const [mime, value] of Object.entries(data)) {
    if (!DisplayMimes.has(mime)) continue;

    if (mime === "application/json" || typeof value !== "string") {
      items.push(vscode.NotebookCellOutputItem.json(value, mime));
    } else {
      items.push(vscode.NotebookCellOutputItem.text(value, mime));
    }
  }

  return items;
}

export function renderSnapshotMarkdown(output: OutputSnapshot): string {
  const sections: string[] = [];
  if (output.tools?.length) sections.push(renderTools(output.tools));
  if (output.text) sections.push(output.text);
  if (output.truncated) {
    sections.push(
      "> ⚠️ The agent turn was truncated after exhausting its model iteration budget.",
    );
  }
  if (output.error) {
    sections.push(`> ❌ \`${output.error.code}\` — ${output.error.message}`);
  }
  return sections.length > 0 ? sections.join("\n\n") : "";
}

function renderTools(tools: ToolSnapshot[]): string {
  return tools
    .map((tool) => tool.status === "error" ? `- ❌ \`${tool.tool}\`` : `- ✅ \`${tool.tool}\``)
    .join("\n");
}

// NotebookKind is re-exported for the controller's notebook-type contract.
export { NotebookKind };
