// C1 integration test: the host-derived REPL process (`process_main.ts` /
// `process_rpc.ts`) boots the REAL WebSocket REPL — eval + output + comm
// channels and the shared Aves worker — once the host starts it. This test
// runs the process through worker-actor's `spawnProcess` exactly like the
// plugin host derives it, against a minimal in-test WebSocket "kernel" that
// speaks the eval protocol (hello/ready), the /comm channel, the /ws control
// bus, and the binary /v1/repl/output/ws endpoint, and verifies:
//   - the B3 ordering: pregestPid/initialize resolve before startRepl boots
//     the client (status stays idle until then);
//   - startRepl completes the eval hello/ready handshake (status().ready);
//   - a REAL Aves execution of `1 + 1` inside the shared repl_worker returns
//     the value 2 over the eval WS (the fake kernel only relays the code and
//     observes the worker's result);
//   - the script surface binds inside the worker (`globalThis.maieutics`) and
//     `maieutics.health()` round-trips through the pid-attributed /ws bus;
//   - the REPL opens the dedicated output endpoint and streams the worker's
//     output events as binary frames there (console/display/clearOutput no
//     longer travel on the eval channel);
//   - disposeRepl ends the channels and the process exits itself.

import { assert, assertEquals, assertRejects } from "@std/assert";
import { spawnProcess } from "@ghostflyby/worker-actor";
import type { ReplEvalEnvelope } from "./protocol.ts";
import {
  decodeReplEvalEnvelope,
  encodeReplEvalEnvelope,
  REPL_EVAL_WEBSOCKET_PATH,
  ReplEvalMessageType,
  type ReplEvalResultPayload,
} from "./protocol.ts";
import { decodeOutputFrame, REPL_OUTPUT_WEBSOCKET_PATH } from "./output_protocol.ts";
import type { HostReplRpc } from "../maieutics-plugin-host/host_repl_protocol.ts";

const ENTRY_URL = new URL("./process_main.ts", import.meta.url).href;
const CLIENT_URL = new URL("../maieutics-repl-client/mod.ts", import.meta.url).href;
const SESSION_ID = "process-rpc-e2e";
const GENERATION = 0;

/** Minimal eval + comm + control-bus WebSocket "kernel" for the REPL process.
 * The eval channel relays `execute` envelopes to the real worker inside the
 * REPL process and collects the `result` envelopes the worker produces. */
