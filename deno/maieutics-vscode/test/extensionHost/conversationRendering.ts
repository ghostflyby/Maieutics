/// <reference lib="deno.window" />
/// <reference lib="es2024.promise" />
import * as vscode from "vscode";
import type { FrontendClient } from "../../client.ts";
import type { ViewContent, ViewEntry, ViewFrame } from "../../conversationProtocol.ts";
import { ConversationPanel } from "../../conversationPanels.ts";

/** Real production webview: the addressed SSR reply and an Actor action cross the host bridge. */
export async function testConversationRenderingAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  if (!extension) throw new Error("Missing Maieutics extension");
  const sessionId = "f".repeat(32);
  const markdown = "# Conversation output";
  const entries: ViewEntry[] = (["message", "display"] as const).map((kind, ordinal) => ({
    id: `rich-${ordinal}`,
    ordinal,
    interactionId: "run",
    kind,
    contentRevision: "0",
    preview: "rich output",
    state: "sealed",
  }));
  const formState = {
    title: "Shared form",
    fields: [{ name: "answer", type: "text" }],
    values: { answer: "initial" },
  };
  const presentations: NonNullable<ViewContent["presentation"]>[] = [
    { version: 1, kind: "assistant" },
    {
      version: 1,
      kind: "mime",
      value: {
        "application/vnd.maieutics.form+json": {
          version: 1,
          modelId: "shared-form",
          state: formState,
        },
      },
    },
  ];
  const done = Promise.withResolvers<void>();
  const themeReplies = new Map<string, ReturnType<typeof Promise.withResolvers<void>>>();
  const themeSettings = vscode.workspace.getConfiguration("workbench");
  const initialTheme = themeSettings.inspect<string>("colorTheme")?.globalValue;
  const rendered = new Set<string>();
  const formClosed = Promise.withResolvers<void>();
  const cleanup: Promise<void>[] = [];
  const pending: unknown[] = [];
  let wake: (() => void) | undefined;
  let formOpened = false, formActions = 0;
  const closed = (signal: AbortSignal) =>
    new Promise<void>((resolve) => {
      if (signal.aborted) resolve();
      else signal.addEventListener("abort", () => resolve(), { once: true });
    });
  const fake = {
    capabilities: () =>
      Promise.resolve({
        rendering: {
          version: 1,
          templateVersion: 1,
          vueVersion: 1,
          assistantVersion: 1,
          progressiveVersion: 1,
        },
        comm: { version: 1 },
      }),
    sessionInfo: () =>
      Promise.resolve({ id: sessionId, title: "Conversation rich rendering", turns: 1 }),
    async *conversationEvents(_id: string, signal: AbortSignal) {
      yield {
        type: "view.snapshot",
        page: { sessionId, epoch: "rich", revision: "0", total: entries.length, entries },
      } as ViewFrame;
      await closed(signal);
    },
    async *events(_id: string, options: { signal: AbortSignal }) {
      yield { type: "hello" };
      await closed(options.signal);
    },
    conversationContent: (_session: string, _epoch: string, id: string, revision: string) =>
      Promise.resolve({
        entryId: id,
        revision,
        text: id === "rich-0" ? markdown : "rich output",
        presentation: presentations[Number(id.slice(5))],
      }),
    renderMarkdown(documentId: string, text: string) {
      if (text !== markdown) throw new Error("SSR received content from a different row");
      return Promise.resolve({
        version: 1,
        documentId,
        html:
          "<div class='maieutics-output vp-doc'><p class='prose-proof'>Addressed SSR reply</p></div>",
        css: ".maieutics-output.vp-doc{font-size:16px;line-height:1.75}",
      });
    },
    commSocket: (_session: string, options: { signal: AbortSignal }) => {
      if (_session !== sessionId) throw new Error("Form borrowed another session");
      formOpened = true;
      pending.push({
        kind: "comm",
        sequence: 1,
        message: {
          kind: 0,
          commId: "shared-form",
          targetName: "maieutics.form",
          data: { state: formState },
          buffers: [],
        },
      });
      const close = () => {
        formClosed.resolve();
        wake?.();
      };
      options.signal.addEventListener("abort", close, { once: true });
      return Promise.resolve({
        send(message: { data: { name: string; payload: { values: { answer: string } } } }) {
          if (message.data.name !== "submit" || message.data.payload.values.answer !== "typed") {
            throw new Error("Form submission lost its structured values");
          }
          formActions++;
          void panel.webview.postMessage({ richFormAccepted: true }).then(undefined, done.reject);
        },
        async *messages() {
          try {
            while (!options.signal.aborted) {
              const next = pending.shift();
              if (next) yield next;
              else await new Promise<void>((resolve) => wake = resolve);
            }
          } finally {
            options.signal.removeEventListener("abort", close);
            formClosed.resolve();
          }
        },
        close,
      });
    },
  } as unknown as FrontendClient;
  // UiBridge consumes an async generator property rather than a factory.
  const comm = fake.commSocket.bind(fake);
  fake.commSocket = (async (...args: Parameters<FrontendClient["commSocket"]>) => {
    const socket = await comm(...args) as unknown as { messages: () => AsyncGenerator<unknown> };
    return { ...socket, messages: socket.messages() };
  }) as FrontendClient["commSocket"];
  const panel = vscode.window.createWebviewPanel(
    "maieutics.conversation-rendering-test",
    "Conversation rendering",
    vscode.ViewColumn.Active,
    { enableScripts: true },
  );
  const timer = setTimeout(
    () => done.reject(new Error("Conversation rendering conformance exceeded its budget")),
    30_000,
  );
  const listener = panel.webview.onDidReceiveMessage((message) => {
    if (message.method === "presentation" && message.params?.message?.type === "applied") {
      rendered.add(message.params.message.engine);
    }
    if (message.method === "draft" && message.params?.text === "rich-rendering-passed") {
      done.resolve();
    }
    if (message.method === "draft") themeReplies.get(message.params?.text)?.resolve();
    if (message.method === "draft" && String(message.params?.text).startsWith("TEST_FAILED:")) {
      done.reject(new Error(message.params.text));
      for (const reply of themeReplies.values()) reply.reject(new Error(message.params.text));
    }
  });
  try {
    new ConversationPanel(
      panel,
      extension.extensionUri,
      sessionId,
      () => Promise.resolve(fake),
      console.log,
      (task) => cleanup.push(task),
      (html) => {
        const nonce = /nonce="([^"]+)"/.exec(html)?.[1];
        if (!nonce) throw new Error("Missing production CSP nonce");
        return html.replace(
          "</body>",
          `<script nonce="${nonce}">
      const assert = (value, reason) => {if(!value)throw new Error(reason)};
      const roots = () => {
        const values=[document.body];
        for(let i=0;i<values.length;i++)for(const node of values[i].querySelectorAll('*'))if(node.shadowRoot)values.push(node.shadowRoot);
        return values;
      };
      const query = selector => roots().flatMap(root=>Array.from(root.querySelectorAll(selector)));
      const wait = (predicate, reason) => new Promise((resolve,reject)=>{
        const observed=new Set();
        const observer=new MutationObserver(check);
        const timer=setTimeout(()=>{observer.disconnect();reject(new Error(reason))},20000);
        function check(){for(const root of roots())if(!observed.has(root)){observed.add(root);observer.observe(root,{subtree:true,childList:true,attributes:true,characterData:true})}if(predicate()){observer.disconnect();clearTimeout(timer);resolve()}}
        check();
      });
      const draft = value => {const editor=document.querySelector('textarea');editor.value=value;editor.dispatchEvent(new Event('input',{bubbles:true}))};
      window.addEventListener('message',async event=>{
        const theme=event.data.richTheme;if(!theme)return;
        try{
          const editor=document.querySelector('textarea'),prose=query('.prose-proof')[0];
          await wait(()=>document.body.classList.contains(theme),'host did not deliver theme '+theme);
          assert(document.querySelector('textarea')===editor && query('.prose-proof')[0]===prose,'theme switch remounted history/editor content');
          assert(getComputedStyle(prose).color===getComputedStyle(editor).color,'rich content missed the live editor theme foreground');
          assert(getComputedStyle(prose).fontSize===getComputedStyle(editor).fontSize,'theme changed prose/editor font sizes independently');
          draft('theme-checked:'+theme);
        }catch(error){draft('TEST_FAILED:'+String(error.stack||error))}
      });
      (async()=>{try{
        await wait(()=>query('.prose-proof').some(node=>node.textContent==='Addressed SSR reply'),'SSR did not cross the production webview bridge');
        await wait(()=>query('fieldset').some(node=>!node.disabled),'form did not attach over the existing binary Actor bridge');
        const field=query('input[name="answer"]')[0];field.value='typed';field.dispatchEvent(new Event('input'));
        const accepted=new Promise(resolve=>window.addEventListener('message',function receive(event){if(event.data.richFormAccepted){window.removeEventListener('message',receive);resolve()}}));
        query('button.submit')[0].click();await accepted;
        draft('rich-rendering-passed');
      }catch(error){draft('TEST_FAILED:'+String(error.stack||error))}})();
      </script></body>`,
        );
      },
    );
    await done.promise;
    clearTimeout(timer);
    const themes = vscode.extensions.getExtension("vscode.theme-defaults")?.packageJSON.contributes
      .themes as { id: string; uiTheme: string }[] | undefined;
    for (
      const [uiTheme, className] of [
        ["vs", "vscode-light"],
        ["vs-dark", "vscode-dark"],
        ["hc-black", "vscode-high-contrast"],
        ["hc-light", "vscode-high-contrast-light"],
      ]
    ) {
      const theme = themes?.find((theme) => theme.uiTheme === uiTheme);
      if (!theme) throw new Error(`Missing built-in ${uiTheme} theme`);
      const reply = Promise.withResolvers<void>();
      themeReplies.set(`theme-checked:${className}`, reply);
      const deadline = setTimeout(
        () => reply.reject(new Error(`No theme acknowledgement: ${className}`)),
        20_000,
      );
      try {
        await themeSettings.update("colorTheme", theme.id, vscode.ConfigurationTarget.Global);
        await panel.webview.postMessage({ richTheme: className });
        await reply.promise;
      } finally {
        clearTimeout(deadline);
        themeReplies.delete(`theme-checked:${className}`);
      }
    }
    if (!rendered.has("ssr") || formActions !== 1) {
      throw new Error("Missing shared renderer acknowledgements or exact form action");
    }
  } finally {
    clearTimeout(timer);
    try {
      await themeSettings.update("colorTheme", initialTheme, vscode.ConfigurationTarget.Global);
    } finally {
      listener.dispose();
      panel.dispose();
      await Promise.all(cleanup);
      if (formOpened) await formClosed.promise;
    }
  }
}
