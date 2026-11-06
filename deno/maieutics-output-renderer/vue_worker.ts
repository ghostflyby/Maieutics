/// <reference lib="deno.worker" />
import * as Vue from "vue";
import { renderToString } from "vue/server-renderer";

// The reply port stays in this module's lexical scope; component code receives only Vue.
const request = await new Promise<{ code: string; port: MessagePort }>((resolve) => {
  self.onmessage = (event) => {
    self.onmessage = null;
    resolve(event.data);
  };
});
const send = request.port.postMessage.bind(request.port);
// Authored component diagnostics never enter the privileged child's retained logs.
const noop = () => {};
globalThis.console = new Proxy(console, { get: () => noop });
try {
  const component = new Function("Vue", request.code)(Vue);
  const app = Vue.createSSRApp(component);
  let failed = false;
  app.config.warnHandler = noop;
  app.config.errorHandler = () => {
    failed = true;
  };
  const html = await renderToString(app);
  send(!failed && html.length <= 4_000_000 ? { html } : { error: true });
} catch {
  send({ error: true });
} finally {
  request.port.close();
  self.close();
}
