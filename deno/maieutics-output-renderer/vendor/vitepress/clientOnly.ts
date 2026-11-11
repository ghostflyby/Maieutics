/** VitePress v1.6.4 ClientOnly mount gate (MIT; see LICENSE and upstream.json).
 * Factory form keeps the component self-contained in both SSR and browser output.
 */
export const clientOnlyFactory = `Vue.defineComponent({
  setup(_, { slots }) {
    const show = Vue.ref(false);
    Vue.onMounted(() => { show.value = true; });
    return () => (show.value && slots.default ? slots.default() : null);
  }
})`;
