/**
 * `%`-command completion for notebook cells: bridges the server's command
 * completion (profile-aware) onto the VSCode completion model. The cursor and
 * replacement range on the wire are UTF-16 code-unit offsets — the same units
 * as VSCode document positions, so they map directly.
 */

import * as vscode from "vscode";
import type { FrontendClient } from "./client.ts";

const TriggerCharacters = ["%", "/"];

export function registerCommandCompletion(
  clientOf: () => Promise<FrontendClient>,
  log: (message: string) => void,
): vscode.Disposable {
  return vscode.languages.registerCompletionItemProvider(
    { language: "markdown", scheme: "untitled" },
    {
      async provideCompletionItems(document, position): Promise<vscode.CompletionItem[]> {
        if (!isMaieuticsNotebook(document.uri)) return [];

        const wordRange = document.getWordRangeAtPosition(
          position,
          /[%\/][^\s]*/,
        );
        const text = document.getText();
        const cursor = document.offsetAt(position);
        try {
          const client = await clientOf();
          const matches = await client.complete(text, cursor);
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
        } catch (error) {
          // Completion is a convenience: degrade silently.
          log(`completion failed: ${error}`);
          return [];
        }
      },
    },
    ...TriggerCharacters,
  );
}

function isMaieuticsNotebook(uri: vscode.Uri): boolean {
  // Cell documents live in the notebook scheme and their path ends with the
  // notebook's file name; the selector matches the extension's notebook type
  // by file extension, which is the same contract the serializer registers.
  return uri.scheme === "vscode-notebook-cell" && uri.path.endsWith(".maieuticsnb");
}
