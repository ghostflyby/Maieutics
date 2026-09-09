/**
 * `%`-command completion for notebook cells: bridges the server's command
 * completion (profile-aware) onto the VSCode completion model. The cursor and
 * replacement range on the wire are UTF-16 code-unit offsets — the same units
 * as VSCode document positions, so they map directly.
 */

import * as vscode from "vscode";
import { createCompletionGate } from "./completionCore.ts";
import type { FrontendClient } from "./client.ts";
import { NotebookType } from "./serializer.ts";

const TriggerCharacters = ["%", "/"];

export function registerCommandCompletion(
  clientOf: () => Promise<FrontendClient>,
  log: (message: string) => void,
): vscode.Disposable {
  const gate = createCompletionGate(
    (text, cursor, signal) => clientOf().then((client) => client.complete(text, cursor, signal)),
    log,
  );
  return vscode.languages.registerCompletionItemProvider(
    // Cell documents of this extension's notebook type. Notebook-type scoping
    // is the selector that actually fires for notebook cells: a plain
    // `{ scheme: "untitled" }` selector never matches cell documents, which
    // carry the vscode-notebook-cell scheme. Scheme stays unset so stored and
    // untitled notebooks both match.
    { language: "markdown", notebookType: NotebookType },
    {
      async provideCompletionItems(document, position): Promise<vscode.CompletionItem[]> {
        const wordRange = document.getWordRangeAtPosition(
          position,
          /[%\/][^\s]*/,
        );
        const matches = await gate(document.getText(), document.offsetAt(position));
        return matches.map((match) => {
          const item = new vscode.CompletionItem(
            match,
            vscode.CompletionItemKind.Keyword,
          );
          // Replace the whole partial command token, not just the word under
          // the cursor (a command token contains no whitespace).
          if (wordRange) {
            item.range = wordRange;
          }

          return item;
        });
      },
    },
    ...TriggerCharacters,
  );
}
