// Integration test for the blocking input mailbox: a real repl_worker is
// spawned, the input mailbox link is established exactly as repl_client does,
// and prompt/confirm/alert are driven to completion (including an interrupt
// that wakes the blocked worker early).

import { assertEquals } from "@std/assert";
import { spawn } from "@ghostflyby/worker-actor";
import { INPUT_MAILBOX_LINK_LABEL } from "./repl_actor.ts";
import { InputMailboxKind } from "./input_mailbox.ts";
import { spawnBootstrapWorker } from "../maieutics-runtime/worker_factory.ts";
import type * as ReplWorker from "./repl_worker.ts";

interface Request {
  sab: SharedArrayBuffer;
  kind: "prompt" | "confirm" | "alert";
  prompt: string;
}

/** Spawn the real worker, establish the mailbox link, and collect requests. */
async function spawnWorkerWithMailbox(): Promise<{
  worker: Worker;
  actor: Awaited<ReturnType<typeof spawn<typeof ReplWorker.rpc>>>;
  requests: Request[];
  answer(index: number, kind: InputMailboxKind, value?: string | boolean): void;
  interrupt(index: number): void;
  waitForRequests(count: number): Promise<void>;
}> {
  const worker = spawnBootstrapWorker(new URL("./repl_worker.ts", import.meta.url), {
    profile: "repl",
    deno: { permissions: { env: true, read: true } },
  });
  const actor = await spawn<typeof ReplWorker.rpc>(worker, {
    signal: AbortSignal.timeout(10_000),
  });
  const { port1, port2 } = new MessageChannel();
  worker.postMessage(
    { type: "__link", label: INPUT_MAILBOX_LINK_LABEL, port: port1 },
    { transfer: [port1] },
  );
  const requests: Request[] = [];
  port2.onmessage = (e: MessageEvent) => {
    const frame = e.data as { type: string; value?: Request };
    if (frame.type === "__link-value" && frame.value !== undefined) {
      requests.push(frame.value);
      console.log(
        `[test] request #${requests.length}: kind=${frame.value.kind} prompt=${
          JSON.stringify(frame.value.prompt)
        }`,
      );
    }
  };

  const write = (index: number, kind: InputMailboxKind, value?: string | boolean): void => {
    const req = requests[index];
    if (req === undefined) throw new Error(`No request at index ${index}`);
    const status = new Int32Array(req.sab, 0, 1);
    const kindSlot = new Int32Array(req.sab, 4, 1);
    const answerSlot = new Int32Array(req.sab, 8, 1);
    const text = new Uint8Array(req.sab, 12);
    kindSlot[0] = kind;
    if (kind === InputMailboxKind.prompt) {
      const bytes = new TextEncoder().encode(String(value));
      text.set(bytes);
      answerSlot[0] = bytes.length;
    } else if (kind === InputMailboxKind.confirm) {
      answerSlot[0] = value === true ? 1 : 0;
    }
    Atomics.store(status, 0, 1);
    Atomics.notify(status, 0, 1);
  };

  return {
    worker,
    actor,
    requests,
    answer: write,
    interrupt(index) {
      const req = requests[index];
      const status = new Int32Array(req.sab, 0, 1);
      Atomics.store(status, 0, 2);
      Atomics.notify(status, 0, 1);
    },
    async waitForRequests(count) {
      const deadline = Date.now() + 2000;
      while (requests.length < count && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 10));
      }
      assertEquals(
        requests.length >= count,
        true,
        `expected ${count} requests, got ${requests.length}`,
      );
    },
  };
}

Deno.test("sync prompt/confirm/alert block and are answered through the mailbox", async () => {
  // repl_worker's initialize() imports the maieutics client module from this
  // env var; keep it set for the whole test (worker inherits it at spawn).
  const envName = "MAIEUTICS_REPL_CLIENT";
  const previous = Deno.env.get(envName);
  Deno.env.set(envName, "data:text/javascript,export%20default%20%7B%7D");
  try {
    const { worker, actor, answer, waitForRequests } = await spawnWorkerWithMailbox();
    await actor.initialize();

    const executionPromise = (async () => {
      const events: string[] = [];
      try {
        for await (
          const event of actor.execute(
            "exec-1",
            `
            globalThis.syncResult = [prompt("p1?"), confirm("c1?"), (alert("a1?"), "after-alert")];
            globalThis.syncResult[2];
          `,
            AbortSignal.timeout(5000),
          )
        ) {
          events.push(event.type);
        }
      } catch (error) {
        console.log(
          `[test] execute stream error: ${error instanceof Error ? error.message : String(error)}`,
        );
        throw error;
      }
      return events;
    })();

    // The worker blocks on each input in turn: answer each request as it
    // arrives, then wait for the next one.
    await waitForRequests(1); // prompt
    answer(0, InputMailboxKind.prompt, "world");
    await waitForRequests(2); // confirm
    answer(1, InputMailboxKind.confirm, true);
    await waitForRequests(3); // alert
    answer(2, InputMailboxKind.alert);

    const events = await executionPromise;
    await actor.dispose();
    worker.terminate();

    assertEquals(events.includes("terminal"), true);
  } finally {
    if (previous === undefined) Deno.env.delete(envName);
    else Deno.env.set(envName, previous);
  }
});
