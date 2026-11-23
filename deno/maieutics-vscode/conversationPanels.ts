import * as vscode from "vscode";
import { randomUUID } from "node:crypto";
import type { FrontendClient } from "./client.ts";
import { FrontendError } from "./protocol.ts";
import {
  readViewRequest,
  type ViewAddress,
  type ViewEvent,
  type ViewRequest,
  type ViewResponse,
} from "./conversationProtocol.ts";
import { presentFrontendInput } from "./inputPresentation.ts";
import { waitForStartup } from "./startupWait.ts";
import { executeIdeMethod } from "./controller.ts";
import { extractIdeRequest } from "./ide.ts";

import { ConversationPresentation } from "./conversationPresentation.ts";

const PanelType = "maieutics.conversation";

/** Editor-area host; browser code receives only addressed capabilities, never kernel credentials. */
export class ConversationPanels implements vscode.Disposable {
  private readonly panels = new Map<string, ConversationPanel>();
  private readonly closing = new Set<Promise<void>>();
  private readonly serializer: vscode.Disposable;
  private readonly owner = new AbortController();
  private closed = false;
  constructor(
    private readonly root: vscode.Uri,
    private readonly clientOf: () => Promise<FrontendClient>,
    private readonly log: (message: string) => void,
  ) {
    this.serializer = vscode.window.registerWebviewPanelSerializer(PanelType, {
      deserializeWebviewPanel: (panel, state: unknown) => {
        const saved = state as { sessionId?: unknown } | undefined;
        if (typeof saved?.sessionId !== "string" || !/^[a-f0-9]{32}$/.test(saved.sessionId)) {
          panel.dispose();
          return Promise.resolve();
        }
        if (this.panels.has(saved.sessionId)) {
          panel.dispose();
          return Promise.resolve();
        }
        this.attach(panel, saved.sessionId);
        return Promise.resolve();
      },
    });
  }

  async open(sessionId: string): Promise<void> {
    if (this.closed) return;
    if (!/^[a-f0-9]{32}$/.test(sessionId)) throw new Error("Invalid session id");
    const existing = this.panels.get(sessionId);
    if (existing) {
      existing.panel.reveal();
      return;
    }
    const client = await waitForStartup(this.clientOf(), this.owner.signal);
    if ((await client.capabilities(this.owner.signal)).conversationView?.version !== 1) {
      throw new Error("Update the Maieutics executable to use conversation views");
    }
    if (this.closed) return;
    const competing = this.panels.get(sessionId);
    if (competing) {
      competing.panel.reveal();
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      PanelType,
      "Maieutics",
      vscode.ViewColumn.Active,
      { enableScripts: true },
    );
    this.attach(panel, sessionId);
  }

  private attach(panel: vscode.WebviewPanel, sessionId: string): void {
    const view = new ConversationPanel(
      panel,
      this.root,
      sessionId,
      this.clientOf,
      this.log,
      (task) => this.track(task),
    );
    this.panels.set(sessionId, view);
    panel.onDidDispose(() => {
      if (this.panels.get(sessionId) === view) this.panels.delete(sessionId);
    });
  }
  private track(task: Promise<void>): void {
    const observed = task.catch((error) =>
      this.log(`Conversation cleanup failed: ${String(error)}`)
    ).finally(() => this.closing.delete(observed));
    this.closing.add(observed);
  }
  dispose(): void {
    if (this.closed) return;
    this.closed = true;
    this.owner.abort();
    this.serializer.dispose();
    for (const view of this.panels.values()) view.panel.dispose();
    this.panels.clear();
  }
  async settled(): Promise<void> {
    await Promise.all(this.closing);
  }
}

/** One physical webview and its cancellable bridge; also exercised by real-editor conformance. */
export class ConversationPanel {
  private address: ViewAddress;
  private owner = new AbortController();
  private readonly tasks = new Set<Promise<void>>();
  private readonly requests = new Map<number, AbortController>();
  private readonly listeners: vscode.Disposable[] = [];
  private epoch = "";
  private activeRunId: string | undefined;
  private draft = "";
  private presentation: ConversationPresentation | undefined;
  private ready = false;
  private submitting = false;
  private readonly viewId = randomUUID();
  private lastRequestId = 0;

