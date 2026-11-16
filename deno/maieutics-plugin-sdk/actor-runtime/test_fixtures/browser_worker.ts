import { serveWorker } from "../browser.ts";
export const rpc = {
  clone(value: unknown): unknown {
    return value;
  },
  invoke(callback: () => unknown): unknown {
    return callback();
  },
  uncloneableResult() {
    return Symbol("uncloneable response");
  },
  async pending(started: () => Promise<void>): Promise<void> {
    await started();
    await new Promise(() => {});
  },
  echo(bytes: Uint8Array): Uint8Array {
    return bytes;
  },
  async *numbers(): AsyncIterable<number> {
    yield 1;
    yield 2;
  },
};
serveWorker(rpc);
