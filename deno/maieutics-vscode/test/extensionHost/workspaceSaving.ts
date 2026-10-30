import * as vscode from "vscode";
import { executeIdeMethod } from "../../controller.ts";
import type { DocumentSaveResult, DocumentSaveTarget } from "../../documentSaving.ts";
import { emptyNotebook } from "../../notebookFormat.ts";

function assert(value: unknown, message: string): asserts value {
  if (!value) throw new Error(message);
}

async function changeFolders(
  index: number,
  remove: number,
  folders: { uri: vscode.Uri }[],
): Promise<void> {
  let listener: vscode.Disposable | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    await new Promise<void>((resolve, reject) => {
      listener = vscode.workspace.onDidChangeWorkspaceFolders(() => resolve());
      timer = setTimeout(
        () => reject(new Error("Save-all test workspace-folder change was not delivered")),
        10_000,
      );
      if (!vscode.workspace.updateWorkspaceFolders(index, remove, ...folders)) {
        reject(new Error("Workspace folder update failed"));
      }
    });
  } finally {
    listener?.dispose();
    if (timer !== undefined) clearTimeout(timer);
  }
}

export async function testWorkspaceSaving(): Promise<void> {
  const folders = vscode.workspace.workspaceFolders ?? [];
  assert(folders.length > 0, "workspace exists");
  const root = folders[0].uri;
  const secondRoot = vscode.Uri.joinPath(root, "..", "save-second-project");
  const outside = vscode.Uri.joinPath(root, "..", "save-unrelated.ts");
  await vscode.workspace.fs.createDirectory(secondRoot);
  await changeFolders(folders.length, 0, [{ uri: secondRoot }]);
  try {
    const first = vscode.Uri.joinPath(root, "save-first.ts");
    const second = vscode.Uri.joinPath(secondRoot, "save-second.ts");
    for (const uri of [first, second, outside]) {
      await vscode.workspace.fs.writeFile(
        uri,
        new TextEncoder().encode("export const saved = 1;\n"),
      );
    }
    for (const uri of [first, second, outside]) {
      await vscode.workspace.openTextDocument(uri);
      const edit = new vscode.WorkspaceEdit();
      edit.insert(uri, new vscode.Position(0, 0), "// unsaved edit\n");
      assert(await vscode.workspace.applyEdit(edit), "test edit applied");
    }
    const notebookUri = vscode.Uri.joinPath(root, "save-notebook.maieuticsnb");
    await vscode.workspace.fs.writeFile(
      notebookUri,
      new TextEncoder().encode(JSON.stringify(emptyNotebook())),
    );
    const notebook = await vscode.workspace.openNotebookDocument(notebookUri);
    const edit = new vscode.WorkspaceEdit();
    edit.set(notebookUri, [vscode.NotebookEdit.replaceCells(new vscode.NotebookRange(0, 1), [
      new vscode.NotebookCellData(
        vscode.NotebookCellKind.Code,
        "unsaved notebook input",
        "markdown",
      ),
    ])]);
    assert(await vscode.workspace.applyEdit(edit), "notebook dirty edit applied");

    const targets = await executeIdeMethod("maieutics/documents/dirty", {}) as {
      documents: DocumentSaveTarget[];
    };
    for (const uri of [first, second, notebookUri]) {
      assert(
        targets.documents.some((target) => target.uri === uri.toString()),
        "all project text/notebook documents are enumerated",
      );
    }
    assert(
      !targets.documents.some((target) => target.uri === outside.toString()),
      "unrelated documents are excluded",
    );
    const result = await executeIdeMethod(
      "maieutics/documents/save",
      targets,
    ) as DocumentSaveResult;
    assert(result.status === "saved", `workspace save succeeded: ${JSON.stringify(result)}`);
    for (const uri of [first, second]) {
      assert(
        new TextDecoder().decode(await vscode.workspace.fs.readFile(uri)).startsWith(
          "// unsaved edit",
        ),
        "both projects' buffer contents reached disk",
      );
      assert(
        !(await vscode.workspace.openTextDocument(uri)).isDirty,
        "saved text document is clean",
      );
    }
    assert(!notebook.isDirty, "notebook is saved through its document API");
    assert(
      new TextDecoder().decode(await vscode.workspace.fs.readFile(notebookUri)).includes(
        "unsaved notebook input",
      ),
      "notebook serializer persisted the edited document",
    );
    assert(
      (await vscode.workspace.openTextDocument(outside)).isDirty,
      "unrelated buffer remains unsaved",
    );
    assert(
      !new TextDecoder().decode(await vscode.workspace.fs.readFile(outside)).includes(
        "unsaved edit",
      ),
      "save-all never writes outside the IDE workspace",
    );
  } finally {
    await changeFolders(folders.length, 1, []);
  }
}
