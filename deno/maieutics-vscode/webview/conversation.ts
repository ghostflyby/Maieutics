import messages from "../locales/en.json" with { type: "json" };
import { localize } from "./localization.ts";
/// <reference lib="dom" />
/// <reference lib="dom.iterable" />
// Browser-only build entry. The extension host imports only its own wire contract.
import { ConversationRuntime, type ConversationSubmission, type ConversationViewState, mountConversationComposer, mountConversationHistory } from "../../maieutics-ui/mod.ts";
import type { ViewAddress, ViewEvent, ViewFrame, ViewPage, ViewRequest, ViewResponse } from "../conversationProtocol.ts";

import { createConversationSurfaces } from "./presentation.ts";

import { ViewRequests } from "./requests.ts";

declare function acquireVsCodeApi(): { postMessage(value: unknown): void; setState(value: unknown): void };
const vscode = acquireVsCodeApi();
const config = JSON.parse(document.getElementById("configuration")!.textContent!) as ViewAddress;
vscode.setState({ sessionId: config.sessionId, viewId: config.viewId });
const owner = new AbortController();
const requests = new ViewRequests(config, (value) => vscode.postMessage(value), owner.signal, undefined, localize);
const send = (method: ViewRequest["method"], params?: Record<string, unknown>) => requests.send(method, params);
const request = <T>(method: ViewRequest["method"], params?: Record<string, unknown>, caller?: AbortSignal): Promise<T> => requests.request<T>(method, params, caller);

const outputTheme = document.querySelector<HTMLLinkElement>("#output-theme");
if (!outputTheme) throw new Error(localize(messages.missingStylesheet));
const surfaces = createConversationSurfaces(
  (message) => send("presentation", { message }),
  outputTheme.href,
  localize,
);

const element = (id: string) => document.getElementById(id)!;
const status = element("status");
const notice = element("notice");
const cancel = element("cancel") as HTMLButtonElement;
let runtime: ConversationRuntime | undefined;
let view: ConversationViewState | undefined;
let draft = "";
let stopDraft: (() => void) | undefined;
let history: ReturnType<typeof mountConversationHistory> | undefined;
let composer: ReturnType<typeof mountConversationComposer> | undefined;
let sending = false;
let frameQueue = Promise.resolve();
let queuedFrames = 0;

function fail(error: unknown): void {
  notice.textContent = error instanceof Error ? error.message : String(error);
}
function clear(): void {
  stopDraft?.();
  history?.unmount();
  composer?.unmount();
  view?.dispose();
  const old = runtime;
  runtime = undefined;
  view = undefined;
  if (old) void old.dispose().catch(fail);
}
async function apply(frame: ViewFrame): Promise<void> {
  if (owner.signal.aborted) return;
  const state = frame.activeRunId ? localize(messages.running) : localize(messages.connected);
  if (status.textContent !== state) status.textContent = state;
  cancel.disabled = !frame.activeRunId;
  cancel.hidden = !frame.activeRunId;
  if (frame.type === "view.snapshot") {
    clear();
    let first: ViewPage | undefined = frame.page;
    const current = new ConversationRuntime(config.sessionId, {
      readPage: (query, signal) => {
        if (first) {
          const page = first;
          first = undefined;
          return Promise.resolve(page);
        }
        return request("page", { offset: query.offset, limit: query.limit }, signal);
      },
      readContent: (id, revision, signal) => request("content", { id, revision }, signal),
    });
    runtime = current;
    await current.loadPage(undefined, owner.signal);
    owner.signal.throwIfAborted();
    view = current.attachView(config.viewId);
    view.setDraft(draft);
    const localView = view;
    stopDraft = view.subscribe(() => {
      if (draft !== localView.draft) {
        draft = localView.draft;
        send("draft", { text: draft });
      }
    });
    history = mountConversationHistory(element("history"), {
      view,
      localize,
      host: { writeText: (text) => request("copy", { text }), mountBlock: surfaces.mountBlock },
    });
    composer = mountConversationComposer(element("composer"), { view, localize, onSubmit: (submission) => void submit(submission).catch(fail) });
  } else {
    if (!runtime) throw new Error(localize(messages.missingSnapshot));
    runtime.apply(frame.change);
  }
}
async function submit(submission: ConversationSubmission): Promise<void> {
  if (sending || !runtime) return;
  sending = true;
  notice.textContent = "";
  try {
    const answer = await request<{ kind: "turn" | "command"; markdown?: string; sessionId?: string }>("submit", { text: submission.text });
    runtime?.confirmSubmission(submission.viewId, submission.revision);
    if (answer.kind === "command") {
      notice.textContent = answer.markdown ?? localize(messages.commandComplete);
      if (answer.sessionId && answer.sessionId !== config.sessionId) notice.textContent += "\n" + localize(messages.sessionSwitched);
    }
  } finally {
    sending = false;
  }
}
cancel.addEventListener("click", () => {
  void request("cancel").catch(fail);
}, { signal: owner.signal });
element("reload").addEventListener("click", () => send("reload"), { signal: owner.signal });
globalThis.addEventListener("message", (event: MessageEvent<ViewResponse | ViewEvent>) => {
  const message = event.data;
  if (!message || message.version !== 1 || message.viewId !== config.viewId || message.sessionId !== config.sessionId || message.generation !== config.generation) return;
  if ("id" in message) {
    requests.receive(message);
  } else if (message.event === "presentation") {
    void surfaces.receive(message.value).catch(fail);
  } else if (message.event === "draft" && typeof message.value === "string") {
    draft = message.value;
    view?.setDraft(draft);
  } else if (message.event === "failure") {
    status.textContent = localize(messages.interrupted);
    fail((message.value as { message: string }).message);
  } else if (message.event === "frame") {
    if (++queuedFrames > 256) {
      fail(localize(messages.frameBacklog));
      owner.abort();
      return;
    }
    frameQueue = frameQueue.then(() => apply(message.value as ViewFrame)).catch(fail).finally(() => queuedFrames--);
  }
}, { signal: owner.signal });
globalThis.addEventListener("pagehide", () => {
  clear();
  void surfaces.dispose().catch(fail);
  owner.abort();
}, { once: true });
void request("ready").catch(fail);
