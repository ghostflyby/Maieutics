import { effectScope, shallowRef, type VNode, watch } from "vue";

/** Replace binding-owned effects without adding a renderless component to the tree. */
export function useBinding<T>(
  source: () => T,
  create: (value: T) => () => VNode,
): () => VNode | null {
  const render = shallowRef<() => VNode>();
  watch(source, (value, _previous, onCleanup) => {
    const scope = effectScope();
    onCleanup(() => scope.stop());
    try {
      render.value = scope.run(() => create(value));
    } catch (error) {
      scope.stop();
      throw error;
    }
    // One Vue prop update may replace runtime and viewId together; bind only the complete pair.
  }, { immediate: true, flush: "pre" });
  return () => render.value?.() ?? null;
}
