import * as vscode from "vscode";

/** Exercise the shared Vue components and the actual notebook adapter in a real browser. */
export async function testVueFormsAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  if (!extension) throw new Error("Maieutics extension missing");
  const panel = vscode.window.createWebviewPanel("maieutics-form-test", "Vue form regression", {
    viewColumn: vscode.ViewColumn.Beside,
    preserveFocus: true,
  }, { enableScripts: true, localResourceRoots: [extension.extensionUri] });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  try {
    const verdict = new Promise<void>((resolve, reject) => {
      timer = setTimeout(() => reject(new Error("Vue form browser test did not complete")), 30_000);
      subscription = panel.webview.onDidReceiveMessage((message) =>
        message.ok ? resolve() : reject(new Error(message.error))
      );
    });
    const asset = (name: string) =>
      panel.webview.asWebviewUri(vscode.Uri.joinPath(extension.extensionUri, "media", name))
        .toString();
    panel.webview.html =
      `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src ${panel.webview.cspSource} 'unsafe-inline'; style-src 'unsafe-inline';"></head><body><script type="module">
      const vscode = acquireVsCodeApi();
      const assert = (value, message) => { if (!value) throw new Error(message); };
      try {
        const module = await import(${JSON.stringify(asset("outputClient.js"))});
        const adapter = await import(${JSON.stringify(asset("formRenderer.js"))});
        const messages = [];
        const client = module.createFormRenderer({ sendMessage: (message) => messages.push(message) });
        const element = () => { const value = document.createElement('div'); document.body.append(value); return value; };
        const state = { title: 'Original', fields: [
          {name:'text',type:'text',required:true}, {name:'number',type:'number'},
          {name:'ok',type:'boolean'}, {name:'pick',type:'choice',choices:['a','b']}, {name:'secret',type:'password'}
        ], values: {text:'initial',number:1,ok:false,pick:'a',secret:''} };
        const output = {version:1,modelId:'model',state};
        const first = element(); client.render('one', output, first);
        const root = first.shadowRoot;
        assert(root.querySelector('fieldset').disabled, 'no actions before the kernel confirms a live model');
        await client.receive({source:'maieutics-form',type:'state',modelId:'model',state});
        assert(!root.querySelector('fieldset').disabled, 'live model enables the form');
        const input = root.querySelector('[name=text]'); input.focus(); input.value='typed'; input.dispatchEvent(new Event('input'));
        const number = root.querySelector('[name=number]'); number.value='2.5'; number.dispatchEvent(new Event('input'));
        const ok = root.querySelector('[name=ok]'); ok.checked=true; ok.dispatchEvent(new Event('input'));
        const pick = root.querySelector('[name=pick]'); pick.value='b'; pick.dispatchEvent(new Event('change'));
        const secret = root.querySelector('[name=secret]'); secret.value='value'; secret.dispatchEvent(new Event('input'));
        await module.vueUpdatesComplete();
        await client.receive({source:'maieutics-form',type:'state',modelId:'model',state:{...state,title:'Updated'}});
        assert(root.querySelector('[name=text]') === input && input.value === 'typed', 'state updates preserve edited controls');
        assert(root.activeElement === input, 'state updates preserve input focus');
        client.render('one', output, first);
        assert(root.querySelector('[name=text]') === input, 'duplicate output retains the Vue app');
        root.querySelector('.submit').click();
        const submitted = messages.find((message) => message.name === 'submit');
        assert(JSON.stringify(submitted?.payload.values) === JSON.stringify({text:'typed',number:2.5,ok:true,pick:'b',secret:'value'}), 'submit gathers the real mounted field values');
        root.querySelector('.cancel').click(); assert(messages.at(-1).name === 'cancel', 'cancel reaches the host');
        const second = element(); client.render('two', output, second);
        await client.receive({source:'maieutics-form',type:'state',modelId:'model',state:{...state,title:'Shared'}});
        assert(root.querySelector('.title').textContent === 'Shared' && second.shadowRoot.querySelector('.title').textContent === 'Shared', 'two views share backend state');
        const oldButton = root.querySelector('.cancel'); const before = messages.length;
        client.disposeOutput('one'); oldButton.click();
        assert(messages.length === before + 1 && messages.at(-1).type === 'unmount', 'unmount releases handlers without closing the shared model');
        second.shadowRoot.querySelector('.cancel').click(); assert(messages.at(-1).name === 'cancel', 'the other view remains functional');
        await client.receive({source:'maieutics-form',type:'closed',modelId:'model'});
        assert(second.shadowRoot.querySelector('fieldset').disabled, 'closed models become readonly snapshots');
        const frozen = element(); const prior = messages.length;
        client.render('frozen', {...output,snapshot:true,esmSource:'globalThis.executedOldSource=true'}, frozen);
        await client.receive({source:'maieutics-form',type:'state',modelId:'model',state});
        assert(frozen.shadowRoot.querySelector('fieldset').disabled && messages.length === prior, 'saved forms neither connect nor acquire live authority');
        assert(globalThis.executedOldSource === undefined, 'legacy component source is inert data');
        client.dispose(); client.dispose();
        let rejected = false; try { client.render('late', output, element()); } catch { rejected = true; }
        assert(rejected, 'terminal disposal rejects future mounts');
        const cleanup = module.createFormRenderer({sendMessage(message){if(message.type === 'unmount') throw new Error('host closed');}});
        const cleanupA = element(), cleanupB = element();
        cleanup.render('cleanup-a', output, cleanupA); cleanup.render('cleanup-b', output, cleanupB);
        try { cleanup.dispose(); } catch {}
        assert(!cleanupA.shadowRoot.querySelector('form') && !cleanupB.shadowRoot.querySelector('form'), 'host delivery failure cannot prevent other Vue scopes from being unmounted');
        const posted = []; let receive;
        const notebook = adapter.activate({postMessage:(message)=>posted.push(message),onDidReceiveMessage:(handler)=>{receive=handler;}});
        const adapted = element(); notebook.renderOutputItem({id:'notebook',json:()=>output}, adapted);
        assert(posted[0]?.type === 'mount', 'the notebook adapter uses its owning renderer context');
        await receive({source:'maieutics-form',type:'state',modelId:'model',state});
        adapted.shadowRoot.querySelector('.cancel').click();
        assert(posted.at(-1).name === 'cancel', 'notebook actions use the same Vue implementation');
        notebook.disposeOutputItem();
        vscode.postMessage({ok:true});
      } catch(error) { vscode.postMessage({ok:false,error:error.stack??String(error)}); }
    </script></body></html>`;
    await verdict;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    subscription?.dispose();
    panel.dispose();
  }
}

