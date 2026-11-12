import { createFormRenderer } from "./outputClient.js";

/** Notebook adapter: all component and state handling lives in the shared Vue client. */
export function activate(context) {
  const client = createFormRenderer({ sendMessage: (message) => context.postMessage?.(message) });
  context.onDidReceiveMessage?.(async (message) => {
    try {
      await client.receive(message);
    } catch {
      console.warn("Maieutics rejected invalid form state.");
    }
  });
  return {
    renderOutputItem(output, element) {
      const value = output.json();
      client.render(output.id, value, element);
      context.postMessage?.({
        source: "maieutics-form",
        type: "applied",
        modelId: value.modelId,
        viewId: output.id,
        snapshot: value.snapshot === true,
      });
    },
    disposeOutputItem(id) {
      client.disposeOutput(id);
    },
  };
}
