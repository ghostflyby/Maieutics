import * as vscode from "vscode";
import { ConversationPanel } from "../../conversationPanels.ts";
import type { FrontendClient } from "../../client.ts";
import type { ViewEntry, ViewFrame } from "../../conversationProtocol.ts";
import hostEnglish from "../../locales/en.json" with { type: "json" };
import uiEnglish from "../../../maieutics-ui/locales/en.json" with { type: "json" };

async function expectedLocale(extension: vscode.Extension<unknown>) {
  const locale = vscode.workspace.getConfiguration("maieutics").get<string>("testLocale");
  if (!locale) return undefined;
  if (vscode.env.language.toLowerCase() !== locale) {
    throw new Error(`Requested VS Code locale ${locale}, received ${vscode.env.language}`);
  }
  const filename = Intl.getCanonicalLocales(locale)[0] + ".json";
  const catalog = async (path: string) =>
    JSON.parse(new TextDecoder().decode(
      await vscode.workspace.fs.readFile(
        vscode.Uri.joinPath(extension.extensionUri, path, filename),
      ),
    )) as Record<string, string>;
  const [host, ui] = await Promise.all([catalog("locales"), catalog("../maieutics-ui/locales")]);
  if (
    !host.reload || host.reload === hostEnglish.reload ||
    !ui.placeholder || ui.placeholder === uiEnglish.placeholder
  ) {
    throw new Error(`Locale ${locale} must supply distinct host and composer translations`);
  }
  return { locale, reload: host.reload, placeholder: ui.placeholder };
}

