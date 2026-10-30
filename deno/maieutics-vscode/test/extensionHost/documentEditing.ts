import * as vscode from "vscode";
import { executeIdeMethod } from "../../controller.ts";
import { sha256Hex } from "../../ide.ts";

function assert(value: unknown, message: string): asserts value {
  if (!value) throw new Error(message);
}

interface Snapshot {
  uri: string;
  content: string;
  version: number;
}

async function snapshot(uri: vscode.Uri): Promise<Snapshot> {
  const answer = await executeIdeMethod("maieutics/documents/read", { uris: [uri.toString()] }) as {
    documents: Snapshot[];
  };
  assert(answer.documents.length === 1, "one document snapshot returned");
  return answer.documents[0];
}

async function replacement(before: Snapshot, content: string) {
  return { ...before, content, expectedHash: await sha256Hex(before.content) };
}

async function rejected(run: () => Promise<unknown>, code: string): Promise<void> {
  try {
    await run();
  } catch (error) {
    assert(
      typeof error === "object" && error !== null && "code" in error && error.code === code,
      `expected ${code}, received ${String(error)}`,
    );
    return;
  }
  throw new Error(`Expected ${code} but the edit succeeded`);
}

async function editorCommand(
  document: vscode.TextDocument,
  command: string,
  expected: string,
): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let listener: vscode.Disposable | undefined;
  try {
    const changed = new Promise<void>((resolve, reject) => {
      listener = vscode.workspace.onDidChangeTextDocument((event) => {
        if (event.document === document && document.getText() === expected) resolve();
      });
      timer = setTimeout(
        () =>
          reject(
            new Error(
              `${command} did not change the target document; active=${vscode.window.activeTextEditor?.document.uri}; content=${document.getText()}`,
            ),
          ),
        10_000,
      );
    });
    await vscode.commands.executeCommand("workbench.action.focusActiveEditorGroup");
    await vscode.commands.executeCommand(command);
    await changed;
  } finally {
    listener?.dispose();
    if (timer !== undefined) clearTimeout(timer);
  }
}

export async function testDocumentEditing(): Promise<void> {
  const root = vscode.workspace.workspaceFolders?.[0].uri;
  assert(root, "test workspace exists");
  const first = vscode.Uri.joinPath(root, "document-edit.ts");
  const second = vscode.Uri.joinPath(root, "document-edit-second.ts");
  const disk = "export const original: number = 1;\n";
  for (const uri of [first, second]) {
    await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(disk));
  }
  const document = await vscode.workspace.openTextDocument(first);
  // Undo targets the focused native code widget. Close unrelated workbench
  // parts so a built-in Chat input cannot receive this test's editor commands.
  await vscode.commands.executeCommand("workbench.action.closeAuxiliaryBar");
  await vscode.commands.executeCommand("workbench.action.closeSidebar");
  await vscode.commands.executeCommand("workbench.action.closePanel");
  await vscode.window.showTextDocument(document, {
    viewColumn: vscode.ViewColumn.One,
    preserveFocus: false,
    preview: false,
  });
  const initial = await snapshot(first);
  const dirty = "export function add(left:number,right:number){return left+right;}\nadd(1,2);\n";
  await executeIdeMethod("maieutics/documents/apply", {
    documents: [await replacement(initial, dirty)],
  });
  assert(
    document.isDirty && document.getText() === dirty,
    "document transaction creates unsaved buffer edits",
  );
  assert(
    new TextDecoder().decode(await vscode.workspace.fs.readFile(first)) === disk,
    "document editing never saves the file",
  );
  await editorCommand(document, "undo", disk);
  assert(document.getText() === disk, "editor undo reverts the document transaction");
  await editorCommand(document, "redo", dirty);
  assert(document.getText() === dirty, "editor redo restores the document transaction");

  const before = await snapshot(first);
  const otherBefore = await snapshot(second);
  const edit = new vscode.WorkspaceEdit();
  edit.insert(first, new vscode.Position(0, 0), "// user intervened\n");
  assert(await vscode.workspace.applyEdit(edit), "simulated user edit succeeds");
  const stale = await replacement(before, "unwanted");
  const other = await replacement(otherBefore, "unwanted");
  await rejected(
    () => executeIdeMethod("maieutics/documents/apply", { documents: [other, stale] }),
    "ide_document_conflict",
  );
  assert(
    (await snapshot(second)).content === disk,
    "valid targets remain unchanged after a later version conflict",
  );
  const firstFresh = await snapshot(first);
  const secondFresh = await snapshot(second);
  await executeIdeMethod("maieutics/documents/apply", {
    documents: [
      await replacement(firstFresh, firstFresh.content + "// paired edit\n"),
      await replacement(secondFresh, secondFresh.content + "// paired edit\n"),
    ],
  });
  assert(
    document.getText().endsWith("// paired edit\n") &&
      (await snapshot(second)).content.endsWith("// paired edit\n"),
    "one transaction applies both valid document edits",
  );
  assert(
    new TextDecoder().decode(await vscode.workspace.fs.readFile(second)) === disk,
    "multi-document edits remain unsaved",
  );
  const current = await snapshot(first);
  const cancelled = new AbortController();
  cancelled.abort();
  try {
    await executeIdeMethod("maieutics/documents/apply", {
      documents: [await replacement(current, "unwanted")],
    }, cancelled.signal);
    throw new Error("Cancelled document edit was accepted");
  } catch (error) {
    assert(cancelled.signal.reason === error, "owner cancellation stops edits");
  }
  assert(document.getText() === current.content, "cancelled edit leaves the buffer intact");

  // Built-in TS is the standard; this suite has already awaited its diagnostic
  // event in testIdeExecutorAsync, so no polling or external language extension.
  const formatted = await executeIdeMethod("textDocument/formatting", {
    uri: first.toString(),
    options: { tabSize: 2, insertSpaces: true },
  }) as vscode.TextEdit[];
  assert(formatted.length > 0, "built-in TS formats the dirty buffer");
  assert(
    formatted.every((e) =>
      Number.isInteger(e.range.start.line) && Number.isInteger(e.range.start.character)
    ),
    "format edits carry public position coordinates",
  );
  const rename = await executeIdeMethod("textDocument/rename", {
    uri: first.toString(),
    position: { line: 1, character: 17 },
    newName: "sum",
  }) as { changes: Record<string, unknown[]>; versions: Record<string, number> };
  assert(
    rename.changes[first.toString()]?.length > 0,
    "built-in TS rename sees the unsaved function",
  );
  assert(
    rename.versions[first.toString()] === document.version,
    "rename carries the source document version",
  );
  const actions = await executeIdeMethod("textDocument/codeAction", {
    uri: first.toString(),
    position: { line: 1, character: 17 },
    context: { diagnostics: [] },
  });
  assert(Array.isArray(actions), "code actions accept a document range");

  // An existing buffer remains authoritative even after its disk file disappears.
  await vscode.workspace.fs.delete(first);
  const deleted = await snapshot(first);
  await executeIdeMethod("maieutics/documents/apply", {
    documents: [await replacement(deleted, deleted.content + "// still buffered\n")],
  });
  assert(
    document.getText().endsWith("// still buffered\n"),
    "deleted-on-disk open buffers remain editable",
  );
}