  constructor(
    readonly panel: vscode.WebviewPanel,
    private readonly root: vscode.Uri,
    private readonly sessionId: string,
    private readonly clientOf: () => Promise<FrontendClient>,
    private readonly log: (message: string) => void,
    private readonly closing: (task: Promise<void>) => void,
    private readonly decorateHtml: (html: string) => string = (html) => html,
  ) {
    this.address = this.newAddress();
    panel.webview.options = {
      enableScripts: true,
      localResourceRoots: [vscode.Uri.joinPath(root, "media")],
    };
    this.listeners.push(panel.webview.onDidReceiveMessage((value) => {
      const request = readViewRequest(value, this.address);
      if (request) this.dispatch(request);
    }));
    this.listeners.push(panel.onDidChangeViewState(() => {
      if (!panel.visible) this.stop();
      else if (this.owner.signal.aborted) {
        this.owner = new AbortController();
        this.address = this.newAddress();
        this.ready = false;
        this.render();
      }
    }));
    this.listeners.push(panel.onDidDispose(() => {
      this.stop();
      for (const listener of this.listeners) listener.dispose();
    }));
    this.render();
  }
  private newAddress(): ViewAddress {
    this.lastRequestId = 0;
    return { version: 1, sessionId: this.sessionId, viewId: this.viewId, generation: randomUUID() };
  }
  private stop(): void {
    this.owner.abort();
    if (this.presentation) {
      this.closing(this.presentation.dispose());
      this.presentation = undefined;
    }
    for (const controller of this.requests.values()) controller.abort();
    this.requests.clear();
    this.closing(Promise.all([...this.tasks]).then(() => {}));
  }
  private track(task: Promise<void>, address: ViewAddress): void {
    const observed = task.catch((error) => {
      if (address.generation !== this.address.generation || this.owner.signal.aborted) return;
      this.log(
        `Conversation operation failed: ${
          error instanceof FrontendError ? error.code : "view_error"
        }`,
      );
      this.trackFailure(this.post({
        ...address,
        event: "failure",
        value: { message: "连接已中断。重新打开此会话以恢复；运行会继续。" },
      }));
    }).finally(() => this.tasks.delete(observed));
    this.tasks.add(observed);
  }
  private dispatch(request: ViewRequest): void {
    const address = this.address;
    if (this.owner.signal.aborted) return;
    if (request.id <= this.lastRequestId) return;
    this.lastRequestId = request.id;
    if (request.method === "reload") {
      this.stop();
      this.owner = new AbortController();
      this.address = this.newAddress();
      this.ready = false;
      this.render();
      return;
    }
    if (request.method === "abort") {
      this.requests.get(Number(request.params?.requestId))?.abort();
      return;
    }
    if (this.requests.size >= 64) {
      this.trackFailure(
        this.post({
          ...address,
          id: request.id,
          error: { code: "request_capacity", message: "Too many pending view requests" },
        }),
      );
      return;
    }
    const controller = new AbortController();
    const signal = AbortSignal.any([this.owner.signal, controller.signal]);
    this.requests.set(request.id, controller);
    const task = (async () => {
      try {
        const result = await this.execute(request, signal);
        if (!signal.aborted) await this.post({ ...address, id: request.id, result });
      } catch (error) {
        if (!signal.aborted) {
          await this.post({
            ...address,
            id: request.id,
            error: {
              code: error instanceof FrontendError ? error.code : "view_error",
              message: error instanceof Error ? error.message : "Conversation operation failed",
            },
          });
        }
      } finally {
        if (this.requests.get(request.id) === controller) this.requests.delete(request.id);
      }
    })();
    this.track(task, address);
  }
  private trackFailure(post: Promise<void>): void {
    const observed = post.catch(() => this.log("Conversation diagnostic delivery failed"))
      .finally(() => this.tasks.delete(observed));
    this.tasks.add(observed);
  }
  private async execute(request: ViewRequest, signal: AbortSignal): Promise<unknown> {
    const params = request.params ?? {};
    if (request.method === "presentation") {
      await this.presentation?.receive(params.message);
      return null;
    }
    if (request.method === "draft") {
      this.draft = text(params.text, 64_000);
      return null;
    }
    if (request.method === "copy") {
      await vscode.env.clipboard.writeText(text(params.text, 8_000_000));
      return null;
    }
    const client = await waitForStartup(this.clientOf(), signal);
    signal.throwIfAborted();
    if (request.method === "ready") {
      if (this.ready) return null;
      this.ready = true;
      const session = await client.sessionInfo(this.sessionId, signal);
      signal.throwIfAborted();
      this.panel.title = session.title || "Maieutics";
      await this.post({ ...this.address, event: "draft", value: this.draft });
      const nativeReady = Promise.withResolvers<void>();
      const native = this.interactions(client, signal, nativeReady.resolve).then(
        () => nativeReady.reject(new Error("Native interaction channel closed")),
        (error) => {
          nativeReady.reject(error);
          throw error;
        },
      );
      this.track(native, this.address);
      // The hello follows the server's IDE capability announcement. Do not expose a
      // submit-capable browser before the first turn can use that native channel.
      await waitForStartup(nativeReady.promise, signal);
      this.track(this.pump(client, signal), this.address);
      return null;
    }
    if (request.method === "page") {
      const offset = params.offset;
      if (offset !== undefined && (!Number.isSafeInteger(offset) || Number(offset) < 0)) {
        throw new Error("Invalid page offset");
      }
      const limit = Number(params.limit);
      if (!Number.isInteger(limit) || limit < 1 || limit > 80) {
        throw new Error("Invalid page limit");
      }
      return await client.conversationPage(
        this.sessionId,
        this.epoch,
        offset as number | undefined,
        limit,
        signal,
      );
    }
    if (request.method === "content") {
      return await client.conversationContent(
        this.sessionId,
        this.epoch,
        text(params.id, 256),
        text(params.revision, 24),
        signal,
      );
    }
    if (request.method === "cancel") {
      if (this.activeRunId) await client.cancelRun(this.activeRunId, signal);
      return null;
    }
    if (request.method === "submit") {
      if (this.submitting) throw new Error("A submission is already pending");
      this.submitting = true;
      try {
        return await client.submitTurn(this.sessionId, text(params.text, 64_000), signal);
      } finally {
        this.submitting = false;
      }
    }
    throw new Error("Unsupported conversation request");
  }
  private async pump(client: FrontendClient, signal: AbortSignal): Promise<void> {
    const address = this.address;
    for await (const frame of client.conversationEvents(this.sessionId, signal)) {
      if (frame.type === "view.snapshot") this.epoch = frame.page.epoch;
      this.activeRunId = frame.activeRunId;
      await this.post({ ...address, event: "frame", value: frame });
    }
  }
  private async interactions(
    client: FrontendClient,
    signal: AbortSignal,
    ready: () => void,
  ): Promise<void> {
    const pending = new Set<Promise<void>>();
    try {
      for await (const frame of client.events(this.sessionId, { signal })) {
        if (frame.type === "hello") ready();
        if (frame.type === "session.updated" && frame.session?.id === this.sessionId) {
          this.panel.title = frame.session.title || "Maieutics";
        }
        if (pending.size >= 64) await Promise.race(pending);
        const ide = extractIdeRequest(frame);
        const operation = frame.type === "input.request"
          ? presentFrontendInput(frame, client, signal)
          : ide
          ? client.ideRequests.respond(ide, {
            execute: executeIdeMethod,
            submit: (id, answer, cancellation) => client.submitIdeAnswer(id, answer, cancellation),
            log: this.log,
          }, signal)
          : undefined;
        if (operation) {
          const task = operation.then(() => {}).catch((error) => {
            if (!signal.aborted && !(error instanceof FrontendError && error.status === 404)) {
              this.log(`Conversation input failed: ${String(error)}`);
            }
          }).finally(() => pending.delete(task));
          pending.add(task);
        }
      }
    } finally {
      await Promise.all(pending);
    }
  }
  private async post(message: ViewResponse | ViewEvent): Promise<void> {
    if (message.generation !== this.address.generation || this.owner.signal.aborted) return;
    await this.panel.webview.postMessage(message);
  }
  private render(): void {
    const address = this.address;
    const presentation = new ConversationPresentation({
      sessionId: this.sessionId,
      signal: this.owner.signal,
      client: this.clientOf,
      post: (value) => this.post({ ...address, event: "presentation", value }),
      externalUri: async (url) =>
        (await vscode.env.asExternalUri(vscode.Uri.parse(url))).toString(),
      writeClipboard: async (text) => await vscode.env.clipboard.writeText(text),
      log: this.log,
    });
    this.presentation = presentation;
    this.track(
      (async () => {
        let connectionSource = "'none'";
        try {
          connectionSource = await presentation.connectionSource();
        } catch {
          if (!this.owner.signal.aborted) {
            this.log(
              "Conversation forms are unavailable; retaining Markdown and readonly previews.",
            );
          }
        }
        if (this.owner.signal.aborted || address.generation !== this.address.generation) return;
        this.renderDocument(connectionSource);
      })(),
      address,
    );
  }
  private renderDocument(connectionSource: string): void {
    const webview = this.panel.webview;
    const uri = (name: string) =>
      webview.asWebviewUri(vscode.Uri.joinPath(this.root, "media", name)).toString();
    const nonce = randomUUID();
    const config = JSON.stringify(this.address).replaceAll("<", "\\u003c");
    this.panel.webview.html = this.decorateHtml(
      `<!doctype html><html lang="zh-CN"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${nonce}' 'unsafe-eval'; style-src ${webview.cspSource} 'unsafe-inline'; font-src ${webview.cspSource}; img-src ${webview.cspSource} data:; connect-src ${connectionSource};">
<link rel="stylesheet" href="${uri("conversation.css")}"><link rel="stylesheet" href="${
        uri("conversationTheme.css")
      }"></head>
<body><main id="app" aria-label="Maieutics 会话"><header><span id="status" role="status">正在连接…</span><button id="reload" type="button">重新连接</button><button id="cancel" type="button" disabled>停止</button></header><div id="history"></div><div id="composer"></div><p id="notice" role="alert"></p></main>
<script id="configuration" nonce="${nonce}" type="application/json">${config}</script><script type="module" nonce="${nonce}" src="${
        uri("conversation.js")
      }"></script></body></html>`,
    );
  }
}

function text(value: unknown, limit: number): string {
  if (typeof value !== "string" || value.length > limit) {
    throw new Error("Invalid or oversized conversation text");
  }
  return value;
}