/** Uses the shipped browser bundle and real editor bridge; the source controls every interleaving. */
export async function testConversationPanelAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  if (!extension) throw new Error("Maieutics extension missing");
  const locale = await expectedLocale(extension);
  // The filtered probe may run before workbench startup activates this extension.
  if (locale) await extension.activate();
  const localeJson = JSON.stringify(locale ?? null).replaceAll("<", "\\u003c");
  const sessionId = "d".repeat(32);
  const entries: ViewEntry[] = Array.from({ length: 10_000 }, (_, ordinal) => ({
    id: `entry-${ordinal}`,
    ordinal,
    interactionId: `turn-${ordinal}`,
    kind: "message",
    contentRevision: "0",
    preview: `正文 ${ordinal}`,
    state: "sealed",
  }));
  let pages = 0;
  let viewStarted = false;
  let cancelled = 0;
  let submitted = "";
  let verdictResolve: (() => void) | undefined;
  const closed = Promise.withResolvers<void>();
  const accepted = Promise.withResolvers<{ kind: "turn"; runId: string }>();
  const nativeStarted = Promise.withResolvers<void>();
  const nativeHello = Promise.withResolvers<void>();
  const updates: ViewFrame[] = [];
  let wake: (() => void) | undefined;
  const fake = {
    sessionInfo: () =>
      Promise.resolve({ id: sessionId, title: "Conversation bridge conformance", turns: 5000 }),
    async *conversationEvents(_id: string, signal: AbortSignal) {
      viewStarted = true;
      const abort = () => {
        wake?.();
        closed.resolve();
      };
      signal.addEventListener("abort", abort, { once: true });
      try {
        yield {
          type: "view.snapshot",
          page: {
            sessionId,
            epoch: "test",
            revision: "0",
            total: entries.length,
            entries: entries.slice(-80),
          },
          activeRunId: "run",
        } as ViewFrame;
        while (!signal.aborted) {
          const frame = updates.shift();
          if (frame) yield frame;
          else await new Promise<void>((resolve) => wake = resolve);
        }
      } finally {
        signal.removeEventListener("abort", abort);
        closed.resolve();
      }
    },
    async *events(_id: string, options: { signal: AbortSignal }) {
      nativeStarted.resolve();
      await nativeHello.promise;
      yield { type: "hello" };
      if (!options.signal.aborted) {
        await new Promise<void>((resolve) =>
          options.signal.addEventListener("abort", () => resolve(), { once: true })
        );
      }
    },
    conversationPage: (id: string, epoch: string, offset: number | undefined, limit: number) => {
      if (id !== sessionId || epoch !== "test" || limit !== 80) {
        throw new Error("Bridge lost page addressing");
      }
      if (offset !== undefined) pages++;
      const start = offset ?? Math.max(0, entries.length - limit);
      return Promise.resolve({
        sessionId,
        epoch,
        revision: "0",
        total: entries.length,
        entries: entries.slice(start, start + limit),
      });
    },
    conversationContent: (viewedSession: string, epoch: string, id: string, revision: string) => {
      if (viewedSession !== sessionId || epoch !== "test") {
        throw new Error("Bridge lost content addressing");
      }
      return Promise.resolve({
        entryId: id,
        revision,
        text: revision === "1" ? "已更新的历史" : `已加载 ${id}`,
      });
    },
    submitTurn: async (_id: string, text: string) => {
      submitted = text;
      await panel.webview.postMessage({ testSignal: "submission-pending" });
      return await accepted.promise;
    },
    cancelRun: () => {
      cancelled++;
      if (pages > 0) verdictResolve?.();
      return Promise.resolve();
    },
  } as unknown as FrontendClient;
  const panel = vscode.window.createWebviewPanel(
    "maieutics.conversation-test",
    "Conversation conformance",
    vscode.ViewColumn.Active,
    { enableScripts: true },
  );
  const cleanup: Promise<void>[] = [];
  let stage = "waiting for browser readiness";
  let timer: ReturnType<typeof setTimeout> | undefined;
  let listener: vscode.Disposable | undefined;
  try {
    const verdict = new Promise<void>((resolve, reject) => {
      verdictResolve = resolve;
      timer = setTimeout(
        () => reject(new Error(`Conversation browser conformance exceeded its budget: ${stage}`)),
        30_000,
      );
      listener = panel.webview.onDidReceiveMessage((message) => {
        stage = message.method ?? stage;
        if (message.method === "draft" && message.params?.text === "后续草稿") {
          if (submitted !== "提交这条消息") {
            reject(new Error("Submission did not reach the addressed host"));
            return;
          }
          accepted.resolve({ kind: "turn", runId: "run" });
          updates.push({
            type: "view.change",
            activeRunId: "run",
            change: {
              epoch: "test",
              baseRevision: "0",
              revision: "1",
              total: entries.length,
              operations: [{
                type: "upsert",
                entry: { ...entries[0], contentRevision: "1", preview: "已更新的历史" },
              }],
            },
          });
          wake?.();
        } else if (
          message.method === "draft" && String(message.params?.text).startsWith("TEST_FAILED:")
        ) reject(new Error(message.params.text));
      });
    });
    const decorate = (html: string) => {
      const nonce = /nonce="([^"]+)"/.exec(html)?.[1];
      if (!nonce) throw new Error("Production webview is missing its CSP nonce");
      return html.replace(
        "</body>",
        `<script nonce="${nonce}">
      const assert = (ok, reason) => { if (!ok) throw new Error(reason); };
      const wait = (predicate, name) => new Promise((resolve, reject) => {
        const finish = error => { observer.disconnect(); clearTimeout(timer); error ? reject(error) : resolve(); };
        const check = () => { if (predicate()) finish(); };
        const observer = new MutationObserver(check);
        const timer = setTimeout(() => finish(new Error(name + ': ' + JSON.stringify({
          status:document.getElementById('status')?.textContent, notice:document.getElementById('notice')?.textContent,
          viewport:document.querySelector('.mui-viewport')?.getBoundingClientRect().toJSON(),
          top:document.querySelector('.mui-viewport')?.scrollTop,
          entries:Array.from(document.querySelectorAll('[data-entry-id]')).map(node=>node.dataset.entryId),
          errors:Array.from(document.querySelectorAll('.mui-error')).map(node=>node.textContent)
        }))), 20000);
        observer.observe(document.body, {childList:true,subtree:true,attributes:true,characterData:true}); check();
      });
      const message = predicate => new Promise((resolve, reject) => {
        const finish = error => { window.removeEventListener('message', accept); clearTimeout(timer); error ? reject(error) : resolve(); };
        const accept = event => { if (predicate(event.data)) finish(); };
        const timer = setTimeout(() => finish(new Error('missing host acknowledgement')), 20000);
        window.addEventListener('message', accept);
      });
      (async () => {
        try {
          await wait(() => {
            const stop = document.getElementById('cancel');
            return stop && !stop.hidden && !stop.disabled && document.querySelector('[data-entry-id] .mui-text');
          }, 'webview did not apply the addressed snapshot and read its content');
          const locale = ${localeJson};
          if (locale) {
            assert(document.documentElement.lang.toLowerCase() === locale.locale, 'The selected locale did not reach the document');
            assert(document.getElementById('reload').textContent === locale.reload, 'VS Code did not translate the host toolbar label');
            assert(document.querySelector('textarea').placeholder === locale.placeholder, 'The injected catalog did not translate the shared composer');
          }
          assert(Object.keys(JSON.parse(document.getElementById('configuration').textContent)).sort().join(',') === 'generation,sessionId,version,viewId', 'configuration exposed more than the view address');
          const viewport = document.querySelector('.mui-viewport'); viewport.scrollTop = 0; viewport.dispatchEvent(new Event('scroll'));
          await wait(
            () => document.querySelector('[data-entry-id="entry-0"] .mui-text')?.textContent === '已加载 entry-0',
            'dragged page did not return its addressed content',
          );
          const editor = document.querySelector('textarea'); editor.value = '提交这条消息'; editor.dispatchEvent(new Event('input', {bubbles:true}));
          const submitted = message(value => value.testSignal === 'submission-pending');
          const acknowledged = message(value => value.result?.kind === 'turn');
          document.querySelector('form').requestSubmit();
          await submitted;
          editor.value = '后续草稿'; editor.dispatchEvent(new Event('input', {bubbles:true}));
          await acknowledged;
          await wait(() => document.querySelector('[data-entry-id="entry-0"]')?.textContent.includes('已更新的历史'), 'projection change did not apply');
          assert(editor.value === '后续草稿', 'submission acknowledgement erased a newer draft');
          document.getElementById('cancel').click();

        } catch (error) {
          const editor = document.querySelector('textarea');
          if (editor) { editor.value = 'TEST_FAILED:' + String(error.stack || error); editor.dispatchEvent(new Event('input', {bubbles:true})); }
          else console.error(error);
        }
      })();
    </script></body>`,
      );
    };
    const view = new ConversationPanel(
      panel,
      extension.extensionUri,
      sessionId,
      () => Promise.resolve(fake),
      console.log,
      (task) => cleanup.push(task),
      decorate,
    );
    void view;
    await Promise.race([nativeStarted.promise, verdict]);
    if (viewStarted) {
      throw new Error("Conversation became submit-capable before native interaction readiness");
    }
    nativeHello.resolve();
    await verdict;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    listener?.dispose();
    panel.dispose();
    nativeHello.resolve();
    if (viewStarted) await closed.promise;
    await Promise.all(cleanup);
  }
  if (cancelled !== 1) throw new Error("Disposing the view cancelled the Agent run");
}
