/// <reference lib="dom" />
import * as Vue from "@vue/runtime-dom";

/** Materializes only kernel-compiled, explicitly selected live component output. */
export function mountVueIsland(container: HTMLElement, code: string): () => void {
  if (code.length > 400_000) throw new Error("invalid_vue_component");
  const component = new Function("Vue", code)(Vue);
  const app = Vue.createSSRApp(component);
  let failed = false;
  app.config.warnHandler = () => {};
  app.config.errorHandler = () => {
    failed = true;
    container.dataset.vueState = "failed";
  };
  try {
    app.mount(container);
    if (failed) throw new Error("vue_mount_failed");
    container.dataset.vueState = "mounted";
  } catch {
    app.unmount();
    throw new Error("vue_mount_failed");
  }
  let closed = false;
  return () => {
    if (closed) return;
    closed = true;
    try {
      app.unmount();
    } finally {
      container.dataset.vueState = "disposed";
    }
  };
}

/** Completion barrier for queued Vue reactive DOM updates. */
export async function vueUpdatesComplete(): Promise<void> {
  await Vue.nextTick();
}
