import { vueWorkerSource } from "./bundle/vue_worker_source.js";
import { type CompiledVueComponent, VueComponentError } from "./vueComponent.ts";

/** One worker, private reply channel and deadline per component evaluation. */
export async function renderVueComponent(
  component: CompiledVueComponent,
  signal?: AbortSignal,
): Promise<string> {
  signal?.throwIfAborted();
  const worker = new Worker(
    new URL("data:application/javascript," + encodeURIComponent(vueWorkerSource)),
    { type: "module", deno: { permissions: "none" } },
  );
  const channel = new MessageChannel();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let abort: (() => void) | undefined;
  try {
    return await new Promise<string>((resolve, reject) => {
      abort = () => {
        worker.terminate();
        channel.port1.close();
        channel.port2.close();
        reject(signal?.reason ?? new DOMException("Cancelled", "AbortError"));
      };
      signal?.addEventListener("abort", abort, { once: true });
      let unexpected = 0;
      worker.onmessage = () => {
        if (++unexpected > 4) reject(new VueComponentError("component_failed"));
      };
      worker.onerror = (event) => {
        event.preventDefault();
        reject(new VueComponentError("component_failed"));
      };
      channel.port1.onmessage = ({ data }) => {
        if (typeof data?.html === "string" && data.html.length <= 4_000_000) resolve(data.html);
        else reject(new VueComponentError("component_failed"));
      };
      timer = setTimeout(() => reject(new VueComponentError("component_timeout")), 5000);
      worker.postMessage({ code: component.ssrCode ?? component.code, port: channel.port2 }, [
        channel.port2,
      ]);
    });
  } finally {
    if (timer !== undefined) clearTimeout(timer);
    if (abort) signal?.removeEventListener("abort", abort);
    channel.port1.close();
    channel.port2.close();
    worker.onmessage = null;
    worker.onerror = null;
    worker.terminate();
  }
}