class FakeKernel implements AsyncDisposable {
  static start(): FakeKernel {
    const path = `/tmp/mc-repl-test-${crypto.randomUUID().slice(0, 8)}.sock`;
    const kernel = new FakeKernel(path);
    kernel.#server = Deno.serve(
      {
        transport: "unix",
        path,
        onListen: () => {},
        onError: (error) => {
          console.error(`[test-kernel] server error: ${error}`);
          return new Response("server error", { status: 500 });
        },
      },
      (request) => kernel.#handleRequest(request),
    );
    return kernel;
  }

  readonly address: string;
  #listener: Deno.Listener | undefined;
  #server: Deno.HttpServer<Deno.UnixAddr> | undefined;
  #evalSocket: WebSocket | undefined;
  /** Every eval envelope this kernel received, in wire order. */
  readonly evalMessages: ReplEvalEnvelope[] = [];
  /** Whether the /comm channel connected and its declared session id. */
  commSessionId: string | undefined;
  /** Whether the script /ws control bus connected. */
  busConnected = false;
  /** Whether the binary /v1/repl/output/ws endpoint connected. */
  outputConnected = false;
  /** Binary output frames decoded from the output endpoint, in wire order. */
  readonly outputFrames: ReturnType<typeof decodeOutputFrame>[] = [];

  private constructor(path: string) {
    this.address = path;
  }

  #handleRequest(request: Request): Response {
    const path = new URL(request.url).pathname;
    if (path === REPL_EVAL_WEBSOCKET_PATH) {
      const { socket, response } = Deno.upgradeWebSocket(request);
      void this.#serveEval(socket);
      return response;
    }
    if (path === REPL_OUTPUT_WEBSOCKET_PATH) {
      const { socket, response } = Deno.upgradeWebSocket(request);
      void this.#serveOutput(socket);
      return response;
    }
    if (path === "/comm") {
      const { socket, response } = Deno.upgradeWebSocket(request);
      void this.#serveComm(socket);
      return response;
    }
    if (path === "/ws") {
      const { socket, response } = Deno.upgradeWebSocket(request);
      void this.#serveBus(socket);
      return response;
    }
    return new Response("not found", { status: 404 });
  }

  #serveEval(socket: WebSocket): void {
    this.#evalSocket = socket;
    socket.onmessage = (event) => {
      const text = typeof event.data === "string"
        ? event.data
        : new TextDecoder().decode(event.data);
      const envelope = decodeReplEvalEnvelope(text);
      this.evalMessages.push(envelope);
      // The client waits for the ready reply to its hello before executing.
      if (envelope.type === ReplEvalMessageType.hello) {
        socket.send(encodeReplEvalEnvelope({
          type: ReplEvalMessageType.ready,
          correlationId: envelope.correlationId,
          payload: { sessionId: SESSION_ID, generation: GENERATION },
        }));
      }
    };
    socket.onclose = () => {
      socket.onmessage = null;
    };
  }

  /** Receives the REPL's binary output frames (console/display/clearOutput). */
  #serveOutput(socket: WebSocket): void {
    this.outputConnected = true;
    socket.onmessage = (event) => {
      void (async () => {
        let data: Uint8Array;
        if (typeof event.data === "string") {
          data = new TextEncoder().encode(event.data);
        } else if (event.data instanceof Blob) {
          data = new Uint8Array(await event.data.arrayBuffer());
        } else if (event.data instanceof ArrayBuffer) {
          data = new Uint8Array(event.data);
        } else {
          data = new Uint8Array(event.data.buffer, event.data.byteOffset, event.data.byteLength);
        }
        try {
          this.outputFrames.push(decodeOutputFrame(data));
        } catch (error) {
          console.error(
            `[test-kernel] undecodable output frame: ${
              error instanceof Error ? error.message : error
            }`,
          );
        }
      })();
    };
    socket.onclose = () => {
      socket.onmessage = null;
    };
  }

  #serveComm(socket: WebSocket): void {
    socket.onmessage = (event) => {
      const text = typeof event.data === "string"
        ? event.data
        : new TextDecoder().decode(event.data);
      // The first frame is the JSON hello; the ready ack completes connectComm.
      this.commSessionId = (JSON.parse(text) as { sessionId: string }).sessionId;
      socket.send(JSON.stringify({ ready: true }));
      socket.onmessage = null;
    };
  }

  #serveBus(socket: WebSocket): void {
    socket.onmessage = (event) => {
      const text = typeof event.data === "string"
        ? event.data
        : new TextDecoder().decode(event.data);
      const frame = JSON.parse(text) as {
        type?: string;
        payload?: { sessionId?: string };
        correlationId?: string;
      };
      if (frame.type === "control.hello") {
        this.busConnected = true;
        socket.send(JSON.stringify({ version: 1, type: "control.ready" }));
        return;
      }
      if (frame.type === "control.ping") {
        socket.send(JSON.stringify({
          version: 1,
          type: "control.pong",
          correlationId: frame.correlationId,
        }));
      }
    };
  }

  /** Sends an execute envelope and waits for the worker's result envelope. */
  async execute(code: string): Promise<unknown> {
    const socket = this.#evalSocket;
    if (socket === undefined || socket.readyState !== WebSocket.OPEN) {
      throw new Error("The eval channel is not open.");
    }
    const executionId = crypto.randomUUID();
    socket.send(encodeReplEvalEnvelope({
      type: ReplEvalMessageType.execute,
      correlationId: executionId,
      payload: { executionId, code },
    }));
    const deadline = Date.now() + 15_000;
    while (Date.now() < deadline) {
      const result = this.evalMessages.findLast((envelope) =>
        envelope.type === ReplEvalMessageType.result &&
        (envelope.payload as { executionId?: string }).executionId === executionId
      );
      if (result !== undefined) {
        return (result.payload as ReplEvalResultPayload).value;
      }
      const failed = this.evalMessages.findLast((envelope) =>
        (envelope.type === ReplEvalMessageType.error ||
          envelope.type === ReplEvalMessageType.cancelled) &&
        (envelope.payload as { executionId?: string }).executionId === executionId
      );
      if (failed !== undefined) {
        throw new Error(
          `Execution '${executionId}' failed with '${failed.type}': ${
            JSON.stringify(failed.payload)
          }`,
        );
      }
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    throw new Error(`Timed out waiting for the result of execution '${executionId}'.`);
  }

  /** Waits until an output frame matching the predicate arrives. */
  async waitForOutput(
    predicate: (frame: ReturnType<typeof decodeOutputFrame>) => boolean,
    timeoutMs = 5_000,
  ): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (this.outputFrames.some(predicate)) return;
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    throw new Error("Timed out waiting for a matching output frame.");
  }

  async [Symbol.asyncDispose](): Promise<void> {
    try {
      this.#listener?.close();
      await this.#server?.shutdown();
    } catch {
      // Already closed.
    }
  }
}

