/// <reference lib="dom" />
/// <reference lib="dom.iterable" />
// Browser-only build entry. The extension host imports only its own wire contract.
import { ConversationError, ConversationRuntime, type ConversationSubmission, type ConversationViewState, mountConversationComposer, mountConversationHistory } from "../../maieutics-ui/mod.ts";
import type { ViewAddress, ViewEvent, ViewFrame, ViewPage, ViewRequest, ViewResponse } from "../conversationProtocol.ts";

declare function acquireVsCodeApi(): { postMessage(value: unknown): void; setState(value: unknown): void };
const vscode = acquireVsCodeApi();
const config = JSON.parse(document.getElementById("configuration")!.textContent!) as ViewAddress;
vscode.setState({ sessionId: config.sessionId, viewId: config.viewId });
const owner = new AbortController();
let nextId = 0;
const pending = new Map<number, { resolve(value: unknown): void; reject(error: unknown): void }>();
function send(method: ViewRequest["method"], params?: Record<string, unknown>): number {
  const id = ++nextId;
  vscode.postMessage({ ...config, id, method, params } satisfies ViewRequest);
  return id;
}
function request<T>(method: ViewRequest["method"], params?: Record<string, unknown>, caller?: AbortSignal): Promise<T> {
  const signal = caller ? AbortSignal.any([owner.signal, caller]) : owner.signal;
  signal.throwIfAborted();
  if (pending.size >= 64) return Promise.reject(new ConversationError("request_capacity"));
  return new Promise<T>((resolve, reject) => {
    const id = nextId + 1;
    const finish = (value: unknown, error?: unknown) => {
      if (!pending.delete(id)) return;
      clearTimeout(timer);
      signal.removeEventListener("abort", abort);
      if (error !== undefined) reject(error);
      else resolve(value as T);
    };
    const abort = () => {
      send("abort", { requestId: id });
      finish(undefined, signal.reason);
    };
    const timer = setTimeout(() => {
      send("abort", { requestId: id });
      finish(undefined, new Error(method === "submit" ? "无法确认提交结果，请检查历史后再决定是否重发。" : "请求超时，请重新打开会话。"));
    }, 15_000);
    pending.set(id, { resolve: (value) => finish(value), reject: (error) => finish(undefined, error) });
    signal.addEventListener("abort", abort, { once: true });
    send(method, params);
  });
}

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
  const state = frame.activeRunId ? "正在运行" : "已连接";
  if (status.textContent !== state) status.textContent = state;
  cancel.disabled = !frame.activeRunId;
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
      title: "对话历史",
      host: { writeText: (text) => request("copy", { text }) },
    });
    composer = mountConversationComposer(element("composer"), { view, onSubmit: (submission) => void submit(submission).catch(fail) });
  } else {
    if (!runtime) throw new Error("变更流缺少初始快照。");
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
      notice.textContent = answer.markdown ?? "命令已完成";
      if (answer.sessionId && answer.sessionId !== config.sessionId) notice.textContent += "\n已切换当前会话；此视图仍显示原会话。";
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
    const target = pending.get(message.id);
    if (message.error) {
      const error = new ConversationError(message.error.code);
      error.message = message.error.message;
      target?.reject(error);
    } else target?.resolve(message.result);
  } else if (message.event === "draft" && typeof message.value === "string") {
    draft = message.value;
    view?.setDraft(draft);
  } else if (message.event === "failure") {
    status.textContent = "连接已中断";
    fail((message.value as { message: string }).message);
  } else if (message.event === "frame") {
    if (++queuedFrames > 256) {
      fail("视图变更积压；请重新打开会话。");
      owner.abort();
      return;
    }
    frameQueue = frameQueue.then(() => apply(message.value as ViewFrame)).catch(fail).finally(() => queuedFrames--);
  }
}, { signal: owner.signal });
globalThis.addEventListener("pagehide", () => {
  owner.abort();
  clear();
}, { once: true });
void request("ready").catch(fail);
