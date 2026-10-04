/**
 * `.maieuticsnb` serializer: bridges the frontend-owned snapshot format onto
 * the VSCode notebook model. Deserialization only reads the file; live session
 * state is never touched (invariant 13). Structured turn results restore as
 * the single turn+json output item — the timeline renderer's default view and
 * the save round-trip's source (findTurnSnapshot); the markdown fallback view
 * lives inside the renderer. Markdown-only outputs from older files keep the
 * markdown fallback (snapshotFromMarkdownOutputs). The per-cell turn binding
 * (runId + submitted input) round-trips through cell metadata so
 * committed/stale history states survive save/reopen.
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
  stripBundledDisplaySources,
  type TurnBinding,
} from "./notebookFormat.ts";
import { readTurnBinding, TurnBindingMetadataKey } from "./cellHistory.ts";
import { turnOutputItems, type TurnOutputItemSpec, TurnOutputMime } from "./turnView.ts";

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
      // The turn binding rides in cell metadata so committed/stale states
      // survive reopen even when the outputs were cleared.
      if (snapshot.turn !== undefined) {
        cell.metadata = { [TurnBindingMetadataKey]: snapshot.turn };
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
        turn: readCellBinding(cell),
      };
    });
    return serializeNotebookBytes(notebook);
  }
}

/** The persisted turn binding of a snapshot cell, if its metadata carries one. */
function readCellBinding(cell: vscode.NotebookCellData): TurnBinding | undefined {
  return readTurnBinding({ metadata: cell.metadata ?? {}, text: cell.value });
}

/** The structured output the controller leaves on executed cells; bundled
 * component sources are stripped before persistence (untrusted notebook
 * files must not carry executable renderer code — ADR 0038 stage 1b). */
function findTurnSnapshot(cell: vscode.NotebookCellData): OutputSnapshot | undefined {
  for (const output of cell.outputs ?? []) {
    for (const item of output.items) {
      if (item.mime !== TurnOutputMime) continue;
      try {
        const snapshot = JSON.parse(new TextDecoder().decode(item.data)) as OutputSnapshot;
        if (typeof snapshot === "object" && snapshot !== null) {
          return stripBundledDisplaySources(snapshot);
        }
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

/** Restores one snapshot as cell outputs: REPL displays first, then the
 * turn+json item — the ONLY render item, so the timeline renderer is picked
 * automatically (a sibling text/markdown item would always win VS Code's
 * mime display order). The same item is what serializeNotebook round-trips
 * from (findTurnSnapshot). Cells whose outputs carry no turn+json item are
 * older saves; they keep the markdown fallback view. */
export function renderSnapshotOutputs(output: OutputSnapshot): vscode.NotebookCellOutput[] {
  const outputs = (output.repl ?? []).map((display) =>
    new vscode.NotebookCellOutput(bundleItems(display.data))
  );
  outputs.push(
    new vscode.NotebookCellOutput(turnOutputItems(output).map(outputItemFromSpec)),
  );
  return outputs;
}

/** Maps a pure turn output item spec (turnView) onto a vscode output item:
 * the vscode-coupled half of the shared output composition. */
export function outputItemFromSpec(spec: TurnOutputItemSpec): vscode.NotebookCellOutputItem {
  return spec.encoding === "text"
    ? vscode.NotebookCellOutputItem.text(String(spec.value), spec.mime)
    : vscode.NotebookCellOutputItem.json(spec.value, spec.mime);
}

/** Renderable mimes in a REPL display bundle; anything else is skipped. */
const DisplayMimes = new Set([
  "text/markdown",
  "text/html",
  "text/plain",
  "application/json",
  // Widget views ride display bundles as structured JSON; the widget
  // renderer claims them (ADR 0024).
  "application/vnd.jupyter.widget-view+json",
  // Native view-family announcements (custom-UI framework, ADR 0038); the
  // view renderer claims them.
  "application/vnd.maieutics.view+json",
]);

/** A reference to an immutable binary display payload, addressed by its
 * relative URL (content-addressed: same URL = same bytes forever). */
export interface ObjectReference {
  $object: string;
  byteLength: number;
}

export function isObjectReference(value: unknown): value is ObjectReference {
  if (typeof value !== "object" || value === null) return false;
  const record = value as Record<string, unknown>;
  return typeof record.$object === "string" && record.$object.startsWith("/") &&
    typeof record.byteLength === "number";
}

/** Maps a mime bundle onto notebook output items in bundle order. Object
 * references become binary items fetched from the server (invariant 26: the
 * wire never carries the bytes as base64 text). */
export function bundleItems(
  data: Record<string, unknown>,
  fetchObject?: (sha256: string) => Promise<Uint8Array>,
): vscode.NotebookCellOutputItem[] {
  const items: vscode.NotebookCellOutputItem[] = [];
  for (const [mime, value] of Object.entries(data)) {
    if (!DisplayMimes.has(mime)) continue;

    if (isObjectReference(value)) {
      if (fetchObject === undefined) {
        items.push(
          vscode.NotebookCellOutputItem.text(
            `[binary ${mime}: ${value.byteLength} bytes]`,
            "text/plain",
          ),
        );
        continue;
      }

      // Binary items are filled asynchronously via pendingObjectItems below.
      pendingObjectItems.push(fillObjectItem(mime, value, fetchObject));
      continue;
    }

    if (mime === "application/json" || typeof value !== "string") {
      items.push(vscode.NotebookCellOutputItem.json(value, mime));
    } else {
      items.push(vscode.NotebookCellOutputItem.text(value, mime));
    }
  }

  return items;
}

/** Pending binary fills from the most recent bundleItems call. */
let pendingObjectItems: Promise<vscode.NotebookCellOutputItem | null>[] = [];

/** Awaits all pending binary fills from the last bundleItems call. Items whose
 * fetch failed resolve to null and are dropped (the text placeholder already
 * shipped with the synchronous items). */
export async function drainPendingObjectItems(): Promise<vscode.NotebookCellOutputItem[]> {
  const pending = pendingObjectItems;
  pendingObjectItems = [];
  const settled = await Promise.all(pending);
  return settled.filter((item) => item !== null);
}

async function fillObjectItem(
  mime: string,
  reference: ObjectReference,
  fetchObject: (sha256: string) => Promise<Uint8Array>,
): Promise<vscode.NotebookCellOutputItem | null> {
  try {
    const bytes = await fetchObject(reference.$object);
    return new vscode.NotebookCellOutputItem(bytes, mime);
  } catch {
    return null;
  }
}

// NotebookKind is re-exported for the controller's notebook-type contract.
export { NotebookKind };
