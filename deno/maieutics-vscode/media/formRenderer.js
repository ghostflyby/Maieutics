import { createActorFormRenderer } from "./outputClient.js";
import { createNotebookLocalizer } from "./notebookLocalization.js";

/** Notebook adapter: all component and state handling lives in the shared Vue client. */
export function activate(context) {
  const localization = createNotebookLocalizer();
  const client = createActorFormRenderer({
    localize: localization.localize,
    postMessage: (message) => context.postMessage?.(message),
  });
  context.onDidReceiveMessage?.((message) => {
    try {
      client.receive(message);
    } catch {
      console.warn("Maieutics rejected invalid form state.");
    }
  });
  return {
    renderOutputItem(output, element) {
      localization.select(output.metadata);
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
