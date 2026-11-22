import * as vscode from "vscode";
import { ConversationPanel } from "../../conversationPanels.ts";
import type { FrontendClient } from "../../client.ts";
import { IdeRequestOwner } from "../../ide.ts";
import { waitForStartup } from "../../startupWait.ts";

/** Hide a real webview while its first-observed editor action is parked. */
export async function testHiddenConversationIdeAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  if (!extension) throw new Error("Maieutics extension missing");
  const deadline = new AbortController();
  const timer = setTimeout(
    () => deadline.abort(new Error("Hidden-view IDE conformance exceeded its budget")),
    20_000,
  );
  const owner = new IdeRequestOwner();
  const projectionStarted = Promise.withResolvers<void>();
  const announce = Promise.withResolvers<void>();
  const actionStarted = Promise.withResolvers<AbortSignal>();
  const actionFinished = Promise.withResolvers<unknown>();
  const observerClosed = Promise.withResolvers<void>();
  const answered = Promise.withResolvers<void>();
  const cleanup: Promise<void>[] = [];
  let executions = 0, answers = 0;
  const sessionId = "e".repeat(32);
  const request = { requestId: "hidden-edit", method: "maieutics/documents/apply", params: {} };
  const abort = (signal: AbortSignal) =>
    new Promise<void>((resolve) => {
      if (signal.aborted) resolve();
      else signal.addEventListener("abort", () => resolve(), { once: true });
    });
  const fake = {
    sessionInfo: () => Promise.resolve({ id: sessionId, turns: 0 }),
    async *conversationEvents(_id: string, signal: AbortSignal) {
      projectionStarted.resolve();
      yield {
        type: "view.snapshot",
        page: { sessionId, epoch: "hidden", revision: "0", total: 0, entries: [] },
      };
      await abort(signal);
    },
    async *events(_id: string, options: { signal: AbortSignal }) {
      yield { type: "hello" };
      await waitForStartup(announce.promise, options.signal);
      yield { type: "ide.request", data: request };
      await abort(options.signal);
    },
    ideRequests: {
      respond: (
        value: typeof request,
        host: Parameters<IdeRequestOwner["respond"]>[1],
        signal: AbortSignal,
      ) => {
        signal.addEventListener("abort", () => observerClosed.resolve(), { once: true });
        return owner.respond(value, {
          ...host,
          execute: (_method, _params, actionSignal) => {
            if (!actionSignal) throw new Error("Missing IDE action lifetime");
            executions++;
            actionStarted.resolve(actionSignal);
            return actionFinished.promise;
          },
        }, signal);
      },
    },
    submitIdeAnswer: (id: string, answer: { value?: unknown }) => {
      if (id !== request.requestId || JSON.stringify(answer.value) !== '{"applied":true}') {
        throw new Error("Lost IDE request answer");
      }
      answers++;
      answered.resolve();
      return Promise.resolve(true);
    },
  } as unknown as FrontendClient;
  const panel = vscode.window.createWebviewPanel(
    "maieutics-hidden-ide-test",
    "Hidden IDE conformance",
    vscode.ViewColumn.Active,
    { enableScripts: true },
  );
  let cover: vscode.WebviewPanel | undefined;
  try {
    new ConversationPanel(
      panel,
      extension.extensionUri,
      sessionId,
      () => Promise.resolve(fake),
      console.log,
      (task) => cleanup.push(task),
    );
    await waitForStartup(projectionStarted.promise, deadline.signal);
    announce.resolve();
    const actionSignal = await waitForStartup(actionStarted.promise, deadline.signal);
    cover = vscode.window.createWebviewPanel(
      "maieutics-cover",
      "Hide observer",
      panel.viewColumn ?? vscode.ViewColumn.Active,
      {},
    );
    await waitForStartup(observerClosed.promise, deadline.signal);
    if (actionSignal.aborted) {
      throw new Error("Hiding a webview cancelled its connection-owned edit");
    }
    actionFinished.resolve({ applied: true });
    await waitForStartup(answered.promise, deadline.signal);
    await owner.respond(request, {
      execute: () => {
        throw new Error("An edit was replayed");
      },
      submit: () => {
        throw new Error("An answer was replayed");
      },
      log() {},
    }, deadline.signal);
    if (executions !== 1 || answers !== 1) {
      throw new Error(`Physical IDE work or answers repeated: ${executions}/${answers}`);
    }
  } finally {
    panel.dispose();
    cover?.dispose();
    announce.resolve();
    actionFinished.resolve(null);
    await owner.dispose();
    await Promise.all(cleanup);
    clearTimeout(timer);
  }
}
