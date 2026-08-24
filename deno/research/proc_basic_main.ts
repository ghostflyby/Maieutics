// Research: spawn a process actor via spawnProcess (worker-actor 0.4.0, JSR).
// Verifies: cross-process RPC, AsyncIterable returns, dispose.
import type * as WorkerModule from "./proc_basic_worker.ts";
import { spawnProcess } from "@ghostflyby/worker-actor";

const actor = await spawnProcess<typeof WorkerModule.rpc>(
  new URL("./proc_basic_worker.ts", import.meta.url).pathname,
  { permissions: { read: true } },
);

console.log("add(1, 2)    =", await actor.add(1, 2));
console.log("greet(world) =", await actor.greet("world"));

const got: number[] = [];
for await (const n of await actor.count(4)) got.push(n);
console.log("count(4)     =", got);

await actor.dispose();
console.log("proc_basic OK");
