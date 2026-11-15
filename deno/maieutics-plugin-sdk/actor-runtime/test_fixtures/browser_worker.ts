import { serveWorker } from "../browser.ts";
export const rpc = {
  echo(bytes: Uint8Array): Uint8Array {
    return bytes;
  },
  async *numbers(): AsyncIterable<number> {
    yield 1;
    yield 2;
  },
};
serveWorker(rpc);
