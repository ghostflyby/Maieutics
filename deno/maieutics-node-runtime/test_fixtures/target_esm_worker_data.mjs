import { parentPort, workerData } from "node:worker_threads";

parentPort.postMessage({
  phase: "target-top-level",
  workerData,
});
