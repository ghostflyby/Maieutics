import * as vscode from "vscode";
import { createServer } from "node:http";

/** Native browser Fetch, Vue injection, CORS, streaming and unmount against a real HTTP server. */
export async function testPluginHttpAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension("ghostflyby.maieutics-vscode");
  if (!extension) throw new Error("Maieutics extension missing");
  let release: (() => void) | undefined;
  let cancelled: (() => void) | undefined;
  const cancellation = new Promise<void>((resolve) => {
    cancelled = resolve;
  });
  const server = createServer((request, response) => {
    response.setHeader("access-control-allow-origin", "*");
    response.setHeader("access-control-allow-headers", "content-type");
    if (request.method === "OPTIONS") {
      response.writeHead(204);
      response.end();
      return;
    }
    const path = request.url?.replace("/scoped/plugins/probe", "");
    if (path === "/echo") {
      request.pipe(response);
      return;
    }
    if (path === "/stream") {
      response.writeHead(200);
      response.write(new Uint8Array([0, 255]));
      release = () => response.end(new Uint8Array([128]));
      return;
    }
    if (path === "/release") {
      release?.();
      response.end();
      return;
    }
    if (path === "/pending") {
      response.writeHead(200);
      response.write(new Uint8Array([42]));
      response.on("close", () => cancelled?.());
      return;
    }
    if (path === "/cancelled") {
      void cancellation.then(() => response.end("cancelled"));
      return;
    }
    response.end("ok");
  });
  const panel = vscode.window.createWebviewPanel("maieutics-http-test", "Vue HTTP", {
    viewColumn: vscode.ViewColumn.Beside,
    preserveFocus: true,
  }, { enableScripts: true, localResourceRoots: [extension.extensionUri] });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: vscode.Disposable | undefined;
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", resolve);
    });
    const address = server.address();
    if (!address || typeof address === "string") throw new Error("Missing HTTP test address");
    const origin = `http://127.0.0.1:${address.port}`;
    const asset = panel.webview.asWebviewUri(
      vscode.Uri.joinPath(extension.extensionUri, "media", "outputClient.js"),
    ).toString();
    const verdict = new Promise<void>((resolve, reject) => {
      timer = setTimeout(() => reject(new Error("Vue HTTP browser test did not complete")), 30_000);
      subscription = panel.webview.onDidReceiveMessage((message) =>
        message.ok ? resolve() : reject(new Error(message.error))
      );
    });
    panel.webview.html =
      `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src ${panel.webview.cspSource} 'unsafe-inline' 'unsafe-eval'; connect-src ${origin};"></head><body><script type="module">
      const vscode = acquireVsCodeApi();
      const assert = (value,message) => {if(!value)throw new Error(message);};
      let client, first, second;
      try {
        const module = await import(${JSON.stringify(asset)});
        client = module.createPluginHttpClient(${
        JSON.stringify(`${origin}/scoped/plugins/probe/`)
      });
        const code = 'return {setup(){globalThis.httpFetchers.push(Maieutics.useFetch());return ()=>Vue.h("div","HTTP");}};';
        globalThis.httpFetchers = [];
        const element = () => {const e=document.createElement('div');e.innerHTML='<div>HTTP</div>';document.body.append(e);return e;};
        first = module.mountVueIsland(element(),code,undefined,client);
        second = module.mountVueIsland(element(),code,undefined,client);
        const fetch = httpFetchers[0];
        const echo = await fetch('/echo',{method:'POST',headers:{'content-type':'application/octet-stream'},body:new Uint8Array([0,255,128])});
        assert(echo instanceof Response,'Native Response is preserved');
        assert(String(new Uint8Array(await echo.arrayBuffer()))==='0,255,128','Binary crosses native browser HTTP');
        const reader = (await fetch('/stream')).body.getReader();
        assert(String((await reader.read()).value)==='0,255','First chunk arrives before producer completion');
        await (await fetch('/release',{method:'POST'})).text();
        assert(String((await reader.read()).value)==='128','Later chunk remains incremental');
        assert((await reader.read()).done,'Stream terminates');reader.releaseLock();
        const pending = (await fetch('/pending')).body.getReader();await pending.read();
        first();first=undefined;
        let aborted=false;try{await pending.read();}catch(error){aborted=error.name==='AbortError';}
        assert(aborted,'Unmount cancels an active native response body');pending.releaseLock();
        assert(await (await httpFetchers[1]('/cancelled')).text()==='cancelled','Server observes cancellation');
        assert(await (await httpFetchers[1]('/api')).text()==='ok','Another view keeps working');
        second();second=undefined;client.dispose();client=undefined;
        vscode.postMessage({ok:true});
      } catch(error) {vscode.postMessage({ok:false,error:error.stack??String(error)});}
      finally {first?.();second?.();client?.dispose();}
    </script></body></html>`;
    await verdict;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    subscription?.dispose();
    panel.dispose();
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
}