Deno.test("host-derived REPL process runs the real REPL over the eval channel", async () => {
  await using kernel = FakeKernel.start();
  const previousEnv = captureEnv();
  setKernelEnv(kernel.address);
  try {
    const actor = await spawnProcess<HostReplRpc>(ENTRY_URL, {
      permissions: {
        read: true,
        env: true,
        net: true,
        write: true,
        run: true,
        sys: true,
        ffi: true,
      },
    });
    try {
      // B3 ordering: pregestPid and initialize must not boot the client.
      const pid = await actor.pregestPid();
      assert(Number.isSafeInteger(pid) && pid > 0, "pregestPid must be positive");
      const idle = await actor.status();
      assertEquals(idle, { started: false, ready: false });

      const identity = await actor.initialize();
      assertEquals(identity.sessionId, SESSION_ID);
      assertEquals(identity.pid, pid);

      // startRepl boots the WebSocket client and completes the eval handshake.
      await actor.startRepl();
      const running = await actor.status();
      assertEquals(running, { started: true, ready: true });

      // REAL Aves execution inside the shared repl_worker: the fake kernel only
      // relays the code; the worker computes 1 + 1 = 2.
      assertEquals(await kernel.execute("1 + 1"), 2);

      // The script surface: the worker bound globalThis.maieutics; health()
      // round-trips through the /ws control bus, attributed to this session.
      assertEquals(await kernel.execute("await globalThis.maieutics.health()"), "ok");
      assertEquals(kernel.busConnected, true);

      // The output endpoint carries the worker's console events as binary
      // frames; the eval channel stays a pure JSON control plane.
      assertEquals(kernel.outputConnected, true);
      assertEquals(await kernel.execute('console.log("hello output")'), undefined);
      await kernel.waitForOutput(
        (frame) => frame.type === 0 && frame.text === "hello output\n",
      );

      // Comm events ride the dedicated comm channel and must not consume the output frame
      // sequence: interleaving `Deno.jupyter.broadcast` (comm) with console/display output keeps
      // the output endpoint's per-execution sequence contiguous (phase 3 exposes the strict
      // validation; a gap would terminate the connection).
      await kernel.execute(
        "const commId = 'probe-comm'; " +
          "await Deno.jupyter.broadcast('comm_open', " +
          "{ comm_id: commId, target_name: 'probe', data: {} }, " +
          "{ buffers: [new Uint8Array([1, 2, 3])] }); " +
          "console.log('before-display'); " +
          "await Deno.jupyter.display(" +
          "{ 'text/plain': 'probe-display' }, { raw: true }); " +
          "const w = { [Deno.jupyter.$display]: async () => " +
          "({ 'application/vnd.jupyter.widget-view+json': { model_id: commId } }) }; w",
      );
      await kernel.waitForOutput(
        (frame) => frame.type === 0 && frame.text === "before-display\n",
      );
      await kernel.waitForOutput(
        (frame) => frame.type === 2 && JSON.stringify(frame.data).includes("probe-display"),
      );
      await kernel.waitForOutput(
        (frame) => frame.type === 2 && JSON.stringify(frame.data).includes("widget-view"),
      );
      const widgetDisplay = kernel.outputFrames
        .filter((frame) => frame.type === 2 && JSON.stringify(frame.data).includes("widget-view"))
        .at(-1);
      assert(widgetDisplay !== undefined);
      const outputSequences = kernel.outputFrames
        .filter((frame) => frame.executionId === widgetDisplay.executionId)
        .map((frame) => frame.seq);
      // console(1), display(2), display(3): comm events never appear on the output endpoint.
      assertEquals(outputSequences, [1, 2, 3]);
      assertEquals(
        kernel.evalMessages.every((envelope) =>
          envelope.type === ReplEvalMessageType.hello ||
          envelope.type === ReplEvalMessageType.ready ||
          envelope.type === ReplEvalMessageType.execute ||
          envelope.type === ReplEvalMessageType.result
        ),
        true,
        "the eval channel must not carry console/display output events",
      );

      // disposeRepl ends the channels and the process exits itself.
      await actor.disposeRepl();
    } finally {
      await actor.dispose().catch(() => {});
    }
  } finally {
    restoreEnv(previousEnv);
  }
});

