/// <reference lib="deno.window" />
/// <reference lib="es2024.promise" />
import * as vscode from "vscode";
import type { FrontendClient } from "../../client.ts";
import type { ViewContent, ViewEntry, ViewFrame } from "../../conversationProtocol.ts";
import { ConversationPanel } from "../../conversationPanels.ts";

/** Real production webview: the same renderer performs Markdown fallback, SSR, Vue and live forms. */
export async function testConversationRenderingAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  if (!extension) throw new Error("Missing Maieutics extension");
  const sessionId = "f".repeat(32);
  const markdown =
    "# Shared rendering\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n<script>globalThis.untrustedExecuted=true</script>";
  const entries: ViewEntry[] = ["message", "display", "display", "tool", "display"].map((
    kind,
    ordinal,
  ) => ({
    id: `rich-${ordinal}`,
    ordinal,
    interactionId: "run",
    kind: kind as ViewEntry["kind"],
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
        "application/vnd.maieutics.vue+json": {
          version: 1,
          format: "sfc",
          source: "<template><button>Clicks 0</button></template>",
        },
      },
    },
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
    {
      version: 1,
      kind: "tool",
      value: {
        callId: "call",
        tool: "read_file",
        status: "ok",
        arguments: { path: "workspace://local/example.ts" },
        result: { text: "structured result" },
      },
    },
  ];
  presentations.push(
    {
      version: 1,
      kind: "mime",
      value: {
        "application/vnd.maieutics.template+json": {
          version: 1,
          source: "# Template",
          bindings: [],
        },
      },
    },
  );
  const done = Promise.withResolvers<void>();
  const rendered = new Set<string>();
  const native = Promise.withResolvers<void>();
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
    renderMarkdown: async (
      documentId: string,
      text: string,
      signal: AbortSignal,
      template: unknown,
      vue: unknown,
    ) => {
      if (template) {
        return {
          version: 1,
          documentId,
          html: "<p class=template-proof>Shared template</p>",
          css: "",
        };
      }
      if (vue) {
        return {
          version: 1,
          documentId,
          html: "<button>Clicks 0</button>",
          css: "",
          vue: {
            version: 1,
            scopeId: `data-v-${documentId}`,
            code:
              "const {h,ref}=Vue;return {setup(){const n=ref(0);return ()=>h('button',{onClick:()=>n.value++},'Clicks '+n.value)}}",
          },
        };
      }
      await Promise.race([native.promise, closed(signal)]);
      signal.throwIfAborted();
      if (text !== markdown) throw new Error("The shared bridge lost the exact Markdown source");
      return {
        version: 1,
        documentId,
        html:
          "<h1>Shared rendering</h1><table><tbody><tr><td>SSR table</td></tr></tbody></table><pre class='shiki'><code>highlighted</code></pre>",
        css: "",
      };
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
    if (message.method === "draft" && message.params?.text === "native-markdown-observed") {
      native.resolve();
    }
    if (message.method === "draft" && message.params?.text === "rich-rendering-passed") {
      done.resolve();
    }
    if (message.method === "draft" && String(message.params?.text).startsWith("TEST_FAILED:")) {
      done.reject(new Error(message.params.text));
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
      (async()=>{try{
        await wait(()=>query('h1').some(node=>node.textContent==='Shared rendering'),'shared Markdown fallback missing');
        assert(query('table').length>0,'Markdown table was flattened to text');
        assert(globalThis.untrustedExecuted===undefined,'untrusted HTML executed');
        draft('native-markdown-observed');
        await wait(()=>query('[data-render-engine="ssr"]').length>0 && query('[data-render-engine="vue"]').length>0,'shared SSR/Vue did not apply');
        await wait(()=>query('.template-proof').length===1,'shared data template was not connected');
        assert(query('.shiki').length===1,'code markup did not use the shared SSR result');
        const counter=query('button').find(node=>node.textContent==='Clicks 0');assert(counter,'Vue component absent');counter.click();
        await wait(()=>counter.textContent==='Clicks 1','Vue island did not hydrate');
        await wait(()=>query('fieldset').some(node=>!node.disabled),'form did not attach over the existing binary Actor bridge');
        const field=query('input[name="answer"]')[0];field.value='typed';field.dispatchEvent(new Event('input'));
        const accepted=new Promise(resolve=>window.addEventListener('message',function receive(event){if(event.data.richFormAccepted){window.removeEventListener('message',receive);resolve()}}));
        query('button.submit')[0].click();await accepted;
        const tool=query('details.tool')[0];assert(tool,'structured tool renderer missing');tool.open=true;
        assert(tool.textContent.includes('workspace://local/example.ts') && tool.textContent.includes('structured result'),'tool arguments/results were flattened or lost');
        assert(document.querySelectorAll('.mui-history').length===1 && document.querySelectorAll('#composer > form').length===1,'history/composer separation regressed');
        draft('rich-rendering-passed');
      }catch(error){draft('TEST_FAILED:'+String(error.stack||error))}})();
      </script></body>`,
        );
      },
    );
    await done.promise;
    if (!rendered.has("ssr") || !rendered.has("vue") || formActions !== 1) {
      throw new Error("Missing shared renderer acknowledgements or exact form action");
    }
  } finally {
    clearTimeout(timer);
    listener.dispose();
    native.resolve();
    panel.dispose();
    await Promise.all(cleanup);
    if (formOpened) await formClosed.promise;
  }
}
