import { FormActorClient } from "./formActorClient.ts";
import { createFormRenderer } from "./forms.ts";
import type { ActorFormRenderer, ActorFormRendererOptions } from "./types.d.ts";

/** The ordinary Vue form renderer with an explicitly owned browser-to-host Actor attachment. */
export function createActorFormRenderer(options: ActorFormRendererOptions): ActorFormRenderer {
  const forms = createFormRenderer({
    sendMessage: (message) => actor.send(message),
    activeView: (viewId) => actor.hasView(viewId),
  });
  const actor = new FormActorClient({
    postMessage: options.postMessage,
    receive: (message) => forms.receive(message),
    log: options.log ?? ((message) => console.warn(message)),
  });
  let closing: Promise<void> | undefined;
  return {
    render: forms.render,
    receive: (message) => actor.receive(message),
    disposeOutput: forms.disposeOutput,
    dispose() {
      closing ??= (async () => {
        try {
          forms.dispose();
        } finally {
          await actor.dispose();
        }
      })();
      return closing;
    },
  };
}