Deno.test("host-derived REPL process without a kernel env stays a control-plane actor", async () => {
  const previousEnv = captureEnv();
  deleteKernelEnv();
  try {
    const actor = await spawnProcess<HostReplRpc>(ENTRY_URL, {
      permissions: {
        read: true,
        env: true,
        net: true,
        write: true,
        run: true,
        sys: true,
        ffi: true,
      },
    });
    try {
      await actor.initialize();
      // No MAIEUTICS_REPL_IPC: startRepl records the failure, status surfaces
      // it, and the process stays alive (the host owns its lifecycle).
      await assertRejects(() => actor.startRepl());
      const status = await actor.status();
      assertEquals(status.started, true);
      assertEquals(status.ready, false);
      assert(typeof status.error === "string" && status.error.length > 0);
      await actor.disposeRepl();
    } finally {
      await actor.dispose().catch(() => {});
    }
  } finally {
    restoreEnv(previousEnv);
  }
});

function captureEnv(): Map<string, string | undefined> {
  const previous = new Map<string, string | undefined>();
  for (
    const name of [
      "MAIEUTICS_REPL_IPC",
      "MAIEUTICS_REPL_SESSION",
      "MAIEUTICS_REPL_GENERATION",
      "MAIEUTICS_REPL_CLIENT",
    ]
  ) {
    previous.set(name, Deno.env.get(name));
  }
  return previous;
}

function setKernelEnv(address: string): void {
  Deno.env.set("MAIEUTICS_REPL_IPC", address);
  Deno.env.set("MAIEUTICS_REPL_SESSION", SESSION_ID);
  Deno.env.set("MAIEUTICS_REPL_GENERATION", String(GENERATION));
  Deno.env.set("MAIEUTICS_REPL_CLIENT", CLIENT_URL);
}

function deleteKernelEnv(): void {
  for (
    const name of [
      "MAIEUTICS_REPL_IPC",
      "MAIEUTICS_REPL_SESSION",
      "MAIEUTICS_REPL_GENERATION",
      "MAIEUTICS_REPL_CLIENT",
    ]
  ) {
    Deno.env.delete(name);
  }
}

function restoreEnv(previous: Map<string, string | undefined>): void {
  for (const [name, value] of previous) {
    if (value === undefined) Deno.env.delete(name);
    else Deno.env.set(name, value);
  }
}
