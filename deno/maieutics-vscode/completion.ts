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

// Markdown disables quick suggestions by default. Spaces must trigger the
// command's next argument explicitly, without changing all Markdown editors.
const TriggerCharacters = ["%", "/", " "];

export function registerCommandCompletion(
  clientOf: () => Promise<FrontendClient>,
  log: (message: string) => void,
): vscode.Disposable {
  const gate = createCompletionGate(
    (text, cursor, signal) => clientOf().then((client) => client.complete(text, cursor, signal)),
    log,
  );
  const registration = vscode.languages.registerCompletionItemProvider(
    // Cell documents of this extension's notebook type. Notebook-type scoping
    // is the selector that actually fires for notebook cells: a plain
    // `{ scheme: "untitled" }` selector never matches cell documents, which
    // carry the vscode-notebook-cell scheme. Scheme stays unset so stored and
    // untitled notebooks both match.
    { language: "markdown", notebookType: NotebookType },
    {
      async provideCompletionItems(document, position): Promise<vscode.CompletionItem[]> {
        const text = document.getText();
        // A space in prose must not start the kernel just to ask for commands.
        if (!/^\s*[%/]/.test(text) && text.trim().length > 0) {
          gate.cancel();
          return [];
        }
        const completion = await gate(text, document.offsetAt(position));
        if (completion === undefined) return [];
        const range = new vscode.Range(
          document.positionAt(completion.tokenStart),
          document.positionAt(completion.tokenEnd),
        );
        const slash = text[completion.tokenStart] === "/";
        return completion.matches.map((match) => {
          const item = new vscode.CompletionItem(
            match,
            vscode.CompletionItemKind.Keyword,
          );
          item.range = range;
          // Slash discovery inserts the canonical % command. VS Code filters
          // against what was typed, so its filter must keep the slash leader.
          if (slash && match.startsWith("%")) {
            item.filterText = `/${match.slice(1)}`;
          }

          return item;
        });
      },
    },
    ...TriggerCharacters,
  );
  return {
    dispose: () => {
      gate.cancel();
      registration.dispose();
    },
  };
}
