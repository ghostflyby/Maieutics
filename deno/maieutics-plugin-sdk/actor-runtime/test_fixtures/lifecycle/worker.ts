import { serveWorker } from "../../worker_runtime.ts";
import { throwingCleanupCodec } from "./codec.ts";

export const rpc = {
  diagnostic() {
    (self as unknown as { postMessage(value: unknown): void }).postMessage({
      diagnostic: "plugin diagnostic",
    });
  },
  invoke: (callback: () => unknown) => callback(),
  uncloneableResult: () => Symbol("uncloneable result"),
  echo(value: unknown): unknown {
    return value;
  },
  async pending(started: () => Promise<void>): Promise<void> {
    await started();
    await new Promise(() => {});
  },
};

serveWorker(rpc, {
  codecs: new URL(import.meta.url).searchParams.has("cleanup") ? [throwingCleanupCodec] : [],
});
