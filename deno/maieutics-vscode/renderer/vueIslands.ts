/// <reference lib="dom" />
import * as Vue from "@vue/runtime-dom";

/** Materializes only kernel-compiled, explicitly selected live component output. */
export interface VueIslandDisposer {
  (): void;
  update?(setupKey: string, renderCode: string): Promise<boolean>;
}

export function mountVueIsland(
  container: HTMLElement,
  code: string,
  progressive?: { setupKey: string; renderCode: string },
): VueIslandDisposer {
  if (code.length > 400_000) throw new Error("invalid_vue_component");
  const component = new Function("Vue", code)(Vue);
  const render = Vue.shallowRef(component.render);
  let lastRender = component.render;
  if (progressive) {
    const deoptimize = (node: unknown): void => {
      if (!Vue.isVNode(node)) return;
      node.patchFlag = -2;
      if (Array.isArray(node.children)) { for (const child of node.children) deoptimize(child); }
    };
    component.render = function (...args: unknown[]) {
      if (lastRender !== render.value) {
        if (Array.isArray(args[1])) args[1].length = 0;
        lastRender = render.value;
      }
      const node = render.value.apply(this, args);
      // Render functions can change shape between source revisions. Use ordinary keyed Vue
      // reconciliation instead of optimized block arrays from a different compiled template.
      deoptimize(node);
      return node;
    };
  }
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
  let updateVersion = 0;
  let committedRender = render.value;
  const dispose: VueIslandDisposer = () => {
    if (closed) return;
    closed = true;
    try {
      app.unmount();
    } finally {
      container.dataset.vueState = "disposed";
    }
  };
  if (progressive) {
    dispose.update = async (setupKey, renderCode) => {
      if (
        closed || setupKey !== progressive.setupKey || renderCode.length > 400_000
      ) throw new Error("component_scope_changed");
      const next = new Function("Vue", renderCode)(Vue);
      if (typeof next !== "function") throw new Error("invalid_component_update");
      const revision = ++updateVersion;
      failed = false;
      render.value = next;
      await Vue.nextTick();
      if (closed || revision !== updateVersion) return false;
      if (failed) {
        render.value = committedRender;
        await Vue.nextTick();
        if (closed || revision !== updateVersion) return false;
        throw new Error("component_update_failed");
      }
      committedRender = next;
      return true;
    };
  }
  return dispose;
}

/** Completion barrier for queued Vue reactive DOM updates. */
export async function vueUpdatesComplete(): Promise<void> {
  await Vue.nextTick();
}
