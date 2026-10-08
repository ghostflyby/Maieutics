/**
 * The `vscode` shell of the skill selection loop (ADR 0039): `$`-triggered
 * completion over the live catalog mints markers into the cell document, the
 * document's change stream feeds the input-boundary reference model, and the
 * model's ranges render as chip-style decorations. The raw marker text is the
 * truth — decorations are a visual projection, never the source of intent.
 */

import * as vscode from "vscode";
import { NotebookType } from "./serializer.ts";
import {
  buildSkillMarker,
  encodeSkillSubmission as encodeSkillSubmission0,
  forgetSkillReferences,
  referenceModelFor,
} from "./skillReferences.ts";
import { type SkillInfo, SkillsCatalog } from "./skillsCatalog.ts";

const CellSelector: vscode.DocumentSelector = {
  language: "markdown",
  notebookType: NotebookType,
};

/** The `$` word the completion replaces: `$` plus the name characters that
 * follow, nothing else — anything not matching stays untouched text. */
const SkillTriggerPattern = /\$[a-z0-9][a-z0-9-]*/;

/** A change whose inserted text is exactly one whole marker is a mint. */
const SkillMarkerWholeInsert = /^\[\[maieutics:skill name="([a-z0-9][a-z0-9-]{0,63})"\]\]$/;

export function registerSkillInteraction(
  clientOf: () => Promise<{
    listSkills: (
      signal?: AbortSignal,
    ) => Promise<{ skills: SkillInfo[]; diagnostics: number }>;
  }>,
  log: (message: string) => void,
): vscode.Disposable {
  const catalog = new SkillsCatalog(() =>
    clientOf()
      .then((client) => client.listSkills().then((listed) => listed.skills))
      .catch((error) => {
        log(`skill catalog refresh failed: ${error}`);
        return [];
      })
  );

  const chipDecoration = vscode.window.createTextEditorDecorationType({
    backgroundColor: "rgba(127, 127, 127, 0.15)",
    border: "1px solid",
    borderRadius: "3px",
  });

  function refreshDecorations(document: vscode.TextDocument): void {
    const text = document.getText();
    const model = referenceModelFor(document.uri.toString(), text);
    model.validate(text);
    for (const editor of vscode.window.visibleTextEditors) {
      if (editor.document.uri.toString() !== document.uri.toString()) continue;
      editor.setDecorations(
        chipDecoration,
        model.ranges().map((tracked) =>
          new vscode.Range(
            document.positionAt(tracked.start),
            document.positionAt(tracked.end),
          )
        ),
      );
    }
  }

  const completion = vscode.languages.registerCompletionItemProvider(
    CellSelector,
    {
      async provideCompletionItems(document, position) {
        const wordRange = document.getWordRangeAtPosition(
          position,
          SkillTriggerPattern,
        );
        const prefix = wordRange ? document.getText(wordRange).slice(1) : "";
        const skills = await catalog.all();
        return skills
          .filter((skill) => skill.name.startsWith(prefix))
          .map((skill) => {
            const item = new vscode.CompletionItem(
              `$${skill.name}`,
              vscode.CompletionItemKind.Reference,
            );
            if (wordRange) item.range = wordRange;
            // The insert is the mint: the marker text lands in the document,
            // and the change stream below registers it with the model.
            item.insertText = buildSkillMarker(skill.name);
            item.detail = skill.description;
            item.sortText = `0${skill.name}`;
            return item;
          });
      },
    },
    "$",
  );

  const changes = vscode.workspace.onDidChangeTextDocument((event) => {
    const key = event.document.uri.toString();
    const text = event.document.getText();
    const model = referenceModelFor(key, text);
    for (const change of event.contentChanges) {
      const offset = event.document.offsetAt(change.range.start);
      if (SkillMarkerWholeInsert.test(change.text)) {
        model.applyChange(offset, change.rangeLength, change.text.length);
        model.mintIfInsertion(offset, change.text);
      } else {
        model.applyChange(offset, change.rangeLength, change.text.length);
      }
    }
    model.validate(text);
    refreshDecorations(event.document);
  });

  const opens = vscode.workspace.onDidOpenTextDocument((document) => {
    // Reopen form-default: markers already in the document are references
    // (intent cannot survive plain-text serialization; the form is the best
    // available default across sessions).
    referenceModelFor(document.uri.toString(), document.getText());
    refreshDecorations(document);
  });

  const closes = vscode.workspace.onDidCloseTextDocument((document) => {
    forgetSkillReferences(document.uri.toString());
  });

  return vscode.Disposable.from(completion, changes, opens, closes, chipDecoration);
}

/** The controller's submit hook: encodes the cell text through the boundary
 * model before the wire (tracked selections stay markers; untracked
 * marker-shaped text rides as a mention). */
export function encodeSkillSubmission(documentUri: string, text: string): string {
  return encodeSkillSubmission0(documentUri, text);
}
