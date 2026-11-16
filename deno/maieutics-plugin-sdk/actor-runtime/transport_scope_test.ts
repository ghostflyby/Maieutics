import { assert, assertEquals, assertInstanceOf, assertRejects } from "@std/assert";
import { ActorTransportError } from "./core/lifetime.ts";
import {
  createMux,
  fromMessagePort,
  fromNodeIpc,
  type MessageEndpoint,
  messageTransport,
} from "./core/transport.ts";
import { PROTOCOL_VERSION } from "./core/protocol.ts";
import { spawn } from "./spawn_worker.ts";

function native() {
  const endpoint: MessageEndpoint = { onmessage: null, onmessageerror: null, postMessage() {} };
  const transport = fromMessagePort(endpoint);
  return {
    transport,
    deliver: (data: unknown) => endpoint.onmessage?.(new MessageEvent("message", { data })),
  };
}

for (const kind of ["messageport", "mux", "message", "ipc"]) {
  Deno.test(`dedicated ${kind} rejects typeless frames and settles pending calls`, async () => {
    const source = kind === "messageport" ? native() : (() => {
      const transport = kind === "mux"
        ? createMux(() => {})
        : kind === "ipc"
        ? fromNodeIpc(() => true)
        : messageTransport({ send() {} });
      return { transport, deliver: transport.deliver };
    })();
    const creation = spawn<{ pending(): void }>(source.transport);
    source.deliver({
      type: "handshake",
      version: PROTOCOL_VERSION,
      kind: source.transport.kind,
      codecs: ["iterable", "error", "abort-signal", "callback"],
    });
    const actor = await creation;
    const pending = actor.pending();
    const rejected = assertRejects(() => pending, ActorTransportError, "frame");
    source.deliver({ diagnostic: "not an Actor frame on a dedicated transport" });
    assert(source.transport.closed, "Only a real shared Worker may tolerate typeless diagnostics");
    assertInstanceOf(source.transport.reason, ActorTransportError);
    assertEquals(source.transport.reason.code, "protocol");
    await rejected;
    await actor.dispose();
  });
}