/** Verify real notebook MIME selection through the production serializer and renderer registration. */
export async function testNotebookVueFormAsync(): Promise<void> {
  await testSavedForm(false);
  await testSavedForm(true);
}

async function testSavedForm(legacy: boolean): Promise<void> {
  const root = vscode.workspace.workspaceFolders?.[0]?.uri;
  if (!root) throw new Error("Missing test workspace");
  const id = `frozen-form-${legacy ? "legacy" : "current"}-${Date.now()}`;
  const uri = vscode.Uri.joinPath(root, `${id}.maieuticsnb`);
  const channel = vscode.notebooks.createRendererMessaging("maieutics-form-renderer");
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  let created = false;
  try {
    const applied = new Promise<void>((resolve, reject) => {
      timer = setTimeout(
        () => reject(new Error("The actual notebook did not select its Vue form renderer")),
        30_000,
      );
      subscription = channel.onDidReceiveMessage(({ message }) => {
        if (
          message?.source === "maieutics-form" && message.type === "applied" &&
          message.modelId === id
        ) {
          if (message.snapshot === true) resolve();
          else reject(new Error("A saved form must remain a readonly snapshot"));
        }
      });
    });
    const state = {
      title: "Frozen Vue form",
      fields: [{ name: "note", type: "text" }],
      values: { note: "saved" },
    };
    const data = legacy
      ? {
        "text/plain": "Frozen Vue form",
        "application/vnd.maieutics.view+json": {
          version: "1.0",
          modelId: id,
          viewFamily: "maieutics/form",
          state,
          esmSource: "throw new Error('retired source')",
        },
      }
      : {
        "application/vnd.maieutics.form+json": { version: 1, modelId: id, state },
        "text/plain": "Frozen Vue form",
      };
    const notebook = {
      maieutics: "maieutics-notebook",
      version: 1,
      cells: [{ kind: "agent", text: "Saved form", output: { repl: [{ displayId: id, data }] } }],
    };
    await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(JSON.stringify(notebook)));
    created = true;
    const document = await vscode.workspace.openNotebookDocument(uri);
    await vscode.window.showNotebookDocument(document, { preview: false });
    await applied;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    subscription?.dispose();
    await closeFixture(uri, created);
  }
}

async function closeFixture(uri: vscode.Uri, created: boolean): Promise<void> {
  const tabs = vscode.window.tabGroups.all.flatMap((group) => group.tabs).filter((tab) =>
    tab.input instanceof vscode.TabInputNotebook && tab.input.uri.toString() === uri.toString()
  );
  if (tabs.length && !await vscode.window.tabGroups.close(tabs, true)) {
    throw new Error("The form notebook fixture did not close");
  }
  if (created) await vscode.workspace.fs.delete(uri);
}
