// Research: process-actor worker (serveProcess). The host spawns this process
// and calls the rpc surface over fork IPC. stdout/stderr are out-of-band.
import { serveProcess } from "@ghostflyby/worker-actor";

export const rpc = {
  add(a: number, b: number): number {
    return a + b;
  },
  greet(name: string): string {
    return `hello ${name}`;
  },
  async *count(n: number): AsyncIterable<number> {
    for (let i = 1; i <= n; i++) yield i;
  },
};

serveProcess(rpc);
